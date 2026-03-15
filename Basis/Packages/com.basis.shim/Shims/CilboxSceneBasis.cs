using UnityEngine;
using System.Collections.Generic;
using System;
using System.Collections.Specialized;
using System.Collections;
using System.Runtime.InteropServices;
using System.Reflection;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;

namespace Cilbox
{
	[CilboxTarget]
	public class CilboxSceneBasis : Cilbox
	{
		static HashSet<String> whiteListType = new HashSet<String>(){
			// Basis types
			"Basis.BasisImageDownloader",
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
			"Basis.BasisUrl",
			"Basis.IBasisImageDownload",
			"BasisNetworkCommon+EventTiming",
			"BasisSDKMirror",
			"BasisSDKMirror+MirrorClearFlags",

			// Cilbox types
			"Cilbox.CilboxPublicUtils",

			// System types
			"System.Action",
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
			"System.Delegate",
			"System.Diagnostics.Stopwatch",
			"System.Double",
			"System.Exception",
			"System.Guid",
			"System.IDisposable",
			"System.Int16",
			"System.Int32",
			"System.Int64",
			"System.IO.BinaryReader",
			"System.IO.BinaryWriter",
			"System.IO.MemoryStream",
			"System.IO.Stream",
			"System.Math",
			"System.MathF",
			"System.Object",
			"System.Random",
			"System.Single",
			"System.String",
			"System.TimeSpan",
			"System.UInt16",
			"System.UInt32",
			"System.UInt64",
			"System.ValueTuple",
			"System.Void",
			"<PrivateImplementationDetails>", // Probably remove me? But we need a way to handle string hashing.  We can do it with our own function but that's slower.

			// TMPro types
			"TMPro.TextMeshPro",
			"TMPro.TextMeshProUGUI",
			"TMPro.TMP_Text",

			// Unity types
			"UnityEngine.AudioClip",
			"UnityEngine.AudioSource",
			"UnityEngine.Behaviour",
			"UnityEngine.BoxCollider",
			"UnityEngine.CapsuleCollider",
			"UnityEngine.Collider",
			"UnityEngine.Color",
			"UnityEngine.Component",
			"UnityEngine.CustomRenderTexture",
			"UnityEngine.Debug",
			"UnityEngine.DynamicGI",
			"UnityEngine.Events.UnityAction",
			"UnityEngine.Events.UnityEvent",
			"UnityEngine.EventSystems.BaseEventData",
			"UnityEngine.EventSystems.EventTrigger",
			"UnityEngine.EventSystems.EventTrigger+Entry",
			"UnityEngine.EventSystems.EventTrigger+TriggerEvent",
			"UnityEngine.EventSystems.EventTriggerType",
			"UnityEngine.GameObject",     // Hyper restrictive.
			"UnityEngine.Gradient",
			"UnityEngine.LayerMask",
			"UnityEngine.Light",
			"UnityEngine.Material",
			"UnityEngine.MaterialPropertyBlock",
			"UnityEngine.Mathf",
			"UnityEngine.MeshCollider",
			"UnityEngine.MeshRenderer",
			"UnityEngine.MonoBehaviour",   // Note this is needed for the 'ctor, but we can be very restrictive.
			"UnityEngine.Object",
			"UnityEngine.ParticleSystem",
			"UnityEngine.Quaternion",
			"UnityEngine.Random",
			"UnityEngine.Renderer",
			"UnityEngine.Rendering.AmbientMode",
			"UnityEngine.RenderSettings",
			"UnityEngine.Shader",
			"UnityEngine.TextAsset",
			"UnityEngine.Texture",
			"UnityEngine.Texture2D",
			"UnityEngine.Texture2DArray",
			"UnityEngine.Time",
			"UnityEngine.Transform",
			"UnityEngine.UI.Button",
			"UnityEngine.UI.Button+ButtonClickedEvent",
			"UnityEngine.UI.InputField",
			"UnityEngine.UI.InputField+OnChangeEvent",
			"UnityEngine.UI.RawImage",
			"UnityEngine.UI.Scrollbar",
			"UnityEngine.UI.Selectable",
			"UnityEngine.UI.Slider",
			"UnityEngine.UI.Text",
			"UnityEngine.UI.Toggle",
			"UnityEngine.UI.Toggle+ToggleEvent",
			"UnityEngine.Vector2",
			"UnityEngine.Vector3",
			"UnityEngine.Vector4",
        };

		static HashSet<String> whiteListFields = new HashSet<String>(){
			// Basis fields
			"Basis.BasisNetworkBehaviour.CurrentOwnerId",
			"Basis.BasisNetworkBehaviour.IsOwnedLocallyOnServer",
			"Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.playerId",

			// Unity fields
			"UnityEngine.EventSystems.EventTrigger+Entry.eventID",
			"UnityEngine.EventSystems.EventTrigger+Entry.callback",
			"UnityEngine.UI.Toggle.onValueChanged",
			"UnityEngine.Vector3.x",
			"UnityEngine.Vector3.y",
			"UnityEngine.Vector3.z",
			"UnityEngine.Vector4.x",
			"UnityEngine.Vector4.y",
			"UnityEngine.Vector4.z",
			"UnityEngine.Vector4.w",
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
		static Dictionary<Type, HashSet<string>> whiteListMethods = new Dictionary<Type, HashSet<string>>()
		{
			{ typeof(UnityEngine.MonoBehaviour),       new HashSet<string>{ ".ctor" } },
			{ typeof(UnityEngine.Events.UnityAction),  new HashSet<string>{ ".ctor" } },
			{ typeof(UnityEngine.GameObject),          new HashSet<string>{ nameof(GameObject.SetActive), nameof(GameObject.GetComponents) } },
			{ typeof(System.Type),                     new HashSet<string>() }, // nothing allowed
			{ typeof(BasisNetworkPlayer), new HashSet<string> {
				typeof(BasisNetworkPlayer).GetProperty(nameof(BasisNetworkPlayer.playerId)).GetGetMethod().Name,
				typeof(BasisNetworkPlayer).GetProperty(nameof(BasisNetworkPlayer.LocalPlayer)).GetGetMethod().Name,
				} },
			{ typeof(BasisLocalPlayer), new HashSet<string>{ nameof(BasisLocalPlayer.GetPositionAndRotation),
				nameof(BasisLocalPlayer.Teleport),
				nameof(BasisLocalPlayer.Respawn),
				$"get_{nameof(BasisLocalPlayer.Instance)}", }},
		};

		// After a type is allowed, this is called to see if the specific method is OK.
		override public bool CheckMethodAllowed( out MethodInfo mi, Type declaringType, String name, Serializee [] parametersIn, Serializee [] genericArgumentsIn, String fullSignature )
		{
			mi = null;

			if( name.Contains( "Invoke" ) ) return false;

			if( whiteListMethods.TryGetValue( declaringType, out var allowed ) )
			{
				if( !allowed.Contains( name ) ) return false;
			}

			return true;
		}

        public override bool GetComponentTypeOverride(string sType, out Type t)
        {
			t = null;
            return false;
        }
	}
}
