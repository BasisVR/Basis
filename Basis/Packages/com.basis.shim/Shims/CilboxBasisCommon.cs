using UnityEngine;
using System.Collections.Generic;
using System;
using System.Reflection;

namespace Cilbox
{
	public abstract class CilboxBasisCommon : Cilbox
	{
		protected static readonly HashSet<string> commonWhiteListType = new HashSet<string>(){
			// Text Mesh Pro types
			"TMPro.*",

			// Basis types
			"BasisNetworkContentBase",
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
			// Roster access plus the pose reads IBasisPlayer withholds. Returns players as
			// IBasisPlayer, and poses as copied Vector3/Quaternion — never a Transform.
			"Basis.Shims.BasisPlayersShim",
			// Late-latch callback. Auto-added by GetComponent<T> since it derives from CilboxShim.
			"Basis.Shims.BasisBeforeRenderShim",
			"Basis.Scripts.BasisSdk.Players.BasisLocalPlayer",
			"Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer",
			"HVR.Basis.Comms.OSC*",

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
		};

		public override bool CheckTypeAllowed(string sType)
		{
			if (sType != null && hardDeniedTypes.Contains(sType)) return false;
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
			return false;
		}

		public override bool CheckFieldAllowed(string sType, string sFieldName)
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
			return false;
		}

		public override bool CheckMethodAllowed(out MethodInfo mi, Type declaringType, string name, Serializee[] parametersIn, Serializee[] genericArgumentsIn, string fullSignature)
		{
			mi = null;

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

			// NativeArray<T> only bounds-checks its indexer under ENABLE_UNITY_COLLECTIONS_CHECKS,
			// which release players do not define. Restricted to the members that copy out.
			if (declaringType != null && declaringType.IsGenericType &&
				declaringType.GetGenericTypeDefinition().FullName == "Unity.Collections.NativeArray`1")
			{
				return name == "get_Length" || name == "get_IsCreated" ||
					   name == "ToArray" || name == "CopyTo" ||
					   name == "Equals" || name == "GetHashCode" || name == "ToString";
			}

			bool inCommon = commonMethodWhitelist.TryGetValue(declaringType, out var commonAllowed);
			bool inExtra = ExtraMethodWhitelist.TryGetValue(declaringType, out var extraAllowed);
			if (inCommon || inExtra)
			{
				bool allowed = (inCommon && commonAllowed.Contains(name)) || (inExtra && extraAllowed.Contains(name));
				if (!allowed) return false;
			}

			return true;
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
