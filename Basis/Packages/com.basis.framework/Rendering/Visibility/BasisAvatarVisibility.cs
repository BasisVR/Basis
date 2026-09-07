using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Rendering
{
    public static class BasisAvatarVisibility
    {
        public const float MinimumRadius = 0.6f;
        public const float RadiusPadding = 0.35f;

        private static readonly List<BasisRemotePlayer> Registered = new List<BasisRemotePlayer>(32);

        public static void Register(BasisRemotePlayer remote)
        {
            if (remote == null || remote.BasisAvatar == null)
            {
                return;
            }

            Unregister(remote);

            Renderer[] renderers = remote.BasisAvatar.Renders;
            if (renderers == null || renderers.Length == 0)
            {
                return;
            }

            Transform root = ResolveRoot(remote);
            float3 center = root != null ? (float3)root.position : float3.zero;
            float localRadius = MeasureRadius(renderers) / ReadMaxScale(root);

            remote.VisibilityHandle = BasisVisibilityDatabase.Register(
                renderers,
                root,
                center,
                new float3(localRadius),
                BasisVisibilityFlags.Dynamic);

            Registered.Add(remote);
            ApplyShadowEligibility(remote, remote.CurrentLodLevel);
        }

        public static void Unregister(BasisRemotePlayer remote)
        {
            if (remote == null)
            {
                return;
            }

            if (remote.VisibilityHandle != BasisVisibilityDatabase.InvalidHandle)
            {
                BasisVisibilityDatabase.Unregister(remote.VisibilityHandle);
                remote.VisibilityHandle = BasisVisibilityDatabase.InvalidHandle;
            }

            int index = Registered.IndexOf(remote);
            if (index >= 0)
            {
                int last = Registered.Count - 1;
                Registered[index] = Registered[last];
                Registered.RemoveAt(last);
            }
        }

        public static void ApplyShadowEligibility(BasisRemotePlayer remote, int lod)
        {
            if (remote == null || remote.VisibilityHandle == BasisVisibilityDatabase.InvalidHandle)
            {
                return;
            }

            bool shadowsSuppressed = BasisAvatarShadowLOD.Enabled && !BasisAvatarShadowLOD.CastsAtLod(lod);
            bool eligible = shadowsSuppressed && !remote.AvatarAlwaysLoaded;
            BasisVisibilityDatabase.SetCullEligible(remote.VisibilityHandle, eligible);
        }

        /// <summary>
        /// Re-runs eligibility when <c>AvatarAlwaysLoaded</c> flips. Without this the flag is only read
        /// at registration and on shadow-LOD applies, so marking someone always-show while they are
        /// already past the shadow boundary would leave them cullable — the opposite of the setting.
        /// </summary>
        public static void OnAvatarAlwaysLoadedChanged(BasisRemotePlayer remote)
        {
            ApplyShadowEligibility(remote, remote != null ? remote.CurrentLodLevel : 0);
        }

        private static float ReadMaxScale(Transform root)
        {
            if (root == null)
            {
                return 1f;
            }
            Vector3 lossy = root.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
            return scale < 1e-4f ? 1f : scale;
        }

        private static Transform ResolveRoot(BasisRemotePlayer remote)
        {
            if (remote.AvatarTransform != null)
            {
                return remote.AvatarTransform;
            }
            return remote.PlayerSelf;
        }

        private static float MeasureRadius(Renderer[] renderers)
        {
            float radius = MinimumRadius;
            for (int index = 0; index < renderers.Length; index++)
            {
                Renderer renderer = renderers[index];
                if (renderer == null)
                {
                    continue;
                }
                Vector3 extents = renderer.bounds.extents;
                float magnitude = extents.magnitude;
                if (magnitude > radius)
                {
                    radius = magnitude;
                }
            }
            return radius + RadiusPadding;
        }
    }
}
