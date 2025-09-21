using System.Runtime.CompilerServices;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Helpers
{
    public static class BasisHelpers
    {
        public static T GetOrAddComponent<T>(GameObject gameObject) where T : Component
        {
            if (!gameObject.TryGetComponent(out T component))
            {
                component = gameObject.AddComponent<T>();
            }
            return component;
        }

        public static bool CheckInstance<T>(T component) where T : Component
        {
            if (component != null)
            {
                Debug.LogError("Instance already exists of " + typeof(T).Name);
                return false;
            }
            return true;
        }

        public static Vector3 ScaleVector(Vector3 vector, float scaleFactor = 1.6f)
        {
            return vector * scaleFactor;
        }

        public static bool TryCheckOrAttempt<T>(GameObject gameObject, ref T component) where T : Component
        {
            if (component != null)
            {
                Debug.Log("Already found component " + component.GetType().Name);
                return true;
            }
            if (gameObject.TryGetComponent(out component))
            {
                return true;
            }
            return false;
        }
        public static bool TryGetTransformBone(Animator animator, HumanBodyBones bone, out Transform boneTransform)
        {
            boneTransform = animator.GetBoneTransform(bone);
            return boneTransform != null;
        }
        /// <summary>
        /// Local → World: world = origin + rotation * local
        /// </summary>
        public static Vector3 ConvertFromLocalSpace(Vector3 localPosition, Vector3 origin, Quaternion rotation)
        {
            return origin + rotation * localPosition;
        }

        /// <summary>
        /// World → Local: local = inverse(rotation) * (world - origin)
        /// </summary>
        public static Vector3 ConvertToLocalSpace(Vector3 worldPosition, Vector3 origin, Quaternion rotation)
        {
            return Quaternion.Inverse(rotation) * (worldPosition - origin);
        }

        // --- LEGACY SIGNATURES (if something else calls these) -------------------
        // If other code still calls the old methods (without rotation), they will behave
        // like your original “no-rotation” logic. Prefer using the rotation-aware ones.

        public static Vector3 ConvertFromLocalSpace(Vector3 notFloorPosition, Vector3 floorPosition)
        {
            // original behavior: translation only
            return notFloorPosition + floorPosition;
        }

        public static Vector3 ConvertToLocalSpace(Vector3 notFloorPosition, Vector3 floorPosition)
        {
            // original behavior: translation only
            return notFloorPosition - floorPosition;
        }

        // --- 2D/3D AVATAR POSITION MAPPINGS --------------------------------------

        /// <summary>
        /// Map (y,z) -> (x=0, y, z) for use in 3D local/world math
        /// </summary>
        public static Vector3 AvatarPositionConversion(Vector2 input)
        {
            return new Vector3(0f, input.x, input.y);
        }

        /// <summary>
        /// Map (x=ignored, y, z) -> (y,z)
        /// </summary>
        public static Vector2 AvatarPositionConversion(Vector3 input)
        {
            return new Vector2(input.y, input.z);
        }
        public static bool TryGetVector3Bone(Animator animator, HumanBodyBones bone, out Vector3 position)
        {
            if (animator.avatar != null && animator.avatar.isHuman)
            {
                Transform boneTransform = animator.GetBoneTransform(bone);
                if (boneTransform != null)
                {
                    position = boneTransform.position;
                    return true;
                }
                else
                {
                    position = Vector3.zero;
                    return false;
                }
            }
            position = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Calculates camera-space plane from a world-space plane
        /// </summary>
        public static float4 CameraSpacePlane(in Matrix4x4 worldToCameraMatrix, in float3 pos, in float3 normal, float clipOffset, float sideSign = 1.0f)
        {
            float3 offset = normal * clipOffset;
            float3 offsetPos = pos + offset;

            float3 cPos = worldToCameraMatrix.MultiplyPoint(offsetPos);
            float3 cNormal = worldToCameraMatrix.MultiplyVector(normal) * sideSign;

            return new float4(cNormal.x, cNormal.y, cNormal.z, -math.dot(cPos, cNormal));
        }
    }
}
