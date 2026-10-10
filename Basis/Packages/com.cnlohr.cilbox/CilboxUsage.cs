//
// CilboxUsage.cs - this file contains:
//  * Security
//  * Any types/methods that get swapped out at load time.
//
// Security Checking:
//   For Methods:
//     1. HandleEarlyMethodRewrite() -- This can rewrite declaring type + method.
//     2. GetNativeTypeNameFromDescriptor on declaring type
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
//   GetNativeTypeFromDescriptor (can only be used on non-templated types)
//     1. Checks to see if it's a type from within this cilbox.  If so GO!
//     2. CheckReplaceTypeNotRecursive ON BASE TYPE ONLY
//     3. Recursively check all template types through GetNativeTypeFromDescriptor
//     4. Type.GetType
//     5. MakeGenericType
//
//   GetNativeTypeNameFromDescriptor mimics GetNativeTypeFromDescriptor
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
    public abstract class CilboxShim : MonoBehaviour {}

	public class CilboxUsage
	{
		private Cilbox box;
		public CilboxUsage( Cilbox b ) { box = b; }

		// This is after the type has been fully de-arrayed and de-templated.
		bool CheckTypeSecurity( Type sType )
		{
			// Types defined inside this cilbox are serialized and interpreted locally,
			// so they should not be forced through the native whitelist.
			if( box.CheckTypeAllowed( sType ) == true ) return true;

			Debug.LogError( $"TYPE FAILED CHECK: {sType}" );
			return false;
		}

		public MethodBase GetNativeMethodFromTypeAndName( Type declaringType, String name, SerializedTypeDescriptor [] parametersIn, SerializedTypeDescriptor [] genericArgumentsIn, String fullSignature )
		{
			MethodInfo mi = null;
			if(!box.CheckMethodAllowed( out mi, declaringType, name, parametersIn, genericArgumentsIn, fullSignature ))
			{
				Debug.LogError( $"Privilege failed for {declaringType}.{name} method" );
				return null;
			}
			if( mi != null ) return mi;

			// Replace delegate construction with cilbox-backed host delegates.
			if( typeof(Delegate).IsAssignableFrom(declaringType) && name == ".ctor" )
			{
				int argct = declaringType.GenericTypeArguments.Length;
				Type specific = typeof(CilboxPlatform);
				mi = specific.GetMethod( "ProxyForGeneratingActions" );
				mi = mi.MakeGenericMethod( declaringType );
				return mi;
			}

			Type[] parameters = DescriptorsToArrayOfNativeTypes( parametersIn );
			Type[] genericArguments = DescriptorsToArrayOfNativeTypes( genericArgumentsIn );

			MethodBase m = InternalGetNativeMethodFromTypeAndNameNoSecurity( declaringType, name, parameters, genericArguments, fullSignature );

			// Check all parameters for type safety.
			for (int i = 0; i < parameters.Length; i++)
			{
				if( !CheckTypeSecurityRecursive(parameters[i]) )
				{
					string typeName = parametersIn[i].typeName;
					Debug.LogError( $"Privilege failed for {declaringType}.{name} parameter {i} type {typeName}" );
					return null;
				}
			}
			for (int i = 0; i < genericArguments.Length; i++)
			{
				if( !CheckTypeSecurityRecursive(genericArguments[i]) )
				{
					string typeName = genericArgumentsIn[i].typeName;
					Debug.LogError( $"Privilege failed for {declaringType}.{name} generic argument {i} type {typeName}" );
					return null;
				}
			}
			if( m is MethodInfo mm && !CheckTypeSecurityRecursive( mm.ReturnType ) )
			{
				Debug.LogError( $"Privilege failed for {declaringType}.{name} return type {mm.ReturnType.FullName}" );
				return null;
			}

			return m;
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
			if( o is GameObject || o is Component )
			{
				GameObject gameObject = (o is GameObject) ? (GameObject)o : ((Component)o).gameObject;
				Component component;
				if(typeof(CilboxShim).IsAssignableFrom(t))
				{
					Debug.Log($"OverrideGetComponentT: Overriding {t.FullName} with a CilboxShim component because it is abstract and assignable from CilboxShim" );
					if(gameObject.TryGetComponent(t, out Component c)) {
						component = c;
					} else
					{
						component = gameObject.AddComponent(t);
					}
				} else
				{
					component = gameObject.GetComponent(t);
				}
				ret.Load( component );
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
					CilboxClass pcls = p.cls != null ? p.cls : cls.box.GetClass( p.className );
					if( p.className == compName || ( pcls != null && pcls.baseClassNames != null && System.Array.IndexOf( pcls.baseClassNames, compName ) >= 0 ) )
					{
						// force-load the matched proxy so it's usable when returned; RuntimeProxyLoad no-ops if already loaded (guards on proxyWasSetup)
						p.RuntimeProxyLoad();
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
			if( (o is GameObject || o is Component) && parameters[1].type == StackType.Address )
			{
				GameObject gameObject = (o is GameObject) ? (GameObject)o : ((Component)o).gameObject;
				Component component;
				if(typeof(CilboxShim).IsAssignableFrom(t))
				{
					Debug.Log($"OverrideTryGetComponentT: Overriding {t.FullName} with a CilboxShim component because it is abstract and assignable from CilboxShim" );
					if(gameObject.TryGetComponent(t, out Component c)) {
						component = c;
					} else
					{
						component = gameObject.AddComponent(t);
					}
				} else
				{
					component = gameObject.GetComponent(t);
				}
				ret.Load( component != null );
				parameters[1].DereferenceLoadAddress( component );
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
					CilboxClass pcls = p.cls != null ? p.cls : cls.box.GetClass( p.className );
					if( ( p.className == compName || ( pcls != null && pcls.baseClassNames != null && System.Array.IndexOf( pcls.baseClassNames, compName ) >= 0 ) ) && parameters[1].type == StackType.Address )
					{
						// force-load the matched proxy so it's usable when returned; RuntimeProxyLoad no-ops if already loaded (guards on proxyWasSetup)
						p.RuntimeProxyLoad();
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

		public (String, SerializedTypeDescriptor) HandleEarlyMethodRewrite( String name, SerializedTypeDescriptor declaringType, SerializedTypeDescriptor [] genericArguments )
		{
			String typeName = declaringType.typeName;

// This is no longer done here, but stands as an example of how you could use this function.
//			if( typeName == "<PrivateImplementationDetails>" && name == "ComputeStringHash" )
//			{
//				SerializedTypeDescriptor exportType = new SerializedTypeDescriptor();
//				exportType.typeName = "Cilbox.CilboxPublicUtils";
//				return ( "ComputeStringHashProxy", exportType );
//				//return ( "ComputeStringHashProxy", declaringType );
//			}

			return ( name, declaringType );
		}

		public bool OptionallyOverride( String name, SerializedTypeDescriptor declaringType, String fullSignature, bool isStatic, SerializedTypeDescriptor [] genericArguments, ref CilMetadataTokenInfo t )
		{
			String typeName = declaringType.typeName;

			// We want to allow GetComponent and TryGetComponent.

			if( (typeName == "UnityEngine.GameObject" || typeName == "UnityEngine.Component") && name == "GetComponent" && genericArguments.Length == 1 )
			{
				Type tTemplate = GetNativeTypeFromDescriptor( genericArguments[0] );
				String genericTypeName = genericArguments[0].typeName;
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
			if( (typeName == "UnityEngine.GameObject" || typeName == "UnityEngine.Component") && name == "TryGetComponent" && genericArguments.Length == 1 )
			{
				Type tTemplate = GetNativeTypeFromDescriptor( genericArguments[0] );
				String genericTypeName = genericArguments[0].typeName;
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

			// Why are we checking type security here?
			//if( !CheckTypeSecurity( typeNameNoArray ) ) return null;

			return typeNameNoArray + arrayEnding + refSuffix;
		}

		public bool CheckTypeSecurityRecursive( Type t )
		{
			if( t == null ) return false;
			TypeInfo typeInfo = t.GetTypeInfo();
			if( typeInfo == null ) return false;
			String typeName = typeInfo.ToString();
			// Perform check by removing "&" from the end of ref types
			// This happens when the type is a reference to a specific type
			// But for security purposes, we just want to check the base type
			//  i.e.  System.byte& ===> System.byte

			
			/*
			if( typeName.EndsWith('&') ) typeName = typeName[..^1];
			String [] vTypeNameNoArray = typeName.Split( "[" );
			typeName = ( vTypeNameNoArray.Length > 0 ) ? vTypeNameNoArray[0] : typeName;
			String [] vTypeNameNoGenerics = typeName.Split( "`" );
			typeName = ( vTypeNameNoGenerics.Length > 0 ) ? vTypeNameNoGenerics[0] : typeName;
			*/
			if (typeInfo.IsPointer || typeInfo.IsArray) t = typeInfo.GetElementType();
			if( !CheckTypeSecurity( t ) ) return false;
			foreach( Type tt in typeInfo.GenericTypeArguments )
			{
				if( !CheckTypeSecurityRecursive( tt ) ) return false;
			}
			return true;
		}

		////////////////////////////////////////////////////////////////////////////////////
		// INTERNAL CHECKING ///////////////////////////////////////////////////////////////
		////////////////////////////////////////////////////////////////////////////////////

		// Strip array suffixes and nested type suffixes to check if the base type is Cilboxable
		public bool IsCilboxInternalType( String typeName )
		{
			if( box.classes.ContainsKey( typeName ) ) return true;
			if( box.cilboxEnums != null && box.cilboxEnums.ContainsKey( typeName ) ) return true;

			// Strip array suffix: "Foo.Bar[]" or "Foo.Bar[][]" -> "Foo.Bar"
			int bracket = typeName.IndexOf( '[' );
			String baseName = bracket >= 0 ? typeName.Substring( 0, bracket ) : typeName;

			// Strip "&" for ref types: "Foo.Bar&" -> "Foo.Bar"
			if( baseName.EndsWith('&') ) baseName = baseName.Substring( 0, baseName.Length - 1 );

			if( box.classes.ContainsKey( baseName ) ) return true;
			if( box.cilboxEnums != null && box.cilboxEnums.ContainsKey( baseName ) ) return true;

			return false;
		}

		public Type GetNativeTypeFromDescriptor( SerializedTypeDescriptor td )
		{
			if( td.HasUnderlyingType ) return GetNativeTypeFromDescriptor( td.underlyingType );
			String typeName = td.typeName;
			String assemblyName = td.assemblyName;
			if( IsCilboxInternalType( typeName ) ) return null;
			if(box.GetTypeOverride( typeName, out Type overrideType )) {
				Debug.Log( $"{nameof(GetNativeTypeFromDescriptor)}: Override {typeName} with {overrideType.FullName}" );
				typeName = overrideType.FullName;
				assemblyName = overrideType.Assembly.GetName().Name;
			}
			typeName = CheckReplaceTypeNotRecursive( typeName );
			if( typeName == null ) return null;

			Type [] ga = null;
			if( td.IsGeneric )
			{
				// If there are generics then use the serialized generic name instead of stripped type name
				// typeName = Namespace.Outer+Middle+Inner		genericName = Namespace.Outer`1+Middle`2+Inner`1
				typeName = td.genericName;
				ga = new Type[td.genericArgs.Length];
				for( int i = 0; i < td.genericArgs.Length; i++ )
				{
					Type gt = GetNativeTypeFromDescriptor( td.genericArgs[i] );
					if( gt == null ) return null;
					ga[i] = gt;
				}
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

			if( !CheckTypeSecurity( ret ) ) return null;
			return ret;
		}

		public String GetNativeTypeNameFromDescriptor( SerializedTypeDescriptor td )
		{
			if( td.HasUnderlyingType ) return GetNativeTypeNameFromDescriptor( td.underlyingType );
			String typeName = td.typeName;
			if( IsCilboxInternalType( typeName ) ) return typeName;
			if(box.GetTypeOverride( typeName, out Type overrideType )) {
				typeName = overrideType.FullName;
			}
			typeName = CheckReplaceTypeNotRecursive( typeName );
			if( typeName == null ) return null;

			if( td.IsGeneric )
			{
				String ret = typeName + "`[";
				for( int i = 0; i < td.genericArgs.Length; i++ )
					ret += (i==0?"":",") + GetNativeTypeNameFromDescriptor( td.genericArgs[i] );
				return ret + "]";
			}
			else
			{
				return typeName;
			}
		}

		public Type [] DescriptorsToArrayOfNativeTypes( SerializedTypeDescriptor [] descs )
		{
			Type [] ret = new Type[descs.Length];
			for( int i = 0; i < descs.Length; i++ )
				ret[i] = GetNativeTypeFromDescriptor( descs[i] );
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
		static public object ProxyForGeneratingActions<T>( CilboxProxy proxy, object methodOrDelegate )
		{
			if( methodOrDelegate is T existingTypedDelegate )
			{
				return existingTypedDelegate;
			}

			if( methodOrDelegate is Delegate existingDelegate &&
				typeof(T).IsAssignableFrom( existingDelegate.GetType() ) )
			{
				return existingDelegate;
			}

			if( methodOrDelegate is not CilboxMethod method )
			{
				throw new ArgumentException(
					$"ProxyForGeneratingActions expected a CilboxMethod or compatible delegate, got {methodOrDelegate?.GetType().FullName ?? "null"}" );
			}

			CilboxPlatform.DelegateRepackage rp = new CilboxPlatform.DelegateRepackage();
			rp.meth = method;
			rp.o = proxy;

			Type[] parameterTypes;
			Type returnType = typeof(void);
			MethodInfo dMethod = typeof(T).GetMethod("Invoke");
			if( dMethod != null )
			{
				System.Reflection.ParameterInfo[] parameters = dMethod.GetParameters();
				parameterTypes = new Type[parameters.Length];
				for( int n = 0; n < parameters.Length; n++ )
					parameterTypes[n] = parameters[n].ParameterType;
				returnType = dMethod.ReturnType;
			}
			else
			{
				// For some reason, in some contexts we get non-generic delegates.
				parameterTypes = typeof(T).GenericTypeArguments;
			}
			int parameterCount = parameterTypes.Length;

			bool isVoid = returnType == typeof(void);
			Type[] callbackTypes;
			if( isVoid )
			{
				callbackTypes = parameterTypes;
			}
			else
			{
				callbackTypes = new Type[parameterCount+1];
				Array.Copy( parameterTypes, callbackTypes, parameterCount );
				callbackTypes[parameterCount] = returnType;
			}

			MethodInfo mthis = typeof(CilboxPlatform.DelegateRepackage)
				.GetMethod( ( isVoid ? "ActionCallback" : "FuncCallback" ) + parameterCount.ToString() );
			if( mthis == null )
				throw new ArgumentException( $"Cilbox cannot marshal a {parameterCount} parameter delegate returning {returnType} ({typeof(T)})." );
			if( mthis.IsGenericMethod )
				mthis = mthis.MakeGenericMethod( callbackTypes );
			return Delegate.CreateDelegate( typeof(T), rp, mthis );
		}

		public class DelegateRepackage
		{
			public CilboxMethod meth;
			public CilboxProxy o;
		    public void ActionCallback0( )                                         { FuncCallback0<object>(); }
		    public void ActionCallback1<T0>( T0 o0 )                               { FuncCallback1<T0,object>( o0 ); }
		    public void ActionCallback2<T0,T1>( T0 o0, T1 o1 )                     { FuncCallback2<T0,T1,object>( o0, o1 ); }
		    public void ActionCallback3<T0,T1,T2>( T0 o0, T1 o1, T2 o2 )           { FuncCallback3<T0,T1,T2,object>( o0, o1, o2 ); }
		    public void ActionCallback4<T0,T1,T2,T3>( T0 o0, T1 o1, T2 o2, T3 o3 ) { FuncCallback4<T0,T1,T2,T3,object>( o0, o1, o2, o3 ); }

		    public TR FuncCallback0<TR>( )                                             { object[] oa = System.Array.Empty<object>(); return Coerce<TR>( meth.Interpret( o, oa ) ); }
		    public TR FuncCallback1<T0,TR>( T0 o0 )                                    { object[] oa = new object[1]; oa[0] = o0; return Coerce<TR>( meth.Interpret( o, oa ) ); }
		    public TR FuncCallback2<T0,T1,TR>( T0 o0, T1 o1 )                          { object[] oa = new object[2]; oa[0] = o0; oa[1] = o1; return Coerce<TR>( meth.Interpret( o, oa ) ); }
		    public TR FuncCallback3<T0,T1,T2,TR>( T0 o0, T1 o1, T2 o2 )                { object[] oa = new object[3]; oa[0] = o0; oa[1] = o1; oa[2] = o2; return Coerce<TR>( meth.Interpret( o, oa ) ); }
		    public TR FuncCallback4<T0,T1,T2,T3,TR>( T0 o0, T1 o1, T2 o2, T3 o3 )      { object[] oa = new object[4]; oa[0] = o0; oa[1] = o1; oa[2] = o2; oa[3] = o3; return Coerce<TR>( meth.Interpret( o, oa ) ); }

			private static TR Coerce<TR>( object v )
			{
				if( v is TR typed ) return typed;
				if( v == null ) return default(TR);
				Type t = Nullable.GetUnderlyingType( typeof(TR) ) ?? typeof(TR);
				if( t.IsEnum ) return (TR)Enum.ToObject( t, v );
				if( v is IConvertible ) return (TR)Convert.ChangeType( v, t );
				return (TR)v;
			}
		}
	}
}

