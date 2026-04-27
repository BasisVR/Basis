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
	public class CilboxAvatarBasis : Cilbox
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
			"HVR.Basis.Comms.OSC.OscData",
			"HVR.Basis.Comms.OSC.OscDataKind",
			"HVR.Basis.Comms.OSC.OscMessage",
			"Basis.Shims.BasisNet*", // Restrictive, only used as a type and for events.
			"Basis.Shims.BasisAvatarShim",
			"Basis.Shims.BasisAvatarShim+OnReady",
			"Basis.Shims.BasisAvatarShim+AvatarReadyEvent",
			"Basis.Shims.BasisCilboxInstantiateShim", // Restrictive, only used as a type and for Instantiate methods.
			"Basis.Shims.BasisOsc",
			"Basis.Shims.BasisOsc+OscMessageEvent",
			"Basis.Shims.BasisOsc+OscValueEvent",
			"Basis.Shims.BasisDebugPropsShim", // Restrictive, only used as a type and for logging methods.

			// Cilbox types
			"Cilbox.CilboxPublicUtils",

			// System types
			"System.Array",
			"System.BitConverter", // HMMMMMMMMM SUSSY
			"System.Boolean",
			"System.Buffer",
			"System.Byte",
			"System.SByte",
			"System.Char",
			"System.Collections.Generic.*",
			"System.Convert", // HMMMMMMMMM SUSSY
			"System.DateTime",
			"System.DateTimeOffset",
			"System.DayOfWeek",
			"System.Delegate",
			"System.Diagnostics.Stopwatch",
			"System.Double",
			"System.Exception",
			"System.Float",
			"System.Int*",
			"System.Long",
			"System.ULong",
			"System.Math",
			"System.MathF",
			"System.Object",
			"System.Short",
			"System.Ushort",
			"System.Single",
			"System.String",
			"System.StringComparison",
			"System.TimeSpan",
			"System.Text.Encoding",
			"System.UInt16",
			"System.UInt32",
			"System.UInt*",
			"System.Void",
			"<PrivateImplementationDetails>", // Probably remove me? But we need a way to handle string hashing.  We can do it with our own function but that's slower.

			// Unity types
			"UnityEngine.Animator",
			"UnityEngine.AnimatorStateInfo",
			"UnityEngine.AnimatorTransitionInfo",
            "UnityEngine.AudioClip",
			"UnityEngine.AudioSource",
			"UnityEngine.Color",
			"UnityEngine.Component",
			"UnityEngine.Events.UnityAction",
			"UnityEngine.Events.UnityEvent",
			"UnityEngine.GameObject",     // Hyper restrictive.
			"UnityEngine.Material",
			"UnityEngine.MaterialPropertyBlock",
			"UnityEngine.Mathf",
			"UnityEngine.MeshRenderer",
			"UnityEngine.MonoBehaviour",   // Note this is needed for the 'ctor, but we can be very restrictive.
			"UnityEngine.Object",
			"UnityEngine.Random",
			"UnityEngine.Renderer",
			"UnityEngine.TextAsset",
			"UnityEngine.Texture",
			"UnityEngine.Texture2D",
			"UnityEngine.Time",
			"UnityEngine.Transform",
			"UnityEngine.Quaternion",
			"UnityEngine.Rigidbody",
			"UnityEngine.RenderTexture",
			"UnityEngine.RenderTextureFormat",
			"UnityEngine.SkinnedMeshRenderer",
			"UnityEngine.UI.*",
			"UnityEngine.Vector*",
			"UnityEngine.UI.InputField",
			"UnityEngine.UI.InputField+OnChangeEvent",
			"UnityEngine.UI.Scrollbar",
			"UnityEngine.UI.Selectable",
			"UnityEngine.UI.Slider",
			"UnityEngine.UI.Text",
			"UnityEngine.Vector2",
			"UnityEngine.Vector3",
			"UnityEngine.Vector4",
		};

		static HashSet<String> whiteListFields = new HashSet<String>(){
			// Unity fields
			"UnityEngine.Vector*.x",
			"UnityEngine.Vector*.y",
			"UnityEngine.Vector*.z",
			"UnityEngine.Vector*.w",
			"UnityEngine.Quaternion*",

			// System fields
			"System.Array.*",
			"System.String.*",
			

			// Basis types
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
			"Basis.Scripts.BasisSdk.Interactions.BasisPickupInteractable.OnPickupUse",
            "Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject.OnInteractStartEvent",
            "Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject.OnInteractEndEvent",
			"Basis.BasisNetworkBehaviour.CurrentOwnerId",
			"Basis.BasisNetworkBehaviour.IsOwnedLocallyOnServer",
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
			{ typeof(Buffer), new HashSet<string>{ "BlockCopy" } },
			{ typeof(System.Type),                     new HashSet<string>() }, // nothing allowed
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
			if( declaringType == typeof(UnityEngine.Application) && name == "OpenURL" )
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
