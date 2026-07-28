using UnityEngine;
using System.Collections.Generic;
using System;

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
            "Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer",
            "Basis.Scripts.Networking.BasisNetworkPlayers", // Restrictive, see method whitelist (TryGetPlayerByUUID only).
            "Basis.Scripts.BasisSdk.BasisAvatar",           // Restrictive, see method whitelist (empty) + Animator field only.
            "Basis.Scripts.Drivers.BasisLocalAvatarDriver", // Restrictive, see method whitelist (empty) + HeadScale field only.
            "BasisPickupSyncNetworking",                    // Concrete runtime type of the pickup's BasisNetworkBehaviour field; held only, methods blocked (see below).

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
			// BasisPickupSyncNetworking: type-whitelisted only so the pickupNet reference survives the
			// contraband check; block every direct method. Legitimate calls (TryGetIdentifier) resolve
			// through the base BasisNetworkContentBase / BasisNetworkBehaviour whitelist instead.
			{
				typeof(global::BasisPickupSyncNetworking),
				new HashSet<string>()
			},
		};

		protected override HashSet<string> ExtraWhiteListType => extraWhiteListType;
		protected override HashSet<string> ExtraWhiteListFields => extraWhiteListFields;
		protected override Dictionary<Type, HashSet<string>> ExtraMethodWhitelist => extraMethodWhitelist;

		static readonly HashSet<string> mergedWhiteListType = MergeTypes(extraWhiteListType);
		public static HashSet<string> GetWhiteListTypes() => mergedWhiteListType;
	}
}
