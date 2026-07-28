using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Remote Face Management
/// Multithreaded blink + eye look-around.
///
/// Hot-path optimizations (target: 1k remotes):
/// - Eye rotation math (asin / AxisAngle / quaternion mul) runs INSIDE the Burst job.
///   Apply just writes pre-computed localRotation per eye — no math on main thread.
/// - Blink writes are change-detected against a per-slot cache; identical weight no
///   longer hits SetBlendShapeWeight (the dominant P/Invoke cost at scale).
/// - Managed face/eye-array references are cached in Simulate so the Apply loop
///   doesn't redo receiver→remote→driver chains 1k times per frame.
/// </summary>
public static class BasisRemoteFaceManagement
{
    public static NativeArray<EyeState> eyeStates;
    public static NativeArray<BlinkState> blinkStates;

    // Authoritative per-remote eye state. Job updates in-place; Apply reads.
    public static NativeArray<EyeOutput> eyeOut;
    public static NativeArray<float> blinkOut;

    // Per-slot eye calibration + active flag, pushed each Simulate from Face drivers.
    // useJobEye[i] = 1 when the slot has eye bones AND is not face-tracking-overridden.
    public static NativeArray<EyeCalibrationBlit> eyeCalLeft;
    public static NativeArray<EyeCalibrationBlit> eyeCalRight;
    public static NativeArray<byte> useJobEye;

    // Pre-computed eye localRotation outputs from the burst job.
    public static NativeArray<quaternion> eyeRotL;
    public static NativeArray<quaternion> eyeRotR;

    // Last blink weight (0..100) actually written to SkinnedMeshRenderer per slot.
    // NaN sentinel forces first-write through. Used to skip SetBlendShapeWeight
    // when the weight hasn't changed since last frame (steady-state: w=0 every frame).
    public static NativeArray<float> lastBlinkApplied;

    // Managed caches built each Simulate. Indexed [0, count). Avoids
    // snapshot[i].RemotePlayer.RemoteFaceDriver / .EyesAndMouth dereferences in Apply.
    public static BasisRemoteFaceDriver[] faceCache = Array.Empty<BasisRemoteFaceDriver>();
    public static float[][] eyesCache = Array.Empty<float[]>();

    // Eye transforms as last built into eyeTransforms, per slot. A reference change (avatar reload) means the
    // TransformAccessArray must be rebuilt; a pure calibration bump leaves these equal and skips the rebuild.
    static Transform[] lastLeftEye = Array.Empty<Transform>();
    static Transform[] lastRightEye = Array.Empty<Transform>();

    // Per-slot last-seen BasisRemoteFaceDriver.FaceGeneration; a mismatch means the slot
    // was (re)calibrated and its eye bones / calibration must be re-read.
    public static uint[] lastFaceGen = Array.Empty<uint>();

    // ── IJobParallelForTransform state for eye-bone writes ─────────────────
    // The TransformAccessArray contains 2 transforms per included slot
    // (left, right), and the per-pair pairToSlot map points each pair back to
    // the receiver slot index so the job can index eyeRotL / eyeRotR / useJobEye.
    // Membership is "slot has eye bones AND both transforms non-null" — OverrideEye
    // is checked dynamically inside the job via useJobEye, so flipping override
    // does NOT trigger a rebuild.
    public static TransformAccessArray eyeTransforms;
    public static NativeArray<int> pairToSlot;
    public static int eyeTransformPairCount;

    // Driver-keyed registry behind eyeTransforms: pair p (transforms 2p, 2p+1) is owned by pairDriver[p], in a
    // stable append/swap-back order independent of the snapshot slot index. A join appends one pair instead of
    // rebinding all transforms; pairToSlot[p] is rewritten each reconcile so snapshot reorders stay cheap.
    static BasisRemoteFaceDriver[] pairDriver = Array.Empty<BasisRemoteFaceDriver>();
    static Transform[] pairLeft = Array.Empty<Transform>();
    static Transform[] pairRight = Array.Empty<Transform>();
    static int[] pairSeenGen = Array.Empty<int>();
    // FaceGeneration the pair's transforms were bound at. Eye bones only change when InitializeEyes bumps
    // FaceGeneration, so a match means the pair's transforms are still valid — the reconcile fast-path skips
    // the transform re-read / Unity null-check / re-point and just reindexes the slot.
    static uint[] pairFaceGen = Array.Empty<uint>();
    static int reconcileGen;
    // lastHasEyeBones[i] mirrors the slot's eye-bones-presence as it was when we
    // last built eyeTransforms. Compared against the current value during Simulate
    // to detect avatar reloads / player swaps cheaply.
    public static NativeArray<byte> lastHasEyeBones;
    public static int lastBuiltCount;
    public static bool eyeTransformJobScheduled;
    public static uint lastSnapshotVersion;

    public static int capacity;

    // You can tune this
    const int BatchSize = 64;

    // Blink defaults (you can expose these)
    public static float MinBlinkInterval = 5f;
    public static float MaxBlinkInterval = 25f;
    public static float BlinkDuration = 0.2f;
    public static float OpenDuration = 0.05f;

    /// <summary>Minimum time (in seconds) between randomized look-around events.</summary>
    public const float MinLookAroundInterval = 1f;

    /// <summary>Maximum time (in seconds) between randomized look-around events.</summary>
    public const float MaxLookAroundInterval = 6f;

    /// <summary>
    /// Maximum horizontal eye target as sin(yaw). 0.34 ≈ sin(20°), so the random
    /// idle look stays within ±20° horizontal — the new apply path runs asin on
    /// this value (BasisRemoteFaceDriver.ApplyEyeRotations), not a linear muscle map.
    /// </summary>
    public const float MaxHorizontalLook = 0.34f;

    /// <summary>
    /// Maximum vertical eye target as sin(pitch). 0.26 ≈ sin(15°). Vertical idle
    /// range is intentionally tighter than horizontal — eyes tend to rotate less
    /// vertically without accompanying head movement.
    /// </summary>
    public const float MaxVerticalLook = 0.26f;

    /// <summary>Speed multiplier controlling how quickly the eyes interpolate toward their target.</summary>
    public const float LookSpeed = 15;

    public static JobHandle handle;
    public static BasisNetworkReceiver[] snapshot;
    public static int count;
    public static bool HasJob = false;

    public static void Simulate(double t, float dt)
    {
        snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        count = BasisNetworkPlayers.ReceiverCount;
        if (count <= 0)
        {
            return;
        }

        EnsureArrays(count, t, snapshot);
        EnsureManagedCaches(count);

        bool needRebuild = !eyeTransforms.isCreated || lastBuiltCount != count;
        bool fullScan = needRebuild || BasisNetworkPlayers.SnapshotVersion != lastSnapshotVersion;
        unsafe
        {
            EyeCalibrationBlit* pCalL = (EyeCalibrationBlit*)eyeCalLeft.GetUnsafePtr();
            EyeCalibrationBlit* pCalR = (EyeCalibrationBlit*)eyeCalRight.GetUnsafePtr();
            byte* pUse = (byte*)useJobEye.GetUnsafePtr();
            byte* pLastHas = (byte*)lastHasEyeBones.GetUnsafePtr();

            if (fullScan)
            {
                for (int Index = 0; Index < count; Index++)
                {
                    BasisNetworkReceiver receiver = snapshot[Index];
                    BasisRemoteFaceDriver Face = receiver.RemotePlayer.RemoteFaceDriver;

                    bool slotChanged = Index >= lastBuiltCount
                        || !ReferenceEquals(faceCache[Index], Face)
                        || lastFaceGen[Index] != Face.FaceGeneration;

                    if (slotChanged)
                    {
                        faceCache[Index] = Face;
                        eyesCache[Index] = receiver.EyesAndMouth;
                        if (BlitSlot(Face, Index, pCalL, pCalR, pLastHas)) needRebuild = true;
                    }

                    pUse[Index] = (pLastHas[Index] != 0 && !Face.OverrideEye) ? (byte)1 : (byte)0;
                }
            }
            else
            {
                for (int Index = 0; Index < count; Index++)
                {
                    BasisRemoteFaceDriver Face = faceCache[Index];

                    if (lastFaceGen[Index] != Face.FaceGeneration)
                    {
                        if (BlitSlot(Face, Index, pCalL, pCalR, pLastHas)) needRebuild = true;
                    }

                    pUse[Index] = (pLastHas[Index] != 0 && !Face.OverrideEye) ? (byte)1 : (byte)0;
                }
            }
        }

        lastSnapshotVersion = BasisNetworkPlayers.SnapshotVersion;

        if (needRebuild)
        {
            ReconcileEyeTransforms();
        }

        var job = new RemoteAnimJob
        {
            time = t,
            dt = dt,

            // Eye config
            minLookInterval = MinLookAroundInterval,
            maxLookInterval = MaxLookAroundInterval,
            maxHoriz = MaxHorizontalLook,
            maxVert = MaxVerticalLook,
            lookSpeed = LookSpeed,

            // Blink config
            minBlinkInterval = MinBlinkInterval,
            maxBlinkInterval = MaxBlinkInterval,
            blinkDuration = BlinkDuration,
            openDuration = OpenDuration,

            eyeStates = eyeStates,
            blinkStates = blinkStates,

            eyeOut = eyeOut,
            blinkOut = blinkOut,

            eyeCalLeft = eyeCalLeft,
            eyeCalRight = eyeCalRight,
            useJobEye = useJobEye,
            eyeRotL = eyeRotL,
            eyeRotR = eyeRotR,
        };

        JobHandle animHandle = job.Schedule(count, BatchSize);

        // Chain the transform-write job: worker threads write Transform.localRotation
        // for every eye-having slot in parallel, leaving Apply with no main-thread
        // localRotation P/Invokes for non-overridden remotes.
        if (eyeTransforms.isCreated && eyeTransformPairCount > 0)
        {
            var writeJob = new EyeTransformWriteJob
            {
                pairToSlot = pairToSlot,
                useJobEye = useJobEye,
                eyeRotL = eyeRotL,
                eyeRotR = eyeRotR,
            };
            handle = writeJob.Schedule(eyeTransforms, animHandle);
            eyeTransformJobScheduled = true;
        }
        else
        {
            handle = animHandle;
            eyeTransformJobScheduled = false;
        }
        HasJob = true;
    }

    // Returns true when this slot's contribution to eyeTransforms changed (eye bones gained/lost, or the eye
    // transforms swapped on an avatar reload) so a rebuild is required; a pure calibration bump re-blits the
    // data below and returns false, sparing the full ~26k-transform rebuild.
    static unsafe bool BlitSlot(BasisRemoteFaceDriver Face, int Index, EyeCalibrationBlit* pCalL, EyeCalibrationBlit* pCalR, byte* pLastHas)
    {
        Transform left = Face.LeftEyeTransform;
        Transform right = Face.RightEyeTransform;
        bool hasValidEyeBones = Face.HasEyeBones && left != null && right != null;

        lastFaceGen[Index] = Face.FaceGeneration;

        bool wasValid = pLastHas[Index] != 0;
        pLastHas[Index] = hasValidEyeBones ? (byte)1 : (byte)0;

        // Blit on eye-bone presence, not useJob, so calibration is ready when OverrideEye later clears.
        if (hasValidEyeBones)
        {
            pCalL[Index] = new EyeCalibrationBlit
            {
                basis = Face.calLeft.basis,
                invBasis = Face.calLeft.invBasis,
                initialRotation = Face.calLeft.initialRotation,
            };
            pCalR[Index] = new EyeCalibrationBlit
            {
                basis = Face.calRight.basis,
                invBasis = Face.calRight.invBasis,
                initialRotation = Face.calRight.initialRotation,
            };
        }

        bool setChanged = wasValid != hasValidEyeBones
            || (hasValidEyeBones && (!ReferenceEquals(lastLeftEye[Index], left) || !ReferenceEquals(lastRightEye[Index], right)));
        lastLeftEye[Index] = hasValidEyeBones ? left : null;
        lastRightEye[Index] = hasValidEyeBones ? right : null;
        return setChanged;
    }

    /// <summary>
    /// Incrementally reconciles <see cref="eyeTransforms"/> + <see cref="pairToSlot"/> with the current
    /// eye-having slots. A joining player appends a single pair; a leaving player / lost eye bones is
    /// swap-back removed; an avatar reload re-points its pair in place. pairToSlot is rewritten for every
    /// live pair so snapshot reorders cost an int rewrite, not a transform rebind. Must run on the main
    /// thread with any in-flight eye job already complete (caller's responsibility).
    /// </summary>
    static unsafe void ReconcileEyeTransforms()
    {
        // Stop any in-flight transform job before we mutate the structure it's reading. handle is the chain
        // (anim → transform write); waiting on it covers both.
        if (HasJob)
        {
            handle.Complete();
            HasJob = false;
            eyeTransformJobScheduled = false;
        }

        // If Unity auto-removed destroyed eye transforms (a leave / avatar reload), the array no longer lines up
        // with our pair bookkeeping — resync with a full rebuild. The hot path (joins) never destroys a transform,
        // so it stays incremental.
        if (!eyeTransforms.isCreated || eyeTransforms.length != eyeTransformPairCount * 2)
        {
            FullRebuildEyeTransforms();
            return;
        }

        // Grow once up front so the append path never reallocates pairToSlot mid-loop (which would dangle pSlot).
        // Upper bound on live pairs during the pass = current pairs + at most one new pair per slot: stale pairs
        // still awaiting the remove pass below can transiently push the count above 'count', so reserve for both.
        EnsurePairCapacity(eyeTransformPairCount + count);

        unchecked { reconcileGen++; }
        int gen = reconcileGen;

        byte* pLastHas = (byte*)lastHasEyeBones.GetUnsafePtr();
        int* pSlot = (int*)pairToSlot.GetUnsafePtr();

        // Add / update / mark-seen pass. Each eye-having slot ensures its driver owns a pair.
        for (int slot = 0; slot < count; slot++)
        {
            if (pLastHas[slot] == 0) continue;
            BasisRemoteFaceDriver Face = faceCache[slot];

            // O(1) driver→pair via the driver-side index, validated against the live registry so a stale
            // index (avatar swap / prior build) falls through to the append path instead of corrupting a pair.
            int p = Face.EyePairIndex;
            bool bound = (uint)p < (uint)eyeTransformPairCount && ReferenceEquals(pairDriver[p], Face);

            // Fast path: pair already bound and its avatar hasn't reloaded since (FaceGeneration unchanged), so
            // the eye transforms are still correct. Skip the transform reads, the two Unity null-checks and the
            // re-point — only the snapshot slot can have moved, so just reindex + mark seen. This is ~every slot
            // every frame during a join storm (existing avatars don't reload), so it carries the whole pass.
            if (bound && pairFaceGen[p] == Face.FaceGeneration)
            {
                pairSeenGen[p] = gen;
                pSlot[p] = slot;
                continue;
            }

            Transform left = Face.LeftEyeTransform;
            Transform right = Face.RightEyeTransform;
            if (left == null || right == null) continue; // teardown race; any existing pair is dropped below

            if (bound)
            {
                // Reload: same driver, swapped eye bones — re-point the pair without touching the rest.
                if (!ReferenceEquals(pairLeft[p], left)) { eyeTransforms[p * 2] = left; pairLeft[p] = left; }
                if (!ReferenceEquals(pairRight[p], right)) { eyeTransforms[p * 2 + 1] = right; pairRight[p] = right; }
            }
            else
            {
                // Join / gained eye bones: append one pair at the end (capacity already reserved above).
                p = eyeTransformPairCount;
                eyeTransforms.Add(left);
                eyeTransforms.Add(right);
                pairDriver[p] = Face;
                pairLeft[p] = left;
                pairRight[p] = right;
                Face.EyePairIndex = p;
                eyeTransformPairCount = p + 1;
            }

            pairFaceGen[p] = Face.FaceGeneration;
            pairSeenGen[p] = gen;
            pSlot[p] = slot;
        }

        // Remove pass: pairs not seen this reconcile (driver left or lost eye bones) are swap-back removed.
        int q = 0;
        while (q < eyeTransformPairCount)
        {
            if (pairSeenGen[q] != gen) RemovePairSwapBack(q);
            else q++;
        }

        lastBuiltCount = count;
    }

    // Grows the pair-parallel bookkeeping arrays (and pairToSlot / the TAA) to hold at least 'needed' pairs.
    static void EnsurePairCapacity(int needed)
    {
        if (pairDriver.Length < needed)
        {
            int newSize = math.max(16, math.ceilpow2(needed));
            Array.Resize(ref pairDriver, newSize);
            Array.Resize(ref pairLeft, newSize);
            Array.Resize(ref pairRight, newSize);
            Array.Resize(ref pairSeenGen, newSize);
            Array.Resize(ref pairFaceGen, newSize);
        }
        if (eyeTransforms.capacity < needed * 2)
        {
            eyeTransforms.capacity = math.max(needed * 2, math.max(4, eyeTransforms.capacity * 2));
        }
        if (!pairToSlot.IsCreated || pairToSlot.Length < needed)
        {
            int newSize = math.max(16, math.ceilpow2(needed));
            var grown = new NativeArray<int>(newSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            if (pairToSlot.IsCreated)
            {
                NativeArray<int>.Copy(pairToSlot, grown, eyeTransformPairCount);
                pairToSlot.Dispose();
            }
            pairToSlot = grown;
        }
    }

    // Removes pair p by moving the last pair into its place (two transform indices), keeping the array dense.
    static void RemovePairSwapBack(int p)
    {
        int last = eyeTransformPairCount - 1;
        pairDriver[p].EyePairIndex = -1; // evicted driver now owns no pair

        if (p != last)
        {
            eyeTransforms[p * 2] = pairLeft[last];
            eyeTransforms[p * 2 + 1] = pairRight[last];
            pairDriver[p] = pairDriver[last];
            pairLeft[p] = pairLeft[last];
            pairRight[p] = pairRight[last];
            pairSeenGen[p] = pairSeenGen[last];
            pairFaceGen[p] = pairFaceGen[last];
            pairToSlot[p] = pairToSlot[last];
            pairDriver[p].EyePairIndex = p; // moved driver now lives at p
        }

        eyeTransforms.RemoveAtSwapBack(last * 2 + 1);
        eyeTransforms.RemoveAtSwapBack(last * 2);
        pairDriver[last] = null;
        pairLeft[last] = null;
        pairRight[last] = null;
        eyeTransformPairCount = last;
    }

    // Resync path: rebuild eyeTransforms + the registry from the live slots. Used for the first build and after
    // Unity auto-drops a destroyed transform (which would otherwise leave the incremental bookkeeping skewed).
    static unsafe void FullRebuildEyeTransforms()
    {
        eyeTransformPairCount = 0;

        byte* pLastHas = (byte*)lastHasEyeBones.GetUnsafePtr();

        int validCount = 0;
        for (int i = 0; i < count; i++)
            if (pLastHas[i] != 0) validCount++;

        if (eyeTransforms.isCreated) eyeTransforms.Dispose();
        eyeTransforms = new TransformAccessArray(math.max(2, validCount * 2));
        EnsurePairCapacity(math.max(1, validCount));

        unchecked { reconcileGen++; }
        int gen = reconcileGen;

        // Cache after EnsurePairCapacity so the pointer is into the final (possibly grown) pairToSlot.
        int* pSlot = (int*)pairToSlot.GetUnsafePtr();

        for (int slot = 0; slot < count; slot++)
        {
            if (pLastHas[slot] == 0) continue;
            BasisRemoteFaceDriver Face = faceCache[slot];
            Transform left = Face.LeftEyeTransform;
            Transform right = Face.RightEyeTransform;
            if (left == null || right == null) continue;

            int p = eyeTransformPairCount;
            eyeTransforms.Add(left);
            eyeTransforms.Add(right);
            pairDriver[p] = Face;
            pairLeft[p] = left;
            pairRight[p] = right;
            pairSeenGen[p] = gen;
            pairFaceGen[p] = Face.FaceGeneration;
            Face.EyePairIndex = p;
            pSlot[p] = slot;
            eyeTransformPairCount = p + 1;
        }

        lastBuiltCount = count;
    }

    public static void Apply()
    {
        if (count <= 0)
        {
            return;
        }
        if (!HasJob)
        {
            return;
        }
#if UNITY_EDITOR
        bool _fp = BasisEventDriverProfilerData.Enabled;
        System.Diagnostics.Stopwatch _fs = null;
        if (_fp) _fs = System.Diagnostics.Stopwatch.StartNew();
#endif
        handle.Complete();
        HasJob = false;
#if UNITY_EDITOR
        if (_fp) { _fs.Stop(); BasisEventDriverProfilerData.RemoteFace_JobCompleteMs = _fs.Elapsed.TotalMilliseconds; _fs.Restart(); }
        int _blinkWrites = 0;
#endif
        unsafe
        {
            EyeOutput* pEyeOut = (EyeOutput*)eyeOut.GetUnsafeReadOnlyPtr();
            float* pBlinkOut = (float*)blinkOut.GetUnsafeReadOnlyPtr();
            byte* pUse = (byte*)useJobEye.GetUnsafeReadOnlyPtr();
            float* pLastBlink = (float*)lastBlinkApplied.GetUnsafePtr();

            for (int Index = 0; Index < count; Index++)
            {
                BasisRemoteFaceDriver Face = faceCache[Index];
                float[] eyes = eyesCache[Index];

                // Always write eye floats (cheap, keeps state consistent across range transitions)
                if (!Face.OverrideEye)
                {
                    EyeOutput e = pEyeOut[Index];

                    eyes[0] = e.vL;
                    eyes[1] = e.hL;
                    eyes[2] = e.vR;
                    eyes[3] = e.hR;
                }

                // Apply the eye floats to the actual eye bones. Eye bones are excluded
                // from the network bone sync (BasisBoneRotationCompression.SyncBoneCount = 51),
                // so without this write remote eyes would be frozen at calibration pose.
                //
                // For useJobEye = 1 slots the IJobParallelForTransform already wrote
                // both eye transforms on a worker thread — main thread does nothing.
                // For useJobEye = 0 slots (OverrideEye / face-tracking active), the
                // job skipped them and we do the legacy asin+calib path here so
                // EyesAndMouth values from EyeTrackingBoneActuation flow through.
                if (Face.HasEyeBones && pUse[Index] == 0)
                {
                    Face.ApplyEyeRotations(eyes[0], eyes[1], eyes[2], eyes[3]);
                }

                // BlinkingEnabled is the visibility flag — UpdateFaceVisibility(false)
                // turns it off when BasisMeshRendererCheck reports the face is culled,
                // and also resets the mesh's blendshapes to 0 at that moment. So when
                // this gate is false (invisible / overridden / no mesh) we skip the
                // SetBlendShapeWeight P/Invoke entirely. No meshRenderer null check
                // here: that is a native fake-null call per visible face per frame,
                // and SafeSetBlendShape already guards each actual write — a renderer
                // destroyed behind our back just no-ops until the next face rebuild.
                if (Face.BlinkingEnabled && !Face.OverrideBlinking)
                {
                    float w = pBlinkOut[Index];
                    if (!float.IsFinite(w)) w = 0f;

                    float weight100 = w * 100f;

                    // Change-detection: SetBlendShapeWeight is a P/Invoke that's
                    // cheap individually but lethal at 1k * blendShapeCount per frame.
                    // Most frames the weight is identical to last frame (eyes are
                    // open and were open). NaN sentinel from EnsureArrays guarantees
                    // the first frame writes through.
                    if (weight100 != pLastBlink[Index])
                    {
                        pLastBlink[Index] = weight100;
                        for (int b = 0; b < Face.blendShapeCount; b++)
                        {
                            Face.SafeSetBlendShape(Face.blendShapeIndices[b], weight100);
                        }
#if UNITY_EDITOR
                        _blinkWrites++;
#endif
                    }
                }
                else
                {
                    // Skipping due to invisibility / override / missing mesh.
                    // UpdateFaceVisibility(false) zeroed the blendshapes when the face
                    // went hidden, so the cached weight no longer reflects the mesh
                    // state. Invalidate it so the next frame that does write goes
                    // through even if it computes the same numeric weight as before.
                    pLastBlink[Index] = float.NaN;
                }
            }
        }
#if UNITY_EDITOR
        if (_fp)
        {
            _fs.Stop();
            BasisEventDriverProfilerData.RemoteFace_EyeWriteMs = _fs.Elapsed.TotalMilliseconds; // includes both eye + blink write time
            BasisEventDriverProfilerData.RemoteFace_BlinkWriteCount = _blinkWrites;
        }
#endif
    }

    static void EnsureManagedCaches(int requiredCount)
    {
        if (faceCache.Length < requiredCount)
        {
            int newSize = math.max(16, math.ceilpow2(requiredCount));
            Array.Resize(ref faceCache, newSize);
            Array.Resize(ref eyesCache, newSize);
            Array.Resize(ref lastFaceGen, newSize);
            Array.Resize(ref lastLeftEye, newSize);
            Array.Resize(ref lastRightEye, newSize);
        }
    }

    static void EnsureArrays(int requiredCount, double nowTime, BasisNetworkReceiver[] snapshot)
    {
        // Already sufficient
        if (requiredCount <= capacity && eyeStates.IsCreated)
        {
            return;
        }

        // Never dispose/reallocate while a job might be using them.
        handle.Complete();

        int oldCap = capacity;
        int newCap = math.ceilpow2(math.max(16, requiredCount));

        var newEyeStates = new NativeArray<EyeState>(newCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var newBlinkStates = new NativeArray<BlinkState>(newCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        // ClearMemory prevents “uninitialized NaN” surprises.
        var newEyeOut = new NativeArray<EyeOutput>(newCap, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        var newBlinkOut = new NativeArray<float>(newCap, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        var newEyeCalLeft = new NativeArray<EyeCalibrationBlit>(newCap, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        var newEyeCalRight = new NativeArray<EyeCalibrationBlit>(newCap, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        var newUseJobEye = new NativeArray<byte>(newCap, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        var newEyeRotL = new NativeArray<quaternion>(newCap, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        var newEyeRotR = new NativeArray<quaternion>(newCap, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        // lastHasEyeBones starts at 0 for every slot so the first Simulate after a
        // grow will compare-mismatch any new eye-having receiver and trigger a rebuild.
        var newLastHasEyeBones = new NativeArray<byte>(newCap, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        // lastBlinkApplied uses NaN as "never written" sentinel so the first
        // computed weight forces a SetBlendShapeWeight call through.
        var newLastBlinkApplied = new NativeArray<float>(newCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        // Copy old live data across if present
        if (eyeStates.IsCreated)
        {
            int copyCount = math.min(oldCap, newCap);

            NativeArray<EyeState>.Copy(eyeStates, newEyeStates, copyCount);
            NativeArray<BlinkState>.Copy(blinkStates, newBlinkStates, copyCount);
            NativeArray<EyeOutput>.Copy(eyeOut, newEyeOut, copyCount);
            NativeArray<float>.Copy(blinkOut, newBlinkOut, copyCount);

            NativeArray<EyeCalibrationBlit>.Copy(eyeCalLeft, newEyeCalLeft, copyCount);
            NativeArray<EyeCalibrationBlit>.Copy(eyeCalRight, newEyeCalRight, copyCount);
            NativeArray<byte>.Copy(useJobEye, newUseJobEye, copyCount);
            NativeArray<quaternion>.Copy(eyeRotL, newEyeRotL, copyCount);
            NativeArray<quaternion>.Copy(eyeRotR, newEyeRotR, copyCount);
            NativeArray<byte>.Copy(lastHasEyeBones, newLastHasEyeBones, copyCount);
            NativeArray<float>.Copy(lastBlinkApplied, newLastBlinkApplied, copyCount);

            DisposeArrays();
        }

        capacity = newCap;
        eyeStates = newEyeStates;
        blinkStates = newBlinkStates;
        eyeOut = newEyeOut;
        blinkOut = newBlinkOut;

        eyeCalLeft = newEyeCalLeft;
        eyeCalRight = newEyeCalRight;
        useJobEye = newUseJobEye;
        eyeRotL = newEyeRotL;
        eyeRotR = newEyeRotR;
        lastHasEyeBones = newLastHasEyeBones;
        lastBlinkApplied = newLastBlinkApplied;

        // Seed RNGs + initialize ONLY the new slots
        uint baseSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue);

        unsafe
        {
            EyeState* pEyeStates = (EyeState*)eyeStates.GetUnsafePtr();
            BlinkState* pBlinkStates = (BlinkState*)blinkStates.GetUnsafePtr();
            EyeOutput* pEyeOut = (EyeOutput*)eyeOut.GetUnsafePtr();
            float* pBlinkOut = (float*)blinkOut.GetUnsafePtr();
            float* pLastBlinkApplied = (float*)lastBlinkApplied.GetUnsafePtr();

            for (int i = oldCap; i < newCap; i++)
            {
                uint eyeSeed = HashToNonZero(baseSeed, (uint)(i * 2 + 1));
                uint blinkSeed = HashToNonZero(baseSeed, (uint)(i * 2 + 2));

                // Start pose from snapshot if it exists
                float2 startTarget = float2.zero;
                EyeOutput startEye = default;

                if (i < requiredCount && snapshot != null)
                {
                    var arr = snapshot[i].EyesAndMouth;
                    if (arr != null && arr.Length >= 4)
                    {
                        float vL = float.IsFinite(arr[0]) ? arr[0] : 0f;
                        float hL = float.IsFinite(arr[1]) ? arr[1] : 0f;
                        float vR = float.IsFinite(arr[2]) ? arr[2] : 0f;
                        float hR = float.IsFinite(arr[3]) ? arr[3] : 0f;

                        startTarget = new float2(hL, vL);
                        startEye = new EyeOutput { vL = vL, hL = hL, vR = vR, hR = hR };
                    }
                }

                pEyeStates[i] = new EyeState
                {
                    nextLookAroundTime = nowTime + Unity.Mathematics.Random.CreateFromIndex(eyeSeed)
                        .NextFloat(MinLookAroundInterval, MaxLookAroundInterval),
                    target = startTarget,
                    isLooking = 0,
                    rng = new Unity.Mathematics.Random(eyeSeed),
                };

                pBlinkStates[i] = new BlinkState
                {
                    nextBlinkTime = nowTime + Unity.Mathematics.Random.CreateFromIndex(blinkSeed)
                        .NextFloat(MinBlinkInterval, MaxBlinkInterval),
                    blinkStartTime = 0.0,
                    openStartTime = 0.0,
                    isClosing = 0,
                    isOpening = 0,
                    rng = new Unity.Mathematics.Random(blinkSeed),
                };

                pEyeOut[i] = startEye;
                pBlinkOut[i] = 0f;
                pLastBlinkApplied[i] = float.NaN;
            }
        }
    }

    static uint HashToNonZero(uint a, uint b)
    {
        // Simple mix; must not return 0 for Unity.Mathematics.Random
        uint x = a ^ (b * 0x9E3779B9u);
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x == 0 ? 1u : x;
    }

    static void DisposeArrays()
    {
        if (eyeStates.IsCreated) eyeStates.Dispose();
        if (blinkStates.IsCreated) blinkStates.Dispose();
        if (eyeOut.IsCreated) eyeOut.Dispose();
        if (blinkOut.IsCreated) blinkOut.Dispose();

        if (eyeCalLeft.IsCreated) eyeCalLeft.Dispose();
        if (eyeCalRight.IsCreated) eyeCalRight.Dispose();
        if (useJobEye.IsCreated) useJobEye.Dispose();
        if (eyeRotL.IsCreated) eyeRotL.Dispose();
        if (eyeRotR.IsCreated) eyeRotR.Dispose();
        if (lastHasEyeBones.IsCreated) lastHasEyeBones.Dispose();
        if (lastBlinkApplied.IsCreated) lastBlinkApplied.Dispose();
    }

    public static void Dispose()
    {
        handle.Complete();
        DisposeArrays();
        if (eyeTransforms.isCreated) eyeTransforms.Dispose();
        if (pairToSlot.IsCreated) pairToSlot.Dispose();
        eyeTransformPairCount = 0;
        eyeTransformJobScheduled = false;
        lastBuiltCount = 0;
        capacity = 0;

        faceCache = Array.Empty<BasisRemoteFaceDriver>();
        eyesCache = Array.Empty<float[]>();
        lastFaceGen = Array.Empty<uint>();
        lastLeftEye = Array.Empty<Transform>();
        lastRightEye = Array.Empty<Transform>();

        pairDriver = Array.Empty<BasisRemoteFaceDriver>();
        pairLeft = Array.Empty<Transform>();
        pairRight = Array.Empty<Transform>();
        pairSeenGen = Array.Empty<int>();
        pairFaceGen = Array.Empty<uint>();
        reconcileGen = 0;
    }

    [BurstCompile]
    public struct RemoteAnimJob : IJobParallelFor
    {
        public double time;
        public float dt;

        // Eye config
        public float minLookInterval;
        public float maxLookInterval;
        public float maxHoriz;
        public float maxVert;
        public float lookSpeed;

        // Blink config
        public float minBlinkInterval;
        public float maxBlinkInterval;
        public float blinkDuration;
        public float openDuration;

        public NativeArray<EyeState> eyeStates;
        public NativeArray<BlinkState> blinkStates;

        // eyeOut is BOTH input and output state for eyes
        public NativeArray<EyeOutput> eyeOut;

        public NativeArray<float> blinkOut;

        // Per-slot calibration + active flag, written by Simulate.
        [ReadOnly] public NativeArray<EyeCalibrationBlit> eyeCalLeft;
        [ReadOnly] public NativeArray<EyeCalibrationBlit> eyeCalRight;
        [ReadOnly] public NativeArray<byte> useJobEye;

        // Per-slot computed eye bone rotations (canonical→rig). Apply consumes these.
        [WriteOnly] public NativeArray<quaternion> eyeRotL;
        [WriteOnly] public NativeArray<quaternion> eyeRotR;

        public void Execute(int Index)
        {
            var es = eyeStates[Index];
            var e = eyeOut[Index];

            // Sanitize current state so NaNs don’t propagate forever
            e.vL = Sanitize(e.vL);
            e.hL = Sanitize(e.hL);
            e.vR = Sanitize(e.vR);
            e.hR = Sanitize(e.hR);

            if (time >= es.nextLookAroundTime)
            {
                double interval = es.rng.NextFloat(minLookInterval, maxLookInterval);
                es.nextLookAroundTime = time + interval;

                es.target = new float2(
                    es.rng.NextFloat(-maxHoriz, maxHoriz),
                    es.rng.NextFloat(-maxVert, maxVert)
                );

                es.isLooking = 1;
            }

            if (es.isLooking != 0)
            {
                // Use a stable 0..1 factor; avoids overshoot.
                float t01 = math.saturate(lookSpeed * dt);

                e.vL = math.lerp(e.vL, es.target.y, t01);
                e.vR = math.lerp(e.vR, es.target.y, t01);

                // Both eyes share the same horizontal target. The previous code negated
                // hR — that compensated for non-calibrated eye bone orientations in the
                // old muscle-based pipeline. The new BasisRemoteFaceDriver calibration
                // already normalises canonical→rig per-eye, so a single signed yaw
                // drives both eyes in the same world direction.
                e.hL = math.lerp(e.hL, es.target.x, t01);
                e.hR = math.lerp(e.hR, es.target.x, t01);

                if (math.abs(e.vL - es.target.y) < 0.01f && math.abs(e.hL - es.target.x) < 0.01f)
                    es.isLooking = 0;
            }

            // Clamp to your expected operating range (optional but robust)
            e.vL = math.clamp(e.vL, -1f, 1f);
            e.vR = math.clamp(e.vR, -1f, 1f);
            e.hL = math.clamp(e.hL, -1f, 1f);
            e.hR = math.clamp(e.hR, -1f, 1f);

            eyeStates[Index] = es;
            eyeOut[Index] = e;

            // Pre-compute eye bone localRotation while the data is hot.
            // Apply just assigns transform.localRotation = result — no math,
            // no asin, no quaternion muls on the main thread.
            if (useJobEye[Index] != 0)
            {
                eyeRotL[Index] = ComputeEyeRotation(eyeCalLeft[Index], e.hL, e.vL);
                eyeRotR[Index] = ComputeEyeRotation(eyeCalRight[Index], e.hR, e.vR);
            }

            // --------------------
            // BLINK
            // --------------------
            var bs = blinkStates[Index];
            float w01;

            if (bs.nextBlinkTime <= 0.0)
            {
                bs.nextBlinkTime = time + bs.rng.NextFloat(minBlinkInterval, maxBlinkInterval);
            }

            if (bs.isClosing == 0 && bs.isOpening == 0 && time >= bs.nextBlinkTime)
            {
                bs.isClosing = 1;
                bs.blinkStartTime = time;

                bs.isOpening = 1;
                bs.openStartTime = time;
            }

            if (bs.isClosing != 0)
            {
                float t = (float)((time - bs.blinkStartTime) / blinkDuration);
                t = math.saturate(t);
                w01 = t;

                if (t >= 1f)
                {
                    bs.isClosing = 0;
                    bs.nextBlinkTime = time + bs.rng.NextFloat(minBlinkInterval, maxBlinkInterval);
                }
            }
            else if (bs.isOpening != 0)
            {
                float t = (float)((time - bs.openStartTime) / openDuration);
                t = math.saturate(t);
                w01 = 1f - t;

                if (t >= 1f)
                    bs.isOpening = 0;
            }
            else
            {
                w01 = 0f;
            }

            // Sanitize blink output too
            w01 = Sanitize01(w01);

            blinkStates[Index] = bs;
            blinkOut[Index] = w01;
        }

        // Mirrors BasisRemoteFaceDriver.ApplyOneEye math. Inputs are signed
        // [-1, 1]; asin maps them into a half-pi yaw/pitch range. Negate y so
        // positive vertical means look up. The result is a rig-local rotation
        // ready to assign directly to Transform.localRotation.
        static quaternion ComputeEyeRotation(EyeCalibrationBlit cal, float x, float y)
        {
            x = math.clamp(math.isfinite(x) ? x : 0f, -1f, 1f);
            y = math.clamp(math.isfinite(y) ? y : 0f, -1f, 1f);

            float xRad = math.asin(x);
            float yRad = math.asin(-y);
            quaternion yaw = quaternion.AxisAngle(new float3(0f, 1f, 0f), xRad);
            quaternion pitch = quaternion.AxisAngle(new float3(1f, 0f, 0f), yRad);
            quaternion canonical = math.mul(yaw, pitch);

            quaternion rigOffset = math.mul(math.mul(cal.basis, canonical), cal.invBasis);
            return math.mul(cal.initialRotation, rigOffset);
        }

        static float Sanitize(float x)
        {
            // Burst-friendly finite check
            return math.isfinite(x) ? x : 0f;
        }

        static float Sanitize01(float x)
        {
            if (!math.isfinite(x)) return 0f;
            return math.saturate(x);
        }
    }

    /// <summary>
    /// Writes pre-computed eye localRotations to the receivers' eye Transforms on
    /// worker threads. Iterates a <see cref="TransformAccessArray"/> built once
    /// per change-of-membership; pairs (2 transforms per eye-having slot) are
    /// laid out [L0, R0, L1, R1, …] so <c>index &gt;&gt; 1</c> recovers the pair
    /// and <c>index &amp; 1</c> picks left vs. right. Per-pair the slot in the
    /// underlying NativeArrays comes from <see cref="pairToSlot"/>; OverrideEye
    /// slots are skipped here so Apply can run the legacy main-thread path.
    /// </summary>
    [BurstCompile]
    public struct EyeTransformWriteJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<int> pairToSlot;
        [ReadOnly] public NativeArray<byte> useJobEye;
        [ReadOnly] public NativeArray<quaternion> eyeRotL;
        [ReadOnly] public NativeArray<quaternion> eyeRotR;

        public void Execute(int index, TransformAccess transform)
        {
            int pair = index >> 1;
            int slot = pairToSlot[pair];
            // Slot turned OverrideEye between Simulate and now — main-thread Apply
            // will write face-tracking values for this slot; leave the transform alone.
            if (useJobEye[slot] == 0) return;

            bool isLeft = (index & 1) == 0;
            transform.localRotation = isLeft ? eyeRotL[slot] : eyeRotR[slot];
        }
    }

    public struct EyeState
    {
        public double nextLookAroundTime;
        public float2 target;
        public byte isLooking;
        public Unity.Mathematics.Random rng;
    }

    public struct BlinkState
    {
        public double nextBlinkTime;
        public double blinkStartTime;
        public double openStartTime;
        public byte isClosing;
        public byte isOpening;
        public Unity.Mathematics.Random rng;
    }

    public struct EyeOutput
    {
        public float vL, hL, vR, hR;
    }

    /// <summary>
    /// Blittable copy of <see cref="BasisEyeCalibration"/> so the Burst job can
    /// read calibration via NativeArray (no managed reference indirection).
    /// </summary>
    public struct EyeCalibrationBlit
    {
        public quaternion basis;
        public quaternion invBasis;
        public quaternion initialRotation;
    }
}
