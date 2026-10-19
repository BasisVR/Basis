using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Turns a prop's authored <see cref="BasisPropSpawnMetaData"/> and the player's own library
    /// settings into the pose <see cref="ContentLoader.LoadProp"/> spawns at.
    /// </summary>
    public static class PropSpawnPlacement
    {
        private const float GroundProbeUp = 1.0f;
        private const float GroundProbeDown = 6.0f;

        /// <summary>
        /// Works out which placement actually applies to this spawn, in precedence order: the
        /// player's own pick for this library entry, then the prop's authored request, then the
        /// entry's legacy <see cref="BasisDataStoreItemKeys.ItemKey.PlacementType"/> — which is what
        /// every existing entry and every prop built before this metadata existed lands on.
        /// </summary>
        public static BasisPropSpawnMetaData Resolve(BasisDataStoreItemKeys.ItemKey item, BasisBundleConnector connector)
        {
            BasisPropSpawnMetaData authored = connector != null ? connector.MetaData.PropSpawn : default;

            // Everything except the placement itself — hand, distance, scale — is carried through
            // whichever branch wins, so a prop can author a scale without also having to dictate
            // where it lands.
            BasisPropSpawnMetaData resolved = authored;
            if (!authored.IsSpecified)
            {
                resolved.AlignToSurface = true;
            }

            if (item == null)
            {
                resolved.Placement = BasisPropSpawnPlacement.Raycast;
                return resolved;
            }

            if (item.PlacementOverride != BasisPropSpawnPlacement.Unspecified)
            {
                resolved.Placement = item.PlacementOverride;
                return resolved;
            }

            if (authored.IsSpecified)
            {
                return resolved;
            }

            resolved.Placement = FromPlacementType(item.PlacementType);
            return resolved;
        }

        /// <summary>
        /// Expresses one of the legacy placement types in the new enum so every caller can take a
        /// single code path.
        /// </summary>
        public static BasisPropSpawnPlacement FromPlacementType(BundledContentHolder.PlacementType placementType)
        {
            switch (placementType)
            {
                case BundledContentHolder.PlacementType.SpawnInFrontOfPlayer:
                    return BasisPropSpawnPlacement.InFrontOfPlayer;
                case BundledContentHolder.PlacementType.SpawnAtPlayerOrigin:
                    return BasisPropSpawnPlacement.AtPlayerOrigin;
                default:
                    return BasisPropSpawnPlacement.Raycast;
            }
        }

        /// <summary>
        /// Resolves the hand a prop asked to be handed to, honoring the player's dominant-hand
        /// setting. Fails when the player has no such tracked device, which is the normal case on
        /// desktop and the signal to fall back to an in-air spawn.
        /// </summary>
        public static bool TryResolveHand(BasisPropSpawnHand hand, out BasisInput input, out BasisBoneTrackedRole role)
        {
            switch (hand)
            {
                case BasisPropSpawnHand.NonDominant:
                    role = BasisDominantHand.NonDominantRole;
                    break;
                case BasisPropSpawnHand.Left:
                    role = BasisBoneTrackedRole.LeftHand;
                    break;
                case BasisPropSpawnHand.Right:
                    role = BasisBoneTrackedRole.RightHand;
                    break;
                default:
                    role = BasisDominantHand.DominantRole;
                    break;
            }

            BasisDeviceManagement deviceInstance = BasisDeviceManagement.Instance;
            if (deviceInstance != null && deviceInstance.FindDevice(out input, role) && input.HasControl && input.Control != null)
            {
                return true;
            }

            input = null;
            return false;
        }

        /// <summary>
        /// Computes the spawn pose for every placement that does not need interactive aiming.
        /// <paramref name="bounds"/> is the prop's local render bounds and is used to seat the prop
        /// on a surface rather than burying its pivot in it.
        /// </summary>
        public static void ComputePose(BasisPropSpawnMetaData meta, BasisBounds bounds, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            scale = Vector3.one * meta.ResolvedUniformScale;

            Vector3 head = BasisLocalCameraDriver.HeadPosition;
            Vector3 forward = BasisLocalCameraDriver.HeadForward();
            Vector3 flatForward = Vector3.ProjectOnPlane(forward, Vector3.up);
            flatForward = flatForward.sqrMagnitude <= Mathf.Epsilon ? Vector3.forward : flatForward.normalized;

            float distance = meta.ResolvedDistance * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

            switch (meta.Placement)
            {
                case BasisPropSpawnPlacement.AtPlayerOrigin:
                    position = BasisLocalPlayer.Instance.PlayerSelf.position;
                    rotation = FacingRotation(meta, flatForward);
                    return;

                case BasisPropSpawnPlacement.InAirAtDistance:
                    position = head + forward.normalized * distance;
                    rotation = FacingRotation(meta, flatForward);
                    return;

                case BasisPropSpawnPlacement.OnGround:
                    {
                        Vector3 target = head + flatForward * distance;
                        rotation = FacingRotation(meta, flatForward);

                        if (Physics.Raycast(target + Vector3.up * GroundProbeUp, Vector3.down, out RaycastHit hit,
                                GroundProbeUp + GroundProbeDown, BasisPlayerInteract.Mask, BasisPlayerInteract.TriggerInteraction))
                        {
                            if (meta.AlignToSurface)
                            {
                                rotation = SurfaceRotation(hit.normal, flatForward, meta);
                            }
                            position = SeatOnSurface(hit.point, rotation, bounds, scale);
                            return;
                        }

                        position = SeatOnSurface(new Vector3(target.x, BasisLocalPlayer.Instance.PlayerSelf.position.y, target.z), rotation, bounds, scale);
                        return;
                    }

                case BasisPropSpawnPlacement.InHand:
                    if (TryResolveHand(meta.Hand, out BasisInput handInput, out _))
                    {
                        position = handInput.Control.OutgoingWorldData.position;
                        rotation = handInput.Control.OutgoingWorldData.rotation;
                        return;
                    }
                    position = head + forward.normalized * distance;
                    rotation = FacingRotation(meta, flatForward);
                    return;

                case BasisPropSpawnPlacement.AtAnchor:
                    if (BasisSpawnAnchors.TryGetSelected(out BasisSpawnAnchors.SpawnAnchor anchor))
                    {
                        if (anchor.OverrideScale)
                        {
                            scale = Vector3.one * anchor.Scale;
                        }
                        rotation = anchor.Rotation;
                        position = SeatOnSurface(anchor.Position, rotation, bounds, scale);
                        return;
                    }
                    position = head + flatForward * distance;
                    rotation = FacingRotation(meta, flatForward);
                    return;

                default:
                    position = head + flatForward * distance;
                    rotation = FacingRotation(meta, flatForward);
                    return;
            }
        }

        private static Quaternion FacingRotation(BasisPropSpawnMetaData meta, Vector3 flatForward)
        {
            return Quaternion.LookRotation(meta.FaceThePlayer ? -flatForward : flatForward, Vector3.up);
        }

        private static Quaternion SurfaceRotation(Vector3 normal, Vector3 flatForward, BasisPropSpawnMetaData meta)
        {
            Vector3 up = normal.sqrMagnitude <= Mathf.Epsilon ? Vector3.up : normal.normalized;
            Vector3 facing = meta.FaceThePlayer ? -flatForward : flatForward;
            Vector3 projected = Vector3.ProjectOnPlane(facing, up);
            if (projected.sqrMagnitude <= Mathf.Epsilon)
            {
                projected = Vector3.ProjectOnPlane(Vector3.forward, up);
                if (projected.sqrMagnitude <= Mathf.Epsilon)
                {
                    return Quaternion.identity;
                }
            }
            return Quaternion.LookRotation(projected.normalized, up);
        }

        public static Vector3 SeatOnSurface(Vector3 surfacePoint, Quaternion rotation, BasisBounds bounds, Vector3 scale)
        {
            Vector3 localBottom = bounds.center - Vector3.up * bounds.extents.y;
            localBottom.Scale(scale);
            return surfacePoint - (rotation * localBottom);
        }
    }
}
