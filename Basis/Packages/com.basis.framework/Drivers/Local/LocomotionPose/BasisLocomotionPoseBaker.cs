using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
namespace Basis.Scripts.Drivers
{
    public sealed class BasisLocomotionPoseBake : IDisposable
    {
        public bool Ready;
        public int NodeCount;
        public int HipsNode = -1;
        public NativeArray<quaternion> Rotations;
        public NativeArray<float3> HipsPositions, SnapshotScales;
        public NativeArray<int> ClipRotationOffset, ClipHipsOffset, ClipSampleCount;
        public NativeArray<float> ClipLength;
        public NativeArray<byte> ClipLooping;
        public void Dispose()
        {
            Ready = false;
            if (Rotations.IsCreated) Rotations.Dispose();
            if (HipsPositions.IsCreated) HipsPositions.Dispose();
            if (SnapshotScales.IsCreated) SnapshotScales.Dispose();
            if (ClipRotationOffset.IsCreated) ClipRotationOffset.Dispose();
            if (ClipHipsOffset.IsCreated) ClipHipsOffset.Dispose();
            if (ClipSampleCount.IsCreated) ClipSampleCount.Dispose();
            if (ClipLength.IsCreated) ClipLength.Dispose();
            if (ClipLooping.IsCreated) ClipLooping.Dispose();
        }
    }
    public sealed class BasisLocomotionPoseBaker : IDisposable
    {
        public const float SampleRate = 30f;
        public const int EvaluatesPerTick = 8;
        public bool Failed { get; private set; }
        public bool Done => poseBake != null && poseBake.Ready;
        BasisLocomotionPoseBake poseBake;
        Transform[] sourceNodes;
        Transform[] cloneNodes;
        GameObject cloneRoot;
        Animator cloneAnimator;
        PlayableGraph graph;
        AnimationClipPlayable clipPlayable;
        AnimationPlayableOutput csvOutput;
        AnimationClip[] clips;
        int bakeClip;
        int bakeSample;
        bool playableLive;
        public BasisLocomotionPoseBake TakeBake()
        {
            BasisLocomotionPoseBake bake = poseBake;
            poseBake = null;
            return bake;
        }
        public bool Start(Animator sourceAnimator, RuntimeAnimatorController stockController, Transform[] streamNodes, Transform hips)
        {
            Abort();
            Failed = false;

            if (sourceAnimator == null || stockController == null || streamNodes == null || streamNodes.Length == 0)
            {
                Failed = true;
                return false;
            }

            clips = new AnimationClip[BasisLocomotionGraph.ClipCount];
            AnimationClip[] available = stockController.animationClips;
            for (int i = 0; i < BasisLocomotionGraph.ClipCount; i++)
            {
                string wanted = BasisLocomotionGraph.ClipNames[i];
                for (int j = 0; j < available.Length; j++)
                {
                    if (available[j] != null && available[j].name == wanted)
                    {
                        clips[i] = available[j];
                        break;
                    }
                }
                if (clips[i] == null)
                {
                    BasisDebug.LogError($"Locomotion pose bake: clip '{wanted}' not found on the stock controller.", BasisDebug.LogTag.Avatar);
                    Failed = true;
                    return false;
                }
            }

            Transform sourceRoot = sourceAnimator.transform;
            var map = new Dictionary<Transform, Transform>(256);
            cloneRoot = new GameObject("BasisLocomotionBakeRig");
            cloneRoot.hideFlags = HideFlags.HideAndDontSave;
            cloneRoot.transform.position = new Vector3(0f, -4096f, 0f);
            Transform cloneAvatarRoot = CloneSubtree(sourceRoot, cloneRoot.transform, map);

            sourceNodes = streamNodes;
            cloneNodes = new Transform[streamNodes.Length];
            for (int i = 0; i < streamNodes.Length; i++)
            {
                if (streamNodes[i] == null || !map.TryGetValue(streamNodes[i], out cloneNodes[i]))
                {
                    BasisDebug.LogError("Locomotion pose bake: pose skeleton node missing from the cloned rig.", BasisDebug.LogTag.Avatar);
                    Failed = true;
                    Abort();
                    return false;
                }
            }

            poseBake = new BasisLocomotionPoseBake
            {
                NodeCount = streamNodes.Length,
            };
            for (int i = 0; i < streamNodes.Length; i++)
            {
                if (streamNodes[i] == hips)
                {
                    poseBake.HipsNode = i;
                    break;
                }
            }
            if (poseBake.HipsNode < 0)
            {
                Failed = true;
                Abort();
                return false;
            }

            cloneAnimator = cloneAvatarRoot.gameObject.AddComponent<Animator>();
            cloneAnimator.avatar = sourceAnimator.avatar;
            cloneAnimator.applyRootMotion = false;
            cloneAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            int clipCount = BasisLocomotionGraph.ClipCount;
            poseBake.ClipRotationOffset = new NativeArray<int>(clipCount, Allocator.Persistent);
            poseBake.ClipHipsOffset = new NativeArray<int>(clipCount, Allocator.Persistent);
            poseBake.ClipSampleCount = new NativeArray<int>(clipCount, Allocator.Persistent);
            poseBake.ClipLength = new NativeArray<float>(clipCount, Allocator.Persistent);
            poseBake.ClipLooping = new NativeArray<byte>(clipCount, Allocator.Persistent);
            int rotationTotal = 0;
            int sampleTotal = 0;
            for (int i = 0; i < clipCount; i++)
            {
                float length = Mathf.Max(clips[i].length, 1f / SampleRate);
                int samples = Mathf.Max(2, Mathf.CeilToInt(length * SampleRate) + 1);
                poseBake.ClipRotationOffset[i] = rotationTotal;
                poseBake.ClipHipsOffset[i] = sampleTotal;
                poseBake.ClipSampleCount[i] = samples;
                poseBake.ClipLength[i] = length;
                poseBake.ClipLooping[i] = (byte)(clips[i].isLooping ? 1 : 0);
                rotationTotal += samples * streamNodes.Length;
                sampleTotal += samples;
            }
            poseBake.Rotations = new NativeArray<quaternion>(rotationTotal, Allocator.Persistent);
            poseBake.HipsPositions = new NativeArray<float3>(sampleTotal, Allocator.Persistent);
            poseBake.SnapshotScales = new NativeArray<float3>(streamNodes.Length, Allocator.Persistent);

            graph = PlayableGraph.Create("BasisLocomotionBake");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            csvOutput = AnimationPlayableOutput.Create(graph, "bake", cloneAnimator);
            bakeClip = 0;
            bakeSample = 0;
            playableLive = false;
            return true;
        }
        static Transform CloneSubtree(Transform source, Transform parent, Dictionary<Transform, Transform> map)
        {
            var clone = new GameObject(source.name).transform;
            clone.SetParent(parent, false);
            source.GetLocalPositionAndRotation(out Vector3 position, out Quaternion rotation);
            clone.SetLocalPositionAndRotation(position, rotation);
            clone.localScale = source.localScale;
            map[source] = clone;
            int children = source.childCount;
            for (int i = 0; i < children; i++)
            {
                CloneSubtree(source.GetChild(i), clone, map);
            }
            return clone;
        }
        public bool Tick()
        {
            if (Failed || poseBake == null || poseBake.Ready)
            {
                return false;
            }

            int budget = EvaluatesPerTick;
            while (budget-- > 0)
            {
                if (bakeClip >= BasisLocomotionGraph.ClipCount)
                {
                    FinishBake();
                    return false;
                }

                if (!playableLive)
                {
                    clipPlayable = AnimationClipPlayable.Create(graph, clips[bakeClip]);
                    clipPlayable.SetApplyFootIK(false);
                    clipPlayable.SetApplyPlayableIK(false);
                    csvOutput.SetSourcePlayable(clipPlayable);
                    playableLive = true;
                }

                int samples = poseBake.ClipSampleCount[bakeClip];
                float length = poseBake.ClipLength[bakeClip];
                float time = samples > 1 ? length * bakeSample / (samples - 1) : 0f;
                clipPlayable.SetTime(time);
                graph.Evaluate(0f);

                int nodeCount = poseBake.NodeCount;
                int rotationBase = poseBake.ClipRotationOffset[bakeClip] + bakeSample * nodeCount;
                for (int i = 0; i < nodeCount; i++)
                {
                    poseBake.Rotations[rotationBase + i] = cloneNodes[i].localRotation;
                }
                poseBake.HipsPositions[poseBake.ClipHipsOffset[bakeClip] + bakeSample] = cloneNodes[poseBake.HipsNode].localPosition;

                bakeSample++;
                if (bakeSample >= samples)
                {
                    bakeSample = 0;
                    bakeClip++;
                    clipPlayable.Destroy();
                    playableLive = false;
                }
            }
            return true;
        }
        void FinishBake()
        {
            for (int i = 0; i < poseBake.NodeCount; i++)
            {
                Transform node = sourceNodes[i];
                poseBake.SnapshotScales[i] = node != null ? (float3)node.localScale : new float3(1f, 1f, 1f);
            }
            poseBake.Ready = true;
            ReleaseRig();
        }
        void ReleaseRig()
        {
            if (playableLive)
            {
                clipPlayable.Destroy();
                playableLive = false;
            }
            if (graph.IsValid())
            {
                graph.Destroy();
            }
            if (cloneRoot != null)
            {
                UnityEngine.Object.Destroy(cloneRoot);
                cloneRoot = null;
            }
            cloneAnimator = null;
            cloneNodes = null;
            clips = null;
        }
        public void Abort()
        {
            ReleaseRig();
            if (poseBake != null && !poseBake.Ready)
            {
                poseBake.Dispose();
                poseBake = null;
            }
            sourceNodes = null;
        }
        public void Dispose()
        {
            Abort();
            if (poseBake != null)
            {
                poseBake.Dispose();
                poseBake = null;
            }
        }
    }
}
