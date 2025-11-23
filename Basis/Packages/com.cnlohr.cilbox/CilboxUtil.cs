using UnityEngine;
using System.Collections.Generic;
using System;
using System.Collections.Specialized;
using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Callbacks;
using System.Reflection;
using System.IO;
#endif

namespace Cilbox
{
	///////////////////////////////////////////////////////////////////////////
	//  STACK ELEMENT CONTAINER  //////////////////////////////////////////////
	///////////////////////////////////////////////////////////////////////////

	public enum StackType
	{
		Boolean,
		Sbyte,
		Byte,
		Short,
		Ushort,
		Int,
		Uint,
		Long,
		Ulong,
		Float,
		Double,
		Object,
		Address,
	}

	[StructLayout(LayoutKind.Explicit)]
	public struct StackElement
	{
		[FieldOffset(0)]public StackType type;
		[FieldOffset(8)]public Boolean b;
		[FieldOffset(8)]public float f;
		[FieldOffset(8)]public double d;
		[FieldOffset(8)]public int i;
		[FieldOffset(8)]public uint u;
		[FieldOffset(8)]public long l;
		[FieldOffset(8)]public ulong e;
		[FieldOffset(16)]public object o;

		static public StackElement nil;

		public static readonly Dictionary< String, StackType > TypeToStackType = new Dictionary<String, StackType>(){
			{ "System.Boolean", StackType.Boolean },
			{ "System.SByte", StackType.Sbyte },
			{ "System.Byte", StackType.Byte },
			{ "System.Int16", StackType.Short },
			{ "System.UInt16", StackType.Ushort },
			{ "System.Int32", StackType.Int },
			{ "System.UInt32", StackType.Uint },
			{ "System.Int64", StackType.Long },
			{ "System.UInt64", StackType.Ulong },
			{ "System.Single", StackType.Float },
			{ "System.Double", StackType.Double },
			{ "object", StackType.Object } };

		public StackElement Load( object o )
		{
			switch( o )
			{
			case sbyte t0: i = (sbyte)o;	type = StackType.Sbyte; break;
			case byte  t1: i = (byte)o;		type = StackType.Byte; break;
			case short t2: i = (short)o;	type = StackType.Short; break;
			case ushort t3: i = (ushort)o;	type = StackType.Ushort; break;
			case int t4: i = (int)o;		type = StackType.Int; break;
			case uint t5: u = (uint)o;		type = StackType.Uint; break;
			case long t6: l = (long)o;		type = StackType.Long; break;
			case ulong t7: e = (ulong)o;	type = StackType.Ulong; break;
			case float t8: f = (float)o;	type = StackType.Float; break;
			case double t9: d = (double)o;	type = StackType.Double; break;
			case bool ta0: i = ((bool)o) ? 1 : 0; type = StackType.Boolean; break;
			default: this.o = o; type = StackType.Object; break;
			}
			return this;
		}

		static public StackElement LoadAsStatic( object o )
		{
			StackElement ret = new StackElement();
			ret.i = 0; ret.o = null;
			switch( o )
			{
			case sbyte t0: ret.i = (sbyte)o;	ret.type = StackType.Sbyte; break;
			case byte  t1: ret.i = (byte)o;		ret.type = StackType.Byte; break;
			case short t2: ret.i = (short)o;	ret.type = StackType.Short; break;
			case ushort t3: ret.i = (ushort)o;	ret.type = StackType.Ushort; break;
			case int t4: ret.i = (int)o;		ret.type = StackType.Int; break;
			case uint t5: ret.u = (uint)o;		ret.type = StackType.Uint; break;
			case long t6: ret.l = (long)o;		ret.type = StackType.Long; break;
			case ulong t7: ret.e = (ulong)o;	ret.type = StackType.Ulong; break;
			case float t8: ret.f = (float)o;	ret.type = StackType.Float; break;
			case double t9: ret.d = (double)o;	ret.type = StackType.Double; break;
			case bool ta0: ret.i = ((bool)o) ? 1 : 0; ret.type = StackType.Boolean; break;
			default: ret.o = o; ret.type = StackType.Object; break;
			}
			return ret;
		}

		public StackElement LoadBool( bool b ) { this.b = b; type = StackType.Boolean; return this; }
		public StackElement LoadObject( object o ) { this.o = o; type = StackType.Object; return this; }
		public StackElement LoadSByte( sbyte s ) { this.i = (int)s; type = StackType.Sbyte; return this; }
		public StackElement LoadByte( uint u ) { this.u = u; type = StackType.Byte; return this; }
		public StackElement LoadShort( short s ) { this.i = (int)s; type = StackType.Short; return this; }
		public StackElement LoadUshort( ushort u ) { this.u = u; type = StackType.Ushort; return this; }
		public StackElement LoadInt( int i ) { this.i = i; type = StackType.Int; return this; }
		public StackElement LoadUint( uint u ) { this.u = u; type = StackType.Uint; return this; }
		public StackElement LoadLong( long l ) { this.l = l; type = StackType.Long; return this; }
		public StackElement LoadUlong( ulong e ) { this.e = e; type = StackType.Ulong; return this; }
		public StackElement LoadFloat( float f ) { this.f = f; type = StackType.Float; return this; }
		public StackElement LoadDouble( double d ) { this.d = d; type = StackType.Double; return this; }

		public StackElement LoadUlongType( ulong e, StackType t ) { this.e = e; type = t; return this; }
		public StackElement LoadLongType( long l, StackType t ) { this.l = l; type = t; return this; }

		public Type GetInnerType()
		{
			if( type == StackType.Object )
				return o.GetType();
			else
				return TypeFromStackType[(int)type];
		}

		public void Unbox( object i, StackType st )
		{
			type = st;
			switch( st )
			{
			case StackType.Sbyte: this.u = (uint)(sbyte)i; break;
			case StackType.Byte: this.u = (uint)(byte)i; break;
			case StackType.Short: this.u = (uint)(short)i; break;
			case StackType.Ushort: this.u = (uint)(ushort)i; break;
			case StackType.Int: this.i = (int)i; break;
			case StackType.Uint: this.u = (uint)i; break;
			case StackType.Long: this.l = (long)i; break;
			case StackType.Ulong: this.e = (ulong)i; break;
			case StackType.Float: this.f = (float)i; break;
			case StackType.Double: this.d = (double)i; break;
			case StackType.Boolean: this.i = ((bool)i)?1:0; break;
			default: this.o = i; break;
			}
		}

		public object AsObject()
		{
			switch( type )
			{
			case StackType.Sbyte: return (sbyte)i;
			case StackType.Byte: return (byte)i;
			case StackType.Short: return (short)i;
			case StackType.Ushort: return (ushort)i;
			case StackType.Int: return (int)i;
			case StackType.Uint: return (uint)u;
			case StackType.Long: return (long)l;
			case StackType.Ulong: return (ulong)e;
			case StackType.Float: return (float)f;
			case StackType.Double: return (double)d;
			case StackType.Boolean: return (bool)b;
			case StackType.Address: return Dereference();
			default: return o;
			}
		}

		public int AsInt()
		{
			switch( type )
			{
			case StackType.Sbyte:
			case StackType.Byte:
			case StackType.Short:
			case StackType.Ushort:
			case StackType.Int:
			case StackType.Uint:
			case StackType.Long:
			case StackType.Ulong:
				return (int)i;
			case StackType.Float: return (int)f;
			case StackType.Double: return (int)d;
			case StackType.Boolean: return b ? 1 : 0;
			case StackType.Address: return (int)Dereference();
			default: return (int)o;
			}
		}

		public object CoerceToObject( Type t )
		{
			StackType rt = StackTypeFromType( t );

			if( type < StackType.Float ) 
			{
				switch( rt )
				{
				case StackType.Sbyte:   return (sbyte)i;
				case StackType.Byte:    return (byte)u;
				case StackType.Short:   return (short)i;
				case StackType.Ushort:  return (ushort)u;
				case StackType.Int:     return (int)i;
				case StackType.Uint:    return (uint)u;
				case StackType.Long:    return (long)l;
				case StackType.Ulong:   return (ulong)e;
				case StackType.Float:   return (float)e;
				case StackType.Double:  return (double)e;
				case StackType.Boolean: return e != 0;
				default:
					if( t.IsEnum )
					{
						switch( type )
						{
							case StackType.Sbyte: return Enum.ToObject( t, (sbyte)i );
							case StackType.Byte:  return Enum.ToObject( t, (byte)u );
							case StackType.Short: return Enum.ToObject( t, (short)i );
							case StackType.Ushort:return Enum.ToObject( t, (ushort)u );
							case StackType.Int:   return Enum.ToObject( t, (int)i );
							case StackType.Uint:  return Enum.ToObject( t, (uint)u );
							case StackType.Long:  return Enum.ToObject( t, (long)e );
							case StackType.Ulong: return Enum.ToObject( t, (ulong)u );
						}
					}
					else
					{
						switch( type )
						{
							case StackType.Sbyte: return Convert.ChangeType( (sbyte)i, t );
							case StackType.Byte:  return Convert.ChangeType( (byte)u, t );
							case StackType.Short: return Convert.ChangeType( (short)i, t );
							case StackType.Ushort:return Convert.ChangeType( (ushort)u, t );
							case StackType.Int:   return Convert.ChangeType( (int)i, t );
							case StackType.Uint:  return Convert.ChangeType( (uint)u, t );
							case StackType.Long:  return Convert.ChangeType( (long)e, t );
							case StackType.Ulong: return Convert.ChangeType( (ulong)u, t );
						}
					}
					break;
				}
			}
			else if( type < StackType.Double ) // Float
			{
				switch( rt )
				{
				case StackType.Sbyte:  return (sbyte)f;
				case StackType.Byte:   return (byte)f;
				case StackType.Short:  return (short)f;
				case StackType.Ushort: return (ushort)f;
				case StackType.Int:    return (int)f;
				case StackType.Uint:   return (uint)f;
				case StackType.Long:   return (long)f;
				case StackType.Ulong:  return (ulong)f;
				case StackType.Float:  return (float)f;
				case StackType.Double: return (double)f;
				case StackType.Boolean:  return f != 0.0f;
				default:   return Convert.ChangeType( o, t );
				}
			}
			else if( type < StackType.Object ) // Double
			{
				switch( rt )
				{
				case StackType.Sbyte:   return (sbyte)d;
				case StackType.Byte:    return (byte)d;
				case StackType.Short:   return (short)d;
				case StackType.Ushort:  return (ushort)d;
				case StackType.Int:     return (int)d;
				case StackType.Uint:    return (uint)d;
				case StackType.Long:    return (long)d;
				case StackType.Ulong:   return (ulong)d;
				case StackType.Float:   return (float)d;
				case StackType.Double:  return (double)d;
				case StackType.Boolean: return d != 0;
				default:        return Convert.ChangeType( o, t );
				}
			}
			else if( type == StackType.Object )
			{
				return Convert.ChangeType( o, t );
			}

			throw new Exception( "Erorr invalid type conversion from " + type + " to " + t );
		}

		public object Dereference()
		{
			if( o.GetType() == typeof(StackElement[]) )
				return ((StackElement[])o)[i].AsObject();
			else
				return ((Array)o).GetValue(i);
		}

		// Mostly like a Dereference.
		static public StackElement ResolveToStackElement( StackElement tr )
		{
			if( tr.type == StackType.Address )
			{
				if( tr.o.GetType() == typeof(StackElement[]) )
					return ResolveToStackElement( ((StackElement[])tr.o)[tr.i] );
				else
					return ResolveToStackElement( StackElement.LoadAsStatic(((Array)tr.o).GetValue(tr.i)) );
			}
			else
			{
				return tr;
			}
		}

		// XXX RISKY - generally copy this in-place.
		public void DereferenceLoad( object overwrite )
		{
			if( o.GetType() == typeof(StackElement[]) )
				((StackElement[])o)[i].Load( overwrite );
			else
				((Array)o).SetValue(overwrite, i);
		}

		static public StackElement CreateReference( Array array, uint index )
		{
			StackElement ret = new StackElement();
			ret.type = StackType.Address;
			ret.u = index;
			ret.o = array;
			return ret;
		}

		// This logic is probably incorrect.
		static public StackType StackTypeMaxPromote( StackType a, StackType b )
		{
			if( a < StackType.Int ) a = StackType.Int;
			if( b < StackType.Int ) b = StackType.Int;
			StackType ret = a;
			if( ret < b ) ret = b;

			// Could be Int, Uint, Long, Ulong, Float Double or Object.  But if non-integer must be same type to prompte.
			// I think?
			if( ret >= StackType.Float && a != b )
				throw new Exception( $"Invalid stack conversion from {a} to {b}" );

			return a;
		}

		public static readonly Type [] TypeFromStackType = new Type[] {
			typeof(bool), typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof( int ),
			typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(object),
			typeof(void) /*Tricky, pointer*/ };

		public static StackType StackTypeFromType( Type t )
		{
			switch( t )
			{
				case Type _ when t == typeof(sbyte): return StackType.Sbyte;
				case Type _ when t == typeof(byte): return StackType.Byte;
				case Type _ when t == typeof(short): return StackType.Short;
				case Type _ when t == typeof(ushort): return StackType.Ushort;
				case Type _ when t == typeof(int): return StackType.Int;
				case Type _ when t == typeof(uint): return StackType.Uint;
				case Type _ when t == typeof(long): return StackType.Long;
				case Type _ when t == typeof(ulong): return StackType.Ulong;
				case Type _ when t == typeof(float): return StackType.Float;
				case Type _ when t == typeof(double): return StackType.Double;
				case Type _ when t == typeof(bool): return StackType.Boolean;
				default: return StackType.Object;
			}
		}

	}

	///////////////////////////////////////////////////////////////////////////
	//  SERIALIZATION / DESERIALIZATION  //////////////////////////////////////
	///////////////////////////////////////////////////////////////////////////

	public class Serializee
	{
		private Memory<byte> buffer;
		private ElementType e;

		// TODO: Version 2: Use a string dictionary.
		// lsb 0..2 = # of bytes to describe length or # of elements (Bytes little endian) Only #'s 0..6 are valid.
		//     3..5 = Type
		//
		// When serializing, buffer does not have header.  When Deserializing, it has header.
		//
		// FUTURE: I would like to refactor this to use static functions to serialize/deserialize.

		public enum ElementType
		{
			Invalid = 0,
			String = 1,
			List = 2,
			Map = 3,
			Blob = 4,
		};

		public Serializee( Memory<byte> bufferIn, ElementType eIn ) { buffer = bufferIn; e = eIn; }

		public Serializee( String str ) // For serialization
		{
			int len = System.Text.Encoding.UTF8.GetByteCount( str );
			byte lenBytes = ComputeLengthBytes( len );
			byte[] bytes = new byte[len + 1 + lenBytes];
			System.Text.Encoding.UTF8.GetBytes( str, 0, str.Length, bytes, 1 + lenBytes );
			bytes[0] = (byte)(lenBytes | ((int)(ElementType.String)<<3));
			buffer = new Memory<byte>(bytes);
			FillBytesWithLength( len, lenBytes, buffer.Span.Slice(1) );
			e = ElementType.String;
		}

		static public Serializee CreateFromBlob( byte [] bytes ) // For serialization
		{
			int len = bytes.Length;
			byte lenBytes = ComputeLengthBytes( len );
			byte [] newBytes = new byte[len + 1 + lenBytes];
			Memory<byte> b = new Memory<byte>(newBytes);
			FillBytesWithLength( len, lenBytes, b.Span.Slice(1) );
			bytes.CopyTo( newBytes, 1 + lenBytes );
			b.Span[0] = (byte)(lenBytes | ((int)(ElementType.Blob)<<3));
			Serializee s = new Serializee( b, ElementType.Blob );
			return s;
		}

		public Serializee( String [] listIn ) // For serialization
		{
			Serializee [] lst = new Serializee[listIn.Length];
			for( int i = 0; i < lst.Length; i++ )
				lst[i] = new Serializee( listIn[i] );
			SetAsList( lst );
		}

		public Serializee( Serializee[] list ) // For serialization
		{
			SetAsList( list );
		}

		public Serializee( Dictionary<String, Serializee> dict ) // For serialization
		{
			Serializee [] serOut = new Serializee[dict.Count*2];
			int n = 0;
			foreach( KeyValuePair< String, Serializee > k in dict )
			{
				Serializee s = new Serializee( k.Key );
				serOut[n++] = s;
				serOut[n++] = k.Value;
			}
			SetAsList( serOut, ElementType.Map );
		}

		public Serializee( Dictionary<String, String> dict ) // For serialization
		{
			Serializee [] serOut = new Serializee[dict.Count*2];
			int n = 0;
			foreach( KeyValuePair< String, String > k in dict )
			{
				Serializee s = new Serializee( k.Key );
				serOut[n++] = s;
				s = new Serializee( k.Value );
				serOut[n++] = s;
			}
			SetAsList( serOut, ElementType.Map );
		}

		// Internal function for serialization.  Remember maps are just fancy lists.
		void SetAsList( Serializee[] list, ElementType intendedType = ElementType.List )
		{
			int len = 1;
			for( int i = 0; i < list.Length; i++ )
				len += list[i].buffer.Length;
			byte lenElems = ComputeLengthBytes( list.Length );
			len += lenElems;
			int lenToEncode = len;
			byte lenBytes = ComputeLengthBytes( lenToEncode );
			len += lenBytes + 1;
			byte [] bytes = new byte[len];
			buffer = new Memory<byte>(bytes);

			bytes[0] = (byte)(lenBytes | (((byte)intendedType)<<3));
			FillBytesWithLength( lenToEncode, lenBytes, buffer.Span.Slice(1) );
			bytes[1+lenBytes] = (byte)(lenElems | (((byte)ElementType.Invalid)<<3));
			FillBytesWithLength( list.Length, lenElems, buffer.Span.Slice(2+lenBytes) );

			int place = 2 + lenBytes + lenElems;
			for( int i = 0; i < list.Length; i++ )
			{
				place += list[i].SpliceInto( buffer.Span.Slice(place) );
			}
			e = intendedType;
		}

		// More like dump()
		public override String ToString()
		{
			String s = "";
			for( int i = 0; i < buffer.Length; i++ )
				s += buffer.Span[i].ToString("X2") + " ";
			return s;
		}

		public String AsString() 	// For deserialization
		{
			int l = buffer.Span[0];
			int lenBytes = l & 0x7;
			ElementType typ = (ElementType)((l>>3)&7);
			if( typ != ElementType.String ) throw new Exception( $"Fault, got {typ} expected String" );
			return System.Text.Encoding.UTF8.GetString( buffer.Span.Slice( lenBytes+1 ) );
		}

		public byte[] AsBlob() 		// For deserialization
		{
			int l = buffer.Span[0];
			int lenBytes = l & 0x7;
			ElementType typ = (ElementType)((l>>3)&7);
			if( typ != ElementType.Blob ) throw new Exception( $"Fault, got {typ} expected Blob" );
			return buffer.Span.Slice( lenBytes+1 ).ToArray();
		}

		public Serializee[] AsArray( ElementType intendedType = ElementType.List ) // For deserialization
		{
			int ofs = 0;
			(int len, ElementType typ) = PullInfo( buffer.Span, ref ofs );
			if( typ != intendedType ) throw new Exception( $"Fault, got {typ} expected {intendedType}" );
			(int elems, ElementType typ_dump) = PullInfo( buffer.Span, ref ofs );
			Serializee[] ret = new Serializee[elems];
			for( int i = 0; i < elems; i++ )
				ret[i] = Pull( buffer, ref ofs );
			return ret;
		}

		public Dictionary<String, Serializee> AsMap() // For deserialization
		{
			Dictionary<String, Serializee> ret = new Dictionary<String, Serializee>();
			Serializee[] lst = AsArray( ElementType.Map );
			for( int i = 0; i < lst.Length; i+=2 )
				ret[lst[i+0].AsString()] = lst[i+1];
			return ret;
		}

		public String[] AsStringArray() // For deserialization
		{
			Serializee[] lst = AsArray();
			String[] ret = new String[lst.Length];
			for( int i = 0; i < lst.Length; i++ )
				ret[i] = lst[i].AsString();
			return ret;
		}

		public Dictionary< String, String > AsStringMap() // For deserialization.
		{
			Dictionary<String, String> ret = new Dictionary<String, String>();
			Serializee[] lst = AsArray( ElementType.Map );
			for( int i = 0; i < lst.Length; i+=2 )
				ret[lst[i+0].AsString()] = lst[i+1].AsString();
			return ret;
		}

		public Memory<byte> DumpAsMemory() { return buffer; } // For getting a serialized buffer.

		// Serialization Helpers
		private int SpliceInto( Span<byte> si )
		{
			buffer.Span.CopyTo( si );
			return buffer.Length;
		}

		static private byte ComputeLengthBytes( int len )
		{
			byte ret = 0;
			do {
				if( len == 0 ) return ret;
				len >>= 8;
				ret++;
			} while( ret < 6 );
			throw new Exception( "Invalid ComputeLengthBytes" );
		}

		static private void FillBytesWithLength( int length, int lengthBytes, Span<byte> sp )
		{
			if( lengthBytes > 6 ) throw new Exception( $"Invalid FillBytesWithLength {length} {lengthBytes}" );
			for( int i = 0; i < lengthBytes; i++ )
			{
				sp[i] = (byte)(length&0xff);
				length >>= 8;
			}
		}

		// Deserialization Helpers
		private (int, ElementType) PullInfo( Span<byte> b, ref int i )
		{
			if( buffer.Length <= 0 ) return ( 0, 0 );
			byte bl = b[i++]; // info byte
			int len = 0;
			int lend = bl & 0x7;
			for( int j = 0; j < lend; j++ )
				len |= ((int)b[i++]) << (j*8);
			return (len, (ElementType)((bl>>3)&0x7) );
		}

		// Pull off a "thing" from the bitstream.  Return is the span, -6 = string, otherwise # of elements.
		private Serializee Pull( Memory<byte> b, ref int i )
		{
			//Debug.Log( ToString() );
			Span<byte> sb = b.Span;
			int iStart = i;
			if( buffer.Length <= 0 ) new Serializee( null, ElementType.Invalid );
			byte bl = sb[i++]; // info byte
			int len = 0;
			int blct = bl & 0x7;
			int etype = (bl>>3) & 0x7;

			for( int j = 0; j < blct; j++ )
				len |= ((int)sb[i++]) << (j * 8);

			i += len;
			//Debug.Log( $"Pulling {iStart} BL:{bl.ToString("X2")} {blct} {i} {len} BSL:{b.Length}" );
			return new Serializee( b.Slice( iStart, i - iStart ), (ElementType)len );
		}
	}
	

	public static class CilboxUtil
	{
		// Used both in Cilbox + CilboxProxy for getting strings of fields into objects.
		static public object DeserializeDataForProxyField( Type t, String sInitialize )
		{
			if( sInitialize != null && sInitialize.Length > 0 )
				return TypeDescriptor.GetConverter(t).ConvertFrom(sInitialize);
			else
			{
				if( !t.IsPrimitive ) 
					return null;
				else
					return Activator.CreateInstance(t);
			}
		}

		[StructLayout(LayoutKind.Explicit)]
		public struct IntFloatConverter
		{
			[FieldOffset(0)]private float f;
			[FieldOffset(0)]private double d;
			[FieldOffset(0)]private int i;
			[FieldOffset(0)]private uint u;
			[FieldOffset(0)]private long l;
			[FieldOffset(0)]private ulong e;
			public static float ConvertItoF(int value)
			{
				return new IntFloatConverter { i = value }.f;
			}
			public static float ConvertUtoF(uint value)
			{
				return new IntFloatConverter { u = value }.f;
			}
			public static int ConvertFtoI(float value)
			{
				return new IntFloatConverter { f = value }.i;
			}
			public static double ConvertEtoD(ulong value)
			{
				return new IntFloatConverter { e = value }.d;
			}
		}

		public static ulong BytecodePullLiteral( byte[] byteCode, ref int i, int len )
		{
			ulong ret = 0;
			for( int lr = 0; lr < len; lr++ )
			{
				ret |= ((ulong)byteCode[i++]) << (lr*8);
			}
			return ret;
		}

		public static void BytecodeReplaceLiteral( ref byte[] byteCode, ref int i, int len, ulong operand )
		{
			for( int lr = 0; lr < len; lr++ )
			{
				byteCode[i++] = (byte)(operand & 0xff);
				operand >>= 8;
			}
		}

		///////////////////////////////////////////////////////////////////////////
		//  ASSEMBLY DEBUG LOGGER  ////////////////////////////////////////////////
		///////////////////////////////////////////////////////////////////////////
#if UNITY_EDITOR

		// This produces CilboxLog.txt
		public static void AssemblyLoggerTask( String fileName, String assemblyData, Cilbox b )
		{
			StreamWriter CLog = File.CreateText( fileName );
			CLog.WriteLine( "Cilbox Size: " + assemblyData.Length + " bytes." );

			try
			{
				b.assemblyData = assemblyData;
				b.BoxInitialize( true );

				Dictionary< String, int > classes;
				CilboxClass [] classesList;

				Dictionary< String, Serializee > assemblyRoot = new Serializee( Convert.FromBase64String( assemblyData ), Serializee.ElementType.Map ).AsMap();
				Dictionary< String, Serializee > classData = assemblyRoot["classes"].AsMap();
				Dictionary< String, Serializee > metaData = assemblyRoot["metadata"].AsMap();

				int clsid = 0;
				classes = new Dictionary< String, int >();
				classesList = new CilboxClass[classData.Count];
				foreach( var v in classData )
				{
					CilboxClass cls = new CilboxClass();
					classesList[clsid] = cls;
					classes[(String)v.Key] = clsid;
					clsid++;
				}

				clsid = 0;
				foreach( var v in classData )
				{
					CilboxClass c = classesList[clsid++];

					c.LoadCilboxClass( b, v.Key, v.Value );

					CLog.WriteLine( $"Class: {c.className}" );

					String imports = "";
					int numImportFunctions = Enum.GetNames(typeof(ImportFunctionID)).Length;
					for( int i = 0; i < numImportFunctions; i++ )
					{
						String fn = Enum.GetName(typeof(ImportFunctionID), i);
						if( i == 0 ) fn = ".ctor";
						if( c.importFunctionToId[i] != 0xffffffff )
							imports += " " + fn;
					}
					CLog.WriteLine( $"Imports:{imports}" );

					CLog.WriteLine( "Static Fields:" );
					for( int i = 0; i < c.staticFieldNames.Length; i++ )
						CLog.WriteLine( $"\t{c.staticFieldTypes[i]} {c.staticFieldNames}[i]" );

					CLog.WriteLine( "Instance Fields:" );
					for( int i = 0; i < c.instanceFieldNames.Length; i++ )
						CLog.WriteLine( $"\t{c.instanceFieldTypes[i]} {c.instanceFieldNames[i]}" );

					for( int k = 0; k < c.methods.Length; k++ )
					{
						CilboxMethod m = c.methods[k];

						CLog.WriteLine( $"{c.className}.{m.methodName} - {m.fullSignature}" );
						for( int j = 0; j < m.signatureParameters.Length; j++ )
						{
							CLog.WriteLine( $"\t{m.typeParameters[j]} {m.signatureParameters[j]}" );
						}
						CLog.WriteLine( $"\tBody: Max Stack: {m.MaxStackSize}" );
						for( int j = 0; j < m.methodLocals.Length; j++ )
						{
							CLog.WriteLine( $"\t\t{m.typeLocals[j]} {m.methodLocals[j]}" );
						}

						byte[] byteCode = m.byteCode;

						int i = 0;
						i = 0;
						try {
							do
							{
								int starti = i;
								CilboxUtil.OpCodes.OpCode oc;
								try {
									oc = CilboxUtil.OpCodes.ReadOpCode( byteCode, ref i );
								} catch( Exception e )
								{
									CLog.WriteLine( e );
									CLog.WriteLine( "Exception decoding opcode at address " + i + " in " + m.methodName );
									throw;
								}
								int opLen = CilboxUtil.OpCodes.OperandLength[(int)oc.OperandType];
								int backupi = i;
								uint operand = (uint)CilboxUtil.BytecodePullLiteral( byteCode, ref i, opLen );

								String stline = $"\t\t {starti,-4} {byteCode[starti].ToString("X2")} {oc,-10}";

								// Check to see if this is a meta that we care about.  Then rewrite in a new identifier.
								// ResolveField, ResolveMember, ResolveMethod, ResolveSignature, ResolveString, ResolveType
								// We sort of want to let the other end know what they are. So we mark them with the code
								// from here: https://github.com/jbevain/cecil/blob/master/Mono.Cecil.Metadata/TableHeap.cs#L16

								CilboxUtil.OpCodes.OperandType ot = oc.OperandType;

								if( ot == CilboxUtil.OpCodes.OperandType.InlineTok )
								{
									CilMetadataTokenInfo md = b.metadatas[operand];
									stline += " ";
									if( md != null )
										stline += md.Name;
									else
										stline += operand.ToString("X4") + " ";
								}
								else if( ot == OpCodes.OperandType.InlineSwitch )
								{
									stline += $" Switch {operand} cases";
									int oin;
									for( oin = 0; oin < operand; oin++ )
									{
										int sws = (int)(uint)CilboxUtil.BytecodePullLiteral( byteCode, ref i, 4 );
										stline += " " + sws;
									}
								}
								else if( ot == OpCodes.OperandType.InlineString )
								{
									CilMetadataTokenInfo md = b.metadatas[operand];
									stline += " " + ((md!=null)?md.Name:"UNDEFINED");
								}
								else if( ot == OpCodes.OperandType.InlineMethod )
								{
									CilMetadataTokenInfo md = b.metadatas[operand];
									stline += " ";
									if( md == null )
										stline += operand.ToString("X4");
									else
										stline += md.declaringTypeName + " " + md.Name;
								}
								else if( ot == OpCodes.OperandType.InlineField )
								{
									CilMetadataTokenInfo md = b.metadatas[operand];
									stline += " " + ((md!=null)?md.Name:operand.ToString("X4"));
								}
								else if( ot == OpCodes.OperandType.InlineType )
								{
									CilMetadataTokenInfo md = b.metadatas[operand];
									stline += " " + ((md!=null)?md.Name:operand.ToString("X4"));
								}
								else if( ot == OpCodes.OperandType.ShortInlineI || ot == OpCodes.OperandType.ShortInlineVar )
								{
									stline += " 0x" + operand.ToString("X4") + " ";
								}
								else if( ot == OpCodes.OperandType.InlineI || ot == OpCodes.OperandType.InlineVar )
								{
									stline += " 0x" + operand.ToString("X8") + " ";
								}
								else if( ot == OpCodes.OperandType.InlineI8 )
								{
									stline += " 0x" + operand.ToString("X16") + " ";
								}
								else if( ot == OpCodes.OperandType.ShortInlineBrTarget || ot == OpCodes.OperandType.InlineBrTarget )
								{
									stline += " " + operand + " to " + (i + operand);
								}
								else if( ot == OpCodes.OperandType.ShortInlineR )
								{
									stline += " 0x" + operand.ToString("X8") + " " + IntFloatConverter.ConvertItoF( (int)operand );
								}

								CLog.WriteLine( stline );
								if( i >= byteCode.Length ) break;
							} while( true );
						} catch( Exception e )
						{
							CLog.WriteLine( e.ToString() );
							Debug.LogError( e.ToString() );
						}
					}
				}

				foreach( var v in metaData )
				{
					int mid = Convert.ToInt32((String)v.Key);
					Dictionary< String, Serializee > st = v.Value.AsMap();
					MetaTokenType metatype = (MetaTokenType)Convert.ToInt32(st["mt"].AsString());
					//CilMetadataTokenInfo t = metadatas[mid] = new CilMetadataTokenInfo( metatype );

					//t.type = metatype;
					//t.Name = "<UNKNOWN>";

					String metaLine = $"\t{mid.ToString("X4")} {metatype.ToString().Substring(2),-7} ";

					switch( metatype )
					{
					case MetaTokenType.mtString:
						metaLine += st["s"].AsString();
						break;
					case MetaTokenType.mtArrayInitializer:
						metaLine += Convert.ToBase64String( st["data"].AsBlob() );
						break;
					case MetaTokenType.mtField:
						Type t = b.usage.GetNativeTypeFromSerializee( st["dt"] );
						if( Int32.Parse( st["isStatic"].AsString() ) > 0 ) metaLine += "static ";
						String tname = t?.ToString();
						if( t == null )
							tname = "NR:" + st["dt"].AsMap()["n"].AsString() + " ";
						metaLine += tname + st["name"].AsString();
						break;
					case MetaTokenType.mtType:
					{
						Serializee typ = st["dt"];
						Type nt = b.usage.GetNativeTypeFromSerializee( typ );
						StackType seType = StackElement.StackTypeFromType( nt );
						if( seType < StackType.Object )
						{
							metaLine += $"{nt.ToString()} {seType}";
						}
						else
						{
							if( nt == null )
							{
								bool bFound = false;
								foreach( CilboxClass c in b.classesList )
								{
									if( c.className == typ.AsMap()["n"].AsString() )
									{
										metaLine += $"PROXY: {c.className}";
										bFound = true;
									}
								}

								if( !bFound )
									throw new Exception( $"Type {typ.AsMap()["n"].AsString()} not available." );
							}
							else
							{
								metaLine += $"{nt.ToString()}";
							}
						}
						break;
					}
					case MetaTokenType.mtMethod:
					{
						metaLine += $"{st["name"].AsString()} {st["fullSignature"].AsString()} {st["assembly"].AsString()}";
						break;
					}
					}

					CLog.WriteLine( metaLine );
				}
			}
			catch( Exception e )
			{
				Debug.LogError( e.ToString() );
				CLog.WriteLine( e.ToString() );
			}
			CLog.Close();
		}
#endif

		///////////////////////////////////////////////////////////////////////////
		//  REFLECTION HELPERS  ///////////////////////////////////////////////////
		///////////////////////////////////////////////////////////////////////////
		public static MonoBehaviour [] GetAllBehavioursThatNeedCilboxing()
		{
			List<MonoBehaviour> ret = new List<MonoBehaviour>();

			object[] objToCheck = GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
			foreach (object o in objToCheck)
			{
				GameObject g = (GameObject) o;
				MonoBehaviour [] scripts = g.GetComponents<MonoBehaviour>();
				foreach (MonoBehaviour m in scripts )
				{
					// Skip null objects.
					if (m == null)
						continue;
					object[] attribs = m.GetType().GetCustomAttributes(typeof(CilboxableAttribute), true);
					// Not a proxiable script.
					if (attribs == null || attribs.Length <= 0)
						continue;
					ret.Add(m);
				}
			}
			return ret.ToArray();
		}

		// This does not check any rules, so it can be static.
		public static Serializee GetSerializeeFromNativeType( Type t )
		{
			Dictionary< String, Serializee > ret = new Dictionary< String, Serializee >();
			// Originally I did this to try to narrow down the search.  Now it is not as practical.
			ret["a"] = new Serializee( t.Assembly.GetName().Name );
			if( t.IsGenericType )
			{
				String [] sn = t.FullName.Split( "`" );
				ret["n"] = new Serializee( sn[0] );
				Type [] ta = t.GenericTypeArguments;
				Serializee [] sg = new Serializee[ta.Length];
				for( int i = 0; i < ta.Length; i++ )
					sg[i] = GetSerializeeFromNativeType( ta[i] );
				ret["g"] = new Serializee( sg );
			}
			else
			{
				ret["n"] = new Serializee( t.FullName );
			}
			return new Serializee( ret );
		}


		///////////////////////////////////////////////////////////////////////////
		//  DEFS FROM CECIL FOR PARSING CIL  //////////////////////////////////////
		///////////////////////////////////////////////////////////////////////////

		// From https://raw.githubusercontent.com/jbevain/cecil/refs/heads/master/Mono.Cecil.Cil/OpCodes.cs
		//
		// Author:
		//   Jb Evain (jbevain@gmail.com)
		//
		// Copyright (c) 2008 - 2015 Jb Evain
		// Copyright (c) 2008 - 2011 Novell, Inc.
		//
		// Licensed under the MIT/X11 license.
		//
		public static class OpCodes {

			public enum FlowControl {
				Branch,
				Break,
				Call,
				Cond_Branch,
				Meta,
				Next,
				Phi,
				Return,
				Throw,
			}

			public enum OpCodeType {
				Annotation,
				Macro,
				Nternal,
				Objmodel,
				Prefix,
				Primitive,
			}

			public enum OperandType {
				InlineBrTarget = 0,
				InlineField,
				InlineI,
				InlineI8,
				InlineMethod,
				InlineNone,
				InlinePhi,
				InlineR,
				InlineSig,
				InlineString,
				InlineSwitch,
				InlineTok,
				InlineType,
				InlineVar,
				InlineArg,
				ShortInlineBrTarget,
				ShortInlineI,
				ShortInlineR,
				ShortInlineVar,
				ShortInlineArg,
			}

			// https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.operandtype?view=net-9.0
			static readonly public int [] OperandLength = {
				4, // InlineBrTarget
				4, // InlineField
				4, // InlineI
				8, // InlineI8
				4, // InlineMethod
				0, // InlineNone
				0, // InlinePhi = Unused (Not a real type)
				8, // InlineR
				4, // InlineSig
				4, // InlineString
				4, // InlineSwitch (Not actually 4)
				4, // InlineTok - The operand is a FieldRef, MethodRef, or TypeRef token.
				4, // InlineType
				2, // InlineVar
				2, // InlineArg
				1, // ShortInlineBrTarget
				1, // ShortInlineI
				4, // ShortInlineR
				1, // ShortInlineVar
				1, // ShortInlineArg
			};

			public enum StackBehaviour {
				Pop0,
				Pop1,
				Pop1_pop1,
				Popi,
				Popi_pop1,
				Popi_popi,
				Popi_popi8,
				Popi_popi_popi,
				Popi_popr4,
				Popi_popr8,
				Popref,
				Popref_pop1,
				Popref_popi,
				Popref_popi_popi,
				Popref_popi_popi8,
				Popref_popi_popr4,
				Popref_popi_popr8,
				Popref_popi_popref,
				PopAll,
				Push0,
				Push1,
				Push1_push1,
				Pushi,
				Pushi8,
				Pushr4,
				Pushr8,
				Pushref,
				Varpop,
				Varpush,
			}

			public enum Code {
				Nop,
				Break,
				Ldarg_0,
				Ldarg_1,
				Ldarg_2,
				Ldarg_3,
				Ldloc_0,
				Ldloc_1,
				Ldloc_2,
				Ldloc_3,
				Stloc_0,
				Stloc_1,
				Stloc_2,
				Stloc_3,
				Ldarg_S,
				Ldarga_S,
				Starg_S,
				Ldloc_S,
				Ldloca_S,
				Stloc_S,
				Ldnull,
				Ldc_I4_M1,
				Ldc_I4_0,
				Ldc_I4_1,
				Ldc_I4_2,
				Ldc_I4_3,
				Ldc_I4_4,
				Ldc_I4_5,
				Ldc_I4_6,
				Ldc_I4_7,
				Ldc_I4_8,
				Ldc_I4_S,
				Ldc_I4,
				Ldc_I8,
				Ldc_R4,
				Ldc_R8,
				Dup,
				Pop,
				Jmp,
				Call,
				Calli,
				Ret,
				Br_S,
				Brfalse_S,
				Brtrue_S,
				Beq_S,
				Bge_S,
				Bgt_S,
				Ble_S,
				Blt_S,
				Bne_Un_S,
				Bge_Un_S,
				Bgt_Un_S,
				Ble_Un_S,
				Blt_Un_S,
				Br,
				Brfalse,
				Brtrue,
				Beq,
				Bge,
				Bgt,
				Ble,
				Blt,
				Bne_Un,
				Bge_Un,
				Bgt_Un,
				Ble_Un,
				Blt_Un,
				Switch,
				Ldind_I1,
				Ldind_U1,
				Ldind_I2,
				Ldind_U2,
				Ldind_I4,
				Ldind_U4,
				Ldind_I8,
				Ldind_I,
				Ldind_R4,
				Ldind_R8,
				Ldind_Ref,
				Stind_Ref,
				Stind_I1,
				Stind_I2,
				Stind_I4,
				Stind_I8,
				Stind_R4,
				Stind_R8,
				Add,
				Sub,
				Mul,
				Div,
				Div_Un,
				Rem,
				Rem_Un,
				And,
				Or,
				Xor,
				Shl,
				Shr,
				Shr_Un,
				Neg,
				Not,
				Conv_I1,
				Conv_I2,
				Conv_I4,
				Conv_I8,
				Conv_R4,
				Conv_R8,
				Conv_U4,
				Conv_U8,
				Callvirt,
				Cpobj,
				Ldobj,
				Ldstr,
				Newobj,
				Castclass,
				Isinst,
				Conv_R_Un,
				Unbox,
				Throw,
				Ldfld,
				Ldflda,
				Stfld,
				Ldsfld,
				Ldsflda,
				Stsfld,
				Stobj,
				Conv_Ovf_I1_Un,
				Conv_Ovf_I2_Un,
				Conv_Ovf_I4_Un,
				Conv_Ovf_I8_Un,
				Conv_Ovf_U1_Un,
				Conv_Ovf_U2_Un,
				Conv_Ovf_U4_Un,
				Conv_Ovf_U8_Un,
				Conv_Ovf_I_Un,
				Conv_Ovf_U_Un,
				Box,
				Newarr,
				Ldlen,
				Ldelema,
				Ldelem_I1,
				Ldelem_U1,
				Ldelem_I2,
				Ldelem_U2,
				Ldelem_I4,
				Ldelem_U4,
				Ldelem_I8,
				Ldelem_I,
				Ldelem_R4,
				Ldelem_R8,
				Ldelem_Ref,
				Stelem_I,
				Stelem_I1,
				Stelem_I2,
				Stelem_I4,
				Stelem_I8,
				Stelem_R4,
				Stelem_R8,
				Stelem_Ref,
				Ldelem_Any,
				Stelem_Any,
				Unbox_Any,
				Conv_Ovf_I1,
				Conv_Ovf_U1,
				Conv_Ovf_I2,
				Conv_Ovf_U2,
				Conv_Ovf_I4,
				Conv_Ovf_U4,
				Conv_Ovf_I8,
				Conv_Ovf_U8,
				Refanyval,
				Ckfinite,
				Mkrefany,
				Ldtoken,
				Conv_U2,
				Conv_U1,
				Conv_I,
				Conv_Ovf_I,
				Conv_Ovf_U,
				Add_Ovf,
				Add_Ovf_Un,
				Mul_Ovf,
				Mul_Ovf_Un,
				Sub_Ovf,
				Sub_Ovf_Un,
				Endfinally,
				Leave,
				Leave_S,
				Stind_I,
				Conv_U,
				Arglist,
				Ceq,
				Cgt,
				Cgt_Un,
				Clt,
				Clt_Un,
				Ldftn,
				Ldvirtftn,
				Ldarg,
				Ldarga,
				Starg,
				Ldloc,
				Ldloca,
				Stloc,
				Localloc,
				Endfilter,
				Unaligned,
				Volatile,
				Tail,
				Initobj,
				Constrained,
				Cpblk,
				Initblk,
				No,
				Rethrow,
				Sizeof,
				Refanytype,
				Readonly,
			}
			public struct OpCode : IEquatable<OpCode> {

				readonly byte op1;
				readonly byte op2;
				readonly byte code;
				readonly byte flow_control;
				readonly byte opcode_type;
				readonly byte operand_type;
				readonly byte stack_behavior_pop;
				readonly byte stack_behavior_push;

				public string Name {
					get { return OpCodeNames.names [(int) Code]; }
				}

				public int Size {
					get { return op1 == 0xff ? 1 : 2; }
				}

				public byte Op1 {
					get { return op1; }
				}

				public byte Op2 {
					get { return op2; }
				}

				public short Value {
					get { return op1 == 0xff ? op2 : (short) ((op1 << 8) | op2); }
				}

				public Code Code {
					get { return (Code) code; }
				}

				public FlowControl FlowControl {
					get { return (FlowControl) flow_control; }
				}

				public OpCodeType OpCodeType {
					get { return (OpCodeType) opcode_type; }
				}

				public OperandType OperandType {
					get { return (OperandType) operand_type; }
				}

				public StackBehaviour StackBehaviourPop {
					get { return (StackBehaviour) stack_behavior_pop; }
				}

				public StackBehaviour StackBehaviourPush {
					get { return (StackBehaviour) stack_behavior_push; }
				}

				internal OpCode (int x, int y)
				{
					this.op1 = (byte) ((x >> 0) & 0xff);
					this.op2 = (byte) ((x >> 8) & 0xff);
					this.code = (byte) ((x >> 16) & 0xff);
					this.flow_control = (byte) ((x >> 24) & 0xff);

					this.opcode_type = (byte) ((y >> 0) & 0xff);
					this.operand_type = (byte) ((y >> 8) & 0xff);
					this.stack_behavior_pop = (byte) ((y >> 16) & 0xff);
					this.stack_behavior_push = (byte) ((y >> 24) & 0xff);

					if (op1 == 0xff)
						OpCodes.OneByteOpCode [op2] = this;
					else
						OpCodes.TwoBytesOpCode [op2] = this;
				}

				public override int GetHashCode ()
				{
					return Value;
				}

				public override bool Equals (object obj)
				{
					if (!(obj is OpCode))
						return false;

					var opcode = (OpCode) obj;
					return op1 == opcode.op1 && op2 == opcode.op2;
				}

				public bool Equals (OpCode opcode)
				{
					return op1 == opcode.op1 && op2 == opcode.op2;
				}

				public static bool operator == (OpCode one, OpCode other)
				{
					return one.op1 == other.op1 && one.op2 == other.op2;
				}

				public static bool operator != (OpCode one, OpCode other)
				{
					return one.op1 != other.op1 || one.op2 != other.op2;
				}

				public override string ToString ()
				{
					return Name;
				}
			}

			// Actual opcodes.

			static readonly OpCode [] OneByteOpCode = new OpCode [0xe0 + 1];
			static readonly OpCode [] TwoBytesOpCode = new OpCode [0x1e + 1];


			public static OpCode ReadOpCode ( byte[] bytecode, ref int i )
			{
				//Debug.Log( $"Reading Opcodes: {bytecode[0]} {(bytecode.Length>1?bytecode[1]:-1)}" );
				var il_opcode = bytecode[i++];
				if( il_opcode != 0xfe )
				{
					if( il_opcode >= OpCodes.OneByteOpCode.Length )
						throw new Exception( "Attempting to read opcode " + il_opcode.ToString("X2") + " which is not recognized even by cecil." );
					return OpCodes.OneByteOpCode [il_opcode];
				}
				else
				{
					var il_opcode2 = bytecode[i++];
					if( il_opcode2 >= OpCodes.TwoBytesOpCode.Length )
						throw new Exception( "Attempting to read opcode 0xfe " + il_opcode2.ToString("X2") + " which is not recognized even by cecil." );
					return OpCodes.TwoBytesOpCode [ il_opcode2 ];
				}
			}


			public static readonly OpCode Nop = new OpCode (
				0xff << 0 | 0x00 << 8 | (byte) Code.Nop << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Break = new OpCode (
				0xff << 0 | 0x01 << 8 | (byte) Code.Break << 16 | (byte) FlowControl.Break << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ldarg_0 = new OpCode (
				0xff << 0 | 0x02 << 8 | (byte) Code.Ldarg_0 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldarg_1 = new OpCode (
				0xff << 0 | 0x03 << 8 | (byte) Code.Ldarg_1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldarg_2 = new OpCode (
				0xff << 0 | 0x04 << 8 | (byte) Code.Ldarg_2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldarg_3 = new OpCode (
				0xff << 0 | 0x05 << 8 | (byte) Code.Ldarg_3 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldloc_0 = new OpCode (
				0xff << 0 | 0x06 << 8 | (byte) Code.Ldloc_0 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldloc_1 = new OpCode (
				0xff << 0 | 0x07 << 8 | (byte) Code.Ldloc_1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldloc_2 = new OpCode (
				0xff << 0 | 0x08 << 8 | (byte) Code.Ldloc_2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldloc_3 = new OpCode (
				0xff << 0 | 0x09 << 8 | (byte) Code.Ldloc_3 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Stloc_0 = new OpCode (
				0xff << 0 | 0x0a << 8 | (byte) Code.Stloc_0 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stloc_1 = new OpCode (
				0xff << 0 | 0x0b << 8 | (byte) Code.Stloc_1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stloc_2 = new OpCode (
				0xff << 0 | 0x0c << 8 | (byte) Code.Stloc_2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stloc_3 = new OpCode (
				0xff << 0 | 0x0d << 8 | (byte) Code.Stloc_3 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ldarg_S = new OpCode (
				0xff << 0 | 0x0e << 8 | (byte) Code.Ldarg_S << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineArg << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldarga_S = new OpCode (
				0xff << 0 | 0x0f << 8 | (byte) Code.Ldarga_S << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineArg << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Starg_S = new OpCode (
				0xff << 0 | 0x10 << 8 | (byte) Code.Starg_S << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineArg << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ldloc_S = new OpCode (
				0xff << 0 | 0x11 << 8 | (byte) Code.Ldloc_S << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineVar << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldloca_S = new OpCode (
				0xff << 0 | 0x12 << 8 | (byte) Code.Ldloca_S << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineVar << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Stloc_S = new OpCode (
				0xff << 0 | 0x13 << 8 | (byte) Code.Stloc_S << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineVar << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ldnull = new OpCode (
				0xff << 0 | 0x14 << 8 | (byte) Code.Ldnull << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushref << 24);

			public static readonly OpCode Ldc_I4_M1 = new OpCode (
				0xff << 0 | 0x15 << 8 | (byte) Code.Ldc_I4_M1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4_0 = new OpCode (
				0xff << 0 | 0x16 << 8 | (byte) Code.Ldc_I4_0 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4_1 = new OpCode (
				0xff << 0 | 0x17 << 8 | (byte) Code.Ldc_I4_1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4_2 = new OpCode (
				0xff << 0 | 0x18 << 8 | (byte) Code.Ldc_I4_2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4_3 = new OpCode (
				0xff << 0 | 0x19 << 8 | (byte) Code.Ldc_I4_3 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4_4 = new OpCode (
				0xff << 0 | 0x1a << 8 | (byte) Code.Ldc_I4_4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4_5 = new OpCode (
				0xff << 0 | 0x1b << 8 | (byte) Code.Ldc_I4_5 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4_6 = new OpCode (
				0xff << 0 | 0x1c << 8 | (byte) Code.Ldc_I4_6 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4_7 = new OpCode (
				0xff << 0 | 0x1d << 8 | (byte) Code.Ldc_I4_7 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4_8 = new OpCode (
				0xff << 0 | 0x1e << 8 | (byte) Code.Ldc_I4_8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4_S = new OpCode (
				0xff << 0 | 0x1f << 8 | (byte) Code.Ldc_I4_S << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineI << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I4 = new OpCode (
				0xff << 0 | 0x20 << 8 | (byte) Code.Ldc_I4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineI << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldc_I8 = new OpCode (
				0xff << 0 | 0x21 << 8 | (byte) Code.Ldc_I8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineI8 << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi8 << 24);

			public static readonly OpCode Ldc_R4 = new OpCode (
				0xff << 0 | 0x22 << 8 | (byte) Code.Ldc_R4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.ShortInlineR << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushr4 << 24);

			public static readonly OpCode Ldc_R8 = new OpCode (
				0xff << 0 | 0x23 << 8 | (byte) Code.Ldc_R8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineR << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushr8 << 24);

			public static readonly OpCode Dup = new OpCode (
				0xff << 0 | 0x25 << 8 | (byte) Code.Dup << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push1_push1 << 24);

			public static readonly OpCode Pop = new OpCode (
				0xff << 0 | 0x26 << 8 | (byte) Code.Pop << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Jmp = new OpCode (
				0xff << 0 | 0x27 << 8 | (byte) Code.Jmp << 16 | (byte) FlowControl.Call << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineMethod << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Call = new OpCode (
				0xff << 0 | 0x28 << 8 | (byte) Code.Call << 16 | (byte) FlowControl.Call << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineMethod << 8 | (byte) StackBehaviour.Varpop << 16 | (byte) StackBehaviour.Varpush << 24);

			public static readonly OpCode Calli = new OpCode (
				0xff << 0 | 0x29 << 8 | (byte) Code.Calli << 16 | (byte) FlowControl.Call << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineSig << 8 | (byte) StackBehaviour.Varpop << 16 | (byte) StackBehaviour.Varpush << 24);

			public static readonly OpCode Ret = new OpCode (
				0xff << 0 | 0x2a << 8 | (byte) Code.Ret << 16 | (byte) FlowControl.Return << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Varpop << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Br_S = new OpCode (
				0xff << 0 | 0x2b << 8 | (byte) Code.Br_S << 16 | (byte) FlowControl.Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Brfalse_S = new OpCode (
				0xff << 0 | 0x2c << 8 | (byte) Code.Brfalse_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Brtrue_S = new OpCode (
				0xff << 0 | 0x2d << 8 | (byte) Code.Brtrue_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Beq_S = new OpCode (
				0xff << 0 | 0x2e << 8 | (byte) Code.Beq_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Bge_S = new OpCode (
				0xff << 0 | 0x2f << 8 | (byte) Code.Bge_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Bgt_S = new OpCode (
				0xff << 0 | 0x30 << 8 | (byte) Code.Bgt_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ble_S = new OpCode (
				0xff << 0 | 0x31 << 8 | (byte) Code.Ble_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Blt_S = new OpCode (
				0xff << 0 | 0x32 << 8 | (byte) Code.Blt_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Bne_Un_S = new OpCode (
				0xff << 0 | 0x33 << 8 | (byte) Code.Bne_Un_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Bge_Un_S = new OpCode (
				0xff << 0 | 0x34 << 8 | (byte) Code.Bge_Un_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Bgt_Un_S = new OpCode (
				0xff << 0 | 0x35 << 8 | (byte) Code.Bgt_Un_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ble_Un_S = new OpCode (
				0xff << 0 | 0x36 << 8 | (byte) Code.Ble_Un_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Blt_Un_S = new OpCode (
				0xff << 0 | 0x37 << 8 | (byte) Code.Blt_Un_S << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Br = new OpCode (
				0xff << 0 | 0x38 << 8 | (byte) Code.Br << 16 | (byte) FlowControl.Branch << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Brfalse = new OpCode (
				0xff << 0 | 0x39 << 8 | (byte) Code.Brfalse << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Brtrue = new OpCode (
				0xff << 0 | 0x3a << 8 | (byte) Code.Brtrue << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Beq = new OpCode (
				0xff << 0 | 0x3b << 8 | (byte) Code.Beq << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Bge = new OpCode (
				0xff << 0 | 0x3c << 8 | (byte) Code.Bge << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Bgt = new OpCode (
				0xff << 0 | 0x3d << 8 | (byte) Code.Bgt << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ble = new OpCode (
				0xff << 0 | 0x3e << 8 | (byte) Code.Ble << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Blt = new OpCode (
				0xff << 0 | 0x3f << 8 | (byte) Code.Blt << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Bne_Un = new OpCode (
				0xff << 0 | 0x40 << 8 | (byte) Code.Bne_Un << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Bge_Un = new OpCode (
				0xff << 0 | 0x41 << 8 | (byte) Code.Bge_Un << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Bgt_Un = new OpCode (
				0xff << 0 | 0x42 << 8 | (byte) Code.Bgt_Un << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ble_Un = new OpCode (
				0xff << 0 | 0x43 << 8 | (byte) Code.Ble_Un << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Blt_Un = new OpCode (
				0xff << 0 | 0x44 << 8 | (byte) Code.Blt_Un << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Switch = new OpCode (
				0xff << 0 | 0x45 << 8 | (byte) Code.Switch << 16 | (byte) FlowControl.Cond_Branch << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineSwitch << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ldind_I1 = new OpCode (
				0xff << 0 | 0x46 << 8 | (byte) Code.Ldind_I1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldind_U1 = new OpCode (
				0xff << 0 | 0x47 << 8 | (byte) Code.Ldind_U1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldind_I2 = new OpCode (
				0xff << 0 | 0x48 << 8 | (byte) Code.Ldind_I2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldind_U2 = new OpCode (
				0xff << 0 | 0x49 << 8 | (byte) Code.Ldind_U2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldind_I4 = new OpCode (
				0xff << 0 | 0x4a << 8 | (byte) Code.Ldind_I4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldind_U4 = new OpCode (
				0xff << 0 | 0x4b << 8 | (byte) Code.Ldind_U4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldind_I8 = new OpCode (
				0xff << 0 | 0x4c << 8 | (byte) Code.Ldind_I8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushi8 << 24);

			public static readonly OpCode Ldind_I = new OpCode (
				0xff << 0 | 0x4d << 8 | (byte) Code.Ldind_I << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldind_R4 = new OpCode (
				0xff << 0 | 0x4e << 8 | (byte) Code.Ldind_R4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushr4 << 24);

			public static readonly OpCode Ldind_R8 = new OpCode (
				0xff << 0 | 0x4f << 8 | (byte) Code.Ldind_R8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushr8 << 24);

			public static readonly OpCode Ldind_Ref = new OpCode (
				0xff << 0 | 0x50 << 8 | (byte) Code.Ldind_Ref << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushref << 24);

			public static readonly OpCode Stind_Ref = new OpCode (
				0xff << 0 | 0x51 << 8 | (byte) Code.Stind_Ref << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stind_I1 = new OpCode (
				0xff << 0 | 0x52 << 8 | (byte) Code.Stind_I1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stind_I2 = new OpCode (
				0xff << 0 | 0x53 << 8 | (byte) Code.Stind_I2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stind_I4 = new OpCode (
				0xff << 0 | 0x54 << 8 | (byte) Code.Stind_I4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stind_I8 = new OpCode (
				0xff << 0 | 0x55 << 8 | (byte) Code.Stind_I8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi_popi8 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stind_R4 = new OpCode (
				0xff << 0 | 0x56 << 8 | (byte) Code.Stind_R4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi_popr4 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stind_R8 = new OpCode (
				0xff << 0 | 0x57 << 8 | (byte) Code.Stind_R8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi_popr8 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Add = new OpCode (
				0xff << 0 | 0x58 << 8 | (byte) Code.Add << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Sub = new OpCode (
				0xff << 0 | 0x59 << 8 | (byte) Code.Sub << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Mul = new OpCode (
				0xff << 0 | 0x5a << 8 | (byte) Code.Mul << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Div = new OpCode (
				0xff << 0 | 0x5b << 8 | (byte) Code.Div << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Div_Un = new OpCode (
				0xff << 0 | 0x5c << 8 | (byte) Code.Div_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Rem = new OpCode (
				0xff << 0 | 0x5d << 8 | (byte) Code.Rem << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Rem_Un = new OpCode (
				0xff << 0 | 0x5e << 8 | (byte) Code.Rem_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode And = new OpCode (
				0xff << 0 | 0x5f << 8 | (byte) Code.And << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Or = new OpCode (
				0xff << 0 | 0x60 << 8 | (byte) Code.Or << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Xor = new OpCode (
				0xff << 0 | 0x61 << 8 | (byte) Code.Xor << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Shl = new OpCode (
				0xff << 0 | 0x62 << 8 | (byte) Code.Shl << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Shr = new OpCode (
				0xff << 0 | 0x63 << 8 | (byte) Code.Shr << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Shr_Un = new OpCode (
				0xff << 0 | 0x64 << 8 | (byte) Code.Shr_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Neg = new OpCode (
				0xff << 0 | 0x65 << 8 | (byte) Code.Neg << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Not = new OpCode (
				0xff << 0 | 0x66 << 8 | (byte) Code.Not << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Conv_I1 = new OpCode (
				0xff << 0 | 0x67 << 8 | (byte) Code.Conv_I1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_I2 = new OpCode (
				0xff << 0 | 0x68 << 8 | (byte) Code.Conv_I2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_I4 = new OpCode (
				0xff << 0 | 0x69 << 8 | (byte) Code.Conv_I4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_I8 = new OpCode (
				0xff << 0 | 0x6a << 8 | (byte) Code.Conv_I8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi8 << 24);

			public static readonly OpCode Conv_R4 = new OpCode (
				0xff << 0 | 0x6b << 8 | (byte) Code.Conv_R4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushr4 << 24);

			public static readonly OpCode Conv_R8 = new OpCode (
				0xff << 0 | 0x6c << 8 | (byte) Code.Conv_R8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushr8 << 24);

			public static readonly OpCode Conv_U4 = new OpCode (
				0xff << 0 | 0x6d << 8 | (byte) Code.Conv_U4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_U8 = new OpCode (
				0xff << 0 | 0x6e << 8 | (byte) Code.Conv_U8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi8 << 24);

			public static readonly OpCode Callvirt = new OpCode (
				0xff << 0 | 0x6f << 8 | (byte) Code.Callvirt << 16 | (byte) FlowControl.Call << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineMethod << 8 | (byte) StackBehaviour.Varpop << 16 | (byte) StackBehaviour.Varpush << 24);

			public static readonly OpCode Cpobj = new OpCode (
				0xff << 0 | 0x70 << 8 | (byte) Code.Cpobj << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ldobj = new OpCode (
				0xff << 0 | 0x71 << 8 | (byte) Code.Ldobj << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldstr = new OpCode (
				0xff << 0 | 0x72 << 8 | (byte) Code.Ldstr << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineString << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushref << 24);

			public static readonly OpCode Newobj = new OpCode (
				0xff << 0 | 0x73 << 8 | (byte) Code.Newobj << 16 | (byte) FlowControl.Call << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineMethod << 8 | (byte) StackBehaviour.Varpop << 16 | (byte) StackBehaviour.Pushref << 24);

			public static readonly OpCode Castclass = new OpCode (
				0xff << 0 | 0x74 << 8 | (byte) Code.Castclass << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popref << 16 | (byte) StackBehaviour.Pushref << 24);

			public static readonly OpCode Isinst = new OpCode (
				0xff << 0 | 0x75 << 8 | (byte) Code.Isinst << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popref << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_R_Un = new OpCode (
				0xff << 0 | 0x76 << 8 | (byte) Code.Conv_R_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushr8 << 24);

			public static readonly OpCode Unbox = new OpCode (
				0xff << 0 | 0x79 << 8 | (byte) Code.Unbox << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popref << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Throw = new OpCode (
				0xff << 0 | 0x7a << 8 | (byte) Code.Throw << 16 | (byte) FlowControl.Throw << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ldfld = new OpCode (
				0xff << 0 | 0x7b << 8 | (byte) Code.Ldfld << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineField << 8 | (byte) StackBehaviour.Popref << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldflda = new OpCode (
				0xff << 0 | 0x7c << 8 | (byte) Code.Ldflda << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineField << 8 | (byte) StackBehaviour.Popref << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Stfld = new OpCode (
				0xff << 0 | 0x7d << 8 | (byte) Code.Stfld << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineField << 8 | (byte) StackBehaviour.Popref_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ldsfld = new OpCode (
				0xff << 0 | 0x7e << 8 | (byte) Code.Ldsfld << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineField << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldsflda = new OpCode (
				0xff << 0 | 0x7f << 8 | (byte) Code.Ldsflda << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineField << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Stsfld = new OpCode (
				0xff << 0 | 0x80 << 8 | (byte) Code.Stsfld << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineField << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stobj = new OpCode (
				0xff << 0 | 0x81 << 8 | (byte) Code.Stobj << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popi_pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Conv_Ovf_I1_Un = new OpCode (
				0xff << 0 | 0x82 << 8 | (byte) Code.Conv_Ovf_I1_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_I2_Un = new OpCode (
				0xff << 0 | 0x83 << 8 | (byte) Code.Conv_Ovf_I2_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_I4_Un = new OpCode (
				0xff << 0 | 0x84 << 8 | (byte) Code.Conv_Ovf_I4_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_I8_Un = new OpCode (
				0xff << 0 | 0x85 << 8 | (byte) Code.Conv_Ovf_I8_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi8 << 24);

			public static readonly OpCode Conv_Ovf_U1_Un = new OpCode (
				0xff << 0 | 0x86 << 8 | (byte) Code.Conv_Ovf_U1_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_U2_Un = new OpCode (
				0xff << 0 | 0x87 << 8 | (byte) Code.Conv_Ovf_U2_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_U4_Un = new OpCode (
				0xff << 0 | 0x88 << 8 | (byte) Code.Conv_Ovf_U4_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_U8_Un = new OpCode (
				0xff << 0 | 0x89 << 8 | (byte) Code.Conv_Ovf_U8_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi8 << 24);

			public static readonly OpCode Conv_Ovf_I_Un = new OpCode (
				0xff << 0 | 0x8a << 8 | (byte) Code.Conv_Ovf_I_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_U_Un = new OpCode (
				0xff << 0 | 0x8b << 8 | (byte) Code.Conv_Ovf_U_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Box = new OpCode (
				0xff << 0 | 0x8c << 8 | (byte) Code.Box << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushref << 24);

			public static readonly OpCode Newarr = new OpCode (
				0xff << 0 | 0x8d << 8 | (byte) Code.Newarr << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushref << 24);

			public static readonly OpCode Ldlen = new OpCode (
				0xff << 0 | 0x8e << 8 | (byte) Code.Ldlen << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldelema = new OpCode (
				0xff << 0 | 0x8f << 8 | (byte) Code.Ldelema << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldelem_I1 = new OpCode (
				0xff << 0 | 0x90 << 8 | (byte) Code.Ldelem_I1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldelem_U1 = new OpCode (
				0xff << 0 | 0x91 << 8 | (byte) Code.Ldelem_U1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldelem_I2 = new OpCode (
				0xff << 0 | 0x92 << 8 | (byte) Code.Ldelem_I2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldelem_U2 = new OpCode (
				0xff << 0 | 0x93 << 8 | (byte) Code.Ldelem_U2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldelem_I4 = new OpCode (
				0xff << 0 | 0x94 << 8 | (byte) Code.Ldelem_I4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldelem_U4 = new OpCode (
				0xff << 0 | 0x95 << 8 | (byte) Code.Ldelem_U4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldelem_I8 = new OpCode (
				0xff << 0 | 0x96 << 8 | (byte) Code.Ldelem_I8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushi8 << 24);

			public static readonly OpCode Ldelem_I = new OpCode (
				0xff << 0 | 0x97 << 8 | (byte) Code.Ldelem_I << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldelem_R4 = new OpCode (
				0xff << 0 | 0x98 << 8 | (byte) Code.Ldelem_R4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushr4 << 24);

			public static readonly OpCode Ldelem_R8 = new OpCode (
				0xff << 0 | 0x99 << 8 | (byte) Code.Ldelem_R8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushr8 << 24);

			public static readonly OpCode Ldelem_Ref = new OpCode (
				0xff << 0 | 0x9a << 8 | (byte) Code.Ldelem_Ref << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Pushref << 24);

			public static readonly OpCode Stelem_I = new OpCode (
				0xff << 0 | 0x9b << 8 | (byte) Code.Stelem_I << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stelem_I1 = new OpCode (
				0xff << 0 | 0x9c << 8 | (byte) Code.Stelem_I1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stelem_I2 = new OpCode (
				0xff << 0 | 0x9d << 8 | (byte) Code.Stelem_I2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stelem_I4 = new OpCode (
				0xff << 0 | 0x9e << 8 | (byte) Code.Stelem_I4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stelem_I8 = new OpCode (
				0xff << 0 | 0x9f << 8 | (byte) Code.Stelem_I8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi_popi8 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stelem_R4 = new OpCode (
				0xff << 0 | 0xa0 << 8 | (byte) Code.Stelem_R4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi_popr4 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stelem_R8 = new OpCode (
				0xff << 0 | 0xa1 << 8 | (byte) Code.Stelem_R8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi_popr8 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stelem_Ref = new OpCode (
				0xff << 0 | 0xa2 << 8 | (byte) Code.Stelem_Ref << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popref_popi_popref << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ldelem_Any = new OpCode (
				0xff << 0 | 0xa3 << 8 | (byte) Code.Ldelem_Any << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popref_popi << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Stelem_Any = new OpCode (
				0xff << 0 | 0xa4 << 8 | (byte) Code.Stelem_Any << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popref_popi_popref << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Unbox_Any = new OpCode (
				0xff << 0 | 0xa5 << 8 | (byte) Code.Unbox_Any << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popref << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Conv_Ovf_I1 = new OpCode (
				0xff << 0 | 0xb3 << 8 | (byte) Code.Conv_Ovf_I1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_U1 = new OpCode (
				0xff << 0 | 0xb4 << 8 | (byte) Code.Conv_Ovf_U1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_I2 = new OpCode (
				0xff << 0 | 0xb5 << 8 | (byte) Code.Conv_Ovf_I2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_U2 = new OpCode (
				0xff << 0 | 0xb6 << 8 | (byte) Code.Conv_Ovf_U2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_I4 = new OpCode (
				0xff << 0 | 0xb7 << 8 | (byte) Code.Conv_Ovf_I4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_U4 = new OpCode (
				0xff << 0 | 0xb8 << 8 | (byte) Code.Conv_Ovf_U4 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_I8 = new OpCode (
				0xff << 0 | 0xb9 << 8 | (byte) Code.Conv_Ovf_I8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi8 << 24);

			public static readonly OpCode Conv_Ovf_U8 = new OpCode (
				0xff << 0 | 0xba << 8 | (byte) Code.Conv_Ovf_U8 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi8 << 24);

			public static readonly OpCode Refanyval = new OpCode (
				0xff << 0 | 0xc2 << 8 | (byte) Code.Refanyval << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ckfinite = new OpCode (
				0xff << 0 | 0xc3 << 8 | (byte) Code.Ckfinite << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushr8 << 24);

			public static readonly OpCode Mkrefany = new OpCode (
				0xff << 0 | 0xc6 << 8 | (byte) Code.Mkrefany << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldtoken = new OpCode (
				0xff << 0 | 0xd0 << 8 | (byte) Code.Ldtoken << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineTok << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_U2 = new OpCode (
				0xff << 0 | 0xd1 << 8 | (byte) Code.Conv_U2 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_U1 = new OpCode (
				0xff << 0 | 0xd2 << 8 | (byte) Code.Conv_U1 << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_I = new OpCode (
				0xff << 0 | 0xd3 << 8 | (byte) Code.Conv_I << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_I = new OpCode (
				0xff << 0 | 0xd4 << 8 | (byte) Code.Conv_Ovf_I << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Conv_Ovf_U = new OpCode (
				0xff << 0 | 0xd5 << 8 | (byte) Code.Conv_Ovf_U << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Add_Ovf = new OpCode (
				0xff << 0 | 0xd6 << 8 | (byte) Code.Add_Ovf << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Add_Ovf_Un = new OpCode (
				0xff << 0 | 0xd7 << 8 | (byte) Code.Add_Ovf_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Mul_Ovf = new OpCode (
				0xff << 0 | 0xd8 << 8 | (byte) Code.Mul_Ovf << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Mul_Ovf_Un = new OpCode (
				0xff << 0 | 0xd9 << 8 | (byte) Code.Mul_Ovf_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Sub_Ovf = new OpCode (
				0xff << 0 | 0xda << 8 | (byte) Code.Sub_Ovf << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Sub_Ovf_Un = new OpCode (
				0xff << 0 | 0xdb << 8 | (byte) Code.Sub_Ovf_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Endfinally = new OpCode (
				0xff << 0 | 0xdc << 8 | (byte) Code.Endfinally << 16 | (byte) FlowControl.Return << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Leave = new OpCode (
				0xff << 0 | 0xdd << 8 | (byte) Code.Leave << 16 | (byte) FlowControl.Branch << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineBrTarget << 8 | (byte) StackBehaviour.PopAll << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Leave_S = new OpCode (
				0xff << 0 | 0xde << 8 | (byte) Code.Leave_S << 16 | (byte) FlowControl.Branch << 24,
				(byte) OpCodeType.Macro << 0 | (byte) OperandType.ShortInlineBrTarget << 8 | (byte) StackBehaviour.PopAll << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Stind_I = new OpCode (
				0xff << 0 | 0xdf << 8 | (byte) Code.Stind_I << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Conv_U = new OpCode (
				0xff << 0 | 0xe0 << 8 | (byte) Code.Conv_U << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Arglist = new OpCode (
				0xfe << 0 | 0x00 << 8 | (byte) Code.Arglist << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ceq = new OpCode (
				0xfe << 0 | 0x01 << 8 | (byte) Code.Ceq << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Cgt = new OpCode (
				0xfe << 0 | 0x02 << 8 | (byte) Code.Cgt << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Cgt_Un = new OpCode (
				0xfe << 0 | 0x03 << 8 | (byte) Code.Cgt_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Clt = new OpCode (
				0xfe << 0 | 0x04 << 8 | (byte) Code.Clt << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Clt_Un = new OpCode (
				0xfe << 0 | 0x05 << 8 | (byte) Code.Clt_Un << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1_pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldftn = new OpCode (
				0xfe << 0 | 0x06 << 8 | (byte) Code.Ldftn << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineMethod << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldvirtftn = new OpCode (
				0xfe << 0 | 0x07 << 8 | (byte) Code.Ldvirtftn << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineMethod << 8 | (byte) StackBehaviour.Popref << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Ldarg = new OpCode (
				0xfe << 0 | 0x09 << 8 | (byte) Code.Ldarg << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineArg << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldarga = new OpCode (
				0xfe << 0 | 0x0a << 8 | (byte) Code.Ldarga << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineArg << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Starg = new OpCode (
				0xfe << 0 | 0x0b << 8 | (byte) Code.Starg << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineArg << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Ldloc = new OpCode (
				0xfe << 0 | 0x0c << 8 | (byte) Code.Ldloc << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineVar << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push1 << 24);

			public static readonly OpCode Ldloca = new OpCode (
				0xfe << 0 | 0x0d << 8 | (byte) Code.Ldloca << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineVar << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Stloc = new OpCode (
				0xfe << 0 | 0x0e << 8 | (byte) Code.Stloc << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineVar << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Localloc = new OpCode (
				0xfe << 0 | 0x0f << 8 | (byte) Code.Localloc << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Endfilter = new OpCode (
				0xfe << 0 | 0x11 << 8 | (byte) Code.Endfilter << 16 | (byte) FlowControl.Return << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Unaligned = new OpCode (
				0xfe << 0 | 0x12 << 8 | (byte) Code.Unaligned << 16 | (byte) FlowControl.Meta << 24,
				(byte) OpCodeType.Prefix << 0 | (byte) OperandType.ShortInlineI << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Volatile = new OpCode (
				0xfe << 0 | 0x13 << 8 | (byte) Code.Volatile << 16 | (byte) FlowControl.Meta << 24,
				(byte) OpCodeType.Prefix << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Tail = new OpCode (
				0xfe << 0 | 0x14 << 8 | (byte) Code.Tail << 16 | (byte) FlowControl.Meta << 24,
				(byte) OpCodeType.Prefix << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Initobj = new OpCode (
				0xfe << 0 | 0x15 << 8 | (byte) Code.Initobj << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Constrained = new OpCode (
				0xfe << 0 | 0x16 << 8 | (byte) Code.Constrained << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Prefix << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Cpblk = new OpCode (
				0xfe << 0 | 0x17 << 8 | (byte) Code.Cpblk << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi_popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Initblk = new OpCode (
				0xfe << 0 | 0x18 << 8 | (byte) Code.Initblk << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Popi_popi_popi << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode No = new OpCode (
				0xfe << 0 | 0x19 << 8 | (byte) Code.No << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Prefix << 0 | (byte) OperandType.ShortInlineI << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Rethrow = new OpCode (
				0xfe << 0 | 0x1a << 8 | (byte) Code.Rethrow << 16 | (byte) FlowControl.Throw << 24,
				(byte) OpCodeType.Objmodel << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);

			public static readonly OpCode Sizeof = new OpCode (
				0xfe << 0 | 0x1c << 8 | (byte) Code.Sizeof << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineType << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Refanytype = new OpCode (
				0xfe << 0 | 0x1d << 8 | (byte) Code.Refanytype << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Primitive << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop1 << 16 | (byte) StackBehaviour.Pushi << 24);

			public static readonly OpCode Readonly = new OpCode (
				0xfe << 0 | 0x1e << 8 | (byte) Code.Readonly << 16 | (byte) FlowControl.Next << 24,
				(byte) OpCodeType.Prefix << 0 | (byte) OperandType.InlineNone << 8 | (byte) StackBehaviour.Pop0 << 16 | (byte) StackBehaviour.Push0 << 24);
		}
		static class OpCodeNames {

			internal static readonly string [] names;

			static OpCodeNames ()
			{
				var table = new byte [] {
					3, 110, 111, 112,
					5, 98, 114, 101, 97, 107,
					7, 108, 100, 97, 114, 103, 46, 48,
					7, 108, 100, 97, 114, 103, 46, 49,
					7, 108, 100, 97, 114, 103, 46, 50,
					7, 108, 100, 97, 114, 103, 46, 51,
					7, 108, 100, 108, 111, 99, 46, 48,
					7, 108, 100, 108, 111, 99, 46, 49,
					7, 108, 100, 108, 111, 99, 46, 50,
					7, 108, 100, 108, 111, 99, 46, 51,
					7, 115, 116, 108, 111, 99, 46, 48,
					7, 115, 116, 108, 111, 99, 46, 49,
					7, 115, 116, 108, 111, 99, 46, 50,
					7, 115, 116, 108, 111, 99, 46, 51,
					7, 108, 100, 97, 114, 103, 46, 115,
					8, 108, 100, 97, 114, 103, 97, 46, 115,
					7, 115, 116, 97, 114, 103, 46, 115,
					7, 108, 100, 108, 111, 99, 46, 115,
					8, 108, 100, 108, 111, 99, 97, 46, 115,
					7, 115, 116, 108, 111, 99, 46, 115,
					6, 108, 100, 110, 117, 108, 108,
					9, 108, 100, 99, 46, 105, 52, 46, 109, 49,
					8, 108, 100, 99, 46, 105, 52, 46, 48,
					8, 108, 100, 99, 46, 105, 52, 46, 49,
					8, 108, 100, 99, 46, 105, 52, 46, 50,
					8, 108, 100, 99, 46, 105, 52, 46, 51,
					8, 108, 100, 99, 46, 105, 52, 46, 52,
					8, 108, 100, 99, 46, 105, 52, 46, 53,
					8, 108, 100, 99, 46, 105, 52, 46, 54,
					8, 108, 100, 99, 46, 105, 52, 46, 55,
					8, 108, 100, 99, 46, 105, 52, 46, 56,
					8, 108, 100, 99, 46, 105, 52, 46, 115,
					6, 108, 100, 99, 46, 105, 52,
					6, 108, 100, 99, 46, 105, 56,
					6, 108, 100, 99, 46, 114, 52,
					6, 108, 100, 99, 46, 114, 56,
					3, 100, 117, 112,
					3, 112, 111, 112,
					3, 106, 109, 112,
					4, 99, 97, 108, 108,
					5, 99, 97, 108, 108, 105,
					3, 114, 101, 116,
					4, 98, 114, 46, 115,
					9, 98, 114, 102, 97, 108, 115, 101, 46, 115,
					8, 98, 114, 116, 114, 117, 101, 46, 115,
					5, 98, 101, 113, 46, 115,
					5, 98, 103, 101, 46, 115,
					5, 98, 103, 116, 46, 115,
					5, 98, 108, 101, 46, 115,
					5, 98, 108, 116, 46, 115,
					8, 98, 110, 101, 46, 117, 110, 46, 115,
					8, 98, 103, 101, 46, 117, 110, 46, 115,
					8, 98, 103, 116, 46, 117, 110, 46, 115,
					8, 98, 108, 101, 46, 117, 110, 46, 115,
					8, 98, 108, 116, 46, 117, 110, 46, 115,
					2, 98, 114,
					7, 98, 114, 102, 97, 108, 115, 101,
					6, 98, 114, 116, 114, 117, 101,
					3, 98, 101, 113,
					3, 98, 103, 101,
					3, 98, 103, 116,
					3, 98, 108, 101,
					3, 98, 108, 116,
					6, 98, 110, 101, 46, 117, 110,
					6, 98, 103, 101, 46, 117, 110,
					6, 98, 103, 116, 46, 117, 110,
					6, 98, 108, 101, 46, 117, 110,
					6, 98, 108, 116, 46, 117, 110,
					6, 115, 119, 105, 116, 99, 104,
					8, 108, 100, 105, 110, 100, 46, 105, 49,
					8, 108, 100, 105, 110, 100, 46, 117, 49,
					8, 108, 100, 105, 110, 100, 46, 105, 50,
					8, 108, 100, 105, 110, 100, 46, 117, 50,
					8, 108, 100, 105, 110, 100, 46, 105, 52,
					8, 108, 100, 105, 110, 100, 46, 117, 52,
					8, 108, 100, 105, 110, 100, 46, 105, 56,
					7, 108, 100, 105, 110, 100, 46, 105,
					8, 108, 100, 105, 110, 100, 46, 114, 52,
					8, 108, 100, 105, 110, 100, 46, 114, 56,
					9, 108, 100, 105, 110, 100, 46, 114, 101, 102,
					9, 115, 116, 105, 110, 100, 46, 114, 101, 102,
					8, 115, 116, 105, 110, 100, 46, 105, 49,
					8, 115, 116, 105, 110, 100, 46, 105, 50,
					8, 115, 116, 105, 110, 100, 46, 105, 52,
					8, 115, 116, 105, 110, 100, 46, 105, 56,
					8, 115, 116, 105, 110, 100, 46, 114, 52,
					8, 115, 116, 105, 110, 100, 46, 114, 56,
					3, 97, 100, 100,
					3, 115, 117, 98,
					3, 109, 117, 108,
					3, 100, 105, 118,
					6, 100, 105, 118, 46, 117, 110,
					3, 114, 101, 109,
					6, 114, 101, 109, 46, 117, 110,
					3, 97, 110, 100,
					2, 111, 114,
					3, 120, 111, 114,
					3, 115, 104, 108,
					3, 115, 104, 114,
					6, 115, 104, 114, 46, 117, 110,
					3, 110, 101, 103,
					3, 110, 111, 116,
					7, 99, 111, 110, 118, 46, 105, 49,
					7, 99, 111, 110, 118, 46, 105, 50,
					7, 99, 111, 110, 118, 46, 105, 52,
					7, 99, 111, 110, 118, 46, 105, 56,
					7, 99, 111, 110, 118, 46, 114, 52,
					7, 99, 111, 110, 118, 46, 114, 56,
					7, 99, 111, 110, 118, 46, 117, 52,
					7, 99, 111, 110, 118, 46, 117, 56,
					8, 99, 97, 108, 108, 118, 105, 114, 116,
					5, 99, 112, 111, 98, 106,
					5, 108, 100, 111, 98, 106,
					5, 108, 100, 115, 116, 114,
					6, 110, 101, 119, 111, 98, 106,
					9, 99, 97, 115, 116, 99, 108, 97, 115, 115,
					6, 105, 115, 105, 110, 115, 116,
					9, 99, 111, 110, 118, 46, 114, 46, 117, 110,
					5, 117, 110, 98, 111, 120,
					5, 116, 104, 114, 111, 119,
					5, 108, 100, 102, 108, 100,
					6, 108, 100, 102, 108, 100, 97,
					5, 115, 116, 102, 108, 100,
					6, 108, 100, 115, 102, 108, 100,
					7, 108, 100, 115, 102, 108, 100, 97,
					6, 115, 116, 115, 102, 108, 100,
					5, 115, 116, 111, 98, 106,
					14, 99, 111, 110, 118, 46, 111, 118, 102, 46, 105, 49, 46, 117, 110,
					14, 99, 111, 110, 118, 46, 111, 118, 102, 46, 105, 50, 46, 117, 110,
					14, 99, 111, 110, 118, 46, 111, 118, 102, 46, 105, 52, 46, 117, 110,
					14, 99, 111, 110, 118, 46, 111, 118, 102, 46, 105, 56, 46, 117, 110,
					14, 99, 111, 110, 118, 46, 111, 118, 102, 46, 117, 49, 46, 117, 110,
					14, 99, 111, 110, 118, 46, 111, 118, 102, 46, 117, 50, 46, 117, 110,
					14, 99, 111, 110, 118, 46, 111, 118, 102, 46, 117, 52, 46, 117, 110,
					14, 99, 111, 110, 118, 46, 111, 118, 102, 46, 117, 56, 46, 117, 110,
					13, 99, 111, 110, 118, 46, 111, 118, 102, 46, 105, 46, 117, 110,
					13, 99, 111, 110, 118, 46, 111, 118, 102, 46, 117, 46, 117, 110,
					3, 98, 111, 120,
					6, 110, 101, 119, 97, 114, 114,
					5, 108, 100, 108, 101, 110,
					7, 108, 100, 101, 108, 101, 109, 97,
					9, 108, 100, 101, 108, 101, 109, 46, 105, 49,
					9, 108, 100, 101, 108, 101, 109, 46, 117, 49,
					9, 108, 100, 101, 108, 101, 109, 46, 105, 50,
					9, 108, 100, 101, 108, 101, 109, 46, 117, 50,
					9, 108, 100, 101, 108, 101, 109, 46, 105, 52,
					9, 108, 100, 101, 108, 101, 109, 46, 117, 52,
					9, 108, 100, 101, 108, 101, 109, 46, 105, 56,
					8, 108, 100, 101, 108, 101, 109, 46, 105,
					9, 108, 100, 101, 108, 101, 109, 46, 114, 52,
					9, 108, 100, 101, 108, 101, 109, 46, 114, 56,
					10, 108, 100, 101, 108, 101, 109, 46, 114, 101, 102,
					8, 115, 116, 101, 108, 101, 109, 46, 105,
					9, 115, 116, 101, 108, 101, 109, 46, 105, 49,
					9, 115, 116, 101, 108, 101, 109, 46, 105, 50,
					9, 115, 116, 101, 108, 101, 109, 46, 105, 52,
					9, 115, 116, 101, 108, 101, 109, 46, 105, 56,
					9, 115, 116, 101, 108, 101, 109, 46, 114, 52,
					9, 115, 116, 101, 108, 101, 109, 46, 114, 56,
					10, 115, 116, 101, 108, 101, 109, 46, 114, 101, 102,
					10, 108, 100, 101, 108, 101, 109, 46, 97, 110, 121,
					10, 115, 116, 101, 108, 101, 109, 46, 97, 110, 121,
					9, 117, 110, 98, 111, 120, 46, 97, 110, 121,
					11, 99, 111, 110, 118, 46, 111, 118, 102, 46, 105, 49,
					11, 99, 111, 110, 118, 46, 111, 118, 102, 46, 117, 49,
					11, 99, 111, 110, 118, 46, 111, 118, 102, 46, 105, 50,
					11, 99, 111, 110, 118, 46, 111, 118, 102, 46, 117, 50,
					11, 99, 111, 110, 118, 46, 111, 118, 102, 46, 105, 52,
					11, 99, 111, 110, 118, 46, 111, 118, 102, 46, 117, 52,
					11, 99, 111, 110, 118, 46, 111, 118, 102, 46, 105, 56,
					11, 99, 111, 110, 118, 46, 111, 118, 102, 46, 117, 56,
					9, 114, 101, 102, 97, 110, 121, 118, 97, 108,
					8, 99, 107, 102, 105, 110, 105, 116, 101,
					8, 109, 107, 114, 101, 102, 97, 110, 121,
					7, 108, 100, 116, 111, 107, 101, 110,
					7, 99, 111, 110, 118, 46, 117, 50,
					7, 99, 111, 110, 118, 46, 117, 49,
					6, 99, 111, 110, 118, 46, 105,
					10, 99, 111, 110, 118, 46, 111, 118, 102, 46, 105,
					10, 99, 111, 110, 118, 46, 111, 118, 102, 46, 117,
					7, 97, 100, 100, 46, 111, 118, 102,
					10, 97, 100, 100, 46, 111, 118, 102, 46, 117, 110,
					7, 109, 117, 108, 46, 111, 118, 102,
					10, 109, 117, 108, 46, 111, 118, 102, 46, 117, 110,
					7, 115, 117, 98, 46, 111, 118, 102,
					10, 115, 117, 98, 46, 111, 118, 102, 46, 117, 110,
					10, 101, 110, 100, 102, 105, 110, 97, 108, 108, 121,
					5, 108, 101, 97, 118, 101,
					7, 108, 101, 97, 118, 101, 46, 115,
					7, 115, 116, 105, 110, 100, 46, 105,
					6, 99, 111, 110, 118, 46, 117,
					7, 97, 114, 103, 108, 105, 115, 116,
					3, 99, 101, 113,
					3, 99, 103, 116,
					6, 99, 103, 116, 46, 117, 110,
					3, 99, 108, 116,
					6, 99, 108, 116, 46, 117, 110,
					5, 108, 100, 102, 116, 110,
					9, 108, 100, 118, 105, 114, 116, 102, 116, 110,
					5, 108, 100, 97, 114, 103,
					6, 108, 100, 97, 114, 103, 97,
					5, 115, 116, 97, 114, 103,
					5, 108, 100, 108, 111, 99,
					6, 108, 100, 108, 111, 99, 97,
					5, 115, 116, 108, 111, 99,
					8, 108, 111, 99, 97, 108, 108, 111, 99,
					9, 101, 110, 100, 102, 105, 108, 116, 101, 114,
					10, 117, 110, 97, 108, 105, 103, 110, 101, 100, 46,
					9, 118, 111, 108, 97, 116, 105, 108, 101, 46,
					5, 116, 97, 105, 108, 46,
					7, 105, 110, 105, 116, 111, 98, 106,
					12, 99, 111, 110, 115, 116, 114, 97, 105, 110, 101, 100, 46,
					5, 99, 112, 98, 108, 107,
					7, 105, 110, 105, 116, 98, 108, 107,
					3, 110, 111, 46,
					7, 114, 101, 116, 104, 114, 111, 119,
					6, 115, 105, 122, 101, 111, 102,
					10, 114, 101, 102, 97, 110, 121, 116, 121, 112, 101,
					9, 114, 101, 97, 100, 111, 110, 108, 121, 46,
				};

				names = new string [219];

				for (int i = 0, p = 0; i < names.Length; i++) {
					var buffer = new char [table [p++]];

					for (int j = 0; j < buffer.Length; j++)
						buffer [j] = (char) table [p++];

					names [i] = new string (buffer);
				}
			}
		}
	}
}

