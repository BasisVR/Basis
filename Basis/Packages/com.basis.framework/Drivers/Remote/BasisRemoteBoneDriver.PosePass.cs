using System;
using System.Collections.Generic;
using Basis.Network.Core.Compression;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
public static partial class RemoteBoneJobSystem
{
    public const int ScriptHipsBit = 63;
    public static event Action PosePass;
    [NoAutoStaticsCleanup] static NativeArray<ulong> sScriptMask, sNoScriptMask;
    [NoAutoStaticsCleanup] static NativeArray<int> sScriptBlock, sNoScriptBlock;
    [NoAutoStaticsCleanup] static NativeArray<quaternion> sScriptRot, sScriptNetRot, sNoScriptRot;
    [NoAutoStaticsCleanup] static NativeArray<float3> sScriptHipsPos, sNoScriptHipsPos;
    static readonly Stack<int> sScriptFree = new Stack<int>();
    static int sScriptBlocks, sScriptActive;
    public static bool HasPosePass => PosePass != null;
    public static bool ScriptPoseActive => sScriptActive > 0;
    public static int ScriptPosedCount => sScriptActive;
    static int ScriptStride => BasisBoneRotationCompression.SyncBoneCount + 1;
    public static void RunPosePass()
    {
        Action pass = PosePass;
        if (pass == null || !sInitialized) return;
        pass();
    }
    public static bool IsPosed(int key) => TryGetSOutIndex(key, out _);
    public static bool IsSyncedBone(HumanBodyBones bone) => ScriptBit(bone) >= 0 && bone != HumanBodyBones.Hips;
    public static int ScriptBit(HumanBodyBones bone)
    {
        if (bone == HumanBodyBones.Hips) return ScriptHipsBit;
        int[] slots = BasisBoneRotationCompression.BONE_TO_SLOT;
        return (uint)bone < (uint)slots.Length ? slots[(int)bone] : -1;
    }
    public static bool TryGetHipsPosition(int key, out Vector3 position)
    {
        position = default;
        if (!TryGetSOutIndex(key, out int idx) || !sTmpHipsWorldPos.IsCreated || idx >= sTmpHipsWorldPos.Length) return false;
        position = sTmpHipsWorldPos[idx];
        return true;
    }
    public static bool IsFaceVisible(int key) => sFaceVisible.IsCreated && (uint)key < (uint)sFaceVisible.Length && sFaceVisible[key] != 0;
    public static ulong GetScriptMask(int key) => sScriptMask.IsCreated && (uint)key < (uint)sScriptMask.Length ? sScriptMask[key] : 0ul;
    public static void RestoreNetworkPose(int key)
    {
        ulong mask = GetScriptMask(key);
        if (mask == 0ul || !TryGetSOutIndex(key, out int idx)) return;
        if ((mask & (1ul << ScriptHipsBit)) != 0 && idx < sHips.length && idx < sTmpHipsWorldPos.Length)
        {
            Transform hips = sHips[idx];
            if (hips != null) hips.SetPositionAndRotation(sTmpHipsWorldPos[idx], sTmpHipsWorldRot[idx]);
        }
        int boneCount = BasisBoneRotationCompression.SyncBoneCount, start = idx * boneCount;
        if (!sScriptNetRot.IsCreated || start + boneCount > sScriptNetRot.Length || start + boneCount > sSkeletonBones.length) return;
        for (ulong bones = mask & ~(1ul << ScriptHipsBit); bones != 0ul; bones &= bones - 1ul)
        {
            int slot = math.tzcnt(bones);
            if (slot >= boneCount || sSkeletonValid[start + slot] == 0) continue;
            Transform bone = sSkeletonBones[start + slot];
            if (bone != null) bone.localRotation = sScriptNetRot[start + slot];
        }
    }
    public static void CommitScriptPose(int key, ulong mask)
    {
        if (!sInitialized || (uint)key >= KeySpace) return;
        if (mask == 0ul || !TryGetSOutIndex(key, out int idx))
        {
            ClearScriptPose(key);
            return;
        }
        EnsureScriptPose();
        int block = sScriptBlock[key];
        if (block < 0)
        {
            block = sScriptFree.Count > 0 ? sScriptFree.Pop() : sScriptBlocks++;
            EnsureScriptBlocks(block + 1);
            sScriptBlock[key] = block;
        }
        int boneCount = BasisBoneRotationCompression.SyncBoneCount, start = idx * boneCount, stride = ScriptStride, at = block * stride;
        ulong kept = 0ul;
        if ((mask & (1ul << ScriptHipsBit)) != 0 && idx < sHips.length && idx < sTmpHipsWorldPos.Length)
        {
            Transform hips = sHips[idx];
            if (hips != null)
            {
                hips.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                quaternion inverse = math.inverse(sTmpHipsWorldRot[idx]);
                sScriptHipsPos[block] = math.mul(inverse, (float3)position - sTmpHipsWorldPos[idx]);
                sScriptRot[at + boneCount] = math.normalize(math.mul(inverse, rotation));
                kept |= 1ul << ScriptHipsBit;
            }
        }
        if (start + boneCount <= sSkeletonBones.length)
        {
            for (ulong bones = mask & ~(1ul << ScriptHipsBit); bones != 0ul; bones &= bones - 1ul)
            {
                int slot = math.tzcnt(bones);
                if (slot >= boneCount || sSkeletonValid[start + slot] == 0) continue;
                Transform bone = sSkeletonBones[start + slot];
                if (bone == null) continue;
                sScriptRot[at + slot] = bone.localRotation;
                kept |= 1ul << slot;
            }
        }
        SetScriptMask(key, kept);
    }
    public static void NarrowScriptPose(int key, ulong keep)
    {
        ulong mask = GetScriptMask(key);
        if (mask != 0ul && (mask & keep) != mask) SetScriptMask(key, mask & keep);
    }
    public static void ClearScriptPose(int key)
    {
        if (!sScriptMask.IsCreated || (uint)key >= (uint)sScriptMask.Length) return;
        SetScriptMask(key, 0ul);
    }
    static void SetScriptMask(int key, ulong mask)
    {
        ulong before = sScriptMask[key];
        if (before == 0ul && mask != 0ul) sScriptActive++;
        else if (before != 0ul && mask == 0ul) sScriptActive--;
        sScriptMask[key] = mask;
        if (mask != 0ul || sScriptBlock[key] < 0) return;
        sScriptFree.Push(sScriptBlock[key]);
        sScriptBlock[key] = -1;
    }
    static void EnsureScriptPose()
    {
        if (sScriptMask.IsCreated) return;
        sScriptMask = new NativeArray<ulong>(KeySpace, Allocator.Persistent);
        sScriptBlock = new NativeArray<int>(KeySpace, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        for (int i = 0; i < KeySpace; i++) sScriptBlock[i] = -1;
        sScriptFree.Clear();
        sScriptBlocks = 0;
        sScriptActive = 0;
    }
    static void EnsureScriptBlocks(int blocks)
    {
        int stride = ScriptStride;
        if (sScriptHipsPos.IsCreated && sScriptHipsPos.Length >= blocks) return;
        int capacity = GrowCapacity(math.max(blocks, 16));
        NativeArray<quaternion> rot = new NativeArray<quaternion>(capacity * stride, Allocator.Persistent);
        NativeArray<float3> hips = new NativeArray<float3>(capacity, Allocator.Persistent);
        if (sScriptRot.IsCreated)
        {
            NativeArray<quaternion>.Copy(sScriptRot, rot, sScriptRot.Length);
            sScriptRot.Dispose();
        }
        if (sScriptHipsPos.IsCreated)
        {
            NativeArray<float3>.Copy(sScriptHipsPos, hips, sScriptHipsPos.Length);
            sScriptHipsPos.Dispose();
        }
        sScriptRot = rot;
        sScriptHipsPos = hips;
    }
    static void EnsureScriptNetRot(int totalBones)
    {
        if (!ScriptPoseActive || (sScriptNetRot.IsCreated && sScriptNetRot.Length >= totalBones)) return;
        if (sScriptNetRot.IsCreated) sScriptNetRot.Dispose();
        sScriptNetRot = new NativeArray<quaternion>(GrowCapacity(totalBones), Allocator.Persistent);
    }
    static void EnsureNoScript()
    {
        if (sNoScriptMask.IsCreated) return;
        sNoScriptMask = new NativeArray<ulong>(1, Allocator.Persistent);
        sNoScriptBlock = new NativeArray<int>(1, Allocator.Persistent);
        sNoScriptRot = new NativeArray<quaternion>(1, Allocator.Persistent);
        sNoScriptHipsPos = new NativeArray<float3>(1, Allocator.Persistent);
    }
    static BasisRemoteNetworkDriver.ScriptPoseInputs ScriptInputs(int totalBones)
    {
        EnsureNoScript();
        bool active = ScriptPoseActive && sScriptRot.IsCreated;
        if (active) EnsureScriptNetRot(totalBones);
        return new BasisRemoteNetworkDriver.ScriptPoseInputs
        {
            Active = active,
            Mask = active ? sScriptMask : sNoScriptMask,
            Block = active ? sScriptBlock : sNoScriptBlock,
            Rotations = active ? sScriptRot : sNoScriptRot,
            HipsPositions = active ? sScriptHipsPos : sNoScriptHipsPos,
            NetworkRotations = active ? sScriptNetRot : sNoScriptRot,
            Stride = ScriptStride,
        };
    }
    static void DisposeScriptPose()
    {
        if (sScriptMask.IsCreated) sScriptMask.Dispose();
        if (sScriptBlock.IsCreated) sScriptBlock.Dispose();
        if (sScriptRot.IsCreated) sScriptRot.Dispose();
        if (sScriptNetRot.IsCreated) sScriptNetRot.Dispose();
        if (sScriptHipsPos.IsCreated) sScriptHipsPos.Dispose();
        if (sNoScriptMask.IsCreated) sNoScriptMask.Dispose();
        if (sNoScriptBlock.IsCreated) sNoScriptBlock.Dispose();
        if (sNoScriptRot.IsCreated) sNoScriptRot.Dispose();
        if (sNoScriptHipsPos.IsCreated) sNoScriptHipsPos.Dispose();
        sScriptFree.Clear();
        sScriptBlocks = 0;
        sScriptActive = 0;
    }
}
