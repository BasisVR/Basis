using UnityEngine;
using System.Collections.Generic;
using System;
using System.Collections.Specialized;
using System.Collections;
using System.Runtime.InteropServices;
using System.Reflection;

namespace Cilbox
{
	[CilboxTarget]
	public class CilboxScene : Cilbox
	{
		static HashSet<String> whiteListType = new HashSet<String>(){
			"Cilbox.CilboxPublicUtils",
			"System.Array",
			"System.BitConverter", // HMMMMMMMMM SUSSY
			"System.Boolean",
			"System.Byte",
			"System.Char",
			"System.Convert", // HMMMMMMMMM SUSSY
			"System.Collections.Generic.Dictionary",
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
			"UnityEngine.AudioSource",
			"UnityEngine.AudioClip",
			"UnityEngine.Component",
			"UnityEngine.Color",
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
			"UnityEngine.Time",
			"UnityEngine.Texture",
			"UnityEngine.UI.Button+ButtonClickedEvent",
			"UnityEngine.UI.Button",
			"UnityEngine.UI.InputField",
			"UnityEngine.UI.InputField+OnChangeEvent",
			"UnityEngine.UI.Scrollbar",
			"UnityEngine.UI.Selectable",
			"UnityEngine.UI.Slider",
			"UnityEngine.UI.Text",
			"UnityEngine.TextAsset",
			"UnityEngine.Texture2D",
			"UnityEngine.Transform",
			"UnityEngine.Vector4",
			"UnityEngine.Vector3",
		};

		static public HashSet<String> GetWhiteListTypes() { return whiteListType; }

		// This is called by CilboxUsage to decide of a type is allowed.
		// If a type is allowed, by defalt it is all allowed.
		override public bool CheckTypeAllowed( String sType )
		{
			return whiteListType.Contains( sType );
		}

		// After a type is allowed, this is called to see if the specific method is OK.
		override public bool CheckMethodAllowed( out MethodInfo mi, Type declaringType, String name, Serializee [] parametersIn, Serializee [] genericArgumentsIn, String fullSignature )
		{
			mi = null;

			// You're allowed to get access to the constructor, nothing else.
			// We could selectively open up more methods on MonoBehaviour.
			if( declaringType == typeof(UnityEngine.MonoBehaviour) && name != ".ctor" ) return false;

			if( declaringType == typeof(UnityEngine.Events.UnityAction) && name != ".ctor" ) return false;

			if( declaringType == typeof(UnityEngine.GameObject) &&
				( name != "SetActive" && name != "GetComponents" ) ) return false;

			if( declaringType == typeof(System.Type) ) return false;

			if( name.Contains( "Invoke" ) ) return false;
			return true;
		}
	}
}
