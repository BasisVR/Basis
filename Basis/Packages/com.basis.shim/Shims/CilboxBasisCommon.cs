using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using System.Collections.Generic;
using System;
using System.Reflection;
using Basis.Scripts.BasisSdk.Constraints;
using CilboxItems = Basis.BasisUI.BasisCilboxPermissionText;

namespace Cilbox
{
	[AutoStaticsCleanup]
	public abstract partial class CilboxBasisCommon : Cilbox
	{
		[NoAutoStaticsCleanup] protected static readonly HashSet<string> commonWhiteListType = new HashSet<string>(){
			// Text Mesh Pro types
			"TMPro.*",

			// Basis types
			"BasisNetworkContentBase",
			// "did the local player spawn this" for a prop, world or avatar, as a bool.
			// The creator UUID itself stays prop-box only, see CilboxPropBasis.
			"BasisContent",
            "BasisNetworkContentBase+BasisContentInformation",
            "Basis.Scripts.BasisSdk.Interactions.BasisPickUpUseMode",
			"Basis.Scripts.Device_Management.Devices.BasisInput", // Restrictive, only used as a type.
			"Basis.Scripts.BasisSdk.Interactions.BasisPickupInteractable", // Restrictive (See below), only access field.
			"Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject", // Restrictive (See below), only access field.
			"Basis.BasisNetworkBehaviour",
			"Basis.BasisNetworkShim*",
			"BasisNetworkCommon+EventTiming",
			"Basis.Shims.BasisOsc*",
			"Basis.Network.Core.DeliveryMethod",
			"Basis.SafeUtil",
			// Wire compression helpers: quat <-> uint/ulong (smallest-three) and ranged
			// float <-> ushort. Pure math over already-whitelisted primitives — no handles,
			// no native memory — so default-allow methods are safe.
			"Basis.Scripts.Networking.Compression.BasisCompression+QuaternionCompressor",
			"BasisNetworkPrimitiveCompression+BasisRangedUshortFloatData",
			// Roster access plus the pose reads IBasisPlayer withholds. Returns players as
			// IBasisPlayer, and poses as copied Vector3/Quaternion — never a Transform.
			"Basis.Shims.BasisPlayersShim",
			// "is this player staff" — the local player's own nodes read straight out of memory,
			// anyone else's answered by the server one yes/no at a time. Every member returns a
			// bool or a copied string[]; no UUID and no handle crosses the boundary.
			"Basis.Shims.BasisPermissionsShim",
			// Late-latch callback. Auto-added by GetComponent<T> since it derives from CilboxShim.
			"Basis.Shims.BasisBeforeRenderShim",
			// "what are the player's graphics settings" - read-only tier lookups plus a fixed
			// allowlist of keys, so a world can drop its own expensive content on a weak
			// machine. Every member returns a string, a number, a bool or a copied string[];
			// nothing here writes a setting.
			"Basis.Shims.BasisGraphicsSettingsShim",
			"Basis.Shims.BasisPlatformShim",
			"BasisPlatformSwitch", // Restrictive, see method whitelist.
			"BasisPlatformSwitchRule",
			"Basis.Scripts.Device_Management.BasisPlatformCondition",
			"Basis.Scripts.BasisSdk.Players.BasisLocalPlayer",
			"Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer",
			"HVR.Basis.Comms.OSC*",
			"Basis.Scripts.BasisSdk.Constraints.BasisConstraintBase",
			"Basis.Scripts.BasisSdk.Constraints.BasisConstraintSourceEntry",
			"Basis.Scripts.BasisSdk.Constraints.BasisConstraintAxes",
			"Basis.Scripts.BasisSdk.Constraints.BasisConstraintWorldUp",
			"Basis.Scripts.BasisSdk.Constraints.BasisConstraintType",
			"Basis.Scripts.BasisSdk.Constraints.BasisPositionConstraint",
			"Basis.Scripts.BasisSdk.Constraints.BasisRotationConstraint",
			"Basis.Scripts.BasisSdk.Constraints.BasisScaleConstraint",
			"Basis.Scripts.BasisSdk.Constraints.BasisParentConstraint",
			"Basis.Scripts.BasisSdk.Constraints.BasisAimConstraint",
			"Basis.Scripts.BasisSdk.Constraints.BasisLookAtConstraint",
			"Basis.Scripts.BasisSdk.Constraints.BasisBlendConstraint",
			"Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform",
			"Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform+Space",
			"Basis.Scripts.BasisSdk.Constraints.BasisDampedTransform",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwistCorrection",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwistCorrection+TwistAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwoBoneIK",
			"Basis.Scripts.BasisSdk.Constraints.BasisChainIK",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwistChain",
			"Basis.Scripts.BasisSdk.Constraints.BasisMultiReferential",

			// Cilbox types
			"Cilbox.CilboxPublicUtils",

			// TMPro types
			"TMPro.TextMeshPro",
			"TMPro.TextMeshProUGUI",
			"TMPro.TMP_Text",
			"TMPro.TMP_Dropdown",
			"TMPro.TMP_InputField",

			// System types - primitives and core data
			"System.Action",
			"System.Action`1",
			"System.Array",
			"System.BitConverter", // HMMMMMMMMM SUSSY
			"System.Boolean",
			"System.Buffer",
			"System.Byte",
			"System.SByte",
			"System.Char",
			"System.Collections.Generic.*",
			"System.Collections.IEnumerable",
			"System.Collections.IEnumerator",
			"System.Comparison",
			"System.Convert", // HMMMMMMMMM SUSSY
			"System.DateTime",
			"System.DateTimeKind",
			"System.DateTimeOffset",
			"System.DayOfWeek",
			"System.Decimal",
			"System.Delegate",
			"System.Diagnostics.Stopwatch",
			"System.Double",
			"System.Enum",
			"System.EventArgs",
			"System.Exception",
			"System.Func",
			"System.Globalization.CompareOptions",
			"System.Globalization.CultureInfo",
			"System.Globalization.DateTimeStyles",
			"System.Globalization.NumberStyles",
			"System.Globalization.UnicodeCategory",
			"System.Guid",
			"System.IComparable",
			"System.IDisposable",
			"System.IEquatable",
			"System.IFormatProvider",
			"System.IFormattable",
			"System.Int*",
			"System.KeyValuePair",
			"System.Math",
			"System.MathF",
			"System.Nullable",
			"System.Object",
			"System.Predicate",
			"System.Random",
			"System.RuntimeTypeHandle",
			"System.Single",
			"System.String",
			"System.StringComparer",
			"System.StringComparison",
			"System.StringSplitOptions",
			"System.Text.NormalizationForm",
			"System.Text.StringBuilder",
			"System.Text.Encoding",
			"System.TimeSpan",
			"System.TimeZoneInfo",
			"System.Tuple",
			"System.UInt*",
			"System.ValueTuple",
			"System.ValueType",
			"System.Void",
			"<PrivateImplementationDetails>",

			// Unity types - core
			"UnityEngine.Application", // Restrictive, see method whitelist.
			"UnityEngine.Behaviour",
			"UnityEngine.Color",
			"UnityEngine.Color32",
			"UnityEngine.Component",
			"UnityEngine.Debug", // Remapped via GetTypeOverride to BasisDebugPropsShim.
			"UnityEngine.Events.UnityAction",
			"UnityEngine.Events.UnityEvent",
			"UnityEngine.Events.UnityEventBase",
			"UnityEngine.Events.UnityEventCallState",
			"UnityEngine.GameObject", // Hyper restrictive, see method whitelist.
			"UnityEngine.Gradient",
			"UnityEngine.GradientAlphaKey",
			"UnityEngine.GradientColorKey",
			"UnityEngine.GradientMode",
			"UnityEngine.HideFlags",
			"UnityEngine.KeyCode",
			"UnityEngine.LayerMask",
			"UnityEngine.Mathf",
			"UnityEngine.Matrix4x4",
			"UnityEngine.MonoBehaviour",
			"UnityEngine.Object",
			"UnityEngine.PrimitiveType",
			"UnityEngine.Random",
			"UnityEngine.RuntimePlatform",
			"UnityEngine.ScriptableObject",
			"UnityEngine.SendMessageOptions",
			"UnityEngine.Space",
			"UnityEngine.SystemLanguage",
			"UnityEngine.TextAsset",
			"UnityEngine.Time",
			"UnityEngine.Transform",
			"UnityEngine.Quaternion",
			"UnityEngine.Vector*",
			"UnityEngine.Vector2",
			"UnityEngine.Vector2Int",
			"UnityEngine.Vector3",
			"UnityEngine.Vector3Int",
			"UnityEngine.Vector4",

			// Unity types - math/spatial structs
			"UnityEngine.Bounds",
			"UnityEngine.BoundsInt",
			"UnityEngine.Plane",
			"UnityEngine.Ray",
			"UnityEngine.RaycastHit",
			"UnityEngine.Rect",
			"UnityEngine.RectInt",
			"UnityEngine.RectOffset",
			"UnityEngine.Resolution",

			// Unity types - audio
			"UnityEngine.AudioClip",
			"UnityEngine.AudioClipLoadType",
			"UnityEngine.AudioDataLoadState",
			"UnityEngine.AudioRolloffMode",
			"UnityEngine.AudioSource",
			"UnityEngine.AudioSourceCurveType",
			"UnityEngine.AudioVelocityUpdateMode",
			"UnityEngine.FFTWindow",

			// Unity types - animation
			"UnityEngine.AnimationBlendMode",
			"UnityEngine.AnimationClip",
			"UnityEngine.AnimationCullingType",
			"UnityEngine.AnimationCurve",
			"UnityEngine.AnimationEvent",
			"UnityEngine.AnimationPlayMode",
			"UnityEngine.AnimationState",
			"UnityEngine.Animator",
			"UnityEngine.AnimatorClipInfo",
			"UnityEngine.AnimatorControllerParameter",
			"UnityEngine.AnimatorControllerParameterType",
			"UnityEngine.AnimatorCullingMode",
			"UnityEngine.AnimatorOverrideController",
			"UnityEngine.AnimatorRecorderMode",
			"UnityEngine.AnimatorStateInfo",
			"UnityEngine.AnimatorTransitionInfo",
			"UnityEngine.AnimatorUpdateMode",
			"UnityEngine.Avatar",
			"UnityEngine.AvatarIKGoal",
			"UnityEngine.AvatarIKHint",
			"UnityEngine.AvatarMask",
			"UnityEngine.AvatarMaskBodyPart",
			"UnityEngine.AvatarTarget",
			"UnityEngine.HumanBodyBones",
			"UnityEngine.HumanBone",
			"UnityEngine.HumanLimit",
			"UnityEngine.HumanPose",
			"UnityEngine.HumanPoseHandler",
			"UnityEngine.HumanTrait",
			"UnityEngine.Keyframe",
			"UnityEngine.MatchTargetWeightMask",
			"UnityEngine.PlayMode",
			"UnityEngine.QueueMode",
			"UnityEngine.RuntimeAnimatorController",
			"UnityEngine.SkeletonBone",
			"UnityEngine.WeightedMode",
			"UnityEngine.WrapMode",

			// Unity Animations namespace - constraints
			"UnityEngine.Animations.AimConstraint",
			"UnityEngine.Animations.AimConstraint+WorldUpType",
			"UnityEngine.Animations.Axis",
			"UnityEngine.Animations.ConstraintSource",
			"UnityEngine.Animations.IConstraint",
			"UnityEngine.Animations.LookAtConstraint",
			"UnityEngine.Animations.ParentConstraint",
			"UnityEngine.Animations.PositionConstraint",
			"UnityEngine.Animations.RotationConstraint",
			"UnityEngine.Animations.ScaleConstraint",

			// Unity types - rendering / materials / mesh
			"UnityEngine.BoneWeight",
			"UnityEngine.IndexFormat",
			"UnityEngine.Material",
			"UnityEngine.MaterialGlobalIlluminationFlags",
			"UnityEngine.MaterialPropertyBlock",
			"UnityEngine.Mesh",
			"UnityEngine.MeshFilter",
			"UnityEngine.MeshRenderer",
			"UnityEngine.MeshTopology",
			"UnityEngine.MotionVectorGenerationMode",
			"UnityEngine.LineAlignment",
			"UnityEngine.LineRenderer",
			"UnityEngine.LineTextureMode",
			"UnityEngine.Renderer",
			"UnityEngine.Rendering.AmbientMode",
			"UnityEngine.Rendering.IndexFormat",
			"UnityEngine.Rendering.LightProbeUsage",
			"UnityEngine.Rendering.OpaqueSortMode",
			"UnityEngine.Rendering.ReflectionProbeUsage",
			"UnityEngine.Rendering.ShadowCastingMode",
			"UnityEngine.Rendering.ShadowMapPass",
			"UnityEngine.Rendering.UVChannelFlags",
			"UnityEngine.Rendering.SphericalHarmonicsL2",
			"UnityEngine.RenderTexture",
			"UnityEngine.RenderTextureFormat",
			"UnityEngine.RenderTextureReadWrite",
			"UnityEngine.Shader",
			"UnityEngine.ShadowCastingMode",
			"UnityEngine.SkinnedMeshRenderer",
			"UnityEngine.SkinQuality",
			"UnityEngine.Sprite",
			"UnityEngine.SpriteAlignment",
			"UnityEngine.SpriteDrawMode",
			"UnityEngine.SpriteMaskInteraction",
			"UnityEngine.SpriteMeshType",
			"UnityEngine.SpriteRenderer",
			"UnityEngine.SpriteSortPoint",
			"UnityEngine.SpriteTileMode",
			"UnityEngine.Texture",
			"UnityEngine.Texture2D",
			"UnityEngine.Texture2DArray",
			"UnityEngine.TextureFormat",
			"UnityEngine.TextureWrapMode",
			"UnityEngine.FilterMode",
			"UnityEngine.TrailRenderer",

			// Unity UI
			"UnityEngine.Canvas",
			"UnityEngine.CanvasGroup",
			"UnityEngine.CanvasRenderer",
			"UnityEngine.RectTransform",
			"UnityEngine.RectTransform+Axis",
			"UnityEngine.RectTransform+Edge",
			"UnityEngine.RenderMode",
			"UnityEngine.TextAnchor",
			"UnityEngine.FontStyle",
			"UnityEngine.HorizontalWrapMode",
			"UnityEngine.VerticalWrapMode",
			"UnityEngine.UI.*",

			// Unity Event Systems
			"UnityEngine.EventSystems.AxisEventData",
			"UnityEngine.EventSystems.BaseEventData",
			"UnityEngine.EventSystems.EventTrigger",
			"UnityEngine.EventSystems.EventTrigger+Entry",
			"UnityEngine.EventSystems.EventTriggerType",
			"UnityEngine.EventSystems.PointerEventData",
			"UnityEngine.EventSystems.PointerEventData+InputButton",
			"UnityEngine.EventSystems.RaycastResult",

            "BasisNetworkContentBase",
"BasisNetworkContentBase+BasisContentInformation",
"Basis.Scripts.BasisSdk.Players.IBasisPlayer",
"Basis.Scripts.Networking.BasisNetworkConnection",
        };

		protected static readonly HashSet<string> commonWhiteListFields = new HashSet<string>(){
			// Unity Vector / Quaternion math fields
			"UnityEngine.Vector*.x",
			"UnityEngine.Vector*.y",
			"UnityEngine.Vector*.z",
			"UnityEngine.Vector*.w",
			"UnityEngine.Quaternion*",

			// Unity Color fields
			"UnityEngine.Color.r",
			"UnityEngine.Color.g",
			"UnityEngine.Color.b",
			"UnityEngine.Color.a",
			"UnityEngine.Color32.r",
			"UnityEngine.Color32.g",
			"UnityEngine.Color32.b",
			"UnityEngine.Color32.a",

			// Unity math/spatial struct fields
			"UnityEngine.Bounds.*",
			"UnityEngine.BoundsInt.*",
			"UnityEngine.Plane.*",
			"UnityEngine.Ray.*",
			"UnityEngine.RaycastHit.*",
			"UnityEngine.Rect.*",
			"UnityEngine.RectInt.*",
			"UnityEngine.Resolution.*",
			"UnityEngine.Matrix4x4.m*",
			"UnityEngine.Keyframe.*",
			"UnityEngine.GradientAlphaKey.*",
			"UnityEngine.GradientColorKey.*",
			"UnityEngine.AnimatorClipInfo.*",
			"UnityEngine.AnimatorControllerParameter.*",
			"UnityEngine.HumanBone.*",
			"UnityEngine.HumanLimit.*",
			"UnityEngine.SkeletonBone.*",
			"UnityEngine.Animations.ConstraintSource.*",

			// System fields
			"System.Array.*",
			"System.String.*",
			"System.DateTime.*",
			"System.TimeSpan.*",
			"System.Guid.*",
			"System.Collections.Generic.KeyValuePair*",
			"System.KeyValuePair*",

			// Basis types
			"Basis.Scripts.BasisSdk.Interactions.BasisPickupInteractable.OnPickupUse",
			"Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject.OnInteractStartEvent",
			"Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject.OnInteractEndEvent",
            "BasisNetworkContentBase+BasisContentInformation",
            "Basis.BasisNetworkBehaviour.CurrentOwnerId",
			"Basis.BasisNetworkBehaviour.IsOwnedLocallyOnServer",
			"Basis.BasisNetworkBehaviour.HasNetworkID",
			"Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.playerId",
			// Quantizer configuration (Precision/Min/Max/RequiredBits/Mask) on the script's
			// own instance; Compress/Decompress clamp, so a mangled config can't corrupt.
			"BasisNetworkPrimitiveCompression+BasisRangedUshortFloatData.*",
			"Basis.Scripts.BasisSdk.Constraints.BasisConstraintSourceEntry.*",
			"Basis.Scripts.BasisSdk.Constraints.BasisConstraintBase.constraintActive",
			"Basis.Scripts.BasisSdk.Constraints.BasisConstraintBase.weight",
			"Basis.Scripts.BasisSdk.Constraints.BasisConstraintBase.locked",
			"Basis.Scripts.BasisSdk.Constraints.BasisPositionConstraint.translationAtRest",
			"Basis.Scripts.BasisSdk.Constraints.BasisPositionConstraint.translationOffset",
			"Basis.Scripts.BasisSdk.Constraints.BasisPositionConstraint.translationAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisRotationConstraint.rotationAtRest",
			"Basis.Scripts.BasisSdk.Constraints.BasisRotationConstraint.rotationOffset",
			"Basis.Scripts.BasisSdk.Constraints.BasisRotationConstraint.rotationAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisScaleConstraint.scaleAtRest",
			"Basis.Scripts.BasisSdk.Constraints.BasisScaleConstraint.scaleOffset",
			"Basis.Scripts.BasisSdk.Constraints.BasisScaleConstraint.scalingAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisParentConstraint.translationAtRest",
			"Basis.Scripts.BasisSdk.Constraints.BasisParentConstraint.rotationAtRest",
			"Basis.Scripts.BasisSdk.Constraints.BasisParentConstraint.translationOffsets",
			"Basis.Scripts.BasisSdk.Constraints.BasisParentConstraint.rotationOffsets",
			"Basis.Scripts.BasisSdk.Constraints.BasisParentConstraint.translationAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisParentConstraint.rotationAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisAimConstraint.aimVector",
			"Basis.Scripts.BasisSdk.Constraints.BasisAimConstraint.upVector",
			"Basis.Scripts.BasisSdk.Constraints.BasisAimConstraint.worldUpType",
			"Basis.Scripts.BasisSdk.Constraints.BasisAimConstraint.worldUpObject",
			"Basis.Scripts.BasisSdk.Constraints.BasisAimConstraint.worldUpVector",
			"Basis.Scripts.BasisSdk.Constraints.BasisAimConstraint.rotationAtRest",
			"Basis.Scripts.BasisSdk.Constraints.BasisAimConstraint.rotationOffset",
			"Basis.Scripts.BasisSdk.Constraints.BasisAimConstraint.rotationAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisLookAtConstraint.roll",
			"Basis.Scripts.BasisSdk.Constraints.BasisLookAtConstraint.useUpObject",
			"Basis.Scripts.BasisSdk.Constraints.BasisLookAtConstraint.worldUpObject",
			"Basis.Scripts.BasisSdk.Constraints.BasisLookAtConstraint.rotationAtRest",
			"Basis.Scripts.BasisSdk.Constraints.BasisLookAtConstraint.rotationOffset",
			"Basis.Scripts.BasisSdk.Constraints.BasisBlendConstraint.blendPosition",
			"Basis.Scripts.BasisSdk.Constraints.BasisBlendConstraint.blendRotation",
			"Basis.Scripts.BasisSdk.Constraints.BasisBlendConstraint.positionWeight",
			"Basis.Scripts.BasisSdk.Constraints.BasisBlendConstraint.rotationWeight",
			"Basis.Scripts.BasisSdk.Constraints.BasisBlendConstraint.translationAtRest",
			"Basis.Scripts.BasisSdk.Constraints.BasisBlendConstraint.rotationAtRest",
			"Basis.Scripts.BasisSdk.Constraints.BasisBlendConstraint.translationAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisBlendConstraint.rotationAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform.useSource",
			"Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform.space",
			"Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform.position",
			"Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform.rotation",
			"Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform.positionWeight",
			"Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform.rotationWeight",
			"Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform.translationAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisOverrideTransform.rotationAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisDampedTransform.dampPosition",
			"Basis.Scripts.BasisSdk.Constraints.BasisDampedTransform.dampRotation",
			"Basis.Scripts.BasisSdk.Constraints.BasisDampedTransform.maintainAim",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwistCorrection.twistAxis",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwistCorrection.twistWeight",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwoBoneIK.targetPositionWeight",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwoBoneIK.targetRotationWeight",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwoBoneIK.hintWeight",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwoBoneIK.maintainTargetOffset",
			"Basis.Scripts.BasisSdk.Constraints.BasisChainIK.chainRotationWeight",
			"Basis.Scripts.BasisSdk.Constraints.BasisChainIK.tipRotationWeight",
			"Basis.Scripts.BasisSdk.Constraints.BasisChainIK.tolerance",
			"Basis.Scripts.BasisSdk.Constraints.BasisChainIK.maxIterations",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwistChain.blend",
			"Basis.Scripts.BasisSdk.Constraints.BasisTwistChain.rotationAtRest",
			"Basis.Scripts.BasisSdk.Constraints.BasisMultiReferential.driver",

			// Sync shim configuration. These are plain fields rather than { get; set; } pairs, so
			// unlike a property they need naming here — field access is default-deny, methods are
			// default-allow. The const selectors alongside them (ChannelPose, SpaceWorld, Phase*,
			// Max*) need no entry: the compiler inlines a const to ldc.i4, so no field token is
			// ever emitted for them.
			"Basis.Shims.BasisTransformSyncShim.Channels",
			"Basis.Shims.BasisTransformSyncShim.Space",
			"Basis.Shims.BasisTransformSyncShim.Enabled",
			"Basis.Shims.BasisBlendShapeSyncShim.Epsilon",
			"Basis.Shims.BasisBlendShapeSyncShim.Enabled",
			"BasisPlatformSwitch.Rules",
			"BasisPlatformSwitchRule.*",

			"UnityEngine.UI.Toggle.onValueChanged",

			// Unity Event Systems fields
			"UnityEngine.EventSystems.EventTrigger+Entry.eventID",
			"UnityEngine.EventSystems.PointerEventData.hovered",
			"UnityEngine.EventSystems.EventTriggerType.*",
			"UnityEngine.EventSystems.PointerEventData+InputButton.*",
			"UnityEngine.EventSystems.RaycastResult.*",
		};

		protected static readonly Dictionary<Type, HashSet<string>> commonMethodWhitelist = new Dictionary<Type, HashSet<string>>()
		{
			{ typeof(UnityEngine.MonoBehaviour),       new HashSet<string>{ ".ctor" } },
			{ typeof(UnityEngine.ScriptableObject),    new HashSet<string>{ ".ctor" } },
			{ typeof(UnityEngine.Events.UnityAction),  new HashSet<string>{ ".ctor" } },
			{ typeof(Basis.Scripts.BasisSdk.Interactions.BasisPickupInteractable), new HashSet<string> { } },
			{ typeof(Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject), new HashSet<string> { } },
			{ typeof(Basis.Scripts.Device_Management.Devices.BasisInput), new HashSet<string> { } },
#if BASIS_HAS_EXAMPLES
			{ typeof(global::BasisPlatformSwitch), new HashSet<string> { nameof(global::BasisPlatformSwitch.Apply) } },
#endif
			// IBasisPlayer is reachable through BasisNetworkPlayer.Player, and methods are
			// default-allow once a type is whitelisted — which handed scripts set_DisplayName,
			// set_UUID, get_AvatarTransform, get_PlayerSelf and get_GameObject on ANY player, i.e.
			// a write handle on someone else's avatar. CilboxPropBasis already curated it this way;
			// this is the same treatment for scene and avatar boxes. Entries union with a box's
			// ExtraMethodWhitelist, so a box type can still add its own (prop adds get_BasisAvatar,
			// which is only safe there because prop also blocks every BasisAvatar method).
			//
			// Everything returning a value is allowed. Held back, and why:
			//   set_*, SetSafeDisplayname, UpdateFaceVisibility, AvatarSwitched — mutate another player.
			//   get_AvatarTransform/AvatarAnimatorTransform/PlayerSelf/Transform/AvatarParent — a
			//     Transform is default-allow once handed over, so these are write handles.
			//   get_GameObject — SetActive is allowed on GameObject; that hides or breaks a player.
			//   get_UUID — stable across instances, so it lets any world fingerprint and correlate
			//     users between visits. PlayerId covers per-session identity.
			//   add/remove_OnAvatarSwitched — an interpreted delegate cannot be unsubscribed, and
			//     this event outlives the script.
			//   get_AudioReceived — hands back a Delegate.
			// ProgressReportAvatarLoad, AvatarProgress, FaceRenderer and AvatarMetaData need no entry:
			// their return types are not whitelisted, so the return-type check already blocks them.
			{ typeof(Basis.Scripts.BasisSdk.Players.IBasisPlayer), new HashSet<string> {
				"get_IsLocal",
				"get_PlayerPlatform",
				"get_DisplayName",
				"get_SafeDisplayName",
				"get_IsConsideredFallBackAvatar",
				"get_AvatarLoadMode",
				"get_FaceIsVisible",
				"get_IsDestroyed",
				} },
			{ typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer), new HashSet<string> {
				typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer).GetProperty(nameof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.Player)).GetGetMethod().Name,
				typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer).GetProperty(nameof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.LocalPlayer)).GetGetMethod().Name,
				typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer).GetProperty(nameof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.displayName)).GetGetMethod().Name,
				"get_playerId", nameof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.GetAllPlayers),
				} },
			{ typeof(Basis.SafeUtil), new HashSet<string>{
				".ctor",
				nameof(Basis.SafeUtil.AddEventTrigger),
				nameof(Basis.SafeUtil.MakeNetworkable),
				nameof(Basis.SafeUtil.MakeInteractable),
				nameof(Basis.SafeUtil.GetExecutionBudgetUs),
				nameof(Basis.SafeUtil.GetRemainingExecutionBudgetUs),
				nameof(Basis.SafeUtil.GetLastFrameExecutionUs),
				} },
			{ typeof(BasisContent), new HashSet<string>{ "SpawnedByLocalPlayer" } },
			// Read-only identity. AssignContentIdentifier and get_ContentInformation are held back:
			// the first forges a spawn identity, the second hands over the whole struct.
			{ typeof(BasisNetworkContentBase), new HashSet<string>{
				"TryGetIdentifier",
				"TryGetNetworkGUIDIdentifier",
				"get_clientIdentifier",
				"get_SpawnedByLocalPlayer",
				} },
			{ typeof(BasisConstraintBase), new HashSet<string>{
				$"get_{nameof(BasisConstraintBase.constraintType)}",
				$"get_{nameof(BasisConstraintBase.sourceCount)}",
				nameof(BasisConstraintBase.GetSource),
				nameof(BasisConstraintBase.SetSource),
				nameof(BasisConstraintBase.GetSources),
				nameof(BasisConstraintBase.SetSources),
				nameof(BasisConstraintBase.AddSource),
				nameof(BasisConstraintBase.RemoveSource),
				nameof(BasisConstraintBase.ClearSources),
				nameof(BasisConstraintBase.CaptureRest),
				} },
			{ typeof(BasisPositionConstraint), new HashSet<string>() },
			{ typeof(BasisRotationConstraint), new HashSet<string>() },
			{ typeof(BasisScaleConstraint), new HashSet<string>() },
			{ typeof(BasisParentConstraint), new HashSet<string>{ nameof(BasisParentConstraint.CaptureOffsets) } },
			{ typeof(BasisAimConstraint), new HashSet<string>() },
			{ typeof(BasisLookAtConstraint), new HashSet<string>() },
			{ typeof(BasisBlendConstraint), new HashSet<string>() },
			{ typeof(BasisOverrideTransform), new HashSet<string>{
				nameof(BasisOverrideTransform.CaptureSourceBind),
				$"get_{nameof(BasisOverrideTransform.SourceInvBindPosition)}",
				$"get_{nameof(BasisOverrideTransform.SourceInvBindRotation)}",
				$"get_{nameof(BasisOverrideTransform.SourceToSpaceRotation)}",
				} },
			{ typeof(BasisDampedTransform), new HashSet<string>{
				$"get_{nameof(BasisDampedTransform.BindPosition)}",
				$"get_{nameof(BasisDampedTransform.BindRotation)}",
				$"get_{nameof(BasisDampedTransform.AimBindAxis)}",
				} },
			{ typeof(BasisTwistCorrection), new HashSet<string>{
				$"get_{nameof(BasisTwistCorrection.SourceInverseBindRotation)}",
				$"get_{nameof(BasisTwistCorrection.TwistBindRotation)}",
				$"get_{nameof(BasisTwistCorrection.TwistAxisVector)}",
				} },
			{ typeof(BasisTwoBoneIK), new HashSet<string>{
				$"get_{nameof(BasisTwoBoneIK.TargetOffsetPosition)}",
				$"get_{nameof(BasisTwoBoneIK.TargetOffsetRotation)}",
				nameof(BasisTwoBoneIK.ResolveMid),
				nameof(BasisTwoBoneIK.ResolveRoot),
				} },
			{ typeof(BasisChainIK), new HashSet<string>() },
			{ typeof(BasisTwistChain), new HashSet<string>{ $"get_{nameof(BasisTwistChain.BindOffset)}" } },
			{ typeof(BasisMultiReferential), new HashSet<string>{ $"get_{nameof(BasisMultiReferential.ResolvedDriver)}" } },
			{ typeof(UnityEngine.GameObject),          new HashSet<string>{
				nameof(UnityEngine.GameObject.SetActive),
				nameof(UnityEngine.GameObject.GetComponent),
				} },
			{ typeof(System.Buffer), new HashSet<string>{ "BlockCopy" } },
			{ typeof(System.Type),                     new HashSet<string>() }, // nothing allowed
			{ typeof(UnityEngine.Events.UnityEventBase), new HashSet<string>{
				"GetPersistentEventCount",
				"GetPersistentMethodName",
				"GetPersistentTarget",
				"RemoveAllListeners",
				} },
			{ typeof(UnityEngine.Canvas), new HashSet<string>{
				".ctor",
				"get_renderMode",
				"get_isRootCanvas",
				"get_pixelRect",
				"get_scaleFactor",
				"set_scaleFactor",
				"get_referencePixelsPerUnit",
				"set_referencePixelsPerUnit",
				"get_overridePixelPerfect",
				"set_overridePixelPerfect",
				"get_pixelPerfect",
				"set_pixelPerfect",
				"get_planeDistance",
				"set_planeDistance",
				"get_renderOrder",
				"get_overrideSorting",
				"set_overrideSorting",
				"get_sortingOrder",
				"set_sortingOrder",
				"get_targetDisplay",
				"set_targetDisplay",
				"get_sortingLayerID",
				"set_sortingLayerID",
				"get_cachedSortingLayerValue",
				"get_normalizedSortingGridSize",
				"set_normalizedSortingGridSize",
				"get_sortingGridNormalizedSize",
				"set_sortingGridNormalizedSize",
				"get_additionalShaderChannels",
				"set_additionalShaderChannels",
				"get_sortingLayerName",
				"set_sortingLayerName",
				"ForceUpdateCanvases",
				} },
			// UnityEngine.Application is whitelisted only for harmless read-only platform info.
			// All other entrypoints (OpenURL, Quit, Unload, ExternalCall, LoadLevel*) are blocked
			// in CheckMethodAllowed below.
			{ typeof(UnityEngine.Application), new HashSet<string>{
				"get_companyName",
				"get_genuine",
				"get_genuineCheckAvailable",
				"get_identifier",
				"get_installerName",
				"get_installMode",
				"get_internetReachability",
				"get_isBatchMode",
				"get_isConsolePlatform",
				"get_isEditor",
				"get_isFocused",
				"get_isMobilePlatform",
				"get_isPlaying",
				"get_platform",
				"get_productName",
				"get_runInBackground",
				"get_sandboxType",
				"get_systemLanguage",
				"get_targetFrameRate",
				"get_unityVersion",
				"get_version",
				"IsPlaying",
				} },
		};

		protected abstract HashSet<string> ExtraWhiteListType { get; }
		protected abstract HashSet<string> ExtraWhiteListFields { get; }
		protected abstract Dictionary<Type, HashSet<string>> ExtraMethodWhitelist { get; }

		internal CilboxBasisConsent.State consentState;
		internal string consentHash;
		internal HashSet<string> consentGranted;
		internal CilboxBasisConsent.Scan consentScan;

		protected bool Beyond(string item, bool floor)
		{
			if (consentScan != null)
			{
				(floor ? consentScan.Blocked : consentScan.Items).Add(item);
				return true;
			}
			return !floor && CilboxBasisConsent.Permits(this, item);
		}

		protected static string MethodItem(Type declaringType, string name)
		{
			string type = declaringType != null && declaringType.FullName != null ? GetSanitizedTypeName(declaringType) : "?";
			return CilboxItems.MethodPrefix + type + "." + name;
		}

		// Denied regardless of what a wildcard covers: "System.Int*" is a bare prefix match
		// and would otherwise admit System.IntPtr.
		private static readonly HashSet<string> hardDeniedTypes = new HashSet<string>(StringComparer.Ordinal)
		{
			"System.IntPtr",
			"System.UIntPtr",
			"System.Void*",
			"System.RuntimeFieldHandle",
			"System.RuntimeMethodHandle",
			"System.RuntimeTypeHandle",
			"System.Runtime.CompilerServices.Unsafe",
			"System.Runtime.InteropServices.Marshal",
			"System.Runtime.InteropServices.MemoryMarshal",
			"System.Runtime.InteropServices.GCHandle",
			"System.Runtime.InteropServices.NativeLibrary",
		};

		public override bool CheckTypeAllowed(Type t)
		{
			if (t == null || t.FullName == null) return false;
			if (t.IsPointer) return Beyond(CilboxItems.TypePrefix + t.FullName, true);
			return CheckTypeAllowed(GetSanitizedTypeName(t));
		}

		public override bool CheckFieldAllowed(Type t, string sFieldName)
		{
			if (t == null || t.FullName == null) return false;
			if (t.IsPointer) return Beyond(CilboxItems.FieldPrefix + t.FullName + "." + sFieldName, true);
			FieldInfo field = t.GetField(sFieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.Instance);
			if (field != null && field.FieldType.IsPointer) return Beyond(CilboxItems.FieldPrefix + GetSanitizedTypeName(t) + "." + sFieldName, true);
			return CheckFieldAllowed(GetSanitizedTypeName(t), sFieldName);
		}

		public bool CheckTypeAllowed(string sType)
		{
			if (sType == null) return false;
			if (hardDeniedTypes.Contains(sType)) return Beyond(CilboxItems.TypePrefix + sType, true);
			if (commonWhiteListType.Contains(sType)) return true;
			if (ExtraWhiteListType.Contains(sType)) return true;
			foreach (var allowedType in commonWhiteListType)
			{
				if (MatchesWildcard(allowedType, sType)) return true;
			}
			foreach (var allowedType in ExtraWhiteListType)
			{
				if (MatchesWildcard(allowedType, sType)) return true;
			}
			return Beyond(CilboxItems.TypePrefix + sType, false);
		}

		public bool CheckFieldAllowed(string sType, string sFieldName)
		{
			if (!CheckTypeAllowed(sType)) return false;
			string fullField = sType + "." + sFieldName;
			if (commonWhiteListFields.Contains(fullField)) return true;
			if (ExtraWhiteListFields.Contains(fullField)) return true;
			foreach (var allowedField in commonWhiteListFields)
			{
				if (MatchesWildcard(allowedField, fullField)) return true;
			}
			foreach (var allowedField in ExtraWhiteListFields)
			{
				if (MatchesWildcard(allowedField, fullField)) return true;
			}
			return Beyond(CilboxItems.FieldPrefix + fullField, false);
		}

		public override bool CheckMethodAllowed(out MethodInfo mi, Type declaringType, string name, SerializedTypeDescriptor[] parametersIn, SerializedTypeDescriptor[] genericArgumentsIn, string fullSignature)
		{
			mi = null;

			if (fullSignature != null && fullSignature.IndexOf('*') >= 0) return Beyond(MethodItem(declaringType, name), true);

			// Redirect every UnityEngine.Object.Instantiate variant through the sanitizing
			// shim so spawned prefabs are scrubbed (disallowed components destroyed,
			// persistent UnityEvent listeners killed) while parked under a disabled host
			// before they become active in hierarchy.
			if (declaringType == typeof(UnityEngine.Object) &&
				(name == "Instantiate" || name == "InstantiateAsync"))
			{
				mi = Basis.Shims.BasisCilboxInstantiateShim.ResolveShim(
					usage, name, parametersIn, genericArgumentsIn, fullSignature);
				return mi != null;
			}

			// NativeArray<T> only bounds-checks its indexer under ENABLE_UNITY_COLLECTIONS_CHECKS,
			// which release players do not define. Restricted to the members that copy out.
			if (declaringType != null && declaringType.IsGenericType &&
				declaringType.GetGenericTypeDefinition().FullName == "Unity.Collections.NativeArray`1")
			{
				return name == "get_Length" || name == "get_IsCreated" ||
					   name == "ToArray" || name == "CopyTo" ||
					   name == "Equals" || name == "GetHashCode" || name == "ToString" ||
					   Beyond(MethodItem(declaringType, name), true);
			}

			return CuratedMethodAllowed(declaringType, name) || Beyond(MethodItem(declaringType, name), false);
		}

		private bool CuratedMethodAllowed(Type declaringType, string name)
		{
			if (name.Contains("Invoke")) return false;

			// UnityEngine.Application.OpenURL opens an arbitrary URL in the native browser.
			// Same shape blocks Quit, Unload, LoadLevel*, ExternalCall/Eval and other
			// process-altering escapes.
			if (declaringType == typeof(UnityEngine.Application) && (
				name == "OpenURL" ||
				name == "Quit" ||
				name == "Unload" ||
				name == "CanStreamedLevelBeLoaded" ||
				name == "ExternalCall" ||
				name == "ExternalEval" ||
				name == "GetBuildTags" ||
				name == "RequestUserAuthorization" ||
				name == "SetBuildTags" ||
				name == "SetStackTraceLogType" ||
				name.StartsWith("Load", StringComparison.Ordinal)))
				return false;

			// SendMessage / BroadcastMessage / AddComponent reach behaviours by name and
			// bypass cilbox sanitisation.
			if (declaringType == typeof(UnityEngine.GameObject) && (
				name == "AddComponent" ||
				name == "SendMessage" ||
				name == "SendMessageUpwards" ||
				name == "BroadcastMessage"))
				return false;
			if (declaringType == typeof(UnityEngine.Component) && (
				name == "SendMessage" ||
				name == "SendMessageUpwards" ||
				name == "BroadcastMessage"))
				return false;
			if (declaringType == typeof(UnityEngine.Animator) && (
				name == "GetBehaviour" ||
				name == "GetBehaviours"))
				return false;

			bool inCommon = commonMethodWhitelist.TryGetValue(declaringType, out var commonAllowed);
			bool inExtra = ExtraMethodWhitelist.TryGetValue(declaringType, out var extraAllowed);
			if (inCommon || inExtra)
			{
				bool allowed = (inCommon && commonAllowed.Contains(name)) || (inExtra && extraAllowed.Contains(name));
				if (!allowed) return false;
			}

			return true;
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		static void LoadBclFacades()
		{
			foreach (string name in new[] { "mscorlib", "System", "System.Core" })
			{
				try { Assembly.Load(new AssemblyName(name)); } catch (Exception) { }
			}
		}

		public override bool GetTypeOverride(string sType, out Type t)
		{
			if (ExtraGetTypeOverride(sType, out t)) return true;
			switch (sType)
			{
				case "Basis.Shims.BasisNetworkShim":
					t = typeof(Basis.BasisNetworkShim);
					return true;
				case "Basis.Shims.BasisNetworkShim+NetworkReadyEvent":
					t = typeof(Basis.BasisNetworkShim.NetworkReadyEvent);
					return true;
				case "Basis.Shims.BasisNetworkShim+ServerOwnershipDestroyedEvent":
					t = typeof(Basis.BasisNetworkShim.ServerOwnershipDestroyedEvent);
					return true;
				case "Basis.Shims.BasisNetworkShim+OwnershipTransferEvent":
					t = typeof(Basis.BasisNetworkShim.OwnershipTransferEvent);
					return true;
				case "Basis.Shims.BasisNetworkShim+NetworkMessageEvent":
					t = typeof(Basis.BasisNetworkShim.NetworkMessageEvent);
					return true;
				case "Basis.Shims.BasisNetworkShim+PlayerJoinedEvent":
					t = typeof(Basis.BasisNetworkShim.PlayerJoinedEvent);
					return true;
				case "Basis.Shims.BasisNetworkShim+PlayerLeftEvent":
					t = typeof(Basis.BasisNetworkShim.PlayerLeftEvent);
					return true;
				case "UnityEngine.Video.VideoPlayer":
					t = typeof(Basis.Shims.VideoPlayerShim);
					return true;
				case "UnityEngine.Video.VideoPlayer+ErrorEventHandler":
					t = typeof(Basis.Shims.VideoPlayerShim.ErrorEventHandlerShim);
					return true;
				case "UnityEngine.Video.VideoPlayer+EventHandler":
					t = typeof(Basis.Shims.VideoPlayerShim.EventHandlerShim);
					return true;
				case "UnityEngine.Video.VideoPlayer+FrameReadyEventHandler":
					t = typeof(Basis.Shims.VideoPlayerShim.FrameReadyEventHandlerShim);
					return true;
				case "UnityEngine.Video.VideoPlayer+TimeEventHandler":
					t = typeof(Basis.Shims.VideoPlayerShim.TimeEventHandlerShim);
					return true;
				case "UnityEngine.Debug":
					t = typeof(Basis.Shims.BasisDebugPropsShim);
					return true;
				default:
					t = null;
					return false;
			}
		}

		protected virtual bool ExtraGetTypeOverride(string sType, out Type t)
		{
			t = null;
			return false;
		}

		protected static HashSet<string> MergeTypes(HashSet<string> extras)
		{
			var merged = new HashSet<string>(commonWhiteListType);
			merged.UnionWith(extras);
			return merged;
		}

		private static bool MatchesWildcard(string pattern, string target)
		{
			if (!pattern.Contains('*')) return false;
			string[] parts = pattern.Split('*');
			return target.StartsWith(parts[0], StringComparison.Ordinal) && target.EndsWith(parts[1], StringComparison.Ordinal);
		}
	}
}
