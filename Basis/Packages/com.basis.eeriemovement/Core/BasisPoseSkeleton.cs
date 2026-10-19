using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.IK
{
    public sealed class BasisPoseSkeleton : IDisposable
    {
        public BasisPoseStream Stream;
        public Transform[] Nodes = Array.Empty<Transform>();
        public int[] WriteIndices = Array.Empty<int>();
        public Transform Anchor;
        public float[] FitScale = Array.Empty<float>();
        public bool FitActive;
        float3[] authoredLocalPosition = Array.Empty<float3>();
        public bool IsCreated => Stream.LocalPosition.IsCreated;
        public void Build(Transform root, IReadOnlyList<Transform> bones)
        {
            Dispose();

            var nodes = new List<Transform>();
            var segment = new List<Transform>();
            for (int i = 0; i < bones.Count; i++)
            {
                Transform bone = bones[i];
                if (bone == null)
                {
                    continue;
                }
                segment.Clear();
                Transform walk = bone;
                while (walk != null && !nodes.Contains(walk))
                {
                    segment.Add(walk);
                    if (walk == root)
                    {
                        break;
                    }
                    walk = walk.parent;
                }
                if (walk == null && root != null)
                {
                    Debug.LogWarning($"BasisPoseSkeleton.Build: bone '{bone.name}' is not a descendant of root '{root.name}' -- skipped.");
                    continue;
                }
                for (int s = segment.Count - 1; s >= 0; s--)
                {
                    nodes.Add(segment[s]);
                }
            }
            if (nodes.Count == 0)
            {
                return;
            }

            Nodes = nodes.ToArray();
            int count = Nodes.Length;
            Anchor = Nodes[0].parent;
            Stream = new BasisPoseStream
            {
                LocalPosition = new NativeArray<float3>(count, Allocator.Persistent),
                LocalRotation = new NativeArray<quaternion>(count, Allocator.Persistent),
                LocalScale = new NativeArray<float3>(count, Allocator.Persistent),
                Parent = new NativeArray<int>(count, Allocator.Persistent),
                BindLength = new NativeArray<float>(count, Allocator.Persistent),
                TranslationFree = new NativeArray<byte>(count, Allocator.Persistent),
                RestLocalRotation = new NativeArray<quaternion>(count, Allocator.Persistent),
                RestLocalPosition = new NativeArray<float3>(count, Allocator.Persistent),
                RestLocalScale = new NativeArray<float3>(count, Allocator.Persistent),
                WorldPositionCache = new NativeArray<float3>(count, Allocator.Persistent),
                WorldRotationCache = new NativeArray<quaternion>(count, Allocator.Persistent),
                WorldScaleCache = new NativeArray<float3>(count, Allocator.Persistent),
                WorldCacheStamp = new NativeArray<int>(count + 1, Allocator.Persistent),
                Count = count,
            };
            Stream.WorldCacheStamp[count] = 1;
            FitScale = new float[count];
            authoredLocalPosition = new float3[count];

            int nonFinite = 0;
            string firstNonFinite = null;
            for (int i = 0; i < count; i++)
            {
                Transform node = Nodes[i];
                int parent = Array.IndexOf(Nodes, node.parent);
                node.GetLocalPositionAndRotation(out Vector3 position, out Quaternion rotation);
                float3 authored = position;
                if (!math.all(math.isfinite(authored)))
                {
                    nonFinite++;
                    firstNonFinite ??= node.name;
                    authored = float3.zero;
                }
                quaternion localRotation = rotation;
                float rotationLengthSq = math.lengthsq(localRotation.value);
                float3 localScale = node.localScale;
                authoredLocalPosition[i] = authored;
                Stream.RestLocalPosition[i] = authored;
                FitScale[i] = 1f;
                Stream.Parent[i] = parent;
                Stream.BindLength[i] = math.length(authored);
                Stream.TranslationFree[i] = (byte)(parent < 0 ? 1 : 0);
                Stream.LocalPosition[i] = authored;
                Stream.LocalRotation[i] = math.isfinite(rotationLengthSq) && rotationLengthSq > 1e-8f ? localRotation : quaternion.identity;
                Stream.RestLocalRotation[i] = Stream.LocalRotation[i];
                Stream.LocalScale[i] = math.all(math.isfinite(localScale)) ? localScale : new float3(1f);
                Stream.RestLocalScale[i] = Stream.LocalScale[i];
            }
            if (nonFinite > 0)
            {
                Debug.LogError($"BasisPoseSkeleton.Build: {nonFinite} non-finite rest local position(s), first '{firstNonFinite}' -- substituted zero.");
            }

            var writeIndices = new List<int>();
            for (int i = 0; i < bones.Count; i++)
            {
                int index = Array.IndexOf(Nodes, bones[i]);
                if (index >= 0 && !writeIndices.Contains(index))
                {
                    writeIndices.Add(index);
                }
            }
            WriteIndices = writeIndices.ToArray();

            SyncAnchor();
            Stream.InvalidateWorldCache();
        }
        public BasisBoneHandle Bind(Transform bone) => BasisBoneHandle.FromIndex(Array.IndexOf(Nodes, bone));
        /// <summary>
        /// Reconstructs a bone's fitted rest position in <paramref name="root"/> local space without
        /// reading or disturbing the live animated pose. The root's own transform is intentionally
        /// excluded so callers can apply the avatar's current runtime scale separately.
        /// </summary>
        public bool TryGetRestPositionRelativeTo(Transform bone, Transform root, out Vector3 position)
        {
            position = Vector3.zero;
            if (!IsCreated || bone == null || root == null)
            {
                return false;
            }

            int boneIndex = Array.IndexOf(Nodes, bone);
            int rootIndex = Array.IndexOf(Nodes, root);
            if (boneIndex < 0 || rootIndex < 0)
            {
                return false;
            }
            if (boneIndex == rootIndex)
            {
                return true;
            }

            int[] chain = new int[BasisPoseStream.MaxDepth];
            int depth = 0;
            int walk = boneIndex;
            while (walk >= 0 && walk != rootIndex && depth < chain.Length)
            {
                chain[depth++] = walk;
                walk = Stream.Parent[walk];
            }
            if (walk != rootIndex)
            {
                return false;
            }

            float3 restPosition = float3.zero;
            quaternion restRotation = quaternion.identity;
            float3 restScale = new float3(1f);
            for (int i = depth - 1; i >= 0; i--)
            {
                int index = chain[i];
                restPosition += math.mul(restRotation, Stream.RestLocalPosition[index] * restScale);
                restRotation = math.mul(restRotation, Stream.RestLocalRotation[index]);
                restScale *= Stream.RestLocalScale[index];
            }

            if (!math.all(math.isfinite(restPosition)))
            {
                return false;
            }
            position = restPosition;
            return true;
        }
        public void SetTranslationFree(Transform bone)
        {
            int index = Array.IndexOf(Nodes, bone);
            if (index >= 0)
            {
                Stream.TranslationFree[index] = 1;
            }
        }
        public void SetFitScale(Transform bone, float scale)
        {
            int index = Array.IndexOf(Nodes, bone);
            if (index < 0 || Stream.TranslationFree[index] != 0)
            {
                return;
            }
            float safe = scale > 0f && math.isfinite(scale) ? scale : 1f;
            float3 fitted = authoredLocalPosition[index] * safe;
            FitScale[index] = safe;
            Stream.RestLocalPosition[index] = math.all(math.isfinite(fitted)) ? fitted : float3.zero;
            Stream.BindLength[index] = math.length(authoredLocalPosition[index]) * safe;
            if (!Mathf.Approximately(safe, 1f))
            {
                FitActive = true;
            }
        }
        public void ResetFit()
        {
            for (int i = 0; i < FitScale.Length; i++)
            {
                FitScale[i] = 1f;
                Stream.RestLocalPosition[i] = authoredLocalPosition[i];
                Stream.BindLength[i] = math.length(authoredLocalPosition[i]);
            }
            FitActive = false;
        }
        public void ApplyFit()
        {
            if (!FitActive)
            {
                return;
            }
            for (int i = 0; i < FitScale.Length; i++)
            {
                if (!Mathf.Approximately(FitScale[i], 1f))
                {
                    Stream.LocalPosition[i] = Stream.RestLocalPosition[i];
                }
            }
            Stream.InvalidateWorldCache();
        }
        public void WriteFittedLocalPositions()
        {
            if (!IsCreated)
            {
                return;
            }
            Stream.RestLocalPosition.CopyTo(Stream.LocalPosition);
            for (int i = 0; i < WriteIndices.Length; i++)
            {
                int index = WriteIndices[i];
                if (Nodes[index] != null)
                {
                    Nodes[index].localPosition = Stream.RestLocalPosition[index];
                }
            }
            Stream.InvalidateWorldCache();
        }
        public void RefreshRootFromTransform()
        {
            if (!IsCreated || Nodes[0] == null)
            {
                return;
            }
            SyncAnchor();
            Nodes[0].GetLocalPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Stream.LocalPosition[0] = position;
            Stream.LocalRotation[0] = rotation;
            Stream.LocalScale[0] = Nodes[0].localScale;
            Stream.InvalidateWorldCache();
        }
        public void GatherNow()
        {
            if (!IsCreated)
            {
                return;
            }
            SyncAnchor();
            for (int i = 0; i < Nodes.Length; i++)
            {
                Transform node = Nodes[i];
                if (node == null)
                {
                    continue;
                }
                node.GetLocalPositionAndRotation(out Vector3 position, out Quaternion rotation);
                Stream.LocalPosition[i] = position;
                Stream.LocalRotation[i] = rotation;
                Stream.LocalScale[i] = node.localScale;
            }
            Stream.InvalidateWorldCache();
        }
        public void ScatterNow()
        {
            for (int i = 0; i < WriteIndices.Length; i++)
            {
                int index = WriteIndices[i];
                Transform node = Nodes[index];
                if (node != null)
                {
                    node.SetLocalPositionAndRotation(Stream.LocalPosition[index], Stream.LocalRotation[index]);
                }
            }
        }
        void SyncAnchor()
        {
            if (Anchor == null)
            {
                Stream.AnchorPosition = float3.zero;
                Stream.AnchorRotation = quaternion.identity;
                Stream.AnchorScale = new float3(1f);
                return;
            }
            Anchor.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Stream.AnchorPosition = position;
            Stream.AnchorRotation = rotation;
            Stream.AnchorScale = Anchor.lossyScale;
        }
        public void Dispose()
        {
            if (!IsCreated)
            {
                return;
            }
            Stream.LocalPosition.Dispose();
            Stream.LocalRotation.Dispose();
            Stream.LocalScale.Dispose();
            Stream.Parent.Dispose();
            Stream.BindLength.Dispose();
            Stream.TranslationFree.Dispose();
            Stream.RestLocalRotation.Dispose();
            Stream.WorldPositionCache.Dispose();
            Stream.WorldRotationCache.Dispose();
            Stream.WorldScaleCache.Dispose();
            Stream.WorldCacheStamp.Dispose();
            Stream.RestLocalPosition.Dispose();
            Stream.RestLocalScale.Dispose();
            Stream = default;
            Nodes = Array.Empty<Transform>();
            WriteIndices = Array.Empty<int>();
            FitScale = Array.Empty<float>();
            authoredLocalPosition = Array.Empty<float3>();
            Anchor = null;
            FitActive = false;
        }
    }
}
