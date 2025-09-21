using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs; 
public static class BoneIdx
{
    public const int Head = 0;
    public const int Neck = 1;
    public const int Chest = 2;
    public const int Spine = 3;
    public const int Hips = 4;
    public const int CenterEye = 5;
    public const int Mouth = 6;
    public const int BoneCount = 7;
}
public struct TposeAndOffsetDataJob
{
    public float3 tposeLocal_unscaled_Head;
    public float3 tposeLocal_unscaled_Neck;
    public float3 tposeLocal_unscaled_Chest;
    public float3 tposeLocal_unscaled_Spine;
    public float3 tposeLocal_unscaled_Hips;
    public float3 tposeLocal_unscaled_CenterEye;
    public float3 tposeLocal_unscaled_Mouth;

    public float3 offsets_unscaled_Neck;
    public float3 offsets_unscaled_Chest;
    public float3 offsets_unscaled_Spine;      // Chest→Spine in this chain
    public float3 offsets_unscaled_CenterEye;
    public float3 offsets_unscaled_Mouth;
}
public struct GeneratedTranslationalData
{
    public float3 rootWorld;
    public float3 headWPos;
    public float3 hipsWPos;
    public quaternion headWRot;
    public quaternion hipsWRot;
    public quaternion tposeHeadRot;
    public quaternion tposeHipsRot;
    public float3 nowScale;
}
public struct RemoteScaleCache
{
    public float3 tposeLocal_scaled_Hips;
    public float3 tposeLocal_scaled_Mouth;
    public float3 offsets_scaled_Neck;
    public float3 offsets_scaled_Chest;
    public float3 offsets_scaled_Spine;
    public float3 offsets_scaled_CenterEye;
    public float3 offsets_scaled_Mouth;
}
public struct RemoteFrameOutput
{
    public float3 pos_Head, pos_Neck, pos_Chest, pos_Spine, pos_Hips, pos_CenterEye, pos_Mouth;
    public quaternion rot_Head, rot_Neck, rot_Chest, rot_Spine, rot_Hips, rot_CenterEye, rot_Mouth;
    public float diffHipToHeadMouthY;
}
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
public struct BasisRemoteBoneJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<TposeAndOffsetDataJob> Authoring;
    [ReadOnly] public NativeArray<GeneratedTranslationalData> In;
    [WriteOnly]
    public NativeArray<RemoteFrameOutput> Out;
    public NativeArray<RemoteScaleCache> GeneratedScales;
    public void Execute(int i)
    {
        var a = Authoring[i];
        var f = In[i];
        var sc = GeneratedScales[i];

        sc.tposeLocal_scaled_Hips = a.tposeLocal_unscaled_Hips * f.nowScale;
        sc.tposeLocal_scaled_Mouth = a.tposeLocal_unscaled_Mouth * f.nowScale;
        sc.offsets_scaled_Neck = a.offsets_unscaled_Neck * f.nowScale;
        sc.offsets_scaled_Chest = a.offsets_unscaled_Chest * f.nowScale;
        sc.offsets_scaled_Spine = a.offsets_unscaled_Spine * f.nowScale;
        sc.offsets_scaled_CenterEye = a.offsets_unscaled_CenterEye * f.nowScale;
        sc.offsets_scaled_Mouth = a.offsets_unscaled_Mouth * f.nowScale;
        GeneratedScales[i] = sc;
        quaternion headR = math.mul(f.tposeHeadRot, f.headWRot);
        quaternion hipsR = math.mul(f.tposeHipsRot, f.hipsWRot);
        float3 headP = f.headWPos - f.rootWorld;
        float3 hipsP = f.hipsWPos - f.rootWorld;
        float3 neckP = headP + math.mul(headR, sc.offsets_scaled_Neck);
        float3 chestP = neckP + math.mul(headR, sc.offsets_scaled_Chest);
        float3 spineP = chestP + math.mul(headR, sc.offsets_scaled_Spine);
        float3 eyeP = headP + math.mul(headR, sc.offsets_scaled_CenterEye);
        float3 mouthP = headP + math.mul(headR, sc.offsets_scaled_Mouth);
        Out[i] = new RemoteFrameOutput
        {
            pos_Head = headP,
            pos_Neck = neckP,
            pos_Chest = chestP,
            pos_Spine = spineP,
            pos_Hips = hipsP,
            pos_CenterEye = eyeP,
            pos_Mouth = mouthP,
            rot_Head = headR,
            rot_Neck = headR,
            rot_Chest = headR,
            rot_Spine = headR,
            rot_Hips = hipsR,
            rot_CenterEye = headR,
            rot_Mouth = headR,
            diffHipToHeadMouthY = sc.tposeLocal_scaled_Mouth.y - sc.tposeLocal_scaled_Hips.y
        };
    }
}
[BurstCompile]
struct GatherRootJob : IJobParallelForTransform
{
    [WriteOnly] public NativeArray<float3> rootPos;
    [WriteOnly] public NativeArray<float3> rootScale;
    public void Execute(int index, TransformAccess tx)
    {
        rootPos[index] = tx.position;

        // derive world scale from matrix (no API call to lossyScale in jobs)
        var m = tx.localToWorldMatrix;
        float3 sx = new float3(m.m00, m.m10, m.m20);
        float3 sy = new float3(m.m01, m.m11, m.m21);
        float3 sz = new float3(m.m02, m.m12, m.m22);
        rootScale[index] = new float3(math.length(sx), math.length(sy), math.length(sz));
    }
}
[BurstCompile]
struct GatherHeadJob : IJobParallelForTransform
{
    [WriteOnly] public NativeArray<float3> headPos;
    [WriteOnly] public NativeArray<quaternion> headRot;
    public void Execute(int index, TransformAccess tx)
    {
        headPos[index] = tx.position;
        headRot[index] = tx.rotation;
    }
}
[BurstCompile]
struct GatherHipsJob : IJobParallelForTransform
{
    [WriteOnly] public NativeArray<float3> hipsPos;
    [WriteOnly] public NativeArray<quaternion> hipsRot;
    public void Execute(int index, TransformAccess tx)
    {
        hipsPos[index] = tx.position;
        hipsRot[index] = tx.rotation;
    }
}
[BurstCompile]
struct ApplyMouthJob : IJobParallelForTransform
{
    [ReadOnly]
    public NativeArray<RemoteFrameOutput> MouthRotation;
    public void Execute(int index, TransformAccess tx)
    {
        tx.SetPositionAndRotation(MouthRotation[index].pos_Mouth, MouthRotation[index].rot_Mouth);
    }
}
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
public struct MappedNameplateApplyJob : IJobParallelForTransform
{
    // Precompute 1 + 1/1.25 = 1.8f once; Burst will constant-fold this.
    private const float kY = 1.8f;
    public float3 CameraPosition;
    [ReadOnly] public NativeArray<RemoteFrameOutput> NamePlateIn;
    public void Execute(int jobIndex, TransformAccess tx)
    {
        var data = NamePlateIn[jobIndex];
        float3 hips = data.pos_Hips;
        // y = hips.y + diff * 1.8
        float3 nameplatePos = new float3(hips.x, hips.y + data.diffHipToHeadMouthY * kY, hips.z);

        // Face the camera (yaw only) with zero-distance guard.
        float3 toCam = CameraPosition - nameplatePos;
        float2 xz = new float2(toCam.x, toCam.z);
        float yaw = math.lengthsq(xz) > 1e-12f ? math.atan2(xz.x, xz.y) : 0f;
        quaternion rot = quaternion.RotateY(yaw);

        tx.SetPositionAndRotation(nameplatePos, rot);
    }
}
[BurstCompile]
struct AgrigateTranslationalData : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> rootPos;
    [ReadOnly] public NativeArray<float3> rootScale;
    [ReadOnly] public NativeArray<float3> headPos;
    [ReadOnly] public NativeArray<quaternion> headRot;
    [ReadOnly] public NativeArray<float3> hipsPos;
    [ReadOnly] public NativeArray<quaternion> hipsRot;
    [ReadOnly] public NativeArray<quaternion> tposeHeadRot;
    [ReadOnly] public NativeArray<quaternion> tposeHipsRot;
    [WriteOnly]
    public NativeArray<GeneratedTranslationalData> InOut;
    public void Execute(int i)
    {
        InOut[i] = new GeneratedTranslationalData
        {
            rootWorld = rootPos[i],
            headWPos = headPos[i],
            hipsWPos = hipsPos[i],
            headWRot = headRot[i],
            hipsWRot = hipsRot[i],
            tposeHeadRot = tposeHeadRot[i],
            tposeHipsRot = tposeHipsRot[i],
            nowScale = rootScale[i]
        };
    }
}
public static class RemoteBoneJobSystem
{
    // Persistent SoA
    static NativeList<TposeAndOffsetDataJob> sAuthoring;
    static NativeList<GeneratedTranslationalData> sIn;
    static NativeList<RemoteScaleCache> sScale;
    static NativeList<RemoteFrameOutput> sOut;
    // Cached TPose quats (job friendly)
    static NativeList<quaternion> sTPoseHeadRot;
    static NativeList<quaternion> sTPoseHipsRot;
    // Transform access arrays (roots / heads / hips)
    static TransformAccessArray sRoots;
    static TransformAccessArray sHeads;
    static TransformAccessArray sHips;

    static TransformAccessArray sNamePlate;
    static TransformAccessArray sAvatarScale;
    static TransformAccessArray sMouth;
    // Temp per-frame buffers (reused)
    static NativeArray<float3> sTmpRootPos, sTmpHeadPos, sTmpHipsPos;
    static NativeArray<float3> sTmpRootScale;
    static NativeArray<quaternion> sTmpHeadRot, sTmpHipsRot;
    // Bookkeeping
    static readonly Dictionary<int, int> sKeyToIndex = new Dictionary<int, int>();
    static JobHandle sPending;
    static bool sInitialized;
    public static void Initialize(int initialCapacity = 0)
    {
        if (sInitialized) return;

        sAuthoring = new NativeList<TposeAndOffsetDataJob>(initialCapacity, Allocator.Persistent);
        sIn = new NativeList<GeneratedTranslationalData>(initialCapacity, Allocator.Persistent);
        sScale = new NativeList<RemoteScaleCache>(initialCapacity, Allocator.Persistent);
        sOut = new NativeList<RemoteFrameOutput>(initialCapacity, Allocator.Persistent);

        sTPoseHeadRot = new NativeList<quaternion>(initialCapacity, Allocator.Persistent);
        sTPoseHipsRot = new NativeList<quaternion>(initialCapacity, Allocator.Persistent);

        sRoots = new TransformAccessArray(initialCapacity);
        sHeads = new TransformAccessArray(initialCapacity);
        sHips = new TransformAccessArray(initialCapacity);

        sNamePlate = new TransformAccessArray(initialCapacity);
        sAvatarScale = new TransformAccessArray(initialCapacity);
        sMouth = new TransformAccessArray(initialCapacity);

        sInitialized = true;
    }

    public static void Dispose()
    {
        CompletePending();

        if (sAuthoring.IsCreated) sAuthoring.Dispose();
        if (sIn.IsCreated) sIn.Dispose();
        if (sScale.IsCreated) sScale.Dispose();
        if (sOut.IsCreated) sOut.Dispose();

        if (sTPoseHeadRot.IsCreated) sTPoseHeadRot.Dispose();
        if (sTPoseHipsRot.IsCreated) sTPoseHipsRot.Dispose();

        if (sRoots.isCreated) sRoots.Dispose();
        if (sHeads.isCreated) sHeads.Dispose();
        if (sHips.isCreated) sHips.Dispose();

        if (sNamePlate.isCreated) sNamePlate.Dispose();
        if (sAvatarScale.isCreated) sAvatarScale.Dispose();
        if (sMouth.isCreated) sMouth.Dispose();

        DisposeTempBuffers();

        sKeyToIndex.Clear();
        sInitialized = false;
    }

    static void CompletePending()
    {
        sPending.Complete();
        sPending = default;
    }
    public static int AddRemotePlayer(int key, Transform remotePlayerRoot, Transform head, Transform hips,
        BasisCalibratedCoords tposeHead, BasisCalibratedCoords tposeHips, float3 authoredCenterEyeWorld,
        float3 authoredMouthWorld, Transform NamePlate, Transform AvatarScale, Transform MouthTransform)
    {
        if (!sInitialized) Initialize();
        CompletePending();

        float3 rootWorld = remotePlayerRoot.position;
        float3 ToAvatarLocal(float3 world) => world - rootWorld;

        float3 tHead = ToAvatarLocal(head.position);
        float3 tNeck = float3.zero;
        float3 tChest = float3.zero;
        float3 tSpine = float3.zero;
        float3 tHips = ToAvatarLocal(hips.position);
        float3 tEye = ToAvatarLocal(authoredCenterEyeWorld);
        float3 tMouth = ToAvatarLocal(authoredMouthWorld);

        float3 offNeck = tNeck - tHead;
        float3 offChest = tChest - tNeck;
        float3 offSpine = tSpine - tChest;
        float3 offEye = tEye - tHead;
        float3 offMouth = tMouth - tHead;

        var a = new TposeAndOffsetDataJob
        {
            tposeLocal_unscaled_Head = tHead,
            tposeLocal_unscaled_Neck = tNeck,
            tposeLocal_unscaled_Chest = tChest,
            tposeLocal_unscaled_Spine = tSpine,
            tposeLocal_unscaled_Hips = tHips,
            tposeLocal_unscaled_CenterEye = tEye,
            tposeLocal_unscaled_Mouth = tMouth,

            offsets_unscaled_Neck = offNeck,
            offsets_unscaled_Chest = offChest,
            offsets_unscaled_Spine = offSpine,
            offsets_unscaled_CenterEye = offEye,
            offsets_unscaled_Mouth = offMouth
        };

        int idx = sAuthoring.Length;
        EnsureTaaCapacity(idx + 1);
        sAuthoring.Add(a);
        sIn.Add(default);
        sScale.Add(new RemoteScaleCache());
        sOut.Add(default);
        sTPoseHeadRot.Add((quaternion)tposeHead.rotation);
        sTPoseHipsRot.Add((quaternion)tposeHips.rotation);
        sRoots.Add(remotePlayerRoot);

        sNamePlate.Add(NamePlate);
        sAvatarScale.Add(AvatarScale);
        sMouth.Add(MouthTransform);

        sHeads.Add(head);
        sHips.Add(hips);
        sKeyToIndex[key] = idx;
        return key;
    }
    public static bool RemoveRemotePlayer(int key)
    {
        if (!sInitialized) return false;
        CompletePending();

        if (!sKeyToIndex.TryGetValue(key, out int idx)) return false;

        int last = sAuthoring.Length - 1;
        if (idx != last)
        {
            // Swap-back SoA
            sAuthoring[idx] = sAuthoring[last];
            sIn[idx] = sIn[last];
            sScale[idx] = sScale[last];
            sOut[idx] = sOut[last];
            sTPoseHeadRot[idx] = sTPoseHeadRot[last];
            sTPoseHipsRot[idx] = sTPoseHipsRot[last];

            sNamePlate.RemoveAtSwapBack(idx);
            sAvatarScale.RemoveAtSwapBack(idx);
            sMouth.RemoveAtSwapBack(idx);

            sRoots.RemoveAtSwapBack(idx);
            sHeads.RemoveAtSwapBack(idx);
            sHips.RemoveAtSwapBack(idx);
            foreach (var kv in sKeyToIndex)
            {
                if (kv.Value == last) { sKeyToIndex[kv.Key] = idx; break; }
            }
        }
        else
        {
            sRoots.RemoveAtSwapBack(last);
            sHeads.RemoveAtSwapBack(last);
            sHips.RemoveAtSwapBack(last);

            sNamePlate.RemoveAtSwapBack(last);
            sAvatarScale.RemoveAtSwapBack(last);
            sMouth.RemoveAtSwapBack(last);
        }

        sAuthoring.RemoveAt(last);
        sIn.RemoveAt(last);
        sScale.RemoveAt(last);
        sOut.RemoveAt(last);
        sTPoseHeadRot.RemoveAt(last);
        sTPoseHipsRot.RemoveAt(last);
        sKeyToIndex.Remove(key);
        return true;
    }
    static void EnsureTempBuffers(int count)
    {
        if (count <= 0) return;

        void AllocOrResize<T>(ref NativeArray<T> arr, int len) where T : struct
        {
            if (arr.IsCreated)
            {
                if (arr.Length != len)
                {
                    arr.Dispose();
                    arr = new NativeArray<T>(len, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }
            }
            else
            {
                arr = new NativeArray<T>(len, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
        }
        AllocOrResize(ref sTmpRootPos, count);
        AllocOrResize(ref sTmpRootScale, count);
        AllocOrResize(ref sTmpHeadPos, count);
        AllocOrResize(ref sTmpHeadRot, count);
        AllocOrResize(ref sTmpHipsPos, count);
        AllocOrResize(ref sTmpHipsRot, count);
    }
    static void DisposeTempBuffers()
    {
        if (sTmpRootPos.IsCreated) sTmpRootPos.Dispose();
        if (sTmpRootScale.IsCreated) sTmpRootScale.Dispose();
        if (sTmpHeadPos.IsCreated) sTmpHeadPos.Dispose();
        if (sTmpHeadRot.IsCreated) sTmpHeadRot.Dispose();
        if (sTmpHipsPos.IsCreated) sTmpHipsPos.Dispose();
        if (sTmpHipsRot.IsCreated) sTmpHipsRot.Dispose();
    }
    static void EnsureTaaCapacity(int needed)
    {
        if (sRoots.capacity < needed)
        {
            int newCap = math.max(needed, math.max(4, sRoots.capacity * 2));
            sRoots.capacity = newCap;
            sHeads.capacity = newCap;
            sHips.capacity = newCap;


            sNamePlate.capacity = newCap;
            sAvatarScale.capacity = newCap;
            sMouth.capacity = newCap;
        }
    }
    public static JobHandle Schedule(int batchSize = 64)
    {
        if (!sInitialized || sAuthoring.Length == 0) return default;

        EnsureTempBuffers(sAuthoring.Length);

        var hRoot = new GatherRootJob
        {
            rootPos = sTmpRootPos,
            rootScale = sTmpRootScale
        }.Schedule(sRoots);

        var hHead = new GatherHeadJob
        {
            headPos = sTmpHeadPos,
            headRot = sTmpHeadRot
        }.Schedule(sHeads);

        var hHips = new GatherHipsJob
        {
            hipsPos = sTmpHipsPos,
            hipsRot = sTmpHipsRot
        }.Schedule(sHips);

        var deps = JobHandle.CombineDependencies(hRoot, hHead, hHips);

        var combine = new AgrigateTranslationalData
        {
            rootPos = sTmpRootPos,
            rootScale = sTmpRootScale,
            headPos = sTmpHeadPos,
            headRot = sTmpHeadRot,
            hipsPos = sTmpHipsPos,
            hipsRot = sTmpHipsRot,
            tposeHeadRot = sTPoseHeadRot.AsDeferredJobArray(),
            tposeHipsRot = sTPoseHipsRot.AsDeferredJobArray(),
            InOut = sIn.AsDeferredJobArray()
        }.Schedule(sAuthoring.Length, batchSize, deps);

        var BoneSimulation = new BasisRemoteBoneJob
        {
            Authoring = sAuthoring.AsDeferredJobArray(),
            In = sIn.AsDeferredJobArray(),
            GeneratedScales = sScale.AsDeferredJobArray(),
            Out = sOut.AsDeferredJobArray()
        }.Schedule(sAuthoring.Length, batchSize, combine);
        Vector3 CameraPosition = BasisLocalCameraDriver.Position;
        //ok all positions and scales are computed now lets start apply back to transforms
        var MappedNameplateApplyJob = new MappedNameplateApplyJob
        {
            CameraPosition = CameraPosition,
            NamePlateIn = sOut.AsDeferredJobArray(),

        }.Schedule(sNamePlate, BoneSimulation);
        var ApplyMouthJob = new ApplyMouthJob
        {
            MouthRotation = sOut.AsDeferredJobArray(),
        }.Schedule(sMouth, MappedNameplateApplyJob);


        sPending = ApplyMouthJob;
        return ApplyMouthJob;
    }
    public static void Complete(JobHandle handle)
    {
        handle.Complete();
        if (!sInitialized) return;

        CompletePending();
    }
    public static bool GetOutGoingMouth(int key, out float3 outgoing)
    {
        if (!sKeyToIndex.TryGetValue(key, out int idx))
        {
            outgoing = Vector3.zero;
            return false;
        }
        var o = sOut[idx];
        outgoing = o.pos_Mouth;
        return true;
    }
}
