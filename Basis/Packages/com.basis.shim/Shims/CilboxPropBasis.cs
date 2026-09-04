using UnityEngine;
using System.Collections.Generic;
using System;
using System.Reflection;

namespace Cilbox
{
	[CilboxTarget]
	public class CilboxPropBasis : CilboxBasisCommon
	{
		static readonly HashSet<string> extraWhiteListType = new HashSet<string>(){
			// Prop-specific Basis types
			"Basis.Shims.*",
			"Basis.BasisImageDownloader",
			"Basis.IBasisImageDownload",
			"Basis.BasisStringDownloader",
			// Basis media-player components. BasisMediaPlayer keeps its native API; LoadUrl
			// enforces URL approval internally, while unsafe lower-level entry points are blocked below.
			"BasisMediaPlayer",
			"BasisMediaPlayer+QueueOverflowPolicy",
			"BasisMediaPlayerStatus",
			"BasisVideoBufferMode",
			"BasisMediaPlayerAudio",
			"BasisMediaPlayerStreaming",
			"BasisVideoMaterialOutput",
			"BasisVideoMaterialOutput+MaterialTarget",
			"BasisVideoProjectionMode",
			"BasisVideoStereoEye",
			"BasisVideoAspectMode",
			"BasisVideoPicture",
			"Basis.IBasisStringDownload",
            "Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer",
            "Basis.Scripts.Networking.BasisNetworkPlayers", // Restrictive, see method whitelist (TryGetPlayerByUUID only).
            "Basis.Scripts.BasisSdk.BasisAvatar",           // Restrictive, see method whitelist (empty) + Animator field only.
            "Basis.Scripts.Drivers.BasisLocalAvatarDriver", // Restrictive, see method whitelist (empty) + HeadScale field only.
            "BasisPickupSyncNetworking",                    // Concrete runtime type of the pickup's BasisNetworkBehaviour field; held only, methods blocked (see below).
			"Basis.Scripts.Drivers.BasisLocalCameraDriver", // read-only static CameraInstance (field below)
			// Example-package button (com.basis.examples). Restrictive, see method whitelist below:
			// only set_ButtonDown is reachable, so a prop can wire a handler but not read the current
			// one, trigger ButtonUp, or call TriggerButtonDown()/TriggerButtonUp() directly.
			"Basis.Scripts.BasisSdk.Interactions.BasisInteractableButton",
			// ButtonDown/ButtonUp's delegate type. Needed to construct/assign a handler; the delegate's
			// own Invoke is unreachable regardless (CheckMethodAllowed blanket-denies any "Invoke" method).
			"Basis.Scripts.BasisSdk.Interactions.BasisInteractableButton+ClickEvent",
			"Basis.Scripts.BasisSdk.Players.BasisTeleportMode",

			// System IO
			"System.IO.BinaryReader",
			"System.IO.BinaryWriter",
			"System.IO.MemoryStream",
			"System.IO.Stream",
			"System.IO.SeekOrigin",

			// Unity types - rendering / mesh extras
			"UnityEngine.Graphics",
			"UnityEngine.Rendering.AsyncGPUReadback",
			"UnityEngine.Rendering.AsyncGPUReadbackRequest",
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

			// Unity types - camera
			"UnityEngine.Camera",
			"UnityEngine.Camera+MonoOrStereoscopicEye",
			"UnityEngine.Camera+StereoscopicEye",

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
			"UnityEngine.Physics",
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
		};

		static readonly HashSet<string> extraWhiteListFields = new HashSet<string>(){
			// Unity physics struct fields
			"UnityEngine.ContactPoint.*",
			"UnityEngine.JointAngleLimits2D.*",
			"UnityEngine.JointDrive.*",
			"UnityEngine.JointLimits.*",
			"UnityEngine.JointMotor.*",
			"UnityEngine.JointSpring.*",
			"UnityEngine.SoftJointLimit.*",
			"UnityEngine.SoftJointLimitSpring.*",
			"BasisNetworkContentBase+BasisContentInformation.*",
			// Read-only access to the avatar's Animator so mirror scripts can clone the humanoid root.
			"Basis.Scripts.BasisSdk.BasisAvatar.Animator",
			// Read-only local head scale (Vector3) so cloned mirror heads match the local player.
			"Basis.Scripts.Drivers.BasisLocalAvatarDriver.HeadScale",
			"Basis.Scripts.Drivers.BasisLocalCameraDriver.CameraInstance",
			// Media-player configuration is safe to read/write from a prop. Network/file
			// entry points are methods and are gated separately in CheckMethodAllowed.
			"BasisMediaPlayer.*",
			"BasisMediaPlayerAudio.*",
			// Streaming URLs/platform selection are script-configurable, but ConfigureOnStart
			// is intentionally withheld so Cilbox cannot re-enable content auto-start.
			"BasisMediaPlayerStreaming.StreamUrl",
			"BasisMediaPlayerStreaming.AutoSelectPerPlatform",
			"BasisMediaPlayerStreaming.PcUrl",
			"BasisMediaPlayerStreaming.QuestUrl",
			"BasisVideoMaterialOutput.*",
		};

		static readonly Dictionary<Type, HashSet<string>> extraMethodWhitelist = new Dictionary<Type, HashSet<string>>()
		{
			{ typeof(UnityEngine.GameObject), new HashSet<string>{
				typeof(GameObject).GetProperty(nameof(GameObject.transform)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.activeSelf)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.activeInHierarchy)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.layer)).GetGetMethod().Name,
				} },
			{ typeof(UnityEngine.Graphics), new HashSet<string>{ "Blit" } },
			// Scripted player input is an AVATAR-box feature; Basis.Shims.* is type-whitelisted here, so
			// block every method to keep prop scripts out of the local player's locomotion.
			{ typeof(Basis.Shims.BasisPlayspaceInputShim), new HashSet<string>() },
			{ typeof(Basis.Shims.BasisVixxyShim), new HashSet<string>() },
			// Jiggle grab/touch events, so a prop with jiggle can answer being handled the same way
			// an avatar or a world object can.
			{ typeof(Basis.Shims.BasisJiggleEventShim), new HashSet<string>{
				nameof(Basis.Shims.BasisJiggleEventShim.Rebind),
				nameof(Basis.Shims.BasisJiggleEventShim.GetJiggleRigCount),
				nameof(Basis.Shims.BasisJiggleEventShim.GetJiggleRigName),
				nameof(Basis.Shims.BasisJiggleEventShim.FindJiggleRig),
				} },
			// Local permission changes. Same shape as the jiggle events above: fetching the
			// component is the opt-in, Rebind is only for proxies that appear late, and the
			// callback is resolved by name off the script.
			{ typeof(Basis.Shims.BasisPermissionEventShim), new HashSet<string>{
				nameof(Basis.Shims.BasisPermissionEventShim.Rebind),
				} },
			{ typeof(UnityEngine.Rendering.AsyncGPUReadback), new HashSet<string>{ "Request" } },
			{ typeof(BitConverter), new HashSet<string>{
				"GetBytes", "ToBoolean", "ToChar", "ToDouble", "ToInt16", "ToInt32",
				"ToInt64", "ToSingle", "ToString", "ToUInt16", "ToUInt32", "ToUInt64",
				"DoubleToInt64Bits", "Int64BitsToDouble", "SingleToInt32Bits", "Int32BitsToSingle" } },
			{ typeof(Convert), new HashSet<string>{
				"ToInt16", "ToInt32", "ToInt64", "ToUInt16", "ToUInt32", "ToUInt64",
				"ToByte", "ToSByte", "ToBoolean", "ToChar", "ToSingle", "ToDouble",
				"ToString", "ToBase64String", "FromBase64String",
				"ToDateTime", "ToDecimal" } },
			{ typeof(UnityEngine.Camera), new HashSet<string>{
				"GetStereoNonJitteredProjectionMatrix",
				"GetStereoProjectionMatrix",
				"GetStereoViewMatrix",
				"ScreenPointToRay",
				"ScreenToViewportPoint",
				"ScreenToWorldPoint",
				"ViewportPointToRay",
				"ViewportToScreenPoint",
				"ViewportToWorldPoint",
				"WorldToScreenPoint",
				"WorldToViewportPoint",
				"get_aspect",
				"get_cameraToWorldMatrix",
				"get_farClipPlane",
				"get_fieldOfView",
				"get_main",
				"get_nearClipPlane",
				"get_orthographic",
				"get_orthographicSize",
				"get_pixelHeight",
				"get_pixelWidth",
				"get_projectionMatrix",
				"get_stereoActiveEye",
				"get_stereoConvergence",
				"get_stereoEnabled",
				"get_stereoSeparation",
				"get_worldToCameraMatrix",
				} },
			{ typeof(UnityEngine.Physics), new HashSet<string>{
				"BoxCast", "BoxCastAll", "BoxCastNonAlloc",
				"CapsuleCast", "CapsuleCastAll", "CapsuleCastNonAlloc",
				"CheckBox", "CheckCapsule", "CheckSphere",
				"ClosestPoint",
				"ComputePenetration",
				"GetIgnoreLayerCollision",
				"Linecast",
				"OverlapBox", "OverlapBoxNonAlloc",
				"OverlapCapsule", "OverlapCapsuleNonAlloc",
				"OverlapSphere", "OverlapSphereNonAlloc",
				"Raycast", "RaycastAll", "RaycastNonAlloc",
				"SphereCast", "SphereCastAll", "SphereCastNonAlloc",
				"get_autoSimulation", "get_autoSyncTransforms", "get_gravity",
				"get_queriesHitBackfaces", "get_queriesHitTriggers",
				"get_sleepThreshold", "get_defaultContactOffset",
				} },
			{
				typeof(Basis.Scripts.Networking.BasisNetworkConnection),
				new HashSet<string>
				{
					"get_LocalPlayerIsConnected",
				}
			},
			{
				typeof(BasisNetworkContentBase),
				new HashSet<string>
				{
					"TryGetIdentifier",
					"get_CreatorUUID",
				}
			},
			// The prop box can already correlate a UUID to a player through TryGetPlayerByUUID,
			// so handing it the creator string directly grants nothing new.
			{
				typeof(BasisContent),
				new HashSet<string>
				{
					"CreatorUUID",
				}
			},
            {
				typeof(Basis.Scripts.Networking.BasisNetworkPlayers),
				new HashSet<string>
				{
					"TryGetPlayerByUUID",
				}
			},
			{
				typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer),
				new HashSet<string>
				{
					"get_IsLocal",
					"TryGetPlayer",
				}
			},
            {
				typeof(Basis.Scripts.BasisSdk.Players.IBasisPlayer),
				new HashSet<string>
				{
					"get_BasisAvatar",
					"get_IsLocal",
				}
			},
			// BasisAvatar: block every method; only the Animator field is reachable (field whitelist above).
			{
				typeof(Basis.Scripts.BasisSdk.BasisAvatar),
				new HashSet<string>()
			},
			// BasisLocalAvatarDriver: block every method; only the static HeadScale field is reachable (field whitelist above).
			{
				typeof(Basis.Scripts.Drivers.BasisLocalAvatarDriver),
				new HashSet<string>()
			},
			// BasisLocalCameraDriver: world-space stereo eye reads only (copied Vector3), plus the static CameraInstance field (whitelisted above); everything else blocked.
			{
				typeof(Basis.Scripts.Drivers.BasisLocalCameraDriver),
				new HashSet<string>
				{
					nameof(Basis.Scripts.Drivers.BasisLocalCameraDriver.LeftEyeWorldPosition),
					nameof(Basis.Scripts.Drivers.BasisLocalCameraDriver.RightEyeWorldPosition),
				}
			},
			// BasisPickupSyncNetworking: type-whitelisted only so the pickupNet reference survives the
			// contraband check; block every direct method. Legitimate calls (TryGetIdentifier) resolve
			// through the base BasisNetworkContentBase / BasisNetworkBehaviour whitelist instead.
			{
				typeof(global::BasisPickupSyncNetworking),
				new HashSet<string>()
			},
#if BASIS_HAS_EXAMPLES
			// BasisInteractableButton: only the setter a prop needs to wire its own "pressed" handler.
			// ButtonUp, both getters and TriggerButtonDown/Up are withheld until a script actually
			// needs them - get_ButtonDown/Up would hand back another script's live delegate.
			// Lives in com.basis.examples, which headless builds strip; the string whitelist entries
			// above stay unconditional and are simply unmatchable when the package is absent.
			{
				typeof(Basis.Scripts.BasisSdk.Interactions.BasisInteractableButton),
				new HashSet<string>
				{
					"set_ButtonDown",
				}
			},
#endif
		};

		protected override HashSet<string> ExtraWhiteListType => extraWhiteListType;
		protected override HashSet<string> ExtraWhiteListFields => extraWhiteListFields;
		protected override Dictionary<Type, HashSet<string>> ExtraMethodWhitelist => extraMethodWhitelist;

		public override bool CheckMethodAllowed(out MethodInfo mi, Type declaringType, string name,
			SerializedTypeDescriptor[] parametersIn, SerializedTypeDescriptor[] genericArgumentsIn, string fullSignature)
		{
			if (declaringType == typeof(global::BasisMediaPlayer))
			{
				// Do not let a prop bypass URL consent through source/file APIs or write
				// screenshots to disk. Normal playback controls, status, and events remain
				// available on the real BasisMediaPlayer component.
				if (name == nameof(global::BasisMediaPlayer.LoadLocalPath) ||
					name == nameof(global::BasisMediaPlayer.LoadSource) ||
					name == nameof(global::BasisMediaPlayer.LoadResolvedSource) ||
					name == nameof(global::BasisMediaPlayer.CaptureScreenshot) ||
					name == "set_Source" ||
					name == "set_Renderer")
				{
					mi = null;
					return false;
				}
			}

			return base.CheckMethodAllowed(out mi, declaringType, name, parametersIn, genericArgumentsIn, fullSignature);
		}

		static readonly HashSet<string> mergedWhiteListType = MergeTypes(extraWhiteListType);
		public static HashSet<string> GetWhiteListTypes() => mergedWhiteListType;
	}
}
