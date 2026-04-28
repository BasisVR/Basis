using UnityEngine;
using System.Collections.Generic;
using System;
using System.Collections.Specialized;
using System.Collections;
using System.Runtime.InteropServices;
using System.Reflection;
using Cilbox;

namespace Cilbox
{
	[CilboxTarget]
	public class CilboxPropBasis : Cilbox
	{
		static HashSet<String> whiteListType = new HashSet<String>(){
			// Text Mesh Pro types
			"TMPro.*",

			// Basis types
			"Basis.Scripts.BasisSdk.Interactions.BasisPickUpUseMode",
			"Basis.Scripts.Device_Management.Devices.BasisInput", // Restrictive, only used as a type.
			"Basis.Scripts.BasisSdk.Interactions.BasisPickupInteractable", // Restrictive (See below), only access field.
			"Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject", // Restrictive (See below), only access field.
			"Basis.BasisNetworkBehaviour",
			"Basis.Network.Core.DeliveryMethod",
			"Basis.SafeUtil",
			"Basis.Scripts.BasisSdk.Players.BasisLocalPlayer",
			"Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer",
			"Basis.Shims.*",
            "Basis.BasisImageDownloader",
			"Basis.IBasisImageDownload",

			// Cilbox types
			"Cilbox.CilboxPublicUtils",

			// System types - primitives and core data
			"System.Action",
			"System.Action`*", // Action<T1>, Action<T1,T2>, ... (generic delegates)
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
			"System.Comparison`1",
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
			"System.Float",
			"System.Func`*",
			"System.Globalization.CompareOptions",
			"System.Globalization.CultureInfo",
			"System.Globalization.DateTimeStyles",
			"System.Globalization.NumberStyles",
			"System.Globalization.UnicodeCategory",
			"System.Guid",
			"System.IComparable",
			"System.IComparable`1",
			"System.IDisposable",
			"System.IEquatable`1",
			"System.IFormatProvider",
			"System.IFormattable",
			"System.Int*",
			"System.IO.BinaryReader",
			"System.IO.BinaryWriter",
			"System.IO.MemoryStream",
			"System.IO.Stream",
			"System.IO.SeekOrigin",
			"System.KeyValuePair`2",
			"System.Long",
			"System.ULong",
			"System.Math",
			"System.MathF",
			"System.Nullable`1",
			"System.Object",
			"System.Predicate`1",
			"System.Random",
			"System.RuntimeTypeHandle",
			"System.Short",
			"System.Ushort",
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
			"System.Tuple`*",
			"System.UInt16",
			"System.UInt32",
			"System.UInt*",
			"System.ValueTuple",
			"System.ValueTuple`*",
			"System.ValueType",
			"System.Void",
			"<PrivateImplementationDetails>", // Probably remove me? But we need a way to handle string hashing.  We can do it with our own function but that's slower.

			// Unity types - core
			"UnityEngine.Application", // Restrictive, see method whitelist.
			"UnityEngine.Behaviour",
			"UnityEngine.Color",
			"UnityEngine.Color32",
			"UnityEngine.Component",
			"UnityEngine.Debug", // Remapped via GetTypeOverride to BasisDebugPropsShim.
			"UnityEngine.Events.UnityAction",
			"UnityEngine.Events.UnityAction`*",
			"UnityEngine.Events.UnityEvent",
			"UnityEngine.Events.UnityEvent`*",
			"UnityEngine.Events.UnityEventCallState",
			"UnityEngine.GameObject",     // Hyper restrictive.
			"UnityEngine.Gradient",
			"UnityEngine.GradientAlphaKey",
			"UnityEngine.GradientColorKey",
			"UnityEngine.GradientMode",
			"UnityEngine.HideFlags",
			"UnityEngine.KeyCode",
			"UnityEngine.LayerMask",
			"UnityEngine.Mathf",
			"UnityEngine.Matrix4x4",
			"UnityEngine.MonoBehaviour",   // Note this is needed for the 'ctor, but we can be very restrictive.
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
			"UnityEngine.Graphics",
			"UnityEngine.Renderer",
			"UnityEngine.Rendering.AmbientMode",
			"UnityEngine.Rendering.AsyncGPUReadback",
			"UnityEngine.Rendering.AsyncGPUReadbackRequest",
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

			"Unity.Collections.NativeArray*",

			// Unity types - lighting
			"UnityEngine.Light",
			"UnityEngine.LightShadowCasterMode",
			"UnityEngine.LightShadows",
			"UnityEngine.LightType",
			"UnityEngine.LightRenderMode",
			"UnityEngine.LightProbeProxyVolume",
			"UnityEngine.ShadowQuality",
			"UnityEngine.ShadowResolution",
			"UnityEngine.ShadowProjection",
			"UnityEngine.ShadowmaskMode",

			// Unity types - physics
			"UnityEngine.BoxCollider",
			"UnityEngine.CapsuleCollider",
			"UnityEngine.CharacterController",
			"UnityEngine.Collider",
			"UnityEngine.Collision",
			"UnityEngine.CollisionDetectionMode",
			"UnityEngine.ConfigurableJoint",
			"UnityEngine.ContactPoint",
			"UnityEngine.FixedJoint",
			"UnityEngine.ForceMode",
			"UnityEngine.HingeJoint",
			"UnityEngine.Joint",
			"UnityEngine.JointAngleLimits2D",
			"UnityEngine.JointDrive",
			"UnityEngine.JointLimits",
			"UnityEngine.JointMotor",
			"UnityEngine.JointProjectionMode",
			"UnityEngine.JointSpring",
			"UnityEngine.MeshCollider",
			"UnityEngine.MeshColliderCookingOptions",
			"UnityEngine.PhysicMaterial",
			"UnityEngine.PhysicMaterialCombine",
			"UnityEngine.QueryTriggerInteraction",
			"UnityEngine.Rigidbody",
			"UnityEngine.RigidbodyConstraints",
			"UnityEngine.RigidbodyInterpolation",
			"UnityEngine.SphereCollider",
			"UnityEngine.SoftJointLimit",
			"UnityEngine.SoftJointLimitSpring",
			"UnityEngine.SpringJoint",

			// Unity types - particles
			"UnityEngine.ParticleSystem",
			"UnityEngine.ParticleSystem+*",
			"UnityEngine.ParticleSystemRenderer",
			"UnityEngine.ParticleSystemSimulationSpace",
			"UnityEngine.ParticleSystemShapeType",
			"UnityEngine.ParticleSystemSortMode",
			"UnityEngine.ParticleSystemRenderMode",
			"UnityEngine.ParticleSystemStopBehavior",
			"UnityEngine.ParticleSystemEmissionType",

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
			"UnityEngine.UI.InputField",
			"UnityEngine.UI.InputField+OnChangeEvent",
			"UnityEngine.UI.Scrollbar",
			"UnityEngine.UI.Selectable",
			"UnityEngine.UI.Slider",
			"UnityEngine.UI.Text",

			// Unity Event Systems
			"UnityEngine.EventSystems.AxisEventData",
			"UnityEngine.EventSystems.BaseEventData",
			"UnityEngine.EventSystems.EventTrigger",
			"UnityEngine.EventSystems.EventTrigger+Entry",
			"UnityEngine.EventSystems.EventTrigger+TriggerEvent",
			"UnityEngine.EventSystems.EventTriggerType",
			"UnityEngine.EventSystems.PointerEventData",
			"UnityEngine.EventSystems.PointerEventData+InputButton",
			"UnityEngine.EventSystems.RaycastResult",
		};

		static HashSet<String> whiteListFields = new HashSet<String>(){
			// Unity Vector / Quaternion math fields
			"UnityEngine.Vector*.x",
			"UnityEngine.Vector*.y",
			"UnityEngine.Vector*.z",
			"UnityEngine.Vector*.w",
			"UnityEngine.Quaternion*",

			// Unity Color fields (raw r/g/b/a access for both Color and Color32)
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
			"UnityEngine.Matrix4x4.m*", // m00..m33 entries
			"UnityEngine.Keyframe.*",
			"UnityEngine.GradientAlphaKey.*",
			"UnityEngine.GradientColorKey.*",
			"UnityEngine.AnimatorClipInfo.*",
			"UnityEngine.AnimatorControllerParameter.*",
			"UnityEngine.HumanBone.*",
			"UnityEngine.HumanLimit.*",
			"UnityEngine.SkeletonBone.*",
			"UnityEngine.Animations.ConstraintSource.*",

			// Unity physics struct fields
			"UnityEngine.ContactPoint.*",
			"UnityEngine.JointAngleLimits2D.*",
			"UnityEngine.JointDrive.*",
			"UnityEngine.JointLimits.*",
			"UnityEngine.JointMotor.*",
			"UnityEngine.JointSpring.*",
			"UnityEngine.SoftJointLimit.*",
			"UnityEngine.SoftJointLimitSpring.*",

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
			"Basis.BasisNetworkBehaviour.CurrentOwnerId",
			"Basis.BasisNetworkBehaviour.IsOwnedLocallyOnServer",
            "Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.playerId",
        };

		static public HashSet<String> GetWhiteListTypes() { return whiteListType; }

		// This is called by CilboxUsage to decide of a type is allowed.
		// If a type is allowed, by defalt it is all allowed.
		override public bool CheckTypeAllowed( String sType )
		{
			if( whiteListType.Contains( sType ) ) return true;

			foreach( string allowedType in whiteListType )
			{
				if( !allowedType.Contains( '*' ) ) continue;

				string[] allowedPrefix = allowedType.Split( '*' );
				if( sType.StartsWith( allowedPrefix[0], StringComparison.Ordinal ) && sType.EndsWith( allowedPrefix[1], StringComparison.Ordinal ) ) return true;
			}

			return false;
		}

		override public bool CheckFieldAllowed( String sType, String sFieldName )
		{
			if( !CheckTypeAllowed( sType ) ) return false;
			string fullField = sType + "." + sFieldName;
			if( whiteListFields.Contains( fullField ) ) return true;
			foreach( string allowedType in whiteListFields )
			{
				if( !allowedType.Contains( '*' ) ) continue;

				string[] allowedPrefix = allowedType.Split( '*' );
				if( fullField.StartsWith( allowedPrefix[0], StringComparison.Ordinal ) && fullField.EndsWith( allowedPrefix[1], StringComparison.Ordinal ) ) return true;
			}
			
			return false;
		}

		// Whitelist methods on native types.
		// If a type is not in this dictionary, then all methods are allowed.
		static Dictionary<Type, HashSet<string>> methodWhitelist = new Dictionary<Type, HashSet<string>>()
		{
			{ typeof(UnityEngine.MonoBehaviour),       new HashSet<string>{ ".ctor" } },
			{ typeof(UnityEngine.ScriptableObject),    new HashSet<string>{ ".ctor" } },
			{ typeof(UnityEngine.Events.UnityAction),  new HashSet<string>{ ".ctor" } },
			{ typeof(Basis.Scripts.BasisSdk.Interactions.BasisPickupInteractable), new HashSet<string> { } },
			{ typeof(Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject), new HashSet<string> { } },
			{ typeof(Basis.Scripts.Device_Management.Devices.BasisInput), new HashSet<string> { } },
			{ typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer), new HashSet<string> {
				typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer).GetProperty(nameof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.playerId)).GetGetMethod().Name,
				typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer).GetProperty(nameof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.Player)).GetGetMethod().Name,
				typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer).GetProperty(nameof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.LocalPlayer)).GetGetMethod().Name,
				typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer).GetProperty(nameof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.displayName)).GetGetMethod().Name,
				} },
			{ typeof(UnityEngine.GameObject),          new HashSet<string>{
				nameof(GameObject.SetActive),
				nameof(GameObject.GetComponents),
				typeof(GameObject).GetProperty(nameof(GameObject.transform)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.activeSelf)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.activeInHierarchy)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.layer)).GetGetMethod().Name,
				} },
			{ typeof(UnityEngine.Graphics), new HashSet<string>{ "Blit" } },
			{ typeof(UnityEngine.Rendering.AsyncGPUReadback), new HashSet<string>{ "Request" } },
			{ typeof(Buffer), new HashSet<string>{ "BlockCopy" } },
			{ typeof(BitConverter), new HashSet<string>{
				"GetBytes", "ToBoolean", "ToChar", "ToDouble", "ToInt16", "ToInt32",
				"ToInt64", "ToSingle", "ToString", "ToUInt16", "ToUInt32", "ToUInt64",
				"DoubleToInt64Bits", "Int64BitsToDouble", "SingleToInt32Bits", "Int32BitsToSingle" } },
			{ typeof(Convert), new HashSet<string>{
				"ToInt16", "ToInt32", "ToInt64", "ToUInt16", "ToUInt32", "ToUInt64",
				"ToByte", "ToSByte", "ToBoolean", "ToChar", "ToSingle", "ToDouble",
				"ToString", "ToBase64String", "FromBase64String",
				"ToDateTime", "ToDecimal" } },
			{ typeof(System.Type),                     new HashSet<string>() }, // nothing allowed
			// UnityEngine.Application is whitelisted only for harmless read-only platform info.
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

		// After a type is allowed, this is called to see if the specific method is OK.
		override public bool CheckMethodAllowed( out MethodInfo mi, Type declaringType, String name, Serializee [] parametersIn, Serializee [] genericArgumentsIn, String fullSignature )
		{
			mi = null;

			if( name.Contains( "Invoke" ) ) return false;

			// UnityEngine.Application.OpenURL opens an arbitrary URL in the native
			// browser — the exact payload behind the reported prop exploit. Deny it
			// explicitly so this never works from cilbox regardless of how the
			// Application type ends up whitelisted.
			if( declaringType == typeof(UnityEngine.Application) && (
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
				name.StartsWith( "Load", StringComparison.Ordinal ) ) )
				return false;

			// UnityEngine.Object.Instantiate spawns a prefab tree verbatim, so the clone
			// can carry UnityEvents that execute outside the sandbox (e.g. Button.onClick
			// -> Application.OpenURL). Redirect every Instantiate variant through the
			// sanitizing shim: it spawns under a disabled host, scrubs disallowed
			// components via the prop content-police selector, and kills all persistent
			// UnityEvent listeners before the clone becomes active in hierarchy.
			if( declaringType == typeof(UnityEngine.Object) &&
				( name == "Instantiate" || name == "InstantiateAsync" ) )
			{
				mi = Basis.Shims.BasisCilboxInstantiateShim.ResolveShim(
					usage, name, parametersIn, genericArgumentsIn, fullSignature );
				return mi != null;
			}

			// SendMessage / BroadcastMessage / AddComponent reach behaviours by name and
			// bypass cilbox sanitisation. Block them on every Unity entrypoint.
			if( declaringType == typeof(UnityEngine.GameObject) && (
				name == "AddComponent" ||
				name == "SendMessage" ||
				name == "SendMessageUpwards" ||
				name == "BroadcastMessage" ) )
				return false;
			if( declaringType == typeof(UnityEngine.Component) && (
				name == "SendMessage" ||
				name == "SendMessageUpwards" ||
				name == "BroadcastMessage" ) )
				return false;
			if( declaringType == typeof(UnityEngine.Animator) && (
				name == "GetBehaviour" ||
				name == "GetBehaviours" ) )
				return false;

			if( methodWhitelist.TryGetValue( declaringType, out var allowed ) )
			{
				if( !allowed.Contains( name ) ) return false;
			}

			return true;
		}

        public override bool GetTypeOverride(string sType, out Type t)
        {
			switch(sType)
			{
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
	}
}
