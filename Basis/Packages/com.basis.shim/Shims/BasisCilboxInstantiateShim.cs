using System;
using System.Reflection;
using Basis.Scripts.Device_Management;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
	// Sanitizing replacement for UnityEngine.Object.Instantiate when called
	// from inside a cilbox sandbox. Cilbox scripts can reference prefabs that
	// ship with arbitrary MonoBehaviours and persistent UnityEvent listeners;
	// Unity runs Awake/OnEnable the instant the clone becomes active in
	// hierarchy, so a Button.onClick wired to Application.OpenURL fires
	// outside the sandbox before we can react.
	//
	// This shim funnels every cilbox-initiated Instantiate through
	// ContentPoliceControl.ContentControl with UseContentRemoval=true and
	// ScrubPersistentUnityEvents=true. ContentPoliceControl parks the clone
	// under BasisDeviceManagement.Instance.CreationGameobject (inactive),
	// destroys disallowed components, disables dangerous persistent listeners,
	// then reparents/activates — matching the non-cilbox bundle-load path.
	public static class BasisCilboxInstantiateShim
	{
		// Called from CilboxPropBasis.CheckMethodAllowed / CilboxSceneBasis.CheckMethodAllowed
		// to resolve the shim overload that matches the original
		// UnityEngine.Object.Instantiate call site. Reuses the cilbox usage
		// helper so overload selection (including generics) matches exactly
		// what the interpreter would have picked against UnityEngine.Object.
		public static MethodInfo ResolveShim(
			CilboxUsage usage,
			String name,
			Serializee[] parametersIn,
			Serializee[] genericArgumentsIn,
			String fullSignature )
		{
			if( usage == null ) return null;
			if( name != "Instantiate" && name != "InstantiateAsync" ) return null;

			Type[] parameters = usage.TypeNamesToArrayOfNativeTypes( parametersIn );
			Type[] genericArguments = usage.TypeNamesToArrayOfNativeTypes( genericArgumentsIn );
			if( parameters == null ) return null;
			for( int i = 0; i < parameters.Length; i++ )
				if( parameters[i] == null ) return null;
			if( genericArguments == null ) genericArguments = Type.EmptyTypes;
			for( int i = 0; i < genericArguments.Length; i++ )
				if( genericArguments[i] == null ) return null;

			MethodBase m = usage.InternalGetNativeMethodFromTypeAndNameNoSecurity(
				typeof(BasisCilboxInstantiateShim),
				name,
				parameters,
				genericArguments,
				fullSignature );
			return m as MethodInfo;
		}

		// ------------------------------------------------------------------
		// Overloads that mirror UnityEngine.Object.Instantiate one-for-one.
		// ------------------------------------------------------------------

		public static UnityEngine.Object Instantiate( UnityEngine.Object original )
		{
			return SanitizeInstantiate( original, null, null, null );
		}

		public static UnityEngine.Object Instantiate( UnityEngine.Object original, Transform parent )
		{
			return SanitizeInstantiate( original, null, null, parent );
		}

		public static UnityEngine.Object Instantiate( UnityEngine.Object original, Transform parent, bool worldPositionStays )
		{
			return SanitizeInstantiate( original, null, null, parent, worldPositionStays );
		}

		public static UnityEngine.Object Instantiate( UnityEngine.Object original, Vector3 position, Quaternion rotation )
		{
			return SanitizeInstantiate( original, position, rotation, null );
		}

		public static UnityEngine.Object Instantiate( UnityEngine.Object original, Vector3 position, Quaternion rotation, Transform parent )
		{
			return SanitizeInstantiate( original, position, rotation, parent );
		}

		public static T Instantiate<T>( T original ) where T : UnityEngine.Object
		{
			return SanitizeInstantiate( original, null, null, null ) as T;
		}

		public static T Instantiate<T>( T original, Transform parent ) where T : UnityEngine.Object
		{
			return SanitizeInstantiate( original, null, null, parent ) as T;
		}

		public static T Instantiate<T>( T original, Transform parent, bool worldPositionStays ) where T : UnityEngine.Object
		{
			return SanitizeInstantiate( original, null, null, parent, worldPositionStays ) as T;
		}

		public static T Instantiate<T>( T original, Vector3 position, Quaternion rotation ) where T : UnityEngine.Object
		{
			return SanitizeInstantiate( original, position, rotation, null ) as T;
		}

		public static T Instantiate<T>( T original, Vector3 position, Quaternion rotation, Transform parent ) where T : UnityEngine.Object
		{
			return SanitizeInstantiate( original, position, rotation, parent ) as T;
		}

		// ------------------------------------------------------------------
		// Core pipeline: delegate to ContentPoliceControl with the prop
		// selector and the UnityEvent scrub opt-in, using the canonical
		// disabled host wired on BasisFramework.prefab.
		// ------------------------------------------------------------------

		private static UnityEngine.Object SanitizeInstantiate(
			UnityEngine.Object original,
			Vector3? position,
			Quaternion? rotation,
			Transform parent,
			bool worldPositionStays = true)
		{
			if( original == null )
			{
				Debug.LogWarning( "[BasisCilbox] Instantiate called with null original." );
				return null;
			}

			// Non-GameObject/Component clones (Material, Texture2D, AudioClip, ...)
			// can't carry MonoBehaviours or UnityEvents, so they skip the sanitizer.
			GameObject rootPrefab = ExtractRootGameObject( original );
			if( rootPrefab == null )
				return UnityEngine.Object.Instantiate( original );

			BasisDeviceManagement mgmt = BasisDeviceManagement.Instance;
			if( mgmt == null || mgmt.CreationGameobject == null )
			{
				Debug.LogError( "[BasisCilbox] BasisDeviceManagement.CreationGameobject unavailable; refusing Instantiate." );
				return null;
			}

			Vector3 spawnPos = position ?? (parent != null && !worldPositionStays
				? parent.TransformPoint(rootPrefab.transform.localPosition)
				: rootPrefab.transform.position);

			Quaternion spawnRot = rotation ?? (parent != null && !worldPositionStays
				? parent.rotation * rootPrefab.transform.localRotation
				: rootPrefab.transform.rotation);

			ChecksRequired checks = new ChecksRequired
			{
				UseContentRemoval = true,
				ScrubPersistentUnityEvents = true,
				// Animator events route through SendMessage-by-name, which is
				// another reflection escape — disable them for cilbox spawns.
				DisableAnimatorEvents = true,
				RemoveColliders = false,
				ChangeCollidersToCorrectLayer = false,
			};

			GameObject sanitizedClone = ContentPoliceControl.ContentControl(
				mgmt.CreationGameobject,
				rootPrefab,
				checks,
				spawnPos,
				spawnRot,
				false,
				Vector3.zero,
				BundledContentHolder.Selector.Prop,
				parent );

			if( sanitizedClone == null ) return null;

			// Unity's Instantiate returns whatever sub-object on the clone
			// matches the original's type: GameObject -> GameObject, Component
			// -> the matching component fished off the clone root.
			if( original is GameObject ) return sanitizedClone;
			Type originalType = original.GetType();
			Component match = sanitizedClone.GetComponent( originalType );
			if( match != null ) return match;

			// Component might live on a child (e.g. cloning a Rigidbody nested
			// somewhere). Fall back to a first-match search.
			Component[] all = sanitizedClone.GetComponentsInChildren( originalType, true );
			if( all != null && all.Length > 0 ) return all[0];

			Debug.LogWarning( $"[BasisCilbox] Clone is missing a {originalType.FullName} component (likely scrubbed); returning root GameObject." );
			return sanitizedClone;
		}

		private static GameObject ExtractRootGameObject( UnityEngine.Object o )
		{
			if( o == null ) return null;
			if( o is GameObject go ) return go;
			if( o is Component comp ) return comp.gameObject;
			return null;
		}
	}
}
