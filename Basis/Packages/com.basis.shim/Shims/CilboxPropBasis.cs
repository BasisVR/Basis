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
			// Basis types
			"Basis.Scripts.BasisSdk.Interactions.BasisPickUpUseMode",
			"Basis.Scripts.Device_Management.Devices.BasisInput", // Restrictive, only used as a type.
			"Basis.Scripts.BasisSdk.Interactions.BasisPickupInteractable", // Restrictive (See below), only access field.
			"Basis.Scripts.BasisSdk.Interactions.BasisInteractableObject", // Restrictive (See below), only access field.
			"Basis.BasisInteractableShim",
			"Basis.BasisInteractableShim+ClickEvent",
			"Basis.BasisNetworkBehaviour",
			"Basis.BasisNetworkShim",
			"Basis.BasisNetworkShim+NetworkMessageEvent",
			"Basis.BasisNetworkShim+NetworkReadyEvent",
			"Basis.BasisNetworkShim+OwnershipTransferEvent",
			"Basis.BasisNetworkShim+PlayerJoinedEvent",
			"Basis.BasisNetworkShim+PlayerLeftEvent",
			"Basis.BasisNetworkShim+ServerOwnershipDestroyedEvent",
			"Basis.Network.Core.DeliveryMethod",
			"Basis.SafeUtil",
			"Basis.Scripts.BasisSdk.Players.BasisLocalPlayer",
			"Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer",

			// Cilbox types
			"Cilbox.CilboxPublicUtils",

			// System types
			"System.Array",
			"System.BitConverter", // HMMMMMMMMM SUSSY
			"System.Boolean",
			"System.Byte",
			"System.Char",
			"System.Collections.Generic.Dictionary",
			"System.Collections.Generic.List",
			"System.Convert", // HMMMMMMMMM SUSSY
			"System.DateTime",
			"System.DayOfWeek",
			"System.Diagnostics.Stopwatch",
			"System.Double",
			"System.Exception",
			"System.Int16",
			"System.Int32",
			"System.Int64",
			"System.Math",
			"System.MathF",
			"System.Object",
			"System.Single",
			"System.String",
			"System.TimeSpan",
			"System.UInt16",
			"System.UInt32",
			"System.UInt64",
			"System.ValueTuple",
			"System.Void",
			"<PrivateImplementationDetails>", // Probably remove me? But we need a way to handle string hashing.  We can do it with our own function but that's slower.

			// Unity types
			"UnityEngine.Animator",
			"UnityEngine.AnimatorStateInfo",
            "UnityEngine.AudioClip",
			"UnityEngine.AudioSource",
			"UnityEngine.Color",
			"UnityEngine.Component",
			"UnityEngine.Collider",
			"UnityEngine.Collision",
			"UnityEngine.Debug",
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
			"UnityEngine.UI.Button",
			"UnityEngine.UI.Button+ButtonClickedEvent",
			"UnityEngine.UI.InputField",
			"UnityEngine.UI.InputField+OnChangeEvent",
			"UnityEngine.UI.Scrollbar",
			"UnityEngine.UI.Selectable",
			"UnityEngine.UI.Slider",
			"UnityEngine.UI.Text",
			"UnityEngine.Vector3",
			"UnityEngine.Vector4",
		};

		static HashSet<String> whiteListFields = new HashSet<String>(){
			// Unity fields
			"UnityEngine.Vector3.x",
			"UnityEngine.Vector3.y",
			"UnityEngine.Vector3.z",

			// Basis types
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
			return whiteListType.Contains( sType );
		}

		override public bool CheckFieldAllowed( String sType, String sFieldName )
		{
			if( !CheckTypeAllowed( sType ) ) return false;
			if( sType.Length < 1 || sFieldName.Length < 1 ) return false;
			if( !whiteListFields.Contains( sType + "." + sFieldName ) ) return false;
			return true;
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
				typeof(Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer).GetProperty("playerId").GetGetMethod().Name
				} },
			{ typeof(UnityEngine.GameObject),          new HashSet<string>{ 
				nameof(GameObject.SetActive), 
				nameof(GameObject.GetComponents), 
				typeof(GameObject).GetProperty(nameof(GameObject.transform)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.activeSelf)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.activeInHierarchy)).GetGetMethod().Name,
				typeof(GameObject).GetProperty(nameof(GameObject.layer)).GetGetMethod().Name,
				} },
			{ typeof(System.Type),                     new HashSet<string>() }, // nothing allowed
		};

		// After a type is allowed, this is called to see if the specific method is OK.
		override public bool CheckMethodAllowed( out MethodInfo mi, Type declaringType, String name, Serializee [] parametersIn, Serializee [] genericArgumentsIn, String fullSignature )
		{
			mi = null;

			if( name.Contains( "Invoke" ) ) return false;

			if( methodWhitelist.TryGetValue( declaringType, out var allowed ) )
			{
				if( !allowed.Contains( name ) ) return false;
			}

			return true;
		}
	}
}
