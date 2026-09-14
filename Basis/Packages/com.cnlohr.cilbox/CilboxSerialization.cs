using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Cilbox
{
	public class SerializedTypeDescriptor
	{
		public string assemblyName;
		public string typeName;
		// Generic fields (present when genericArgs token written)
		public string genericName;
		public SerializedTypeDescriptor[] genericArgs;
		// Underlying type (present when underlyingType token written)
		public SerializedTypeDescriptor underlyingType;

		public bool IsGeneric => genericArgs != null && genericArgs.Length > 0;
		public bool HasUnderlyingType => underlyingType != null;

		// Mirrors CilboxUtil.GetSerializeeFromNativeType key order: a, then
		// (n, gn, g) for generics or (n, [ut]) otherwise.
		public Serializee ToSerializee()
		{
			Dictionary<String, Serializee> ret = new Dictionary<String, Serializee>();
			ret["a"] = new Serializee( assemblyName );
			if( IsGeneric )
			{
				ret["n"] = new Serializee( typeName );
				ret["gn"] = new Serializee( genericName );
				Serializee[] sg = new Serializee[genericArgs.Length];
				for( int i = 0; i < genericArgs.Length; i++ )
					sg[i] = genericArgs[i].ToSerializee();
				ret["g"] = new Serializee( sg );
			}
			else
			{
				ret["n"] = new Serializee( typeName );
				if( underlyingType != null )
					ret["ut"] = underlyingType.ToSerializee();
			}
			return new Serializee( ret );
		}

		public static SerializedTypeDescriptor FromSerializee( Serializee s )
		{
			Dictionary<String, Serializee> m = s.AsMap();
			SerializedTypeDescriptor td = new SerializedTypeDescriptor();
			td.assemblyName = m["a"].AsString();
			td.typeName = m["n"].AsString();
			if( m.TryGetValue( "g", out Serializee g ) )
			{
				td.genericName = m["gn"].AsString();
				Serializee[] ga = g.AsArray();
				td.genericArgs = new SerializedTypeDescriptor[ga.Length];
				for( int i = 0; i < ga.Length; i++ )
					td.genericArgs[i] = FromSerializee( ga[i] );
			}
			if( m.TryGetValue( "ut", out Serializee ut ) )
				td.underlyingType = FromSerializee( ut );
			return td;
		}

		public static SerializedTypeDescriptor[] FromSerializeeArray(Serializee s)
		{
			Serializee[] sArr = s.AsArray();
			SerializedTypeDescriptor[] tdArr = new SerializedTypeDescriptor[sArr.Length];
			for (int i = 0; i < sArr.Length; i++)
			{
				tdArr[i] = FromSerializee(sArr[i]);
			}
			return tdArr;
		}
	}

	public class SerializedField
	{
		public string name;
		public SerializedTypeDescriptor type;

		// The same struct is serialized with two different type-key schemes:
		//  - class fields use "type"
		//  - method locals / parameters use "dt"
		private Serializee ToSerializeeWithTypeKey( String typeKey )
		{
			Dictionary<String, Serializee> ret = new Dictionary<String, Serializee>();
			ret["name"] = new Serializee( name );
			ret[typeKey] = type.ToSerializee();
			return new Serializee( ret );
		}

		public Serializee ToClassFieldSerializee() => ToSerializeeWithTypeKey( "type" );
		public Serializee ToMethodFieldSerializee() => ToSerializeeWithTypeKey( "dt" );

		private static SerializedField FromSerializeeWithTypeKey( Serializee s, String typeKey )
		{
			Dictionary<String, Serializee> m = s.AsMap();
			SerializedField f = new SerializedField();
			f.name = m["name"].AsString();
			f.type = SerializedTypeDescriptor.FromSerializee( m[typeKey] );
			return f;
		}

		public static SerializedField FromClassFieldSerializee( Serializee s ) => FromSerializeeWithTypeKey( s, "type" );
		public static SerializedField FromMethodFieldSerializee( Serializee s ) => FromSerializeeWithTypeKey( s, "dt" );
	}

	public class SerializedExceptionHandler
	{
		public int flags;
		public int tryOffset;
		public int tryLength;
		public int handlerOffset;
		public int handlerLength;
		public bool hasCatchType;
		public SerializedTypeDescriptor catchType;

		public Serializee ToSerializee()
		{
			Dictionary<String, Serializee> ret = new Dictionary<String, Serializee>();
			ret["flags"] = new Serializee(flags.ToString());
			ret["tryOff"] = new Serializee(tryOffset.ToString());
			ret["tryLen"] = new Serializee(tryLength.ToString());
			ret["hOff"] = new Serializee(handlerOffset.ToString());
			ret["hLen"] = new Serializee(handlerLength.ToString());
			if (hasCatchType)
				ret["cType"] = catchType.ToSerializee();
			return new Serializee(ret);
		}

		public static SerializedExceptionHandler FromSerializee(Serializee s)
		{
			Dictionary<String, Serializee> m = s.AsMap();
			SerializedExceptionHandler eh = new SerializedExceptionHandler();
			eh.flags = Int32.Parse(m["flags"].AsString());
			eh.tryOffset = Int32.Parse(m["tryOff"].AsString());
			eh.tryLength = Int32.Parse(m["tryLen"].AsString());
			eh.handlerOffset = Int32.Parse(m["hOff"].AsString());
			eh.handlerLength = Int32.Parse(m["hLen"].AsString());
			if (m.TryGetValue("cType", out Serializee cType))
			{
				eh.hasCatchType = true;
				eh.catchType = SerializedTypeDescriptor.FromSerializee( cType );
			}

			return eh;
		}
	}

	public class SerializedMethod
	{
		public string methodName;
		public int maxStack;
		public bool isVoid;
		public bool isStatic;
		public bool isCtor;
		public string fullSignature;
		public byte[] body;
		public SerializedField[] locals;
		public SerializedField[] parameters;
		public SerializedExceptionHandler[] exceptionHandlers;

		// for legacy serializations, methodName is the enclosing Map key, not part of this map.
		// Master MethodProps order: body, [eh], locals, parameters, maxStack,
		// isVoid, isCtor, isStatic, name, fullSignature.
		public Serializee ToSerializee()
		{
			Dictionary<String, Serializee> ret = new Dictionary<String, Serializee>();
			ret["body"] = Serializee.CreateFromBlob( body );

			if( exceptionHandlers != null && exceptionHandlers.Length > 0 )
			{
				Serializee[] eh = new Serializee[exceptionHandlers.Length];
				for( int i = 0; i < exceptionHandlers.Length; i++ )
					eh[i] = exceptionHandlers[i].ToSerializee();
				ret["eh"] = new Serializee( eh );
			}

			Serializee[] loc = new Serializee[locals.Length];
			for( int i = 0; i < locals.Length; i++ )
				loc[i] = locals[i].ToMethodFieldSerializee();
			ret["locals"] = new Serializee( loc );

			Serializee[] par = new Serializee[parameters.Length];
			for( int i = 0; i < parameters.Length; i++ )
				par[i] = parameters[i].ToMethodFieldSerializee();
			ret["parameters"] = new Serializee( par );

			ret["maxStack"] = new Serializee( maxStack.ToString() );
			ret["isVoid"] = new Serializee( isVoid ? "1" : "0" );
			ret["isCtor"] = new Serializee( isCtor ? "1" : "0" );
			ret["isStatic"] = new Serializee( isStatic ? "1" : "0" );
			ret["name"] = new Serializee( methodName );
			ret["fullSignature"] = new Serializee( fullSignature );
			return new Serializee( ret );
		}

		public static SerializedMethod FromSerializee( Serializee s, String methodName )
		{
			Dictionary<String, Serializee> m = s.AsMap();
			SerializedMethod sm = new SerializedMethod();
			sm.methodName = methodName;
			sm.body = m["body"].AsBlob();

			if( m.TryGetValue( "eh", out Serializee ehS ) )
			{
				Serializee[] eh = ehS.AsArray();
				sm.exceptionHandlers = new SerializedExceptionHandler[eh.Length];
				for( int i = 0; i < eh.Length; i++ )
					sm.exceptionHandlers[i] = SerializedExceptionHandler.FromSerializee( eh[i] );
			}
			else
			{
				sm.exceptionHandlers = new SerializedExceptionHandler[0];
			}

			Serializee[] loc = m["locals"].AsArray();
			sm.locals = new SerializedField[loc.Length];
			for( int i = 0; i < loc.Length; i++ )
				sm.locals[i] = SerializedField.FromMethodFieldSerializee( loc[i] );

			Serializee[] par = m["parameters"].AsArray();
			sm.parameters = new SerializedField[par.Length];
			for( int i = 0; i < par.Length; i++ )
				sm.parameters[i] = SerializedField.FromMethodFieldSerializee( par[i] );

			if( m.TryGetValue("isCtor", out Serializee isCtorSer) )
			{
				sm.isCtor = Convert.ToInt32(isCtorSer.AsString()) != 0;
			}
			else
			{
				// Backward compatibility for payloads generated before isCtor existed.
				sm.isCtor = methodName == ".ctor" || methodName == ".cctor" || sm.fullSignature.StartsWith("Void .ctor(") || sm.fullSignature.StartsWith("Void .cctor(");
			}

			sm.maxStack = Int32.Parse( m["maxStack"].AsString() );
			sm.isVoid = Int32.Parse( m["isVoid"].AsString() ) != 0;
			sm.isStatic = Int32.Parse( m["isStatic"].AsString() ) != 0;
			sm.fullSignature = m["fullSignature"].AsString();
			if (m.TryGetValue( "name", out Serializee nameS))
			{
				sm.methodName = nameS.AsString();
			}
			return sm;
		}
	}

	public class SerializedClass
	{
		public string className;
		public SerializedField[] staticFields;
		public SerializedField[] instanceFields;
		public SerializedMethod[] methods;
		public string[] baseClassNames;

		// className is the enclosing Map key. Master classProps order:
		// staticFields, instanceFields, methods (methods is a Map keyed by
		// method name, which collapses overloads to last-value/first-position).
		public Serializee ToSerializee()
		{
			Dictionary<String, Serializee> ret = new Dictionary<String, Serializee>();

			Serializee[] sf = new Serializee[staticFields.Length];
			for( int i = 0; i < staticFields.Length; i++ )
				sf[i] = staticFields[i].ToClassFieldSerializee();
			ret["staticFields"] = new Serializee( sf );

			Serializee[] inf = new Serializee[instanceFields.Length];
			for( int i = 0; i < instanceFields.Length; i++ )
				inf[i] = instanceFields[i].ToClassFieldSerializee();
			ret["instanceFields"] = new Serializee( inf );

			Dictionary<String, Serializee> mm = new Dictionary<String, Serializee>();
			for( int i = 0; i < methods.Length; i++ )
				mm[methods[i].fullSignature] = methods[i].ToSerializee();
			ret["methods"] = new Serializee( mm );

			List<Serializee> bcn = new List<Serializee>();
			foreach (string bc in baseClassNames)
			{
				bcn.Add(new Serializee(bc));
			}
			ret["baseClasses"] = new Serializee(bcn.ToArray());

			return new Serializee( ret );
		}

		public static SerializedClass FromSerializee( Serializee s, String className )
		{
			Dictionary<String, Serializee> m = s.AsMap();
			SerializedClass sc = new SerializedClass();
			sc.className = className;

			Serializee[] sf = m["staticFields"].AsArray();
			sc.staticFields = new SerializedField[sf.Length];
			for( int i = 0; i < sf.Length; i++ )
				sc.staticFields[i] = SerializedField.FromClassFieldSerializee( sf[i] );

			Serializee[] inf = m["instanceFields"].AsArray();
			sc.instanceFields = new SerializedField[inf.Length];
			for( int i = 0; i < inf.Length; i++ )
				sc.instanceFields[i] = SerializedField.FromClassFieldSerializee( inf[i] );

			Dictionary<String, Serializee> mm = m["methods"].AsMap();
			sc.methods = new SerializedMethod[mm.Count];
			int mi = 0;
			foreach( KeyValuePair<String, Serializee> kv in mm )
				sc.methods[mi++] = SerializedMethod.FromSerializee( kv.Value, kv.Key );

			if (m.TryGetValue("baseClasses", out Serializee baseClassesSer))
			{
				Serializee [] bcArr = baseClassesSer.AsArray();
				sc.baseClassNames = new String[bcArr.Length];
				for( int bci = 0; bci < bcArr.Length; bci++ )
					sc.baseClassNames[bci] = bcArr[bci].AsString();
			}
			else
			{
				sc.baseClassNames = new String[0];
			}

			return sc;
		}
	}

	public class SerializedMetadataToken
	{
		// metaTokenIndex is a sequential index for the token particular to this Cilbox.
		// Bytecode operands for this Cilbox reference this value instead of the original CIL one.
		public int metaTokenIndex;
		public byte metaTokenType; // MetaTokenType enum value

		// mtString
		public string stringValue;

		// mtArrayInitializer
		public byte[] arrayInitData;

		// mtType, mtField, mtMethod
		public SerializedTypeDescriptor typeDescriptor;

		// mtField, mtMethod
		public string name;
		public bool isStatic;

		// mtField
		public bool fieldHasIndex;
		public int fieldIndex;

		// mtMethod
		public string methodFullSignature;
		public string methodAssembly;
		public SerializedTypeDescriptor[] methodParameters;
		public SerializedTypeDescriptor[] methodGenericArguments;

		public Serializee ToSerializee()
		{
			Dictionary<String, Serializee> ret = new Dictionary<String, Serializee>();
			MetaTokenType mt = (MetaTokenType)metaTokenType;
			String mtStr = ((int)metaTokenType).ToString();

			switch( mt )
			{
				case MetaTokenType.mtArrayInitializer:
					ret["mt"] = new Serializee( mtStr );
					ret["data"] = Serializee.CreateFromBlob( arrayInitData );
					break;
				case MetaTokenType.mtString:
					ret["mt"] = new Serializee( mtStr );
					ret["s"] = new Serializee( stringValue );
					break;
				case MetaTokenType.mtMethod:
					// Master order: [ga], dt, name, [parameters], fullSignature,
					// isStatic, assembly, mt (mt is written LAST for methods).
					if( methodGenericArguments != null && methodGenericArguments.Length > 0 )
					{
						Serializee[] ga = new Serializee[methodGenericArguments.Length];
						for( int i = 0; i < methodGenericArguments.Length; i++ )
							ga[i] = methodGenericArguments[i].ToSerializee();
						ret["ga"] = new Serializee( ga );
					}
					ret["dt"] = typeDescriptor.ToSerializee();
					ret["name"] = new Serializee( name );
					if( methodParameters != null && methodParameters.Length > 0 )
					{
						Serializee[] par = new Serializee[methodParameters.Length];
						for( int i = 0; i < methodParameters.Length; i++ )
							par[i] = methodParameters[i].ToSerializee();
						ret["parameters"] = new Serializee( par );
					}
					ret["fullSignature"] = new Serializee( methodFullSignature );
					ret["isStatic"] = new Serializee( isStatic ? "1" : "0" );
					ret["assembly"] = new Serializee( methodAssembly );
					ret["mt"] = new Serializee( mtStr );
					break;
				case MetaTokenType.mtField:
					// Master order: mt, dt, name, isStatic, [index] (index appended last).
					ret["mt"] = new Serializee( mtStr );
					ret["dt"] = typeDescriptor.ToSerializee();
					ret["name"] = new Serializee( name );
					ret["isStatic"] = new Serializee( isStatic ? "1" : "0" );
					if( fieldHasIndex )
						ret["index"] = new Serializee( fieldIndex.ToString() );
					break;
				case MetaTokenType.mtType:
					ret["mt"] = new Serializee( mtStr );
					ret["dt"] = typeDescriptor.ToSerializee();
					break;
			}
			return new Serializee( ret );
		}

		public static SerializedMetadataToken FromSerializee( Serializee s, string midStr )
		{
			Dictionary<String, Serializee> m = s.AsMap();
			SerializedMetadataToken t = new SerializedMetadataToken();
			t.metaTokenIndex = Int32.Parse( midStr );
			t.metaTokenType = (byte)Int32.Parse( m["mt"].AsString() );
			MetaTokenType mt = (MetaTokenType)t.metaTokenType;

			switch( mt )
			{
				case MetaTokenType.mtArrayInitializer:
					t.arrayInitData = m["data"].AsBlob();
					break;
				case MetaTokenType.mtString:
					t.stringValue = m["s"].AsString();
					break;
				case MetaTokenType.mtMethod:
					t.typeDescriptor = SerializedTypeDescriptor.FromSerializee( m["dt"] );
					t.name = m["name"].AsString();
					t.methodFullSignature = m["fullSignature"].AsString();
					t.isStatic = Int32.Parse( m["isStatic"].AsString() ) != 0;
					t.methodAssembly = m["assembly"].AsString();
					if( m.TryGetValue( "parameters", out Serializee parS ) )
					{
						Serializee[] par = parS.AsArray();
						t.methodParameters = new SerializedTypeDescriptor[par.Length];
						for( int i = 0; i < par.Length; i++ )
							t.methodParameters[i] = SerializedTypeDescriptor.FromSerializee( par[i] );
					}
					else
					{
						t.methodParameters = new SerializedTypeDescriptor[0];
					}
					if( m.TryGetValue( "ga", out Serializee gaS ) )
					{
						Serializee[] ga = gaS.AsArray();
						t.methodGenericArguments = new SerializedTypeDescriptor[ga.Length];
						for( int i = 0; i < ga.Length; i++ )
							t.methodGenericArguments[i] = SerializedTypeDescriptor.FromSerializee( ga[i] );
					}
					else
					{
						t.methodGenericArguments = new SerializedTypeDescriptor[0];
					}
					break;
				case MetaTokenType.mtField:
					t.typeDescriptor = SerializedTypeDescriptor.FromSerializee( m["dt"] );
					t.name = m["name"].AsString();
					t.isStatic = Int32.Parse( m["isStatic"].AsString() ) != 0;
					if( m.TryGetValue( "index", out Serializee idx ) )
					{
						t.fieldHasIndex = true;
						t.fieldIndex = Int32.Parse( idx.AsString() );
					}
					break;
				case MetaTokenType.mtType:
					t.typeDescriptor = SerializedTypeDescriptor.FromSerializee( m["dt"] );
					break;
			}
			return t;
		}
	}

	public class SerializedEnumValue
	{
		public string name;
		public long value;

		public Serializee ToSerializee()
		{
			Dictionary<String, Serializee> ret = new Dictionary<String, Serializee>();
			ret["n"] = new Serializee( name );
			ret["v"] = new Serializee( value.ToString() );
			return new Serializee( ret );
		}

		public static SerializedEnumValue FromSerializee( Serializee s )
		{
			Dictionary<String, Serializee> m = s.AsMap();
			SerializedEnumValue v = new SerializedEnumValue();
			v.name = m["n"].AsString();
			v.value = Int64.Parse( m["v"].AsString() );
			return v;
		}
	}

	public class SerializedEnum
	{
		public string enumName;
		public SerializedTypeDescriptor underlyingType;
		public SerializedEnumValue[] values;

		// enumName is the enclosing Map key. Master enumProps order: ut, values.
		public Serializee ToSerializee()
		{
			Dictionary<String, Serializee> ret = new Dictionary<String, Serializee>();
			ret["ut"] = underlyingType.ToSerializee();
			Serializee[] vals = new Serializee[values.Length];
			for( int i = 0; i < values.Length; i++ )
				vals[i] = values[i].ToSerializee();
			ret["values"] = new Serializee( vals );
			return new Serializee( ret );
		}

		public static SerializedEnum FromSerializee( Serializee s, String enumName )
		{
			Dictionary<String, Serializee> m = s.AsMap();
			SerializedEnum e = new SerializedEnum();
			e.enumName = enumName;
			e.underlyingType = SerializedTypeDescriptor.FromSerializee( m["ut"] );
			Serializee[] vals = m["values"].AsArray();
			e.values = new SerializedEnumValue[vals.Length];
			for( int i = 0; i < vals.Length; i++ )
				e.values[i] = SerializedEnumValue.FromSerializee( vals[i] );
			return e;
		}
	}

	public class SerializedAssembly
	{
		public SerializedClass[] classes;
		public SerializedMetadataToken[] metadata;
		public SerializedEnum[] enums;

		// Root Map order: classes (keyed className), metadata (keyed by string
		// token index starting at 1), enums (keyed enumName). base64 of the
		// Serializee buffer, byte-for-byte identical to master's assemblyData.
		public string SerializeString()
		{
			Dictionary<String, Serializee> root = new Dictionary<String, Serializee>();

			Dictionary<String, Serializee> cl = new Dictionary<String, Serializee>();
			for( int i = 0; i < classes.Length; i++ )
				cl[classes[i].className] = classes[i].ToSerializee();
			root["classes"] = new Serializee( cl );

			Dictionary<String, Serializee> md = new Dictionary<String, Serializee>();
			for( int i = 0; i < metadata.Length; i++ )
				md[(metadata[i].metaTokenIndex).ToString()] = metadata[i].ToSerializee();
			root["metadata"] = new Serializee( md );

			Dictionary<String, Serializee> en = new Dictionary<String, Serializee>();
			for( int i = 0; i < enums.Length; i++ )
				en[enums[i].enumName] = enums[i].ToSerializee();
			root["enums"] = new Serializee( en );

			return Convert.ToBase64String( new Serializee( root ).DumpAsMemory().ToArray() );
		}

		public static SerializedAssembly DeserializeString( string base64 )
		{
			SerializedAssembly asm = new SerializedAssembly();
			Dictionary<String, Serializee> root =
				new Serializee( Convert.FromBase64String( base64 ), Serializee.ElementType.Map ).AsMap();

			Dictionary<String, Serializee> cl = root["classes"].AsMap();
			asm.classes = new SerializedClass[cl.Count];
			int ci = 0;
			foreach( KeyValuePair<String, Serializee> kv in cl )
				asm.classes[ci++] = SerializedClass.FromSerializee( kv.Value, kv.Key );

			Dictionary<String, Serializee> md = root["metadata"].AsMap();
			asm.metadata = new SerializedMetadataToken[md.Count];
			foreach( KeyValuePair<String, Serializee> kv in md )
			{
				var smt = SerializedMetadataToken.FromSerializee(kv.Value, kv.Key);
				asm.metadata[smt.metaTokenIndex - 1] = smt; // serializee is in a 1-based dict, but store it in a regular 0-based flat array here
			}

			if( root.TryGetValue( "enums", out Serializee enumsS ) )
			{
				Dictionary<String, Serializee> en = enumsS.AsMap();
				asm.enums = new SerializedEnum[en.Count];
				int ei = 0;
				foreach( KeyValuePair<String, Serializee> kv in en )
					asm.enums[ei++] = SerializedEnum.FromSerializee( kv.Value, kv.Key );
			}
			else
			{
				asm.enums = new SerializedEnum[0];
			}

			return asm;
		}
	}

	/// <summary>
	/// Helper to build a SerializedTypeDescriptor from a native System.Type.
	/// Used by the editor compiler to build type descriptors from native System.Type.
	/// </summary>
	public static class SerializedTypeDescriptorBuilder
	{
		public static SerializedTypeDescriptor FromNativeType(Type t)
		{
			var td = new SerializedTypeDescriptor();
			td.assemblyName = t.Assembly.GetName().Name;

			if (t.IsGenericType)
			{
				string genericDefName = t.GetGenericTypeDefinition().FullName;
				// Strip arity markers (`1, `2) from the generic definition name
				var sb = new StringBuilder();
				for (int i = 0; i < genericDefName.Length; i++)
				{
					if (genericDefName[i] != '`')
					{
						sb.Append(genericDefName[i]);
						continue;
					}

					int j = i + 1;
					while (j < genericDefName.Length && char.IsDigit(genericDefName[j]))
						j++;
					if (j == genericDefName.Length || genericDefName[j] == '+' || genericDefName[j] == '[')
						i = j - 1;
				}
				td.typeName = sb.ToString();
				td.genericName = genericDefName;
				Type[] ta = t.GenericTypeArguments;
				td.genericArgs = new SerializedTypeDescriptor[ta.Length];
				for (int i = 0; i < ta.Length; i++)
					td.genericArgs[i] = FromNativeType(ta[i]);
			}
			else
			{
				td.typeName = t.FullName;
				if (t.IsEnum && CilboxUtil.HasCilboxableAttribute(t))
					td.underlyingType = FromNativeType(t.GetEnumUnderlyingType());
			}
			return td;
		}
	}

	public enum ProxyFieldType : byte
	{
		Empty       = 0,  // null field
		String      = 1,  // "s"
		Primitive   = 2,  // "e<StackType>" — primitives and enums
		CilboxRef   = 3,  // "cba" — Cilboxable object reference
		ObjectRef   = 4,  // "obj" — UnityEngine.Object reference
		Array       = 5,  // "a"
		Json        = 6,  // "j" — JsonUtility-serialized struct
	}

	// Primitive-only value payload. 16 bytes (no object slot). NOTE: keep the
	// value-slot field offsets in lockstep with StackElement (offset 8, same
	// union layout) - the consume site copies the 8-byte `l` slot directly into
	// StackElement.l, relying on identical bit layout for b/f/d/l/e.
	[StructLayout(LayoutKind.Explicit)]
	public struct PrimitivePayload
	{
		[FieldOffset(0)] public StackType type;
		[FieldOffset(8)] public bool   b;
		[FieldOffset(8)] public float  f;
		[FieldOffset(8)] public double d;
		[FieldOffset(8)] public long   l;
		[FieldOffset(8)] public ulong  e;

		public void Unbox( object i, StackType st )
		{
			type = st;
			switch( st )
			{
				case StackType.Boolean: this.l = ((bool)i)?1:0; break;
				case StackType.Sbyte:   this.l = (sbyte)i;      break;
				case StackType.Byte:    this.e = (byte)i;       break;
				case StackType.Short:   this.l = (short)i;      break;
				case StackType.Ushort:  this.e = (ushort)i;     break;
				case StackType.Int:     this.l = (int)i;        break;
				case StackType.Uint:    this.e = (uint)i;       break;
				case StackType.Long:    this.l = (long)i;       break;
				case StackType.Ulong:   this.e = (ulong)i;      break;
				case StackType.Float:   this.l = 0; this.f = (float)i;  break;
				case StackType.Double:  this.d = (double)i;     break;
			}
		}

		public void ToStackElement(ref StackElement se)
		{
			se.type = type;
			se.l = l; // copy full 8 bytes
			se.o = null; // clear if previously set
		}
	}

	public class SerializedProxyField
	{
		public byte fieldType;            // ProxyFieldType
		public string fieldName;          // null for non-root (array elements)
		public int matchingInstanceId;    // -1 for non-root

		// String / Json
		public string data;               // value as string / JSON text
		// Primitive
		public PrimitivePayload primitiveValue;  // primitiveValue.type holds the StackType

		// CilboxRef / ObjectRef
		public int fieldObjectIndex;      // index into fieldsObjects
		public bool objectRefIsNull;      // true when the original ref was null
		public string objectRefName;      // fv.ToString() of the referenced object

		// Array / Json
		public SerializedTypeDescriptor elementType;

		// Array (recursive)
		public SerializedProxyField[] arrayElements;

		private static String PrimitiveToString( in PrimitivePayload v )
		{
			switch( v.type )
			{
				case StackType.Boolean: return v.b.ToString();
				case StackType.Sbyte:   return ((sbyte)v.l).ToString();
				case StackType.Byte:    return ((byte)v.e).ToString();
				case StackType.Short:   return ((short)v.l).ToString();
				case StackType.Ushort:  return ((ushort)v.e).ToString();
				case StackType.Int:     return ((int)v.l).ToString();
				case StackType.Uint:    return ((uint)v.e).ToString();
				case StackType.Long:    return v.l.ToString();
				case StackType.Ulong:   return v.e.ToString();
				case StackType.Float:   return v.f.ToString();
				case StackType.Double:  return v.d.ToString();
				default: throw new CilboxException( $"Unknown primitive StackType {v.type}" );
			}
		}

		private static PrimitivePayload PrimitiveFromString( String d, StackType st )
		{
			PrimitivePayload v = default;
			v.type = st;
			switch( st )
			{
				case StackType.Boolean: v.l = bool.Parse( d ) ? 1 : 0; break;
				case StackType.Sbyte:   v.l = sbyte.Parse( d );        break;
				case StackType.Byte:    v.e = byte.Parse( d );         break;
				case StackType.Short:   v.l = short.Parse( d );        break;
				case StackType.Ushort:  v.e = ushort.Parse( d );       break;
				case StackType.Int:     v.l = int.Parse( d );          break;
				case StackType.Uint:    v.e = uint.Parse( d );         break;
				case StackType.Long:    v.l = long.Parse( d );         break;
				case StackType.Ulong:   v.e = ulong.Parse( d );        break;
				case StackType.Float:   v.l = 0; v.f = float.Parse( d ); break;
				case StackType.Double:  v.d = double.Parse( d );       break;
				default: throw new CilboxException( $"Unknown primitive StackType {st}" );
			}
			return v;
		}

		// Mirrors CilboxProxy.SerializeThing. Empty fields emit ONLY {empty:"true"}.
		// Otherwise: [n], [miid], then type-specific keys in master's exact order.
		public Serializee ToSerializee()
		{
			Dictionary<String, Serializee> f = new Dictionary<String, Serializee>();

			if( (ProxyFieldType)fieldType == ProxyFieldType.Empty )
			{
				f["empty"] = new Serializee( "true" );
				return new Serializee( f );
			}

			if( fieldName != null )
				f["n"] = new Serializee( fieldName );
			if( matchingInstanceId >= 0 )
				f["miid"] = new Serializee( matchingInstanceId.ToString() );

			switch( (ProxyFieldType)fieldType )
			{
				case ProxyFieldType.String:
					f["d"] = new Serializee( data );
					f["t"] = new Serializee( "s" );
					break;
				case ProxyFieldType.Primitive:
					f["d"] = new Serializee( PrimitiveToString( primitiveValue ) );
					f["t"] = new Serializee( "e" + primitiveValue.type );
					break;
				case ProxyFieldType.CilboxRef:
					f["fo"] = new Serializee( fieldObjectIndex.ToString() );
					f["t"] = new Serializee( "cba" );
					f["or"] = new Serializee( objectRefName );
					break;
				case ProxyFieldType.ObjectRef:
					f["fo"] = new Serializee( fieldObjectIndex.ToString() );
					f["t"] = new Serializee( "obj" );
					f["or"] = new Serializee( objectRefName );
					break;
				case ProxyFieldType.Array:
					f["t"] = new Serializee( "a" );
					f["at"] = elementType.ToSerializee();
					f["al"] = new Serializee( arrayElements.Length.ToString() );
					Serializee[] ad = new Serializee[arrayElements.Length];
					for( int i = 0; i < arrayElements.Length; i++ )
						ad[i] = arrayElements[i].ToSerializee();
					f["ad"] = new Serializee( ad );
					break;
				case ProxyFieldType.Json:
					f["t"] = new Serializee( "j" );
					f["d"] = new Serializee( data );
					f["at"] = elementType.ToSerializee();
					break;
			}
			return new Serializee( f );
		}

		public static SerializedProxyField FromSerializee( Serializee s )
		{
			Dictionary<String, Serializee> m = s.AsMap();
			SerializedProxyField f = new SerializedProxyField();
			f.matchingInstanceId = -1;

			if( m.ContainsKey( "empty" ) )
			{
				f.fieldType = (byte)ProxyFieldType.Empty;
				return f;
			}

			if( m.TryGetValue( "n", out Serializee nS ) )
				f.fieldName = nS.AsString();
			if( m.TryGetValue( "miid", out Serializee miidS ) )
				f.matchingInstanceId = Int32.Parse( miidS.AsString() );

			String t = m["t"].AsString();
			if( t == "s" )
			{
				f.fieldType = (byte)ProxyFieldType.String;
				f.data = m["d"].AsString();
			}
			else if( t == "cba" )
			{
				f.fieldType = (byte)ProxyFieldType.CilboxRef;
				f.fieldObjectIndex = Int32.Parse( m["fo"].AsString() );
				f.objectRefName = m["or"].AsString();
				f.objectRefIsNull = f.objectRefName == "null";
			}
			else if( t == "obj" )
			{
				f.fieldType = (byte)ProxyFieldType.ObjectRef;
				f.fieldObjectIndex = Int32.Parse( m["fo"].AsString() );
				f.objectRefName = m["or"].AsString();
				f.objectRefIsNull = f.objectRefName == "null";
			}
			else if( t == "a" )
			{
				f.fieldType = (byte)ProxyFieldType.Array;
				f.elementType = SerializedTypeDescriptor.FromSerializee( m["at"] );
				Serializee[] ad = m["ad"].AsArray();
				f.arrayElements = new SerializedProxyField[ad.Length];
				for( int i = 0; i < ad.Length; i++ )
					f.arrayElements[i] = FromSerializee( ad[i] );
			}
			else if( t == "j" )
			{
				f.fieldType = (byte)ProxyFieldType.Json;
				f.data = m["d"].AsString();
				f.elementType = SerializedTypeDescriptor.FromSerializee( m["at"] );
			}
			else if( t.Length > 0 && t[0] == 'e' )
			{
				f.fieldType = (byte)ProxyFieldType.Primitive;
				StackType st = (StackType)Enum.Parse( typeof(StackType), t.Substring(1) );
				f.primitiveValue = PrimitiveFromString( m["d"].AsString(), st );
			}
			return f;
		}
	}

	public class SerializedProxy
	{
		public SerializedProxyField[] fields;

		// Root is a List of per-field Maps, base64-encoded — identical to
		// master's serializedObjectData.
		public string SerializeString()
		{
			Serializee[] arr = new Serializee[fields.Length];
			for( int i = 0; i < fields.Length; i++ )
				arr[i] = fields[i].ToSerializee();
			return Convert.ToBase64String( new Serializee( arr ).DumpAsMemory().ToArray() );
		}

		public static SerializedProxy DeserializeString( string base64 )
		{
			SerializedProxy p = new SerializedProxy();
			Serializee[] arr = new Serializee( Convert.FromBase64String( base64 ), Serializee.ElementType.Map ).AsArray();
			p.fields = new SerializedProxyField[arr.Length];
			for( int i = 0; i < arr.Length; i++ )
				p.fields[i] = SerializedProxyField.FromSerializee( arr[i] );
			return p;
		}
	}
}
