using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;

#if UNITY_EDITOR
using Unity.Profiling;
#endif

namespace Cilbox
{
	public class CilboxProxy : MonoBehaviour
	{
		public StackElement [] fields;
		public List< UnityEngine.Object > fieldsObjects;  // This is generally only held during saving and loading, not in use.
		[NonSerialized] private List< UnityEngine.Object > runtimeFieldsObjects;

		public CilboxClass cls;
		public Cilbox box;
		public String className;
		public String serializedObjectData;

		public String buildTimeGuid;
		public String initialLoadPath;

		private bool proxyWasSetup = false;
		private bool proxyLoadInProgress = false;

		public bool disabled = false;

		public void DisableProxy()
		{
			if( disabled ) return;
			disabled = true;
			enabled = false;
		}

		private void ProxyDebugLog( string message )
		{
				Debug.Log( $"[CilboxProxy:{gameObject.name}] {message}" );
		}

		public CilboxProxy() { }

#if UNITY_EDITOR
		public void SetupProxy( Cilbox box, MonoBehaviour mToSteal, Dictionary< MonoBehaviour, CilboxProxy > refToProxyMap )
		{
			this.box = box;
			this.className = mToSteal.GetType().ToString();

			box.BoxInitialize();
			cls = box.GetClass( className );

			fieldsObjects = new List< UnityEngine.Object >();
			FieldInfo[] fi = CilboxUtil.GetInstanceFieldsBaseFirst( mToSteal.GetType() );

			List< SerializedProxyField > lstFields = new List< SerializedProxyField >();

			foreach( var f in fi )
			{
				// TODO: Consider if we should try serializing _everything_.  No because arrays really need their constructors.
				if( !f.IsPublic && f.GetCustomAttributes(typeof(SerializeField), true).Length <= 0 )
					continue;

				object fv = f.GetValue( mToSteal );

				int matchingInstanceNameID = -1;
				for( int k = 0; k < cls.instanceFieldNames.Length; k++ )
				{
					if( cls.instanceFieldNames[k] == f.Name )
					{
						matchingInstanceNameID = k;
					}
				}


				if( matchingInstanceNameID < 0 )
				{
					Debug.Log( $"Warning: Could not find matching instance name for {f.Name}" );
					continue;
				}

				SerializedProxyField spf = SerializeProxyField(fv, f.Name, matchingInstanceNameID, ref refToProxyMap);

				// Serialize no matter what.
				lstFields.Add( spf );
			}

			SerializedProxy proxy = new SerializedProxy
			{
				fields = lstFields.ToArray(),
			};
			serializedObjectData = proxy.SerializeString();

			buildTimeGuid = Guid.NewGuid().ToString();
		}


		private SerializedProxyField SerializeProxyField( object fv, String fName, int matchingInstanceNameID, ref Dictionary< MonoBehaviour, CilboxProxy > refToProxyMap )
		{
			SerializedProxyField spf = new SerializedProxyField();
			spf.fieldName = fName;
			spf.matchingInstanceId = matchingInstanceNameID;

			// Skip null objects.
			if( fv == null )
			{
				spf.fieldType = (byte)ProxyFieldType.Empty;
				return spf;
			}

			Type fvType = fv.GetType();
			bool hasCilboxable = CilboxUtil.HasCilboxableAttribute( fvType );

			StackType st;

			// Serialize enum field as underlying type
			if( fvType.IsEnum )
			{
				object underlying = Convert.ChangeType( fv, fvType.GetEnumUnderlyingType() );
				if( StackElement.TypeToStackType.TryGetValue( underlying.GetType().ToString(), out st ) && st < StackType.Object )
				{
					spf.fieldType = (byte)ProxyFieldType.Primitive;
					spf.primitiveValue.Unbox( underlying, st );
				}
			}
			// Not a proxiable script.
			else if (hasCilboxable)
			{
				spf.fieldType = (byte)ProxyFieldType.CilboxRef;
				spf.fieldObjectIndex = fieldsObjects.Count;
				spf.objectRefName = fv.ToString();
				spf.objectRefIsNull = spf.objectRefName == "null";
				fieldsObjects.Add( refToProxyMap[(MonoBehaviour)fv] );
			}
			else if( fv is UnityEngine.Object )
			{
				spf.fieldType = (byte)ProxyFieldType.ObjectRef;
				spf.fieldObjectIndex = fieldsObjects.Count;
				spf.objectRefName = fv.ToString();
				spf.objectRefIsNull = spf.objectRefName == "null";
				fieldsObjects.Add( (UnityEngine.Object)fv );
			}
			else if( fv is string )
			{
				spf.fieldType = (byte)ProxyFieldType.String;
				spf.data = fv.ToString();
			}
			else if( StackElement.TypeToStackType.TryGetValue( fvType.ToString(), out st ) && st < StackType.Object )
			{
				spf.fieldType = (byte)ProxyFieldType.Primitive;
				spf.primitiveValue.Unbox( fv, st );
			}
			else if( fvType.IsArray )
			{
				spf.fieldType = (byte)ProxyFieldType.Array;
				Type type = fvType.GetElementType();
				spf.elementType = SerializedTypeDescriptorBuilder.FromNativeType( type );
				Array arr = (Array)fv;
				int len = arr.Length;
				spf.arrayElements = new SerializedProxyField[len];
				for( int i = 0; i < len; i++ )
				{
					object o = arr.GetValue(i);
					spf.arrayElements[i] = SerializeProxyField( o, null, -1, ref refToProxyMap );
				}
			}
			else
			{
				spf.fieldType = (byte)ProxyFieldType.Json;
				spf.data = JsonUtility.ToJson(fv);
				spf.elementType = SerializedTypeDescriptorBuilder.FromNativeType( fvType );
			}

			return spf;
		}

#endif
		void Awake()
		{
			// You cannot do anything in Awake()  Box is not set yet.
		}

		public void RuntimeProxyLoad()
		{
			//Debug.Log( "Runtime Proxy Load " + proxyWasSetup + " " + transform.name + " " + className );
			if( proxyWasSetup ) return;
			if( proxyLoadInProgress ) return;
			if (box == null) return;
			if (string.IsNullOrEmpty(serializedObjectData)) return;
			proxyLoadInProgress = true;
			try
			{
				box.BoxInitialize(); // In case it is not yet initialized.
				bool verboseLogging = box.verboseLogging;

#if UNITY_EDITOR
				using var initMarker = new ProfilerMarker($"Initialize {className}").Auto();
#endif
				var sb = new System.Text.StringBuilder("/" + transform.name);
				Transform aparent = transform.parent;
				while (aparent != null)
				{
					sb.Insert(0, aparent.name).Insert(0, '/');
					aparent = aparent.parent;
				}
				initialLoadPath = sb.ToString();

				if (string.IsNullOrEmpty(className))
				{
					Debug.LogError( $"[CilboxProxy:{gameObject.name}] RuntimeProxyLoad aborted: class {className} was not found in Cilbox assembly data." );
					return;
				}

				cls = box.GetClass( className );

				runtimeFieldsObjects = fieldsObjects != null ? new List<UnityEngine.Object>(fieldsObjects) : new List<UnityEngine.Object>();

				// First thing: Go through any references that are prohibited.
				for( int i = 0; i < runtimeFieldsObjects.Count; i++ )
				{
					UnityEngine.Object o = runtimeFieldsObjects[i];
					if (o == null)
					{
						// If it's null, there's nothing to safety-check.
						continue;
					}
					Type t = o.GetType();
					if(box.GetTypeOverride( t.FullName, out Type overrideType )) {
						Debug.Log( $"RuntimeProxyLoad: Override {t.FullName} with {overrideType.FullName}" );
						t = overrideType;
						if(typeof(CilboxShim).IsAssignableFrom(t) && runtimeFieldsObjects[i] is Component gameObjectComponent)
						{
							GameObject gameObject = gameObjectComponent.gameObject;
							Component component;
							if(gameObject.TryGetComponent(t, out Component c)) {
								component = c;
							} else
							{
								component = gameObject.AddComponent(t);
							}
							runtimeFieldsObjects[i] = component;
						}
					}
					if( t == typeof( CilboxProxy ) )
					{
						// If it's another cilbox proxy, it's OK.
					}
					else if( !box.CheckTypeAllowed( t.FullName ) )
					{
						Debug.LogWarning( $"Contraband found in script {className} field ID {i}: {o.GetType()}" );
						runtimeFieldsObjects[i] = null;
					}
				}

				// Populate fields[]
				int fieldCount = cls.instanceFieldNames.Length;
				fields = new StackElement[fieldCount];

				SerializedProxy proxyData = SerializedProxy.DeserializeString( serializedObjectData );

				SerializedProxyField[] matchingProxyField = new SerializedProxyField[fieldCount];
				foreach( SerializedProxyField spf in proxyData.fields )
				{
					// Go over the root objects, to see which ones slot in and how.
					if( (ProxyFieldType)spf.fieldType != ProxyFieldType.Empty &&
						spf.matchingInstanceId >= 0 &&
						spf.matchingInstanceId < matchingProxyField.Length )
					{
						matchingProxyField[spf.matchingInstanceId] = spf;
					}
				}

				// Preinitialize every field to its CLR default value so that non-serialized fields
				// (especially UnityEngine.Object references) are not left as implicit StackType.Boolean.
				for( int i = 0; i < fieldCount; i++ )
				{
					Type fieldType = cls.instanceFieldTypes[i];
					// Maybe need to GetComponentTypeOverride here as well?  Maybe not, since that should only be for actual UnityEngine.Objects, which should be null at this point if they are contraband.
					if( fieldType == null )
					{
						fields[i].LoadObject( null );
						continue;
					}
					StackType st = StackElement.StackTypeFromType( fieldType );
					if( st < StackType.Object )
					{
						fields[i].type = st;
						if (verboseLogging)
							ProxyDebugLog( $"Default field init {cls.instanceFieldNames[i]} <- default({fieldType})" );
					}
					else if( fieldType.IsValueType )
					{
						try
						{
							// We clean the fieldtype before https://github.com/cnlohr/cilbox/blob/fc608341d293186e0aacf519ea9f0beb43d42cee/Packages/com.cnlohr.cilbox/Cilbox.cs#L1389C40-L1389C67
							object defaultValue = Activator.CreateInstance( fieldType );
							fields[i].LoadObject( defaultValue );
							if (verboseLogging)
								ProxyDebugLog( $"Default field init {cls.instanceFieldNames[i]} <- default({fieldType}) [boxed]" );
						}
						catch( Exception e )
						{
							fields[i].LoadObject( null );
							Debug.LogWarning( $"[CilboxProxy:{gameObject.name}] Failed to create default value for {cls.instanceFieldNames[i]} ({fieldType}): {e.Message}" );
						}
					}
					else if( fieldType == typeof(string) )
					{
						// Empty/unset string fields default to string.Empty (matches Unity), not null.
						fields[i].LoadObject( string.Empty );
						if (verboseLogging)
							ProxyDebugLog( $"Default field init {cls.instanceFieldNames[i]} <- string.Empty" );
					}
					else
					{
						fields[i].LoadObject( null );
						if (verboseLogging)
							ProxyDebugLog( $"Default field init {cls.instanceFieldNames[i]} <- null" );
					}
				}

				// Call interpreted constructor.
				box.InterpretIID( cls, this, ImportFunctionID.dotCtor, null );

				// load serialized fields.
				for( int i = 0; i < fieldCount; i++ )
				{
					SerializedProxyField spf = matchingProxyField[i];

					if( spf == null ) { /* Debug.Log( $"Skipping {i} {cls.instanceFieldNames[i]}" ); */ continue; }

					if( (ProxyFieldType)spf.fieldType == ProxyFieldType.Primitive )
					{
						spf.primitiveValue.ToStackElement(ref fields[i]);
						continue;
					}

					LoadProxyFieldStackElement( spf, ref fields[i], cls.instanceFieldNames[i] );
				}


				proxyWasSetup = true;
				runtimeFieldsObjects = null;
				serializedObjectData = null;
				if (verboseLogging)
					Debug.Log( $"RuntimeProxyLoad complete for class {className}" );
			}
			finally
			{
				proxyLoadInProgress = false;
			}
		}


		// Loads the reference StackElement with the appropriate data
		private void LoadProxyFieldStackElement( SerializedProxyField spf, ref StackElement refElement, String rootFieldName )
		{
			ProxyFieldType ft = (ProxyFieldType)spf.fieldType;
			List<UnityEngine.Object> objectSlots = runtimeFieldsObjects ?? fieldsObjects;

			switch (ft)
			{
			case ProxyFieldType.CilboxRef:
			case ProxyFieldType.ObjectRef:
			{
				int iFO = spf.fieldObjectIndex;
				if (iFO < 0 || iFO >= objectSlots.Count) // break early if index is out of bounds
				{
					Debug.LogWarning(
						$"Failure to load object in field id:{rootFieldName} of {className} (slot out of range, fieldsObjects count={objectSlots.Count})");
					break;
				}

				if (spf.objectRefIsNull)
				{
					// This field was null when serialized, so just load null
					refElement.LoadObject(null);
					return;
				}

				UnityEngine.Object o = objectSlots[iFO];

				if (o)
				{
					if (o is CilboxProxy cilboxProxy)
						cilboxProxy.RuntimeProxyLoad();

					refElement.LoadObject(o);

					// Remove reference out of the fieldsObjects array.
					objectSlots[iFO] = null;

					return;
				}

				Debug.LogWarning(
					$"[CilboxProxy:{gameObject.name}] Object reference slot {iFO} for field {rootFieldName} is null/missing at load time.");

				break;
			}

			case ProxyFieldType.Array:
			{
				Type t = box.usage.GetNativeTypeFromDescriptor( spf.elementType );
				bool isCilboxElementType = false;

				if (t == null)
				{
					if (box.classes.ContainsKey(spf.elementType.typeName))
					{
						// Check the array to see if it is Cilboxed
						t = typeof(CilboxProxy);
						isCilboxElementType = true;
					}
					else // type is null and not a Cilbox class
					{
						refElement.LoadObject(null);
						return;
					}
				}

				if( !isCilboxElementType && !box.CheckTypeAllowed( t.ToString() ) )
				{
					proxyWasSetup = false;
					throw new Exception( $"Contraband ARRAY found in script {className} field {rootFieldName}" );
				}

				int aLen = spf.arrayElements.Length;
				Array arr = Array.CreateInstance( t, aLen );

				StackType elementSt = default;
				bool isPrimArr = !isCilboxElementType
					&& StackElement.TypeToStackType.TryGetValue(t.ToString(), out elementSt)
					&& elementSt < StackType.Object;

				if( isPrimArr )
				{
					// Typed array fill — one cast outside the loop, direct typed writes inside. Zero boxes.
					switch( elementSt )
					{
					case StackType.Boolean: { var a = (bool[])  arr; for (int j = 0; j < aLen; j++) a[j] =         spf.arrayElements[j].primitiveValue.b; break; }
					case StackType.Sbyte:   { var a = (sbyte[]) arr; for (int j = 0; j < aLen; j++) a[j] = (sbyte) spf.arrayElements[j].primitiveValue.l; break; }
					case StackType.Byte:    { var a = (byte[])  arr; for (int j = 0; j < aLen; j++) a[j] = (byte)  spf.arrayElements[j].primitiveValue.e; break; }
					case StackType.Short:   { var a = (short[]) arr; for (int j = 0; j < aLen; j++) a[j] = (short) spf.arrayElements[j].primitiveValue.l; break; }
					case StackType.Ushort:  { var a = (ushort[])arr; for (int j = 0; j < aLen; j++) a[j] = (ushort)spf.arrayElements[j].primitiveValue.e; break; }
					case StackType.Int:     { var a = (int[])   arr; for (int j = 0; j < aLen; j++) a[j] = (int)   spf.arrayElements[j].primitiveValue.l; break; }
					case StackType.Uint:    { var a = (uint[])  arr; for (int j = 0; j < aLen; j++) a[j] = (uint)  spf.arrayElements[j].primitiveValue.e; break; }
					case StackType.Long:    { var a = (long[])  arr; for (int j = 0; j < aLen; j++) a[j] =         spf.arrayElements[j].primitiveValue.l; break; }
					case StackType.Ulong:   { var a = (ulong[]) arr; for (int j = 0; j < aLen; j++) a[j] =         spf.arrayElements[j].primitiveValue.e; break; }
					case StackType.Float:   { var a = (float[]) arr; for (int j = 0; j < aLen; j++) a[j] =         spf.arrayElements[j].primitiveValue.f; break; }
					case StackType.Double:  { var a = (double[])arr; for (int j = 0; j < aLen; j++) a[j] =         spf.arrayElements[j].primitiveValue.d; break; }
					}
				}
				else // non-primitive array
				{
					for( int j = 0; j < aLen; j++ )
					{
						StackElement temp = default;
						LoadProxyFieldStackElement( spf.arrayElements[j], ref temp, rootFieldName );
						arr.SetValue( temp.AsObject(), j );
					}
				}

				refElement.LoadObject(arr);
				return;
			}

			case ProxyFieldType.String:
			{
				refElement.LoadObject(spf.data);
				return;
			}

			case ProxyFieldType.Primitive:
			{
				spf.primitiveValue.ToStackElement(ref refElement);
				return;
			}

			case ProxyFieldType.Json:
			{
				Type t = box.usage.GetNativeTypeFromDescriptor( spf.elementType );
				refElement.LoadObject(JsonUtility.FromJson(spf.data, t));
				return;
			}

			case ProxyFieldType.Empty:
			default:
				break;
			}

			refElement.LoadObject(null);
		}


		void Start() {
			RuntimeProxyLoad();

			if( proxyWasSetup ) {
				// Call Awake after initialization.
				box.InterpretIID( cls, this, ImportFunctionID.Awake, null );
				box.InterpretIID( cls, this, ImportFunctionID.Start, null );
			}
		}
		void FixedUpdate() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.FixedUpdate, null ); }
		void Update() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.Update, null ); }
		void LateUpdate() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.LateUpdate, null ); }
		void OnEnable() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.OnEnable, null ); }
		void OnDisable() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.OnDisable, null ); }
		void OnDestroy() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.OnDestroy, null ); }
		void OnTriggerEnter(Collider c) { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnTriggerEnter, new object[] { c }); }
		void OnTriggerExit(Collider c) { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnTriggerExit, new object[] { c }); }
		void OnCollisionEnter(Collision c) { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnCollisionEnter, new object[] { c }); }
		void OnCollisionExit(Collision c) { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnCollisionExit, new object[] { c }); }
		void OnTriggerStay(Collider c) { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnTriggerStay, new object[] { c }); }
		void OnCollisionStay(Collision c) { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnCollisionStay, new object[] { c }); }
		void OnRenderObject() { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnRenderObject, null); }
		void OnWillRenderObject() { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnWillRenderObject, null); }
	}
}

