using Basis.Scripts.Common;
using Basis.Scripts.Player;
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// The one copy of the grid interpolation. Kept as a free-standing static class so a Burst job
    /// can call it without touching the managed grid object that owns the cells.
    ///
    /// Local and remote MUST reconstruct through this same function. Two consequences if they don't:
    /// the hand a player sees on themselves stops matching the hand everyone else sees, and the
    /// remote apply path's write mask — which skips a localRotation write when the composed value is
    /// bit-identical to last frame — stops firing on settled fingers, dirtying every remote's finger
    /// subtree every frame.
    /// </summary>
    public static class BasisHandPoseSampler
    {
        public const int JointsPerFinger = BasisHandPoseGrid.JointsPerFinger;

        /// <summary>
        /// Bilinearly samples one joint's rotation for a finger at the given curl/splay.
        ///
        /// Percentages outside [-1, 1] clamp to the grid edge rather than reading out of bounds:
        /// MediaPipe's CurlGain/SplayGain and the controller remaps can both overshoot, and the
        /// clamp is on the cell INDEX as well as the blend factor so an overshoot saturates instead
        /// of wrapping onto another finger's cells.
        /// </summary>
        public static quaternion SampleJoint(
            in NativeArray<quaternion> cells, int fingerStride, int gridWidth, int gridHeight,
            float increment, int fingerIndex, int jointIndex, float2 percentage)
        {
            float fx = (percentage.x + 1f) / increment;
            float fy = (percentage.y + 1f) / increment;
            int x0 = math.clamp((int)math.floor(fx), 0, gridWidth - 2);
            int y0 = math.clamp((int)math.floor(fy), 0, gridHeight - 2);
            float tx = math.clamp(fx - x0, 0f, 1f);
            float ty = math.clamp(fy - y0, 0f, 1f);

            int fingerBase = fingerIndex * fingerStride;
            int g00 = fingerBase + (x0 * gridHeight + y0) * JointsPerFinger + jointIndex;
            int g10 = fingerBase + ((x0 + 1) * gridHeight + y0) * JointsPerFinger + jointIndex;
            int g01 = fingerBase + (x0 * gridHeight + y0 + 1) * JointsPerFinger + jointIndex;
            int g11 = fingerBase + ((x0 + 1) * gridHeight + y0 + 1) * JointsPerFinger + jointIndex;

            quaternion bottom = math.slerp(cells[g00], cells[g10], tx);
            quaternion top = math.slerp(cells[g01], cells[g11], tx);
            return math.slerp(bottom, top, ty);
        }
    }

    /// <summary>
    /// The baked map from a hand's twenty-scalar input (five curl/splay pairs per hand, which is what
    /// every Basis finger backend reduces to) onto that avatar's thirty finger joint rotations.
    ///
    /// Split out of BasisLocalHandDriver so a REMOTE player can run the identical reconstruction. The
    /// sampler below is the single copy of the interpolation — local and remote must agree bit for
    /// bit, or the hand a player sees on themselves is not the hand everyone else sees, and the
    /// remote apply path's write mask (which skips a transform write when the composed rotation is
    /// unchanged) stops firing on settled fingers.
    ///
    /// The grid is a property of the AVATAR ASSET, not of the player wearing it, so it is interned
    /// through BasisAvatarModelCache: a crowd in matching avatars bakes once.
    /// </summary>
    public sealed class BasisHandPoseGrid : IDisposable
    {
        public const int FingerCount = 10;
        public const int JointsPerFinger = 3;
        public const int JointCount = FingerCount * JointsPerFinger;
        public const float DefaultIncrement = 0.1f;

        /// <summary>Flat cells: [fingerIdx * FingerStride + gridIdx * 3 + jointIdx].</summary>
        public NativeArray<quaternion> Cells;
        public int GridWidth;
        public int GridHeight;
        public int FingerStride;
        public float Increment = DefaultIncrement;

        /// <summary>
        /// False while <see cref="Cells"/> is a VIEW of the shared array owned by
        /// <see cref="BasisAvatarModelCache"/>, which is the normal state for everyone but the
        /// first wearer of an avatar. Only a grid that baked its own cells and never published
        /// them frees anything.
        /// <para>Each restore used to allocate a fresh 207 KB Persistent array and rebuild all
        /// 13,230 quaternions element by element out of a float[] — per remote player, per avatar
        /// swap, for bytes identical across everyone in the same avatar. Nothing writes cells
        /// after the bake (every consumer only samples), so the copy bought nothing.</para>
        /// </summary>
        public bool OwnsCells { get; private set; }

        /// <summary>
        /// True only for a grid that is actually samplable. The array being allocated is not enough:
        /// a zero-length or single-column grid reports IsCreated on the NativeArray while every
        /// sample indexes out of bounds, and the sampler's clamp range (0 .. gridWidth - 2) inverts
        /// below two columns. Burst turns that into an abort rather than an exception, so the check
        /// belongs here where every caller already looks.
        /// </summary>
        public bool IsCreated =>
            Cells.IsCreated && Cells.Length > 0 && FingerStride > 0 && GridWidth >= 2 && GridHeight >= 2;

        public void Dispose()
        {
            if (OwnsCells && Cells.IsCreated)
            {
                Cells.Dispose();
            }
            // Cleared even for a view: the shared array outlives us, and leaving the handle
            // behind would let a torn-down driver keep sampling a grid it no longer holds.
            Cells = default;
            OwnsCells = false;
            GridWidth = 0;
            GridHeight = 0;
            FingerStride = 0;
        }

        /// <summary>
        /// Teardown for a driver that is going away: frees cells this grid OWNS and leaves a
        /// shared view completely alone.
        /// </summary>
        /// <remarks>
        /// Deliberately does NOT clear a view the way <see cref="Dispose"/> does. The parallel
        /// network compute samples this grid from worker threads and is only guaranteed joined at
        /// a few points in the frame; nulling <see cref="Cells"/> from a teardown that can run
        /// outside those windows is a torn read. Leaving the view costs nothing — the shared
        /// buffer outlives the cache entry by design, and a destroyed player is off the receiver
        /// snapshot so nothing samples it again.
        /// </remarks>
        public void DisposeOwnedOnly()
        {
            if (OwnsCells && Cells.IsCreated)
            {
                Cells.Dispose();
                Cells = default;
                OwnsCells = false;
                GridWidth = 0;
                GridHeight = 0;
                FingerStride = 0;
            }
        }

        public quaternion SampleJoint(int fingerIndex, int jointIndex, float2 percentage)
            => BasisHandPoseSampler.SampleJoint(Cells, FingerStride, GridWidth, GridHeight, Increment,
                fingerIndex, jointIndex, percentage);

        /// <summary>Samples all thirty joints into <paramref name="destination"/>, flat-indexed finger*3+joint.</summary>
        public void SampleAll(in NativeArray<float2> percentages, ref NativeArray<quaternion> destination)
        {
            for (int finger = 0; finger < FingerCount; finger++)
            {
                float2 pct = percentages[finger];
                int baseIdx = finger * JointsPerFinger;
                for (int joint = 0; joint < JointsPerFinger; joint++)
                {
                    destination[baseIdx + joint] = SampleJoint(finger, joint, pct);
                }
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Acquisition
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fills <paramref name="target"/> with the grid for <paramref name="animator"/>'s avatar,
        /// restoring from <see cref="BasisAvatarModelCache"/> when it is already baked and baking
        /// (then storing) when it is not.
        ///
        /// The cache is keyed on the Avatar ASSET, so a crowd wearing the same avatar bakes once —
        /// which matters because a bake instantiates a hidden duplicate and runs 441 SetHumanPose
        /// calls, and remote players now need this too.
        /// </summary>
        public static bool TryAcquire(Animator animator, float increment, BasisHandPoseGrid target)
        {
            if (animator == null || target == null) return false;

            EntityId key = BasisAvatarModelCache.GetKey(animator);
            if (key != EntityId.None
                && BasisAvatarModelCache.TryGet(key, out var cached)
                && cached.HandPoseGrid != null)
            {
                target.RestoreFrom(cached.HandPoseGrid);
                return target.IsCreated;
            }

            if (!target.TryBake(animator, increment, out BakeResult bake)) return false;

            if (key != EntityId.None)
            {
                var entry = BasisAvatarModelCache.GetOrCreate(key, animator.avatar);
                if (entry.HandPoseGrid != null)
                {
                    // Someone else baked this same avatar while we were in TryBake. Publishing over
                    // them would orphan their array with our view still pointing at it, so drop the
                    // duplicate bake and share what is already there.
                    target.Dispose();
                    target.RestoreFrom(entry.HandPoseGrid);
                    return target.IsCreated;
                }
                var data = new BasisAvatarModelCache.HandPoseGridData
                {
                    LeftThumb = bake.LeftThumb,
                    LeftIndex = bake.LeftIndex,
                    LeftMiddle = bake.LeftMiddle,
                    LeftRing = bake.LeftRing,
                    LeftLittle = bake.LeftLittle,
                    RightThumb = bake.RightThumb,
                    RightIndex = bake.RightIndex,
                    RightMiddle = bake.RightMiddle,
                    RightRing = bake.RightRing,
                    RightLittle = bake.RightLittle,

                    InitialPose = bake.RestPose,
                };
                target.PublishCellsTo(data);
                entry.HandPoseGrid = data;
            }
            return target.IsCreated;
        }

        /// <summary>
        /// Expands ten curl/splay pairs into the thirty finger joint rotations, writing them into
        /// <paramref name="boneRotations"/> at the wire slots the finger joints occupy.
        ///
        /// The values written are this rig's LOCAL rotations, not generic-space ones. The receiver
        /// pairs this with identity decode operators on those slots, so the compose job's
        /// <c>DecodePre * value * DecodePost</c> passes them through untouched — which avoids a
        /// generic-space round trip that would only ever undo itself.
        /// </summary>
        public void ExpandInto(in NativeArray<float2> percentages, NativeArray<quaternion> boneRotations, int firstFingerSlot)
        {
            if (!IsCreated || !percentages.IsCreated || !boneRotations.IsCreated) return;

            // BONE_WRITE_ORDER groups the finger slots by joint tier — all ten proximals, then all
            // ten intermediates, then all ten distals — so the slot for (finger, joint) is
            // joint*10 + finger, not finger*3 + joint.
            for (int finger = 0; finger < FingerCount; finger++)
            {
                float2 pct = percentages[finger];
                for (int joint = 0; joint < JointsPerFinger; joint++)
                {
                    int slot = firstFingerSlot + joint * FingerCount + finger;
                    if ((uint)slot >= (uint)boneRotations.Length) continue;
                    boneRotations[slot] = SampleJoint(finger, joint, pct);
                }
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Cache round-trip
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Points this grid at the cache entry's shared cells. No allocation and no copy — see
        /// <see cref="OwnsCells"/>.
        /// </summary>
        public void RestoreFrom(BasisAvatarModelCache.HandPoseGridData cached)
        {
            Dispose();

            GridWidth = cached.GridWidth;
            GridHeight = cached.GridHeight;
            FingerStride = cached.FingerStride;
            Increment = cached.Increment > 0f ? cached.Increment : DefaultIncrement;

            Cells = cached.SharedCells;
        }

        /// <summary>
        /// Hands the freshly baked cells to <paramref name="destination"/> and demotes this grid to
        /// a view of them. Call only with a destination that is about to be stored in
        /// <see cref="BasisAvatarModelCache"/>, which takes over freeing them.
        /// </summary>
        public void PublishCellsTo(BasisAvatarModelCache.HandPoseGridData destination)
        {
            destination.SharedCells = Cells;
            destination.GridWidth = GridWidth;
            destination.GridHeight = GridHeight;
            destination.FingerStride = FingerStride;
            destination.TotalElements = Cells.IsCreated ? Cells.Length : 0;
            destination.Increment = Increment;
            OwnsCells = false;
        }

        // ────────────────────────────────────────────────────────────
        //  Bake
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Result of sampling Unity muscle space across the curl/splay square on a throwaway copy of
        /// the avatar. <paramref name="restPose"/> is the pose recorded before the sweep started.
        /// </summary>
        public struct BakeResult
        {
            public BasisPoseData RestPose;
            public float[] LeftThumb, LeftIndex, LeftMiddle, LeftRing, LeftLittle;
            public float[] RightThumb, RightIndex, RightMiddle, RightRing, RightLittle;
        }

        const int MuscleLeftThumb = 55;

        /// <summary>
        /// The thirty finger bones in the order the grid indexes them — finger*3 + joint, left
        /// thumb through right little, proximal/intermediate/distal.
        /// </summary>
        static readonly HumanBodyBones[] FingerBones =
        {
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
            HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
            HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
            HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
            HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
            HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
            HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
        };

        /// <summary>
        /// Bake scratch, reused across every bake in the process. The sweep used to allocate a
        /// BasisPoseData plus its ten Quaternion[3] arrays PER GRID CELL — 442 * 11 objects for one
        /// avatar model, on the main thread, inside the calibration spike. A bake is main thread
        /// only and cannot nest, so one set of buffers serves all of them.
        /// </summary>
        static readonly Transform[] sBakeJoints = new Transform[JointCount];
        static readonly bool[] sBakePresent = new bool[JointCount];
        static readonly Quaternion[] sBakeRotations = new Quaternion[JointCount];

        /// <summary>
        /// Bakes the grid off a hidden duplicate of <paramref name="source"/>. The duplicate is
        /// destroyed before returning; the source animator is never posed.
        /// </summary>
        public unsafe bool TryBake(Animator source, float increment, out BakeResult result)
        {
            result = default;
            if (source == null) return false;

            // Drop any previous grid up front so every failure path below leaves this object
            // unusable rather than holding the LAST avatar's fingers, which would silently pose the
            // new rig with the old one's curl map instead of falling back to the bind pose.
            Dispose();

            Increment = increment > 0f ? increment : DefaultIncrement;
            GridWidth = Mathf.RoundToInt(2f / Increment) + 1;
            GridHeight = GridWidth;

            GameObject copy = UnityEngine.Object.Instantiate(source.gameObject);
            copy.SetActive(false);
            try
            {
                if (!copy.TryGetComponent(out Animator animator)) return false;
                if (!animator.isHuman)
                {
                    BasisDebug.LogError("We need a Humanoid Animator");
                    return false;
                }

                Transform[] joints = sBakeJoints;
                bool[] present = sBakePresent;
                bool anyFinger = false;
                for (int index = 0; index < JointCount; index++)
                {
                    Transform bone = animator.GetBoneTransform(FingerBones[index]);
                    joints[index] = bone;
                    present[index] = bone != null;
                    anyFinger |= bone != null;
                }

                PutIntoTPose(animator);

                HumanPoseHandler poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
                try
                {
                    HumanPose tpose = new HumanPose();
                    poseHandler.GetHumanPose(ref tpose);

                    result.LeftThumb = CopyMuscles(tpose, 0);
                    result.LeftIndex = CopyMuscles(tpose, 4);
                    result.LeftMiddle = CopyMuscles(tpose, 8);
                    result.LeftRing = CopyMuscles(tpose, 12);
                    result.LeftLittle = CopyMuscles(tpose, 16);
                    result.RightThumb = CopyMuscles(tpose, 20);
                    result.RightIndex = CopyMuscles(tpose, 24);
                    result.RightMiddle = CopyMuscles(tpose, 28);
                    result.RightRing = CopyMuscles(tpose, 32);
                    result.RightLittle = CopyMuscles(tpose, 36);

                    result.RestPose = RecordPose(joints, present);

                    int gridCount = GridWidth * GridHeight;
                    FingerStride = gridCount * JointsPerFinger;

                    // Release only the cells. Dispose() also zeroes the dimensions, and calling it
                    // here would clear the FingerStride computed one line up — allocating 10 * 0
                    // cells and leaving a grid that reports IsCreated while every sample indexes
                    // out of bounds inside Burst.
                    if (OwnsCells && Cells.IsCreated) Cells.Dispose();
                    Cells = new NativeArray<quaternion>(FingerCount * FingerStride, Allocator.Persistent);
                    OwnsCells = true;

                    // The sweep writes the finger muscles straight into the pose and the resulting
                    // local rotations straight into the cells. It used to round-trip both through
                    // per-finger float[4] slices and a freshly allocated BasisPoseData per cell.
                    float[] muscles = tpose.muscles;
                    Quaternion[] rotations = sBakeRotations;
                    quaternion* cells = (quaternion*)Cells.GetUnsafePtr();

                    // A rig with no finger bones records identity in every cell whatever the sweep
                    // poses it to, so the 441 SetHumanPose calls buy nothing. The far LOD skeleton
                    // is exactly this — twenty core bones, no hands — and every far LOD version was
                    // paying a full sweep for a grid of identities. Write them directly; the
                    // allocation zero-inits to (0,0,0,0), which is not a rotation and would come out
                    // of the sampler's slerp as NaN.
                    if (!anyFinger)
                    {
                        for (int cell = 0; cell < Cells.Length; cell++) cells[cell] = quaternion.identity;
                    }
                    else
                    {
                        for (int xi = 0; xi < GridWidth; xi++)
                        {
                            float curl = -1f + xi * Increment;
                            for (int yi = 0; yi < GridHeight; yi++)
                            {
                                float splay = -1f + yi * Increment;
                                for (int finger = 0; finger < FingerCount; finger++)
                                {
                                    int muscle = MuscleLeftThumb + finger * 4;
                                    muscles[muscle] = curl;
                                    muscles[muscle + 1] = splay;
                                    muscles[muscle + 2] = curl;
                                    muscles[muscle + 3] = curl;
                                }
                                poseHandler.SetHumanPose(ref tpose);
                                RecordPoseInto(joints, present, rotations);

                                int cellBase = (xi * GridHeight + yi) * JointsPerFinger;
                                for (int finger = 0; finger < FingerCount; finger++)
                                {
                                    int cell = finger * FingerStride + cellBase, joint = finger * JointsPerFinger;
                                    cells[cell] = rotations[joint];
                                    cells[cell + 1] = rotations[joint + 1];
                                    cells[cell + 2] = rotations[joint + 2];
                                }
                            }
                        }
                    }
                }
                finally
                {
                    poseHandler.Dispose();
                }
            }
            finally
            {
                DestroyCopy(copy);
            }

            return true;
        }

        /// <summary>
        /// Object.Destroy is deferred and logs an error outside play mode, which would leave the
        /// duplicate alive for the rest of the frame and fail any edit-mode caller. Editor tooling
        /// and the rig tests both bake outside play mode.
        /// </summary>
        static void DestroyCopy(GameObject copy)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(copy);
            else UnityEngine.Object.DestroyImmediate(copy);
        }

        static float[] CopyMuscles(HumanPose tpose, int fingerOffset)
        {
            float[] muscles = new float[4];
            Array.Copy(tpose.muscles, MuscleLeftThumb + fingerOffset, muscles, 0, 4);
            return muscles;
        }

        /// <summary>Reads the thirty finger local rotations into a flat finger*3 + joint buffer.</summary>
        static void RecordPoseInto(Transform[] joints, bool[] present, Quaternion[] destination)
        {
            for (int index = 0; index < JointCount; index++)
            {
                destination[index] = present[index] ? joints[index].localRotation : Quaternion.identity;
            }
        }

        static BasisPoseData RecordPose(Transform[] joints, bool[] present)
        {
            BasisPoseData pose = new BasisPoseData();
            int index = 0;

            void Assign(ref Quaternion[] finger)
            {
                finger[0] = present[index] ? joints[index].localRotation : Quaternion.identity; index++;
                finger[1] = present[index] ? joints[index].localRotation : Quaternion.identity; index++;
                finger[2] = present[index] ? joints[index].localRotation : Quaternion.identity; index++;
            }

            Assign(ref pose.LeftThumb);
            Assign(ref pose.LeftIndex);
            Assign(ref pose.LeftMiddle);
            Assign(ref pose.LeftRing);
            Assign(ref pose.LeftLittle);
            Assign(ref pose.RightThumb);
            Assign(ref pose.RightIndex);
            Assign(ref pose.RightMiddle);
            Assign(ref pose.RightRing);
            Assign(ref pose.RightLittle);

            return pose;
        }

        /// <summary>
        /// Poses the throwaway duplicate only. Deliberately does NOT stash the controller in
        /// BasisLocalAvatarDriver.SavedruntimeAnimatorController the way the local-only bake did:
        /// that static doubles as the local player's "currently T-posing" flag
        /// (BasisLocomotionPoseSystem), and a remote's bake setting it would freeze the LOCAL
        /// player's locomotion pose. The copy is destroyed either way, so nothing needs restoring.
        /// </summary>
        static void PutIntoTPose(Animator animator)
        {
            animator.logWarnings = false;
            animator.runtimeAnimatorController = BasisPlayerFactory.TposeController;
            animator.Update(Time.deltaTime);
        }
    }
}
