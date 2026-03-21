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
			"Basis.BasisNetworkBehaviour",
			"Basis.Network.Core.DeliveryMethod",
			"Basis.SafeUtil",
			"Basis.Scripts.BasisSdk.Players.BasisLocalPlayer",
			"Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer",
			"Basis.BasisUrl",
			"Basis.IBasisImageDownload",
			"BasisNetworkCommon+EventTiming",
			"BasisSDKMirror",
			"BasisSDKMirror+MirrorClearFlags",
			"Basis.Shims.*",

			// Cilbox types
			"Cilbox.CilboxPublicUtils",

			// System types
			"System.Action",
			"System.Array",
			"System.BitConverter", // HMMMMMMMMM SUSSY
			"System.Boolean",
			"System.Byte",
			"System.Buffer",
			"System.Char",
			"System.Collections.Generic.*",
			"System.Convert", // HMMMMMMMMM SUSSY
			"System.DateTime",
			"System.DayOfWeek",
			"System.Delegate",
			"System.Diagnostics.Stopwatch",
			"System.Double",
			"System.Exception",
			"System.Guid",
			"System.IDisposable",
			"System.Int*",
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
			"System.StringComparison",
			"System.TimeSpan",
			"System.UInt*",
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
			"UnityEngine.RenderTextureFormat",
			"UnityEngine.Rendering.AmbientMode",
			"UnityEngine.RenderSettings",
			"UnityEngine.Shader",
			"UnityEngine.TextAsset",
			"UnityEngine.Texture",
			"UnityEngine.Texture2D",
			"UnityEngine.Texture2DArray",
			"UnityEngine.Time",
			"UnityEngine.Transform",
			"UnityEngine.UI.*",
			"UnityEngine.Vector*",
        };

		static HashSet<String> whiteListFields = new HashSet<String>(){
			// Basis fields
			"Basis.BasisNetworkBehaviour.CurrentOwnerId",
			"Basis.BasisNetworkBehaviour.IsOwnedLocallyOnServer",
			"Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkPlayer.playerId",

			// Unity fields
			"UnityEngine.Vector*.x",
			"UnityEngine.Vector*.y",
			"UnityEngine.Vector*.z",
			"UnityEngine.Vector*.w",

			// System fields
			"System.Array.*",
			"System.String.*",
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
		static Dictionary<Type, HashSet<string>> whiteListMethods = new Dictionary<Type, HashSet<string>>()
		{
			{ typeof(UnityEngine.MonoBehaviour),       new HashSet<string>{ ".ctor" } },
			{ typeof(UnityEngine.Events.UnityAction),  new HashSet<string>{ ".ctor" } },
			{ typeof(UnityEngine.GameObject),          new HashSet<string>{ nameof(GameObject.SetActive), nameof(GameObject.GetComponents) } },
			{ typeof(System.Type),                     new HashSet<string>() }, // nothing allowed
			{ typeof(BasisNetworkPlayer), new HashSet<string> {
				typeof(BasisNetworkPlayer).GetProperty(nameof(BasisNetworkPlayer.playerId)).GetGetMethod().Name,
				typeof(BasisNetworkPlayer).GetProperty(nameof(BasisNetworkPlayer.LocalPlayer)).GetGetMethod().Name,
				typeof(BasisNetworkPlayer).GetProperty(nameof(BasisNetworkPlayer.Player)).GetGetMethod().Name,
				typeof(BasisNetworkPlayer).GetProperty(nameof(BasisNetworkPlayer.displayName)).GetGetMethod().Name,
				} },
			{ typeof(BasisLocalPlayer), new HashSet<string>{ nameof(BasisLocalPlayer.GetPositionAndRotation),
				nameof(BasisLocalPlayer.Teleport),
				nameof(BasisLocalPlayer.Respawn),
				$"get_{nameof(BasisLocalPlayer.Instance)}", }},
			{ typeof(Buffer), new HashSet<string>{ "BlockCopy" } },
			{ typeof(BitConverter), new HashSet<string>{ "GetBytes", "ToInt32", "ToUInt32" } },
			{ typeof(Convert), new HashSet<string>{ "ToInt32", "ToUInt32", "ToSingle", "ToDouble", "ToString" ,"ToBase64String", "FromBase64String"} },
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
				default:
					t = null;
					return false;
			}
        }
	}
}