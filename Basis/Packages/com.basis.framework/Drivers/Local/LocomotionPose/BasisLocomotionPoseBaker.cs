using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Per-avatar baked pose samples for the stock locomotion clips: local rotations for every pose
    /// skeleton node plus the hips local position, sampled at <see cref="BasisLocomotionPoseBaker.SampleRate"/>.
    /// </summary>
    public sealed class BasisLocomotionPoseBake : IDisposable
    {
        public bool Ready;
        public int NodeCount;
        public int HipsNode = -1;
        public NativeArray<quaternion> Rotations;
        public NativeArray<float3> HipsPositions;
        public NativeArray<float3> SnapshotScales;
        public NativeArray<int> ClipRotationOffset;
        public NativeArray<int> ClipHipsOffset;
        public NativeArray<int> ClipSampleCount;
        public NativeArray<float> ClipLength;
        public float[] ClipLengthsManaged;
        public bool[] ClipLoopingManaged;

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
        }
    }

    /// <summary>
    /// Incrementally samples the stock locomotion clips through a hidden, component-free copy of the
    /// avatar's skeleton (same Avatar asset, so the humanoid retarget is Unity's own — no muscle math
    /// re-derived here). Runs a fixed number of graph evaluates per tick so an avatar load never pays
    /// the whole bake in one frame; the engine Animator path keeps running until the bake is Ready.
    /// </summary>
    public sealed class BasisLocomotionPoseBaker : IDisposable
    {
        public const float SampleRate = 30f;
        // Kept low: each evaluate is a full humanoid retarget on the clone rig (~0.05-0.08 ms), and the
        // engine Animator path covers the avatar until the bake lands, so there is no rush.
        public const int EvaluatesPerTick = 8;

        public bool Failed { get; private set; }
        public bool Done => _bake != null && _bake.Ready;

        BasisLocomotionPoseBake _bake;
        Transform[] _sourceNodes;
        Transform[] _cloneNodes;
        GameObject _cloneRoot;
        Animator _cloneAnimator;
        PlayableGraph _graph;
        AnimationClipPlayable _clipPlayable;
        AnimationPlayableOutput _output;
        AnimationClip[] _clips;
        int _clip;
        int _sample;
        bool _playableLive;

        public BasisLocomotionPoseBake TakeBake()
        {
            BasisLocomotionPoseBake bake = _bake;
            _bake = null;
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

            _clips = new AnimationClip[BasisLocomotionGraph.ClipCount];
            AnimationClip[] available = stockController.animationClips;
            for (int i = 0; i < BasisLocomotionGraph.ClipCount; i++)
            {
                string wanted = BasisLocomotionGraph.ClipNames[i];
                for (int j = 0; j < available.Length; j++)
                {
                    if (available[j] != null && available[j].name == wanted)
                    {
                        _clips[i] = available[j];
                        break;
                    }
                }
                if (_clips[i] == null)
                {
                    BasisDebug.LogError($"Locomotion pose bake: clip '{wanted}' not found on the stock controller.", BasisDebug.LogTag.Avatar);
                    Failed = true;
                    return false;
                }
            }

            Transform sourceRoot = sourceAnimator.transform;
            var map = new Dictionary<Transform, Transform>(256);
            _cloneRoot = new GameObject("BasisLocomotionBakeRig");
            _cloneRoot.hideFlags = HideFlags.HideAndDontSave;
            _cloneRoot.transform.position = new Vector3(0f, -4096f, 0f);
            Transform cloneAvatarRoot = CloneSubtree(sourceRoot, _cloneRoot.transform, map);

            _sourceNodes = streamNodes;
            _cloneNodes = new Transform[streamNodes.Length];
            for (int i = 0; i < streamNodes.Length; i++)
            {
                if (streamNodes[i] == null || !map.TryGetValue(streamNodes[i], out _cloneNodes[i]))
                {
                    BasisDebug.LogError("Locomotion pose bake: pose skeleton node missing from the cloned rig.", BasisDebug.LogTag.Avatar);
                    Failed = true;
                    Abort();
                    return false;
                }
            }

            _bake = new BasisLocomotionPoseBake
            {
                NodeCount = streamNodes.Length,
            };
            for (int i = 0; i < streamNodes.Length; i++)
            {
                if (streamNodes[i] == hips)
                {
                    _bake.HipsNode = i;
                    break;
                }
            }
            if (_bake.HipsNode < 0)
            {
                Failed = true;
                Abort();
                return false;
            }

            _cloneAnimator = cloneAvatarRoot.gameObject.AddComponent<Animator>();
            _cloneAnimator.avatar = sourceAnimator.avatar;
            _cloneAnimator.applyRootMotion = false;
            _cloneAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            int clipCount = BasisLocomotionGraph.ClipCount;
            _bake.ClipRotationOffset = new NativeArray<int>(clipCount, Allocator.Persistent);
            _bake.ClipHipsOffset = new NativeArray<int>(clipCount, Allocator.Persistent);
            _bake.ClipSampleCount = new NativeArray<int>(clipCount, Allocator.Persistent);
            _bake.ClipLength = new NativeArray<float>(clipCount, Allocator.Persistent);
            _bake.ClipLengthsManaged = new float[clipCount];
            _bake.ClipLoopingManaged = new bool[clipCount];
            int rotationTotal = 0;
            int sampleTotal = 0;
            for (int i = 0; i < clipCount; i++)
            {
                float length = Mathf.Max(_clips[i].length, 1f / SampleRate);
                int samples = Mathf.Max(2, Mathf.CeilToInt(length * SampleRate) + 1);
                _bake.ClipRotationOffset[i] = rotationTotal;
                _bake.ClipHipsOffset[i] = sampleTotal;
                _bake.ClipSampleCount[i] = samples;
                _bake.ClipLength[i] = length;
                _bake.ClipLengthsManaged[i] = length;
                _bake.ClipLoopingManaged[i] = _clips[i].isLooping;
                rotationTotal += samples * streamNodes.Length;
                sampleTotal += samples;
            }
            _bake.Rotations = new NativeArray<quaternion>(rotationTotal, Allocator.Persistent);
            _bake.HipsPositions = new NativeArray<float3>(sampleTotal, Allocator.Persistent);
            _bake.SnapshotScales = new NativeArray<float3>(streamNodes.Length, Allocator.Persistent);

            _graph = PlayableGraph.Create("BasisLocomotionBake");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _output = AnimationPlayableOutput.Create(_graph, "bake", _cloneAnimator);
            _clip = 0;
            _sample = 0;
            _playableLive = false;
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

        /// <summary>
        /// Runs up to <see cref="EvaluatesPerTick"/> clip evaluations. Returns true while more work remains.
        /// </summary>
        public bool Tick()
        {
            if (Failed || _bake == null || _bake.Ready)
            {
                return false;
            }

            int budget = EvaluatesPerTick;
            while (budget-- > 0)
            {
                if (_clip >= BasisLocomotionGraph.ClipCount)
                {
                    FinishBake();
                    return false;
                }

                if (!_playableLive)
                {
                    _clipPlayable = AnimationClipPlayable.Create(_graph, _clips[_clip]);
                    _clipPlayable.SetApplyFootIK(false);
                    _clipPlayable.SetApplyPlayableIK(false);
                    _output.SetSourcePlayable(_clipPlayable);
                    _playableLive = true;
                }

                int samples = _bake.ClipSampleCount[_clip];
                float length = _bake.ClipLength[_clip];
                float time = samples > 1 ? length * _sample / (samples - 1) : 0f;
                _clipPlayable.SetTime(time);
                _graph.Evaluate(0f);

                int nodeCount = _bake.NodeCount;
                int rotationBase = _bake.ClipRotationOffset[_clip] + _sample * nodeCount;
                for (int i = 0; i < nodeCount; i++)
                {
                    _bake.Rotations[rotationBase + i] = _cloneNodes[i].localRotation;
                }
                _bake.HipsPositions[_bake.ClipHipsOffset[_clip] + _sample] = _cloneNodes[_bake.HipsNode].localPosition;

                _sample++;
                if (_sample >= samples)
                {
                    _sample = 0;
                    _clip++;
                    _clipPlayable.Destroy();
                    _playableLive = false;
                }
            }
            return true;
        }

        void FinishBake()
        {
            for (int i = 0; i < _bake.NodeCount; i++)
            {
                Transform node = _sourceNodes[i];
                _bake.SnapshotScales[i] = node != null ? (float3)node.localScale : new float3(1f, 1f, 1f);
            }
            _bake.Ready = true;
            ReleaseRig();
        }

        void ReleaseRig()
        {
            if (_playableLive)
            {
                _clipPlayable.Destroy();
                _playableLive = false;
            }
            if (_graph.IsValid())
            {
                _graph.Destroy();
            }
            if (_cloneRoot != null)
            {
                UnityEngine.Object.Destroy(_cloneRoot);
                _cloneRoot = null;
            }
            _cloneAnimator = null;
            _cloneNodes = null;
            _clips = null;
        }

        public void Abort()
        {
            ReleaseRig();
            if (_bake != null && !_bake.Ready)
            {
                _bake.Dispose();
                _bake = null;
            }
            _sourceNodes = null;
        }

        public void Dispose()
        {
            Abort();
            if (_bake != null)
            {
                _bake.Dispose();
                _bake = null;
            }
        }
    }
}
