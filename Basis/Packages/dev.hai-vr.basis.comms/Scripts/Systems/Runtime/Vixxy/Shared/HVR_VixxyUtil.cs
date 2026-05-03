using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace HVR.Vixxy
{
    public static class HVR_VixxyUtil
    {
        [HideInCallstack]
        public static void Log(object caller, string str)
        {
#if HVR_VIXXY_IS_IN_BASIS
            BasisDebug.Log($"({Header(caller)}) {str}");
#else
            HVR_Logging.Log(caller, str);
#endif
        }

        [HideInCallstack]
        public static void LogError(object caller, string str)
        {
#if HVR_VIXXY_IS_IN_BASIS
            BasisDebug.LogError($"({Header(caller)}) {str}");
#else
            HVR_Logging.LogError(caller, str);
#endif
        }

        [HideInCallstack]
        public static void LogUnusual(object caller, string str)
        {
#if HVR_VIXXY_IS_IN_BASIS
            BasisDebug.LogWarning($"({Header(caller)}) {str}");
#else
            HVR_Logging.LogUnusual(caller, str);
#endif
        }

        private static string Header(object caller)
        {
            var callerType = caller is Type t ? t.Name : caller.GetType().Name;
            return callerType;
        }

        /// Given a list of components that is known to have destroyed components in it, clean it.
        public static void RemoveDestroyedFromList(List<Component> listToClean)
        {
            for (var i = listToClean.Count - 1; i >= 0; i--)
                if (null == listToClean[i])
                    listToClean.RemoveAt(i);
        }

        /// Given a list of GameObjects that is known to have destroyed GameObjects in it, clean it.
        public static void RemoveDestroyedFromList(List<GameObject> listToClean)
        {
            for (var i = listToClean.Count - 1; i >= 0; i--)
                if (null == listToClean[i])
                    listToClean.RemoveAt(i);
        }

        /// Enables or disables a component, when applicable. If the component is a transform, then by convention, its GameObject is set active or inactive.
        /// If the component does not have a .enabled state, it is a no-op.
        public static void SetToggleState(Component component, bool isOn)
        {
            switch (component)
            {
                case Transform: component.gameObject.SetActive(isOn); break;
                case Behaviour thatBehaviour: thatBehaviour.enabled = isOn; break;

                // Counter-intuitively, .enabled does not imply Behaviour.
                // The following should cover all known components.
                // (CharacterController is a Collider, and SpriteMask is a Renderer).
                case Renderer thatRenderer: thatRenderer.enabled = isOn; break;
                case Collider thatCollider: thatCollider.enabled = isOn; break;
                case Cloth thatCloth: thatCloth.enabled = isOn; break;
                case LODGroup thatLod: thatLod.enabled = isOn; break;
                // else, there is no effect on other components as they may not have a .enabled property.
            }
        }

        /// Get the enabled state of a component. If the component is a transform, then by convention, we get the activeSelf of its GameObject.
        /// If the component does not have a .enabled state, this returns true.
        public static bool GetToggleState(Component component)
        {
            return component switch
            {
                Transform => component.gameObject.activeSelf,
                Behaviour thatBehaviour => thatBehaviour.enabled,

                // Counter-intuitively, .enabled does not imply Behaviour.
                // The following should cover all known components.
                // (CharacterController is a Collider, and SpriteMask is a Renderer).
                Renderer thatRenderer => thatRenderer.enabled,
                Collider thatCollider => thatCollider.enabled,
                Cloth thatCloth => thatCloth.enabled,
                LODGroup thatLod => thatLod.enabled,
                _ => true
            };
        }

        public static string ResolveAbsolutePath(Transform element)
        {
            return ResolveRelativePathInternal(null, element);
        }

        /// Gets the path of child relative to root, like an animation path that ignores the presence of Animator components.
        public static string ResolveRelativePath(Transform root, Transform child)
        {
            if (root == null) throw new ArgumentNullException(nameof(root)); // Explicitly disallow null for this public method.

            return ResolveRelativePathInternal(root, child);
        }

        private static string ResolveRelativePathInternal(Transform rootNullable, Transform child)
        {
            if (rootNullable == child) return "";

            if (child.parent != rootNullable && child.parent != null) return $"{ResolveRelativePathInternal(rootNullable, child.parent)}/{child.name}";

            return child.name;
        }

        // Calculates a SHA1 hash of a string, to mimic hashes of commits. The default length is 7, to mimic the default length of
        // short hashes on git. Do not use this for cryptographic purposes, this is meant for use in the creation of unique names for
        // parameters.
        public static string SimpleSha1(string str, int length = 7)
        {
            using var sha = SHA1.Create();
            return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding().GetBytes(str)))
                .Replace("-", "")
                .ToLowerInvariant()
                .Substring(0, length);
        }

        /// Returns false if any of the RGB components of this color is above 1.
        /// This is used to determine if we should be using Oklab to interpolate between two HDR colors.
        public static bool IsBelowHDR(Color32 color)
        {
            return color.r <= 1f && color.g <= 1f && color.b <= 1f;
        }

        // Note:
        // Interpolation using OkLab may not be possible with HDR
        // https://blog.selfshadow.com/publications/s2025-shading-course/pdi/s2025_pbs_pdi_slides.pdf
        // page 141
        //
        // (Basis Discord) https://discord.com/channels/1239242259392757822/1288184592930570293/1498540178157867018
        public static Color OklabLerp(Color a, Color b, float t)
        {
            var labA = RGBToOklab(a.linear);
            var labB = RGBToOklab(b.linear);

            var lab = Vector3.Lerp(labA, labB, t);
            var alpha = Mathf.Lerp(a.a, b.a, t);

            return OklabToRGB(lab, alpha);
        }

        private static Vector3 RGBToOklab(Color rgb)
        {
            float r = rgb.r;
            float g = rgb.g;
            float b = rgb.b;

            float l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b;
            float m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b;
            float s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b;

            l = Mathf.Pow(l, 1.0f / 3.0f);
            m = Mathf.Pow(m, 1.0f / 3.0f);
            s = Mathf.Pow(s, 1.0f / 3.0f);

            float L = 0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s;
            float A = 1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s;
            float B = 0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s;

            return new Vector3(L, A, B);
        }

        private static Color OklabToRGB(Vector3 lab, float alpha)
        {
            float L = lab.x;
            float A = lab.y;
            float B = lab.z;

            float l = L + 0.3963377774f * A + 0.2158037573f * B;
            float m = L - 0.1055613458f * A - 0.0638541728f * B;
            float s = L - 0.0894841775f * A - 1.2914855480f * B;

            l = l * l * l;
            m = m * m * m;
            s = s * s * s;

            float r = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
            float g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
            float b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;

            return new Color(r, g, b, alpha);
        }
    }
}
