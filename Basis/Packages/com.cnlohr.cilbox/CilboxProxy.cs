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
			FieldInfo[] fi = mToSteal.GetType().GetFields( BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance );

			List< Serializee > lstObjects = new List< Serializee >();

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

				Serializee e = SerializeThing( fv, f.Name, matchingInstanceNameID, ref refToProxyMap );

				// Serialize no matter what.
				lstObjects.Add( e );
			}

			serializedObjectData =
				Convert.ToBase64String(new Serializee(lstObjects.ToArray()).DumpAsMemory().ToArray());

			buildTimeGuid = Guid.NewGuid().ToString();

			//Debug.Log( $"SERIALIZE --> {serializedObjectData}" );
		}


		private Serializee SerializeThing( object fv, String fName, int matchingInstanceNameID, ref Dictionary< MonoBehaviour, CilboxProxy > refToProxyMap )
		{
			Dictionary<String, Serializee> instanceFields = new Dictionary<String, Serializee>();

			// "n" is the "name" of the variable, only useful for object-root objects.
			// "miid" = Field ID.
			// "t" = Type
			// "d" = Data (If applicable) (For strings and StackElements)

			// Skip null objects.
			if( fv == null )
			{
				instanceFields["empty"] = new Serializee( "true" );
				return new Serializee(instanceFields);
			}

			bool hasCilboxable = CilboxUtil.HasCilboxableAttribute( fv.GetType() );


			if( fName != null )
			{
				instanceFields["n"] = new Serializee(fName);
			}

			if( matchingInstanceNameID >= 0 )
			{
				instanceFields["miid"] = new Serializee(matchingInstanceNameID.ToString());
			}

			StackType st;

			// Serialize enum field as underlying type
			if( fv.GetType().IsEnum )
			{
				object underlying = Convert.ChangeType( fv, fv.GetType().GetEnumUnderlyingType() );
				if( StackElement.TypeToStackType.TryGetValue( underlying.GetType().ToString(), out st ) && st < StackType.Object )
				{
					instanceFields["d"] = new Serializee(underlying.ToString());
					instanceFields["t"] = new Serializee("e" + st);
				}
			}
			// Not a proxiable script.
			else if (hasCilboxable)
			{
				// This is a cilboxable thing.
				instanceFields["fo"] = new Serializee(fieldsObjects.Count.ToString());
				fieldsObjects.Add( refToProxyMap[(MonoBehaviour)fv] );
				instanceFields["t"] = new Serializee("cba");
				instanceFields["or"] = new Serializee(fv.ToString());
			}
			else if( fv is UnityEngine.Object )
			{
				instanceFields["fo"] = new Serializee(fieldsObjects.Count.ToString());
				fieldsObjects.Add( (UnityEngine.Object)fv );
				instanceFields["t"] = new Serializee("obj");
				instanceFields["or"] = new Serializee(fv.ToString());
			}
			else if( fv is string )
			{
				instanceFields["d"] = new Serializee(fv.ToString());
				instanceFields["t"] = new Serializee("s");
			}
			else if( StackElement.TypeToStackType.TryGetValue( fv.GetType().ToString(), out st ) && st < StackType.Object )
			{
				instanceFields["d"] = new Serializee(fv.ToString());
				instanceFields["t"] = new Serializee("e" + st);
			}
			else if( fv.GetType().IsArray )
			{
				instanceFields["t"] = new Serializee( "a" );
				Type type = fv.GetType().GetElementType();
				instanceFields["at"] = CilboxUtil.GetSerializeeFromNativeType( type );
				Array arr = (Array)fv;
				int len = arr.Length;
				instanceFields["al"] = new Serializee( len.ToString() );

				Serializee [] sel = new Serializee[len];
				int i;
				for( i = 0; i < len; i++ )
				{
					object o = arr.GetValue(i);
					sel[i] = SerializeThing( o, null, -1, ref refToProxyMap );
				}
				instanceFields["ad"] = new Serializee( sel );
			}
			else
			{
				string json = JsonUtility.ToJson(fv);
				instanceFields["t"] = new Serializee( "j" );
				instanceFields["d"] = new Serializee(json);
				Type type = fv.GetType();
				instanceFields["at"] = CilboxUtil.GetSerializeeFromNativeType( type );
			}


			return new Serializee(instanceFields);
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
			if (box == null) return;
			box.BoxInitialize(); // In case it is not yet initialized.
			bool verboseLogging = box.verboseLogging;

#if UNITY_EDITOR
			new ProfilerMarker($"Initialize {className}").Auto();
#endif
            var sb = new System.Text.StringBuilder("/" + transform.name);
            Transform aparent = transform.parent;
            while (aparent != null)
            {
                sb.Insert(0, aparent.name).Insert(0, '/');
                aparent = aparent.parent;
            }
            initialLoadPath = sb.ToString();

            Debug.Log($"initialLoadPath {initialLoadPath}");
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

			Serializee [] d = new Serializee( Convert.FromBase64String( serializedObjectData ), Serializee.ElementType.Map ).AsArray();

			Serializee [] matchingSerializeeInstanceField = new Serializee[fieldCount];
			Dictionary< String, Serializee > [] matchingDicts = new Dictionary< String, Serializee >[fieldCount];
			foreach( Serializee s in d )
			{
				// Go over the root objects, to see which ones slot in and how.
				// Cache the parsed dict so the load pass below doesn't reparse.
				Dictionary< String, Serializee > dict = s.AsMap();
				if( dict.ContainsKey( "t" ) && dict.TryGetValue( "miid", out Serializee miid ) )
				{
					if( UInt32.TryParse( miid.AsString(), out UInt32 nMIID ) &&
						nMIID < fieldCount )
					{
						matchingSerializeeInstanceField[nMIID] = s;
						matchingDicts[nMIID] = dict;
					}
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
				Serializee s = matchingSerializeeInstanceField[i];

				if( s == null ) { /* Debug.Log( $"Skipping {i} {cls.instanceFieldNames[i]}" ); */ continue; }

				object o;
				bool bIsObject = LoadObjectFromSerializee( s, out o, cls.instanceFieldNames[i], cls.instanceFieldTypes[i], true, matchingDicts[i] );
				if( bIsObject )
					fields[i].LoadObject( o );
				else
					fields[i].Load( o );
			}


			proxyWasSetup = true;
			runtimeFieldsObjects = null;
			serializedObjectData = null;
			if (verboseLogging)
				Debug.Log( $"RuntimeProxyLoad complete for class {className}" );
		}


		// Returns: true if is object, otherwise is primitive.
		// `dict` may be supplied by callers that already parsed the Serializee to avoid a redundant AsMap allocation.
		private bool LoadObjectFromSerializee( Serializee s, out object oOut, String rootFieldName, Type inType, bool root, Dictionary< String, Serializee > dict = null )
		{
			List<UnityEngine.Object> objectSlots = runtimeFieldsObjects ?? fieldsObjects;
			if( dict == null ) dict = s.AsMap();

			Serializee setype;
			if( dict.TryGetValue( "t", out setype ) )
			{
				String sT = setype.AsString();
				if( sT == "cba" || sT == "obj" )
				{
					Serializee seFO;
					int iFO;
					if( dict.TryGetValue( "fo", out seFO ) &&
						Int32.TryParse( seFO.AsString(), out iFO ) &&
						objectSlots != null &&
						iFO < objectSlots.Count )
					{
						if (dict.TryGetValue("or", out var seOr))
						{
							if (seOr.AsString() == "null")
							{
								// This field was null when serialized, so just return null
								oOut = null;
								return true;
							}
						}

						UnityEngine.Object o = objectSlots[iFO];

						//Debug.Log( $"LOADING FIELD: {i} with {o}" );
						if( o )
						{
							if( o is CilboxProxy )
								((CilboxProxy)o).RuntimeProxyLoad();

							oOut = o;

							// Remove reference out of the fieldsObjects array.
							objectSlots[iFO] = null;

							return true;
						}
						Debug.LogWarning( $"[CilboxProxy:{gameObject.name}] Object reference slot {iFO} for field {rootFieldName} is null/missing at load time." );
					}
					else
					{
						int objectSlotCount = objectSlots != null ? objectSlots.Count : 0;
						Debug.LogWarning( $"Failure to load object in field id:{rootFieldName} of {className} (slot parse failed or out of range, fieldsObjects count={objectSlotCount})");
					}
				}
				else if( sT[0] == 'a' )
				{
					Serializee seT, seAT, seAL, seAD;
					int aLen;
					if( dict.TryGetValue( "t", out seT ) &&
						dict.TryGetValue( "at", out seAT ) &&
						dict.TryGetValue( "al", out seAL ) &&
						dict.TryGetValue( "ad", out seAD ) &&
						Int32.TryParse( seAL.AsString(), out aLen ) )
					{
						Type t = box.usage.GetNativeTypeFromSerializee( seAT );
						bool isCilboxElementType = false;

						if (t == null)
						{
							// Check the array to see if it is Cilboxed
							Dictionary<String, Serializee> atMap = seAT.AsMap();
							String elementTypeName = atMap["n"].AsString();
							if (box.classes.ContainsKey(elementTypeName))
							{
								t = typeof(CilboxProxy);
								isCilboxElementType = true;
							}
							else
							{
								oOut = null;
								return true;
							}
						}

						if( !isCilboxElementType && !box.CheckTypeAllowed( t.ToString() ) )
						{
							proxyWasSetup = false;
							throw new Exception( "Contraband ARRAY found in script {className} { cls.instanceFieldNames[i] }" );
						}

						Array arr = Array.CreateInstance( t, aLen );

						// Hoist AsArray out of the loop — it previously re-parsed seAD and allocated
						// a fresh Serializee[] of size aLen on every iteration (O(aLen^2) GC).
						Serializee [] adArr = seAD.AsArray();
						for( int j = 0; j < aLen; j++ )
						{
							object o;
							LoadObjectFromSerializee( adArr[j], out o, rootFieldName, t, false );
							arr.SetValue( o, j );
						}

						oOut = arr;
						return true;
					}
				}
				else if( sT[0] == 's' || sT[0] == 'e' )
				{
					Serializee dsee;
					if( dict.TryGetValue( "d", out dsee ) )
					{
						oOut = CilboxUtil.DeserializeDataForProxyField( inType, dsee.AsString() );
						return false;
					}
				}
				else if ( sT[0] == 'j' )
				{

					Serializee seT, seAT, seD;
					if( dict.TryGetValue( "t", out seT ) &&
						dict.TryGetValue( "at", out seAT ) &&
						dict.TryGetValue( "d", out seD ) )
					{
						Type t = box.usage.GetNativeTypeFromSerializee( seAT ); // This makes sure we're allowed to have this type.
						oOut = JsonUtility.FromJson(seD.AsString(), t);
						return true;
					}
					else
					{
						Debug.LogWarning( $"Failure to load object in field id:{rootFieldName} of {className}");
					}
				}
				else
				{
					Debug.LogWarning( $"Unknown field type {sT} on field {rootFieldName}" );
				}
			}
			//Debug.Log( $"{i} Output Type:{fields[i].type} Name:{cls.instanceFieldNames[i]} C# field Name:{cls.instanceFieldTypes[i]} Type:{fields[i].type} Value:{((fields[i].type<StackType.Object)?(fields[i].i):(fields[i].o))}" );
			oOut = null;
			return false;
		}


		void Start() {
			RuntimeProxyLoad();

			// Call Awake after initialization.
			box.InterpretIID( cls, this, ImportFunctionID.Awake, null );
			box.InterpretIID( cls, this, ImportFunctionID.Start, null );
		}
		void FixedUpdate() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.FixedUpdate, null ); }
		void Update() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.Update, null ); }
		void OnEnable() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.OnEnable, null ); }
		void OnDisable() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.OnDisable, null ); }
		void OnDestroy() { if( proxyWasSetup ) box.InterpretIID( cls, this, ImportFunctionID.OnDestroy, null ); }
		void OnTriggerEnter(Collider c) { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnTriggerEnter, new object[] { c }); }
		void OnTriggerExit(Collider c) { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnTriggerExit, new object[] { c }); }
		void OnCollisionEnter(Collision c) { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnCollisionEnter, new object[] { c }); }
		void OnCollisionExit(Collision c) { if (proxyWasSetup) box.InterpretIID(cls, this, ImportFunctionID.OnCollisionExit, new object[] { c }); }
	}
}

