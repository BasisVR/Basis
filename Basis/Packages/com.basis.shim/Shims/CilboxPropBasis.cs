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
		};

		protected override HashSet<string> ExtraWhiteListType => extraWhiteListType;
		protected override HashSet<string> ExtraWhiteListFields => extraWhiteListFields;
		protected override Dictionary<Type, HashSet<string>> ExtraMethodWhitelist => extraMethodWhitelist;

		static readonly HashSet<string> mergedWhiteListType = MergeTypes(extraWhiteListType);
		public static HashSet<string> GetWhiteListTypes() => mergedWhiteListType;
	}
}
