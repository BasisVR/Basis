//
// CilboxUsage.cs - this file contains:
//  * Security
//  * Any types/methods that get swapped out at load time.
//
// Security Checking:
//   For Methods:
//     1. HandleEarlyMethodRewrite() -- This can rewrite declaring type + method.
//     2. GetNativeTypeNameFromSerializee on declaring type
//     3. GetNativeMethodFromTypeAndName - Sometimes swap-out stuff
//        a. If rewritten, overrides all further security and fast-paths.
//     4. InternalGetNativeMethodFromTypeAndNameNoSecurity
//     5. Parameters and Arguments are checked with CheckTypeSecurityRecursive
//     6. CheckTypeSecurityRecursive calls CheckTypeSecurity
//     7. CheckTypeSecurity checks with the specific Cilbox if a method is
//        allowed via CheckMethodAllowed.
//     8. If disallowed, abort.  If allowed, and overridden, bypass any further checks.
//
//
//   GetNativeTypeFromSerializee (can only be used on non-templated types)
//     1. Checks to see if it's a type from within this cilbox.  If so GO!
//     2. CheckReplaceTypeNotRecursive ON BASE TYPE ONLY
//     3. Recursively check all template types through GetNativeTypeFromSerializee
//     4. Type.GetType
//     5. MakeGenericType
//
//   GetNativeTypeNameFromSerializee mimics GetNativeTypeFromSerializee
//
//   CheckTypeSecurity calls CheckTypeAllowed on the specific cilbox.
//    If it is allowed, continue normal checks.  If it is disallowed, abort.
//
// TODO: Check UnityAction / UnityEvent
//

using UnityEngine;
using System.Collections.Generic;
using System;
using System.Collections.Specialized;
using System.Collections;
using System.Runtime.InteropServices;
using System.Reflection;

namespace Cilbox
{
	public class CilboxUsage
	{
		private Cilbox box;
		public CilboxUsage( Cilbox b ) { box = b; }

		// This is after the type has been fully de-arrayed and de-templated.
		String CheckTypeSecurity( String sType )
		{
			if( box.CheckTypeAllowed( sType ) == true ) return sType;

			Debug.LogError( $"TYPE FAILED CHECK: {sType}" );
			return null;
		}

		public MethodBase GetNativeMethodFromTypeAndName( Type declaringType, String name, Serializee [] parametersIn, Serializee [] genericArgumentsIn, String fullSignature )
		{
			MethodInfo mi = null;
			bool bDisallowed = box.CheckMethodAllowed( out mi, declaringType, name, parametersIn, genericArgumentsIn, fullSignature );
			if( !bDisallowed ) goto disallowed;
			if( mi != null ) return mi;

			// Replace any delegate creations with their proxies.
			if( typeof(Delegate).IsAssignableFrom(declaringType) )
			{
				int argct = declaringType.GenericTypeArguments.Length;
				Type specific = typeof(CilboxPlatform);
				mi = specific.GetMethod( "ProxyForGeneratingActions" );
				mi = mi.MakeGenericMethod( declaringType );
				return mi;
			}

			Type[] parameters = TypeNamesToArrayOfNativeTypes( parametersIn );
			Type[] genericArguments = TypeNamesToArrayOfNativeTypes( genericArgumentsIn );

			MethodBase m = InternalGetNativeMethodFromTypeAndNameNoSecurity( declaringType, name, parameters, genericArguments, fullSignature );

			// Check all parameters for type safety.
			foreach( Type t in parameters )
				if( CheckTypeSecurityRecursive( t ) == null ) goto disallowed;
			foreach( Type t in genericArguments )
				if( CheckTypeSecurityRecursive( t ) == null ) goto disallowed;
			if( m is MethodInfo && CheckTypeSecurityRecursive( ((MethodInfo)m).ReturnType ) == null ) goto disallowed;

			return m;
		disallowed:
			Debug.LogError( $"Privelege failed {declaringType}.{name}" );
			return null;
		}

		////////////////////////////////////////////////////////////////////////////////////
		// DELEGATE OVERRIDES //////////////////////////////////////////////////////////////
		////////////////////////////////////////////////////////////////////////////////////
		public static StackElement OverrideGetComponentT( CilMetadataTokenInfo ths, ArraySegment<StackElement> stackBufferIn, ArraySegment<StackElement> parametersIn )
		{
			Span<StackElement> parameters = parametersIn.AsSpan();

			StackElement ret = new StackElement();

			ret.LoadObject( null );

			Type t = (Type)ths.opaque;
			object o = parameters[0].AsObject();
			if( o is GameObject )
			{
				ret.Load( ((GameObject)o).GetComponent(t) );
			}
			return ret;
		}

		public static StackElement OverrideGetComponentC( CilMetadataTokenInfo ths, ArraySegment<StackElement> stackBufferIn, ArraySegment<StackElement> parametersIn )
		{
			Span<StackElement> parameters = parametersIn.AsSpan();

			StackElement ret = new StackElement();
			ret.LoadObject( null );

			CilboxClass cls = (CilboxClass)ths.opaque;
			String compName = cls.className;

			object o = parameters[0].AsObject();
			if( o is GameObject )
			{
				CilboxProxy [] comps = ((GameObject)o).GetComponents<CilboxProxy>();
				foreach( CilboxProxy p in comps )
				{
					if( p.className == compName )
					{
						ret.Load( p );
						break;
					}
				}
			}
			return ret;
		}


		public static StackElement OverrideTryGetComponentT( CilMetadataTokenInfo ths, ArraySegment<StackElement> stackBufferIn, ArraySegment<StackElement> parametersIn )
		{
			Span<StackElement> parameters = parametersIn.AsSpan();

			StackElement ret = new StackElement();

			ret.LoadBool( false );

			Type t = (Type)ths.opaque;
			object o = parameters[0].AsObject();
			if( o is GameObject && parameters[1].type == StackType.Address )
			{
				Component c;
				ret.Load( ((GameObject)o).TryGetComponent(t, out c) );
				parameters[1].DereferenceLoadAddress( c );
			}
			return ret;
		}

		public static StackElement OverrideTryGetComponentC( CilMetadataTokenInfo ths, ArraySegment<StackElement> stackBufferIn, ArraySegment<StackElement> parametersIn )
		{
			Span<StackElement> parameters = parametersIn.AsSpan();

			StackElement ret = new StackElement();

			ret.LoadBool( false );

			CilboxClass cls = (CilboxClass)ths.opaque;
			String compName = cls.className;

			object o = parameters[0].AsObject();
			if( o is GameObject )
			{
				CilboxProxy [] comps = ((GameObject)o).GetComponents<CilboxProxy>();
				foreach( CilboxProxy p in comps )
				{
					if( p.className == compName && parameters[1].type == StackType.Address )
					{
						ret.Load( true );
						parameters[1].DereferenceLoadAddress( p );
						break;
					}
				}
			}
			return ret;
		}


		////////////////////////////////////////////////////////////////////////////////////
		// REWRITERS ///////////////////////////////////////////////////////////////////////
		////////////////////////////////////////////////////////////////////////////////////

		public (String, Serializee) HandleEarlyMethodRewrite( String name, Serializee declaringType, Serializee [] genericArguments )
		{
			Dictionary< String, Serializee > ses = declaringType.AsMap();
			String typeName = ses["n"].AsString();

// This is no longer done here, but stands as an example of how you could use this function.
//			if( typeName == "<PrivateImplementationDetails>" && name == "ComputeStringHash" )
//			{
//				Dictionary< String, Serializee > exportType = new Dictionary< String, Serializee >();
//				exportType["n"] = new Serializee( "Cilbox.CilboxPublicUtils" );
//				return ( "ComputeStringHashProxy", new Serializee( exportType ) );
//				//return ( "ComputeStringHashProxy", declaringType );
//			}

			return ( name, declaringType );
		}

		public bool OptionallyOverride( String name, Serializee declaringType, String fullSignature, bool isStatic, Serializee [] genericArguments, ref CilMetadataTokenInfo t )
		{
			Dictionary< String, Serializee > ses = declaringType.AsMap();
			String typeName = ses["n"].AsString();

			// We want to allow GetComponent and TryGetComponent.

			if( typeName == "UnityEngine.GameObject" && name == "GetComponent" && genericArguments.Length == 1 )
			{
				Type tTemplate = GetNativeTypeFromSerializee( genericArguments[0] );
				Dictionary< String, Serializee > gatype = genericArguments[0].AsMap();
				String genericTypeName = gatype["n"].AsString();
				int cilboxClassId = -1;
				if( tTemplate != null || box.classes.TryGetValue( genericTypeName, out cilboxClassId ) )
				{
					t.isValid = true;
					t.isNative = false;
					t.Name = "OverrideGetComponentT";
					t.declaringTypeName = typeName;
					t.opaque = (object) (tTemplate != null ? tTemplate : box.classesList[cilboxClassId] );
					t.shim = ( tTemplate != null ) ? OverrideGetComponentT : OverrideGetComponentC;
					t.shimIsStatic = false;
					t.shimParameterCount = 0;

					Debug.Log( $"HandleEarlyMethodRewrite: {name} {typeName}" );
					// Do something wacky.
					return true;
				}
				else
				{
					Debug.LogWarning( "GetComponent Type Illegal" );
				}
			}
			if( typeName == "UnityEngine.GameObject" && name == "TryGetComponent" && genericArguments.Length == 1 )
			{
				Type tTemplate = GetNativeTypeFromSerializee( genericArguments[0] );
				Dictionary< String, Serializee > gatype = genericArguments[0].AsMap();
				String genericTypeName = gatype["n"].AsString();
				int cilboxClassId = -1;
				if( tTemplate != null || box.classes.TryGetValue( genericTypeName, out cilboxClassId ) )
				{
					t.isValid = true;
					t.isNative = false;
					t.Name = "OverrideTryGetComponentT";
					t.declaringTypeName = typeName;
					t.opaque = (object) (tTemplate != null ? tTemplate : box.classesList[cilboxClassId] );
					t.shim = ( tTemplate != null ) ? OverrideTryGetComponentT : OverrideTryGetComponentC;
					t.shimIsStatic = false;
					t.shimParameterCount = 1;

					Debug.Log( $"HandleEarlyMethodRewrite: {name} {typeName}" );
					// Do something wacky.
					return true;
				}
				else
				{
					Debug.LogWarning( "GetComponent Type Illegal" );
				}
			}
			return false;
		}

		// WARNING: This DOES NOT appropriately handle templated types.
		// TODO: IF YOU WANT THIS TO HANDLE TEMPLATE TYPES, YOU MUST DO SO RECURSIVELY.
		String CheckReplaceTypeNotRecursive( String typeName )
		{
			if (typeName.Equals(typeof(System.Runtime.CompilerServices.RuntimeHelpers).FullName)) {
				// Rewrite RuntimeHelpers.InitializeArray() class name.
				typeName = typeof(CilboxPublicUtils).FullName;
			}
			if (typeName.Equals(typeof(System.RuntimeFieldHandle).FullName)) {
				// Rewrite RuntimeHelpers.InitializeArray() second argument.
				typeName = typeof(byte[]).FullName;
			}

			// Perform check by removing "&" from the end of ref types
			// This happens when the type is a reference to a specific type
			// But for security purposes, we just want to check the base type
			//  i.e.  System.byte& ===> System.byte  /  &
			String refSuffix = ""; // store the ref suffix ("&") to add back on after the check
			if( typeName.EndsWith('&') ) { refSuffix = "&"; typeName = typeName[..^1]; }
			// Perform check without array[]
			//  i.e.  System.byte[][] ===> System.byte  /  [][]
			String [] vTypeNameNoArray = typeName.Split( "[" );
			String typeNameNoArray = ( vTypeNameNoArray.Length > 0 ) ? vTypeNameNoArray[0] : typeName;
			String arrayEnding = typeName.Substring( typeNameNoArray.Length );
			typeNameNoArray = CheckTypeSecurity( typeNameNoArray  );
			if( typeNameNoArray == null ) return null;
			return typeNameNoArray + arrayEnding + refSuffix;
		}

		Type CheckTypeSecurityRecursive( Type t )
		{
			TypeInfo typeInfo = t.GetTypeInfo();
			if( typeInfo == null ) return null;
			String typeName = typeInfo.ToString();
			// Perform check by removing "&" from the end of ref types
			// This happens when the type is a reference to a specific type
			// But for security purposes, we just want to check the base type
			//  i.e.  System.byte& ===> System.byte
			if( typeName.EndsWith('&') ) typeName = typeName[..^1];
			String [] vTypeNameNoArray = typeName.Split( "[" );
			typeName = ( vTypeNameNoArray.Length > 0 ) ? vTypeNameNoArray[0] : typeName;
			String [] vTypeNameNoGenerics = typeName.Split( "`" );
			typeName = ( vTypeNameNoGenerics.Length > 0 ) ? vTypeNameNoGenerics[0] : typeName;

			if( CheckTypeSecurity( typeName ) == null ) return null;
			foreach( Type tt in typeInfo.GenericTypeArguments )
			{
				if( CheckTypeSecurityRecursive( tt ) == null ) return null;
			}
			return t;
		}

		////////////////////////////////////////////////////////////////////////////////////
		// INTERNAL CHECKING ///////////////////////////////////////////////////////////////
		////////////////////////////////////////////////////////////////////////////////////

		// Strip array suffixes and nested type suffixes to check if the base type is Cilboxable
		public bool IsCilboxInternalType( String typeName )
		{
			if( box.classes.ContainsKey( typeName ) ) return true;

			// Strip array suffix: "Foo.Bar[]" or "Foo.Bar[][]" -> "Foo.Bar"
			int bracket = typeName.IndexOf( '[' );
			String baseName = bracket >= 0 ? typeName.Substring( 0, bracket ) : typeName;

			// Strip "&" for ref types: "Foo.Bar&" -> "Foo.Bar"
			if( baseName.EndsWith('&') ) baseName = baseName.Substring( 0, baseName.Length - 1 );

			if( box.classes.ContainsKey( baseName ) ) return true;

			return false;
		}

		public Type GetNativeTypeFromSerializee( Serializee s )
		{
			Dictionary< String, Serializee > ses = s.AsMap();
			String typeName = ses["n"].AsString();
			String assemblyName = ses["a"].AsString();
			if( IsCilboxInternalType( typeName ) ) return null;
			typeName = CheckReplaceTypeNotRecursive( typeName );
			if( typeName == null ) return null;

			Serializee g;
			Type [] ga = null;
			if( ses.TryGetValue( "g", out g ) )
			{
				// If there are generics (stored in g) then use the serialized generic name instead of stripped type name
				// n = Namespace.Outer+Middle+Inner		gn = Namespace.Outer`1+Middle`2+Inner`1
				typeName = ses["gn"].AsString();
				Serializee [] gs = g.AsArray();
				ga = new Type[gs.Length];
				for( int i = 0; i < gs.Length; i++ )
					ga[i] = GetNativeTypeFromSerializee( gs[i] );
			}

			Type ret = null;

			// In case we can find an exact match...
			System.Reflection.Assembly [] assys = AppDomain.CurrentDomain.GetAssemblies();
			foreach( System.Reflection.Assembly a in assys )
			{
				Type t = a.GetType( typeName );
				if( t != null && a.GetName().Name == assemblyName )
				{
					ret = t;
					break;
				}
			}
			if( ret == null )
				ret = Type.GetType( typeName );

			if( ret == null )
			{
				Debug.LogError( $"Could not find type {typeName}" );
				return null;
			}

			if( ga != null )
				ret = ret.MakeGenericType(ga);

			return ret;
		}

		public String GetNativeTypeNameFromSerializee( Serializee s )
		{
			Dictionary< String, Serializee > ses = s.AsMap();
			String typeName = ses["n"].AsString();
			if( IsCilboxInternalType( typeName ) ) return typeName;
			typeName = CheckReplaceTypeNotRecursive( typeName );
			if( typeName == null ) return null;

			Serializee g;
			if( ses.TryGetValue( "g", out g ) )
			{
				String ret = typeName + "`[";
				Serializee [] gs = g.AsArray();
				for( int i = 0; i < gs.Length; i++ )
					ret += (i==0?"":",") + GetNativeTypeNameFromSerializee( gs[i] );
				return ret + "]";
			}
			else
			{
				return typeName;
			}
		}

		public Type [] TypeNamesToArrayOfNativeTypes( Serializee [] sa )
		{
			Type [] ret = new Type[sa.Length];
			for( int i = 0; i < sa.Length; i++ )
				ret[i] = GetNativeTypeFromSerializee( sa[i] );
			return ret;
		}


		public MethodBase InternalGetNativeMethodFromTypeAndNameNoSecurity( Type declaringType, String name, Type [] parameters, Type [] genericArguments, String fullSignature )
		{
			if (genericArguments.Length == 0)
			{
				// Can we combine Constructor + Method?
				MethodBase m = declaringType.GetMethod(
					name,
					genericArguments.Length,
					/*BindingFlags.NonPublic | */ BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static,
					null,
					CallingConventions.Any,
					parameters,
					null ); // TODO I don't ... think? we need parameter modifiers? "To be only used when calling through COM interop, and only parameters that are passed by reference are handled. The default binder does not process this parameter."

				if (m != null) { return m; }

				// Can't use GetConstructor, because somethings have .ctor or .cctor
				ConstructorInfo[] cts = declaringType.GetConstructors(
					/*BindingFlags.NonPublic | */ BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static );
				int ck;
				for( ck = 0; ck < cts.Length; ck++ )
				{
					//Debug.Log( cts[ck] );
					if( fullSignature == cts[ck].ToString() )
					{
						m = cts[ck];
						break;
					}
				}
				if (m != null) { return m; }
			}

			if (genericArguments.Length > 0)
			{
				foreach (MethodInfo mi in declaringType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
				{
					if (mi.Name != name) continue;
					if (!mi.IsGenericMethodDefinition) continue;
					if (mi.GetGenericArguments().Length != genericArguments.Length) continue;
					if (mi.GetParameters().Length != parameters.Length) continue;

					// Need to verify the parameters actually match
					try
					{
						MethodInfo closed = mi.MakeGenericMethod(genericArguments);
						ParameterInfo[] closedPars = closed.GetParameters();
						bool match = true;
						for (int i = 0; i < parameters.Length; i++)
						{
							if (closedPars[i].ParameterType != parameters[i])
							{
								match = false;
								break;
							}
						}

						if (match)
						{
							return closed;
						}
					}
					catch
					{
						// Continue checking the rest of the methods to see if there is another match
						// (maybe there could be one with the same signature but wrong constraints that would cause MakeGenericMethod to fail)
						continue;
					}
				}
			}

			// If we really can't find the method, search through all types of matching assembly names.
			// This is needed for sometimes when we have AsmDef.<PirvateImplementationDetails>.ComputeStringHash()
			System.Reflection.Assembly [] assys = AppDomain.CurrentDomain.GetAssemblies();
			foreach( System.Reflection.Assembly proxyAssembly in assys )
			{
				foreach (Type type in proxyAssembly.GetTypes())
				{
					if( type.Name == declaringType.Name )
					{
						try
						{
							// I don't think there is any case where this would be needed for a type with a constructor.
							MethodBase m = type.GetMethod(
								name,
								genericArguments.Length,
								BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static,
								null,
								CallingConventions.Any,
								parameters,
								null );
							if( m != null )
							{
								Debug.Log(type.Name + " == " + declaringType.Name + " == " + proxyAssembly.GetName().Name + " >> " + m.Name);
								return m;
							}
						}
						catch (Exception e)
						{
							Debug.LogWarning(e.ToString());
							Debug.LogWarning("Failed to find method " + name + " on type " + type.FullName + " with parameters " + string.Join(", ", (object[])parameters) + " and generic arguments " + string.Join(", ", (object[])genericArguments) + " in assembly " + proxyAssembly.GetName().Name);
							// Continue searching the rest of the assemblies to see if there is another match
							continue;
						}

					}
				}
			}

			// If we reach here, no appropriate method was found.
			return null;
		}

	}


	// This overrides System.Runtime.CompilerServices.RuntimeHelpers
	// WARNING: This class is 100% available from WITHIN cilbox.
	public class CilboxPublicUtils
	{
		public static void InitializeArray(Array arr, byte[] initializer)
		{
			if (initializer == null || arr == null)
				throw new Exception( "Error, array or initializer are null" );
			if (initializer.Length != System.Runtime.InteropServices.Marshal.SizeOf(arr.GetType().GetElementType()) * arr.Length)
				throw new Exception( "InitializeArray requires identical array byte length " + initializer.Length );
			Buffer.BlockCopy(initializer, 0, arr, 0, initializer.Length);
		}

		public static String GetProxyInitialPath( MonoBehaviour m )
		{
			CilboxProxy p = (CilboxProxy)m;
			return p.initialLoadPath;
		}

		public static String GetProxyBuildTimeGuid( MonoBehaviour m )
		{
			CilboxProxy p = (CilboxProxy)m;
			return p.buildTimeGuid;
		}
	}

	// Be warned that this class is totally available to the inner box.
	public class CilboxPlatform
	{
		// This is called only when creating a new action, not when it's called.
		// T is the delegate, not the arguments of the delegate.
		static public object ProxyForGeneratingActions<T>( CilboxProxy proxy, CilboxMethod method )
		{
			CilboxPlatform.DelegateRepackage rp = new CilboxPlatform.DelegateRepackage();
			rp.meth = method;
			rp.o = proxy;

			Type[] parameterTypes = typeof(T).GenericTypeArguments;
			int parameterCount = parameterTypes.Length;

			MethodInfo dMethod = typeof(T).GetMethod("Invoke");
			if( dMethod != null )
			{
				// For some reason, in some contexts we get non-generic delegates.
				// If that's the case, just use the parameters.
				System.Reflection.ParameterInfo[] parameters = dMethod.GetParameters();
				int methodParameters = parameters.Length;
				if( methodParameters > parameterCount )
				{
					parameterCount = methodParameters;
					parameterTypes = new Type[parameterCount];
					for( int n = 0; n < parameterCount; n++ )
						parameterTypes[n] = parameters[n].ParameterType;
				}
			}

			MethodInfo mthis = typeof(CilboxPlatform.DelegateRepackage)
				.GetMethod("ActionCallback"+parameterCount.ToString());
			if( mthis.IsGenericMethod )
				mthis = mthis.MakeGenericMethod( parameterTypes );
			return Delegate.CreateDelegate( typeof(T), rp, mthis );
		}

		public class DelegateRepackage
		{
			public CilboxMethod meth;
			public CilboxProxy o;
		    public void ActionCallback0( )                                         { meth.Interpret( o, new object[0] ); }
		    public void ActionCallback1<T0>( T0 o0 )                               { meth.Interpret( o, new object[]{o0} ); }
		    public void ActionCallback2<T0,T1>( T0 o0, T1 o1 )                     { meth.Interpret( o, new object[]{o0,o1} ); }
		    public void ActionCallback3<T0,T1,T2>( T0 o0, T1 o1, T2 o2 )           { meth.Interpret( o, new object[]{o0,o1,o2} ); }
		    public void ActionCallback4<T0,T1,T2,T3>( T0 o0, T1 o1, T2 o2, T3 o3 ) { meth.Interpret( o, new object[]{o0,o1,o2,o3} ); }
		}
	}
}

