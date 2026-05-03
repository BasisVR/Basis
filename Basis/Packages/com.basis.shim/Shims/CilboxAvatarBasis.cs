using UnityEngine;
using System.Collections.Generic;
using System;

namespace Cilbox
{
	[CilboxTarget]
	public class CilboxAvatarBasis : CilboxBasisCommon
	{
		static readonly HashSet<string> extraWhiteListType = new HashSet<string>(){
			// Avatar-specific Basis shim types
			"Basis.Shims.BasisNet*", // Restrictive, only used as a type and for events.
			"Basis.Shims.BasisAvatarShim",
			"Basis.Shims.BasisAvatarShim+OnReady",
			"Basis.Shims.BasisAvatarShim+AvatarReadyEvent",
			"Basis.Shims.BasisCilboxInstantiateShim",
			"Basis.Shims.BasisDebugPropsShim",

			// HVR Vixxy
			"HVR.Vixxy.HVRVixxyMenuItem", // Restrictive, see method whitelist.
		};

		static readonly HashSet<string> extraWhiteListFields = new HashSet<string>(){
			"Basis.Shims.BasisAvatarShim.Animator",
			"Basis.Shims.BasisAvatarShim.FaceVisemeMesh",
			"Basis.Shims.BasisAvatarShim.FaceBlinkMesh",
			"Basis.Shims.BasisAvatarShim.AvatarEyePosition",
			"Basis.Shims.BasisAvatarShim.AvatarMouthPosition",
			"Basis.Shims.BasisAvatarShim.FaceVisemeMovement",
			"Basis.Shims.BasisAvatarShim.BlinkViseme",
			"Basis.Shims.BasisAvatarShim.laughterBlendTarget",
			"Basis.Shims.BasisAvatarShim.AnimatorHumanScale",
			"Basis.Shims.BasisAvatarShim.IsOwnedLocally",
			"Basis.Shims.BasisAvatarShim.HumanScale",
			"Basis.Scripts.BasisSdk.BasisProcessingAvatarOptions.doNotAutoRenameBones",
		};

		static readonly Dictionary<Type, HashSet<string>> extraMethodWhitelist = new Dictionary<Type, HashSet<string>>()
		{
			{ typeof(UnityEngine.GameObject), new HashSet<string>{
				typeof(GameObject).GetProperty(nameof(GameObject.transform)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.activeSelf)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.activeInHierarchy)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.layer)).GetGetMethod().Name,
				} },
			{ typeof(UnityEngine.LayerMask), new HashSet<string>{
				".ctor",
				"get_value",
				"op_Implicit",
				} },
			{ typeof(HVR.Vixxy.HVRVixxyMenuItem), new HashSet<string>{
				nameof(HVR.Vixxy.HVRVixxyMenuItem.GetValue),
				nameof(HVR.Vixxy.HVRVixxyMenuItem.ApplyValue),
				} },
		};

		protected override HashSet<string> ExtraWhiteListType => extraWhiteListType;
		protected override HashSet<string> ExtraWhiteListFields => extraWhiteListFields;
		protected override Dictionary<Type, HashSet<string>> ExtraMethodWhitelist => extraMethodWhitelist;

		static readonly HashSet<string> mergedWhiteListType = MergeTypes(extraWhiteListType);
		public static HashSet<string> GetWhiteListTypes() => mergedWhiteListType;

		protected override bool ExtraGetTypeOverride(string sType, out Type t)
		{
			switch (sType)
			{
				case "Basis.Scripts.BasisSdk.BasisAvatar":
					t = typeof(Basis.Shims.BasisAvatarShim);
					return true;
				case "Basis.Scripts.BasisSdk.BasisAvatar+OnReady":
					t = typeof(Basis.Shims.BasisAvatarShim.OnReady);
					return true;
				default:
					t = null;
					return false;
			}
		}
	}
}
