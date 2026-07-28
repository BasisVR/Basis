using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using System.Linq;
using UnityEngine.UI;

namespace HVR.Vixxy
{
    public static class HVR_VixxyPermitted
    {
        /// List of types that can be toggled and affected by field or property accesses.<br/>
        /// Field and property access have further restrictions, see further down below in this class.
        private static readonly List<Type> PermittedTypes = new()
        {
            // Other
            typeof(Transform),
            typeof(GameObject),
            typeof(ParticleSystem),
            typeof(Cloth),
            // Authored motion
            typeof(BasisAuthoredMotion),
            // Renderers
            typeof(MeshRenderer), // NOTE: MeshRenderer and SkinnedMeshRenderer have hard-coded leniency on material properties. If a property recursively affects a SkinnedMeshRenderer, then it will also affect a MeshRenderer.
            typeof(SkinnedMeshRenderer),
            typeof(TrailRenderer),
            typeof(ParticleSystemRenderer),
            // Constraints
            typeof(ParentConstraint),
            typeof(PositionConstraint),
            typeof(RotationConstraint),
            typeof(ScaleConstraint),
            typeof(AimConstraint),
            typeof(LookAtConstraint),
            // Colliders
            typeof(SphereCollider),
            typeof(CapsuleCollider),
            typeof(BoxCollider),
            typeof(MeshCollider),
            // Physics
            typeof(Rigidbody),
            typeof(ConfigurableJoint),
            typeof(HingeJoint),
            // UI
            typeof(RectTransform),
            typeof(Canvas),
            typeof(Text),
            typeof(Image),
        };

        /// Same as above, but those are strings. This is so we may reference types from other packages without creating a dependency.
        private static readonly List<string> PermittedTypeNames = new()
        {
            // Renderers
            "UnityEngine.Rendering.Universal.DecalProjector",
            // Jiggle
            "GatorDragonGames.JigglePhysics.JiggleRig",
            // UI
            "TMPro.TextMeshPro",
            "TMPro.TextMeshProUGUI",
            // BASISVR: Basis constraints. Content Police rewrites Unity's built-in and
            // Animation Rigging constraints onto these at load, so a Vixxy binding that used to
            // drive the original has to be allowed to drive the replacement or the toggle
            // silently stops working.
            "Basis.Scripts.BasisSdk.Constraints.BasisPositionConstraint",
            "Basis.Scripts.BasisSdk.Constraints.BasisRotationConstraint",
            "Basis.Scripts.BasisSdk.Constraints.BasisScaleConstraint",
            "Basis.Scripts.BasisSdk.Constraints.BasisParentConstraint",
            "Basis.Scripts.BasisSdk.Constraints.BasisAimConstraint",
            "Basis.Scripts.BasisSdk.Constraints.BasisLookAtConstraint",
            "Basis.Scripts.BasisSdk.Constraints.BasisBlendConstraint",
            "Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform",
            "Basis.Scripts.BasisSdk.Constraints.BasisDampedTransform",
            "Basis.Scripts.BasisSdk.Constraints.BasisTwistCorrection",
            "Basis.Scripts.BasisSdk.Constraints.BasisTwoBoneIK",
            "Basis.Scripts.BasisSdk.Constraints.BasisChainIK",
            "Basis.Scripts.BasisSdk.Constraints.BasisTwistChain",
        };

        private static readonly HashSet<string> RuntimePermittedTypeNames;

        /// If true, all field accesses are allowed on the permitted types.<br/>
        /// Otherwise, access is dictated by the PermittedStandardAccess list further down below.
        public static readonly bool AllowArbitraryFieldAccess = false;

        /// If true, all property accesses are allowed on the permitted types.<br/>
        /// Otherwise, access is dictated by the PermittedStandardAccess list further down below.
        public static readonly bool AllowArbitraryPropertyAccess = false;

        /// This list of tuples defines which fields and properties can be accessed on each type.<br/>
        /// The first list of the tuple contains type names. The second list contains property names that can be accessed.
        private static readonly List<(List<string>, List<string>)> PermittedStandardAccess = new()
        {
            (
                new List<string> { typeof(Text).FullName, "TMPro.TextMeshPro", "TMPro.TextMeshProUGUI" },
                new List<string> { "text" }
            ),
            (
                new List<string> { typeof(Transform).FullName },
                new List<string> { "rotation", "localRotation", "position", "localPosition", "localScale" }
            )
        };

        static HVR_VixxyPermitted()
        {
            RuntimePermittedTypeNames = PermittedTypes
                .Select(type => type.FullName)
                .Concat(PermittedTypeNames)
                .ToHashSet();
        }

        public static bool IsPermitted(string typeName) => RuntimePermittedTypeNames.Contains(typeName);

        public static bool IsTypeOfPropertyValuePermitted(HVRVixxyPropertyBase property)
        {
            return property is HVRVixxyPropertyFloat
                or HVRVixxyPropertyVector4
                or HVRVixxyPropertyVector3
                or HVRVixxyPropertyMaterial
                or HVRVixxyPropertyQuaternion
                or HVRVixxyPropertyBool
                or HVRVixxyPropertyColor
                or HVRVixxyPropertyColorHDR
                or HVRVixxyPropertyTexture
                or HVRVixxyPropertyMesh
                or HVRVixxyPropertyString;
        }

        public static bool IsStandardAccessPermitted(string typeName, string propertyName)
        {
            return PermittedStandardAccess.Any(tuple => tuple.Item1.Contains(typeName) && tuple.Item2.Contains(propertyName));
        }
    }
}
