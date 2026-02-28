//#define PER_INSTRUCTION_PROFILING

using UnityEngine;
using System.Collections.Generic;
using System;
using System.Collections.Specialized;
using System.Collections;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading; // At runtime, only used for a lock (Monitor)

#if UNITY_EDITOR
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using System.IO;
using System.Threading.Tasks;
#endif


// To add [Cilboxable] to your classes that you want exported.
public class CilboxableAttribute : Attribute { }
public class CilboxTarget : Attribute { }

namespace Cilbox
{
	public class CilboxExceptionHandlingClause
	{
		public ExceptionHandlingClauseOptions Flags;
		public int TryOffset;
		public int TryLength;
		public int TryEndOffset;
		public int HandlerOffset;
		public int HandlerLength;
		public int HandlerEndOffset;
		public Type? CatchType;
		public string? CatchTypeName;
	}

	public class CilboxMethod
	{
		public CilboxClass parentClass;
		public int MaxStackSize;
		public String methodName;
		public String fullSignature;
		public String[] methodLocals;
		public bool isStatic;
		public Type[] typeLocals;
		public byte[] byteCode;
		public bool isVoid;
		public String[] signatureParameters;
		public Type[]   typeParameters;
		public CilboxExceptionHandlingClause[] exceptionClauses;
		public bool hasExceptionClauses = false;
		public Dictionary<int, CilboxExceptionHandlingClause> handlerOffsetToClauseMap;

#if UNITY_EDITOR
		ProfilerMarker perfMarkerInterpret;
#endif

		public void Load( CilboxClass cclass, String name, Serializee payload )
		{
			methodName = name;
			parentClass = cclass;
			Dictionary<String, Serializee> methodProps = payload.AsMap();

			Serializee [] vl = methodProps["locals"].AsArray();
			methodLocals = new String[vl.Length];
			typeLocals = new Type[vl.Length];
			int iid = 0;
			for( int i = 0; i < vl.Length; i++ )
			{
				Dictionary< String, Serializee > local = vl[i].AsMap();
				methodLocals[iid] = local["name"].AsString();
				typeLocals[iid] = parentClass.box.usage.GetNativeTypeFromSerializee( local["dt"] );
				iid++;
			}

			byteCode = methodProps["body"].AsBlob();

			MaxStackSize = Convert.ToInt32((methodProps["maxStack"].AsString()));
			isVoid = Convert.ToInt32((methodProps["isVoid"].AsString())) != 0;
			isStatic = Convert.ToInt32((methodProps["isStatic"].AsString())) != 0;
			fullSignature = methodProps["fullSignature"].AsString();

			Serializee [] od = methodProps["parameters"].AsArray();
			signatureParameters = new String[od.Length];
			typeParameters = new Type[od.Length];
			int sn = 0;
			for( int p = 0; p < od.Length; p ++ )
			{
				Dictionary< String, Serializee > thisp = od[p].AsMap();
				signatureParameters[sn] = thisp["name"].AsString();
				typeParameters[sn] = parentClass.box.usage.GetNativeTypeFromSerializee( thisp["dt"] );
				sn++;
			}

			if (methodProps.TryGetValue("eh", out Serializee ehArray))
			{
				Serializee[] ehc = ehArray.AsArray();
				exceptionClauses = new CilboxExceptionHandlingClause[ehc.Length];
				handlerOffsetToClauseMap = new Dictionary<int, CilboxExceptionHandlingClause>();
				for (int e = 0; e < ehc.Length; e++)
				{
					Dictionary< String, Serializee > thisehc = ehc[e].AsMap();
					CilboxExceptionHandlingClause clause = new CilboxExceptionHandlingClause();
					clause.Flags = (ExceptionHandlingClauseOptions)Convert.ToInt32(thisehc["flags"].AsString());
					clause.TryOffset = Convert.ToInt32(thisehc["tryOff"].AsString());
					clause.TryLength = Convert.ToInt32(thisehc["tryLen"].AsString());
					clause.TryEndOffset = clause.TryOffset + clause.TryLength;
					clause.HandlerOffset = Convert.ToInt32(thisehc["hOff"].AsString());
					clause.HandlerLength = Convert.ToInt32(thisehc["hLen"].AsString());
					clause.HandlerEndOffset = clause.HandlerOffset + clause.HandlerLength;
					if (thisehc.ContainsKey("cType"))
					{
						clause.CatchType = parentClass.box.usage.GetNativeTypeFromSerializee(thisehc["cType"]);
						if (clause.CatchType == null)
						{
							// Check if it's a Cilboxable type
							String typeName = thisehc["cType"].AsMap()["n"].AsString();
							if (parentClass.box.classes.ContainsKey(typeName))
							{
								clause.CatchTypeName = typeName;
							}
						}
					}
					exceptionClauses[e] = clause;
					handlerOffsetToClauseMap[clause.HandlerOffset] = clause;
				}
				hasExceptionClauses = exceptionClauses.Length > 0;

				Array.Sort(exceptionClauses, CompareExceptionClausesTryLengthDescHandlerOffsetDesc);
			}

#if UNITY_EDITOR
			perfMarkerInterpret = new ProfilerMarker(parentClass.className + ":" + fullSignature);
#endif

			static int CompareExceptionClausesTryLengthDescHandlerOffsetDesc( CilboxExceptionHandlingClause a, CilboxExceptionHandlingClause b )
			{
				int res = b.TryLength.CompareTo( a.TryLength );
				if (res == 0)
				{
					return b.HandlerOffset.CompareTo( a.HandlerOffset );
				}
				return res;
			}

		}

		public object Interpret( CilboxProxy ths, object [] parametersIn )
		{
			StackElement [] parameters;
			StackElement [] stackBuffer = new StackElement[Cilbox.defaultStackSize];

			int plen = 0;
			if( parametersIn != null )
			{
				plen = parametersIn.Length;
			}

			if( isStatic )
			{
				parameters = new StackElement[plen];
				for( int p = 0; p < plen; p++ )
					parameters[p].Load( parametersIn[p] );
			}
			else
			{
				parameters = new StackElement[plen+1];
				parameters[0].Load( ths );
				for( int p = 0; p < plen; p++ )
					parameters[p+1].Load( parametersIn[p] );
				plen++;
			}

			object ret = null;
			if( !parentClass.box.InterpreterEntry(this) ) return null;
			try
			{
				ret = InterpretInner( stackBuffer, parameters ).AsObject();
			} catch( Exception e )
			{
				parentClass.box.InterpreterExit();
				Debug.Log( e.ToString() );
				throw;
			}
			parentClass.box.InterpreterExit();
			return ret;
		}

		private StackElement InterpretInner( ArraySegment<StackElement> stackBufferIn, ArraySegment<StackElement> parametersIn )
		{
			Span<StackElement> stackBuffer = stackBufferIn.AsSpan();
			Span<StackElement> parameters = parametersIn.AsSpan();
			Stack<int> handlerClauseStack = null; // don't allocate unless necessary

#if UNITY_EDITOR
			perfMarkerInterpret.Begin();
#endif

			Cilbox box = parentClass.box;

			int localVarsHead = MaxStackSize;
			int stackContinues = localVarsHead + methodLocals.Length;
			StackElement? exceptionRegister = null;

			// Uncomment for debugging.
#if false
			bool bDeepDebug = false;
			if( parentClass.className.Contains("TestScript2") )//fullSignature.Contains( "TestScript2" ) )
			{
				bDeepDebug = true;
				String parmSt = ""; for( int sk = 0; sk < parameters.Length; sk++ ) {
					parmSt += "/"; parmSt += parameters[sk].AsObject() + "+" + parameters[sk].type;
				}
				Debug.Log( "***** FUNCTION ENTRY " + parentClass.className + " " + methodName + " " + parametersIn.Offset + " PARM:" + parmSt);
				bDeepDebug = true;
			}
#endif
			int sp = -1;
			bool cont = true;
			int pc = 0;
			try
			{
				do
				{
					// While this is not threadsafe, that's OK.  This is more for broad strokes.
					// We don't have to worry about critical pieces going in/out of a race condition
					// for instance, interpreterAccountingLastStart can't go wonky on us.
					//
					// If you use Interlocked.Add() it slows the whole emulator down by about 40%!
					long steps = ++box.interpreterInstructionsCount;
					if( ( steps & 0x3f ) == 0 )
					{
						long now = System.Diagnostics.Stopwatch.GetTimestamp();
						if( now > box.interpreterAccountingDropDead )
						{
							box.interpreterAccountingCumulitiveTicks = now + box.timeoutLengthUs * box.interpreterTicksInUs - box.interpreterAccountingDropDead;
							cont = false;
							throw new CilboxInterpreterTimeoutException( "Script time resources overutilized (Timeout Us: " + box.interpreterAccountingCumulitiveTicks / box.interpreterTicksInUs + "/" + box.timeoutLengthUs + " )", parentClass.className, methodName, pc);
						}
					}

					byte b = byteCode[pc];

#if false
					// Uncomment for debugging.
					if( bDeepDebug )
					{
						String stackSt = ""; for( int sk = 0; sk < stackBufferIn.Count; sk++ ) { stackSt += "/"; if( sk == sp ) stackSt += ">"; stackSt += stackBuffer[sk].AsObject() + "+" + stackBuffer[sk].type; if( sk == sp ) stackSt += "<"; }
						int icopy = pc; CilboxUtil.OpCodes.OpCode opc = CilboxUtil.OpCodes.ReadOpCode ( byteCode, ref icopy );
						Debug.Log( "Bytecode " + opc + " (" + b.ToString("X2") + ") @ " + pc + "/" + byteCode.Length + " " + stackSt);
					}
#endif
// For itty bitty profiling.

#if PER_INSTRUCTION_PROFILING // Opcode profiling
int xicopy = pc; CilboxUtil.OpCodes.OpCode opcx = CilboxUtil.OpCodes.ReadOpCode ( byteCode, ref xicopy );
var spiperf = new ProfilerMarker(opcx.ToString());
spiperf.Begin();
#endif

					pc++;
					switch( b )
					{
					case 0x00: break; // nop
					case 0x01: throw new CilboxInterpreterRuntimeException($"Debug Break", parentClass.className, methodName, pc); // break
					case 0x02: stackBuffer[++sp] = parameters[0]; break; //ldarg.0
					case 0x03: stackBuffer[++sp] = parameters[1]; break; //ldarg.1
					case 0x04: stackBuffer[++sp] = parameters[2]; break; //ldarg.2
					case 0x05: stackBuffer[++sp] = parameters[3]; break; //ldarg.3
					case 0x06: stackBuffer[++sp] = stackBuffer[localVarsHead+0]; break; //ldloc.0
					case 0x07: stackBuffer[++sp] = stackBuffer[localVarsHead+1]; break; //ldloc.1
					case 0x08: stackBuffer[++sp] = stackBuffer[localVarsHead+2]; break; //ldloc.2
					case 0x09: stackBuffer[++sp] = stackBuffer[localVarsHead+3]; break; //ldloc.3
					case 0x0a: stackBuffer[localVarsHead+0] = stackBuffer[sp--]; break; //stloc.0
					case 0x0b: stackBuffer[localVarsHead+1] = stackBuffer[sp--]; break; //stloc.1
					case 0x0c: stackBuffer[localVarsHead+2] = stackBuffer[sp--]; break; //stloc.2
					case 0x0d: stackBuffer[localVarsHead+3] = stackBuffer[sp--]; break; //stloc.3
					case 0x0e: stackBuffer[++sp] = parameters[byteCode[pc++]]; break; // ldarg.s <uint8 (argNum)>
					case 0x0f: stackBuffer[++sp] = StackElement.CreateAddressReference( parametersIn.Array, (uint)parametersIn.Offset + (uint)byteCode[pc++] ); break; // ldarga.s <uint8 (argNum)>
					case 0x11: stackBuffer[++sp] = stackBuffer[localVarsHead+byteCode[pc++]]; break; //ldloc.s
					case 0x12:
					{
						uint whichLocal = byteCode[pc++];
						stackBuffer[++sp] = StackElement.CreateAddressReference( stackBufferIn.Array, (uint)(localVarsHead+whichLocal+stackBufferIn.Offset) );
						break; //ldloca.s // Load address of local variable.
					}
					case 0x13: stackBuffer[localVarsHead+byteCode[pc++]] = stackBuffer[sp--]; break; //stloc.s
					case 0x14: stackBuffer[++sp].LoadObject( null ); break; // ldnull
					case 0x15: stackBuffer[++sp].LoadInt( -1 ); break; // ldc.i4.m1
					case 0x16: stackBuffer[++sp].LoadInt( 0 ); break; // ldc.i4.0
					case 0x17: stackBuffer[++sp].LoadInt( 1 ); break; // ldc.i4.1
					case 0x18: stackBuffer[++sp].LoadInt( 2 ); break; // ldc.i4.2
					case 0x19: stackBuffer[++sp].LoadInt( 3 ); break; // ldc.i4.3
					case 0x1a: stackBuffer[++sp].LoadInt( 4 ); break; // ldc.i4.4
					case 0x1b: stackBuffer[++sp].LoadInt( 5 ); break; // ldc.i4.5
					case 0x1c: stackBuffer[++sp].LoadInt( 6 ); break; // ldc.i4.6
					case 0x1d: stackBuffer[++sp].LoadInt( 7 ); break; // ldc.i4.7
					case 0x1e: stackBuffer[++sp].LoadInt( 8 ); break; // ldc.i4.8

					case 0x1f: stackBuffer[++sp].LoadInt( (sbyte)byteCode[pc++] ); break; // ldc.i4.s <int8>
					case 0x20: stackBuffer[++sp].LoadInt( (int)BytecodeAsU32( ref pc ) ); break; // ldc.i4 <int32>
					case 0x21: stackBuffer[++sp].LoadLong( (long)BytecodeAs64( ref pc ) ); break; // ldc.i8 <int64>
					case 0x22: stackBuffer[++sp].LoadFloat( CilboxUtil.IntFloatConverter.ConvertUtoF(BytecodeAsU32( ref pc ) ) ); break; // ldc.r4 <float32 (num)>
					case 0x23: stackBuffer[++sp].LoadDouble( CilboxUtil.IntFloatConverter.ConvertEtoD(BytecodeAs64( ref pc ) ) ); break; // ldc.r8 <float64 (num)>
					// 0x24 does not exist.
					case 0x25: stackBuffer[sp+1] = stackBuffer[sp]; sp++; break; // dup TODO: Does dup potentially duplicate objects somehow?
					case 0x26: sp--; break; // pop

					case 0x27: //jmp
					case 0x28: //call
					case 0x29: //calli
					case 0x73: //newobj
					case 0x6F: //callvirt
					{
						uint bc = (b == 0x29) ? stackBuffer[sp--].u : BytecodeAsU32( ref pc );
						object iko = null; // Returned value.
						CilMetadataTokenInfo dt = box.metadatas[bc];
						bool isVoid = false;
						MethodBase st;

						if( !dt.isValid )
						{
							throw new CilboxInterpreterRuntimeException("Error, function " + dt.Name + " Not found in " + parentClass.className + ":" + fullSignature, parentClass.className, methodName, pc);
						}

						if( !dt.isNative )
						{
							if( dt.shim != null )
							{
								isVoid = dt.shimIsVoid;
								int staticOffset = dt.shimIsStatic?0:1;
								int numParams = dt.shimParameterCount;
								int nextParameterStart = stackContinues;
								int nextStackHead = nextParameterStart + numParams + staticOffset;

								for( int i = numParams - 1; i >= 0; i-- )
									stackBuffer[nextParameterStart+i+staticOffset] = stackBuffer[sp--];
								if( !dt.shimIsStatic )
									stackBuffer[nextParameterStart] = stackBuffer[sp--];

								if( !isVoid )
									stackBuffer[++sp] = dt.shim( dt, stackBufferIn.Slice( nextStackHead ), stackBufferIn.Slice( nextParameterStart, numParams + staticOffset ) );
								else
									dt.shim( dt, stackBufferIn.Slice( nextStackHead ), stackBufferIn.Slice( nextParameterStart, numParams + staticOffset ) );

							}
							else
							{
								// Sentinel.  interpretiveMethod will contain what method to interpret.
								// interpretiveMethodClass
								CilboxClass targetClass = box.classesList[dt.interpretiveMethodClass];
								CilboxMethod targetMethod = targetClass.methods[dt.interpretiveMethod];
								isVoid = targetMethod.isVoid;
								if( targetMethod == null )
									throw new CilboxInterpreterRuntimeException($"Function {dt.Name} not found", parentClass.className, methodName, pc);

								int staticOffset = (targetMethod.isStatic?0:1);
								int numParams = targetMethod.signatureParameters.Length;

								int nextParameterStart = stackContinues;
								int nextStackHead = nextParameterStart + numParams + staticOffset;

								for( int i = numParams - 1; i >= 0; i-- )
									stackBuffer[nextParameterStart+i+staticOffset] = stackBuffer[sp--];

								if( !targetMethod.isStatic )
									stackBuffer[nextParameterStart] = stackBuffer[sp--];

								if( !isVoid )
									stackBuffer[++sp] = targetMethod.InterpretInner( stackBufferIn.Slice( nextStackHead ), stackBufferIn.Slice( nextParameterStart, numParams + staticOffset ) );
								else
									targetMethod.InterpretInner( stackBufferIn.Slice( nextStackHead ), stackBufferIn.Slice( nextParameterStart, numParams + staticOffset ) );

								if( b == 0x27 )
								{
									// This is returning from a jump, so immediately abort.
									if( isVoid ) stackBuffer[++sp] = StackElement.nil; /// ?? Please check me! If wrong, fix above, too.
									cont = false;
								}
							}
						}
						else
						{
							st = dt.nativeMethod;
							if( st is MethodInfo )
								isVoid = ((MethodInfo)st).ReturnType == typeof(void);

							ParameterInfo [] pa = st.GetParameters();
							int numFields = pa.Length;
							object callthis = null;
							object [] callpar = new object[numFields];
							StackElement callthis_se = new StackElement{};
							StackElement [] callpar_se = new StackElement[numFields];
							int ik;
							for( ik = 0; ik < numFields; ik++ )
							{
								StackElement se = stackBuffer[sp--];
								callpar_se[numFields-ik-1] = se;
								object o = se.AsObject(box);
								Type t = pa[numFields-ik-1].ParameterType;

								// XXX TODO: Copy mechanism below from ResolveToStackElement and Coerce
								if( se.type < StackType.Object )
								{
									if( o != null && t.IsValueType && o.GetType() != t )
									{
										//o = Convert.ChangeType( o, t );
										o = se.CoerceToObject( t );
									}
								}
								callpar[numFields-ik-1] = o;
							}
							if( st.IsConstructor )
							{
								// TRICKY: This generally can only be arrived at when scripts run their own constructors.
								if( st.DeclaringType == typeof( MonoBehaviour ) )
									iko = this;
								else // Otherwise it's normal.
									iko = ((ConstructorInfo)st).Invoke( callpar );
							}
							else if( !st.IsStatic )
							{
								MethodInfo mi = (MethodInfo)st;
								StackElement seorig = stackBuffer[sp--];
								StackElement se = StackElement.ResolveToStackElement( seorig );
								Type t = mi.DeclaringType;

								if( seorig.type == StackType.NativeHandle )
								{
									callthis = seorig.DereferenceNativeHandle(box);
								}
								else if( t.IsValueType && se.type < StackType.Object )
								{
									// Try to coerce types.
									callthis = se.CoerceToObject( t );
								}
								else
								{
									callthis = se.o;
								}

								if (callthis == null)
								{
									interpretedThrow(pc - 1, new NullReferenceException());
									break;
								}

								iko = st.Invoke( callthis, callpar );
								if( seorig.type == StackType.Address )
								{
									seorig.DereferenceLoadAddress( callthis );
								}
								else if ( seorig.type == StackType.NativeHandle )
								{
									seorig.DereferenceLoadNativeHandle( box, callthis );
								}
							}
							else
							{
								iko = st.Invoke( null, callpar );
							}

							// Possibly copy back any references.
							for( ik = 0; ik < numFields; ik++ )
							{
								StackElement se = callpar_se[ik];
								if (se.type == StackType.Address)
								{
									callpar_se[ik].DereferenceLoadAddress( callpar[ik] );
								}
								else if ( se.type == StackType.NativeHandle )
								{
									callpar_se[ik].DereferenceLoadNativeHandle( box, callpar[ik] );
								}
							}

							if( !isVoid )
							{
								stackBuffer[++sp].Load( iko );
							}
							if( b == 0x27 )
							{
								// This is returning from a jump, so immediately abort.
								if( isVoid ) stackBuffer[++sp] = StackElement.nil; /// ?? Please check me! If wrong, fix above, too.
								cont = false;
							}
						}

						break;
					}
					case 0x2a: cont = false; break; // ret

					case 0x2b: pc += (sbyte)byteCode[pc] + 1; break; //br.s
					case 0x38: { int ofs = (int)BytecodeAsU32( ref pc ); pc += ofs; break; } // br

					case 0xdd: // leave
					case 0xde: // leave.s
					{
						int currentInstruction = pc;
						sp = -1; // leave(.s) clears the stack.
						int offset = (b == 0xde) ? (sbyte)byteCode[pc++] : (int)BytecodeAsU32( ref pc );
						int leaveTarget = pc + offset;
						leaveRegionEnqueueFinallys(currentInstruction, leaveTarget, false);
						break;
					}

					case 0xdc: // endfault, endfinally
					{
						if (handlerClauseStack == null || handlerClauseStack.Count == 0)
						{
							throw new CilboxInterpreterRuntimeException("endfinally without a matching target.", parentClass.className, methodName, pc);
						}
						jumpToNextHandlerDestination();
						break;
					}

					case 0x2c: case 0x39: // brfalse.s, brnull.s, brzero.s - is it zero, null or  / brfalse
					case 0x2d: case 0x3a: // brinst.s, brtrue.s / btrue
					{
						StackElement s = stackBuffer[sp--];
						int iop = b - 0x2c;
						if( b >= 0x38 ) iop -= 0xd;
						int offset = (b >= 0x38) ? (int)BytecodeAsU32( ref pc ) : (sbyte)byteCode[pc++];
						switch( iop )
						{
							case 0: if( ( s.type == StackType.Object && s.o == null ) || ( s.type != StackType.Object && s.i == 0 ) ) pc += offset; break;
							case 1: if( ( s.type == StackType.Object && s.o != null ) || ( s.type != StackType.Object && s.i != 0 ) ) pc += offset; break;
						}
						break;
					}
					case 0x2e: case 0x3b: // beq.s / beq
					case 0x2f: case 0x3c: // bge.s
					case 0x30: case 0x3d: // bgt.s
					case 0x31: case 0x3e: // ble.s
					case 0x32: case 0x3f: // blt.s
					case 0x33: case 0x40: // bne.un.s
					case 0x34: case 0x41: // bge.un.s
					case 0x35: case 0x42: // bgt.un.s
					case 0x36: case 0x43: // ble.un.s
					case 0x37: case 0x44: // blt.un.s
					{
						StackElement sb = stackBuffer[sp--]; StackElement sa = stackBuffer[sp--];
						int iop = b - 0x2e;
						if( b >= 0x38 ) iop -= 0xd;
						int joffset = (b >= 0x38) ? (int)BytecodeAsU32( ref pc ) : (sbyte)byteCode[pc++];

						StackType promoted = StackElement.StackTypeMaxPromote( sa.type, sb.type );

						switch( promoted )
						{
						case StackType.Sbyte: case StackType.Short: case StackType.Int:
							switch( iop )
							{
							case 0: if( sa.i == sb.i ) pc += joffset; break;
							case 1: if( sa.i >= sb.i ) pc += joffset; break;
							case 2: if( sa.i >  sb.i ) pc += joffset; break;
							case 3: if( sa.i <= sb.i ) pc += joffset; break;
							case 4: if( sa.i <  sb.i ) pc += joffset; break;
							case 5: if( sa.u != sb.u ) pc += joffset; break;
							case 6: if( sa.u >= sb.u ) pc += joffset; break;
							case 7: if( sa.u >  sb.u ) pc += joffset; break;
							case 8: if( sa.u <= sb.u ) pc += joffset; break;
							case 9: if( sa.u <  sb.u ) pc += joffset; break;
							} break;
						case StackType.Byte: case StackType.Ushort: case StackType.Uint:
							switch( iop )	{
							case 0: if( sa.u == sb.u ) pc += joffset; break;
							case 1: if( sa.u >= sb.u ) pc += joffset; break;
							case 2: if( sa.u >  sb.u ) pc += joffset; break;
							case 3: if( sa.u <= sb.u ) pc += joffset; break;
							case 4: if( sa.u <  sb.u ) pc += joffset; break;
							case 5: if( sa.u != sb.u ) pc += joffset; break;
							case 6: if( sa.u >= sb.u ) pc += joffset; break;
							case 7: if( sa.u >  sb.u ) pc += joffset; break;
							case 8: if( sa.u <= sb.u ) pc += joffset; break;
							case 9: if( sa.u <  sb.u ) pc += joffset; break;
							} break;
						case StackType.Ulong:
							switch( iop )	{
							case 0: if( sa.e == sb.e ) pc += joffset; break;
							case 1: if( sa.e >= sb.e ) pc += joffset; break;
							case 2: if( sa.e >  sb.e ) pc += joffset; break;
							case 3: if( sa.e <= sb.e ) pc += joffset; break;
							case 4: if( sa.e <  sb.e ) pc += joffset; break;
							case 5: if( sa.e != sb.e ) pc += joffset; break;
							case 6: if( sa.e >= sb.e ) pc += joffset; break;
							case 7: if( sa.e >  sb.e ) pc += joffset; break;
							case 8: if( sa.e <= sb.e ) pc += joffset; break;
							case 9: if( sa.e <  sb.e ) pc += joffset; break;
							} break;
						case StackType.Long:
							switch( iop )	{
							case 0: if( sa.l == sb.l ) pc += joffset; break;
							case 1: if( sa.l >= sb.l ) pc += joffset; break;
							case 2: if( sa.l >  sb.l ) pc += joffset; break;
							case 3: if( sa.l <= sb.l ) pc += joffset; break;
							case 4: if( sa.l <  sb.l ) pc += joffset; break;
							case 5: if( sa.e != sb.e ) pc += joffset; break;
							case 6: if( sa.e >= sb.e ) pc += joffset; break;
							case 7: if( sa.e >  sb.e ) pc += joffset; break;
							case 8: if( sa.e <= sb.e ) pc += joffset; break;
							case 9: if( sa.e <  sb.e ) pc += joffset; break;
							} break;
						case StackType.Float:
							switch( iop )	{
							case 0: if( sa.f == sb.f ) pc += joffset; break;
							case 1: if( sa.f >= sb.f ) pc += joffset; break;
							case 2: if( sa.f >  sb.f ) pc += joffset; break;
							case 3: if( sa.f <= sb.f ) pc += joffset; break;
							case 4: if( sa.f <  sb.f ) pc += joffset; break;
							case 5: if( sa.f != sb.f ) pc += joffset; break;
							case 6: if( sa.f >= sb.f ) pc += joffset; break;
							case 7: if( sa.f >  sb.f ) pc += joffset; break;
							case 8: if( sa.f <= sb.f ) pc += joffset; break;
							case 9: if( sa.f <  sb.f ) pc += joffset; break;
							} break;
						case StackType.Double:
							switch( iop )	{
							case 0: if( sa.d == sb.d ) pc += joffset; break;
							case 1: if( sa.d >= sb.d ) pc += joffset; break;
							case 2: if( sa.d >  sb.d ) pc += joffset; break;
							case 3: if( sa.d <= sb.d ) pc += joffset; break;
							case 4: if( sa.d <  sb.d ) pc += joffset; break;
							case 5: if( sa.d != sb.d ) pc += joffset; break;
							case 6: if( sa.d >= sb.d ) pc += joffset; break;
							case 7: if( sa.d >  sb.d ) pc += joffset; break;
							case 8: if( sa.d <= sb.d ) pc += joffset; break;
							case 9: if( sa.d <  sb.d ) pc += joffset; break;
							} break;
						case StackType.Object:
							switch(iop)
							{
							case 0: if( sa.o == sb.o ) pc += joffset; break;
							case 5: if( sa.o != sb.o ) pc += joffset; break;
							default: throw new CilboxInterpreterRuntimeException("Invalid object comparison", parentClass.className, methodName, pc);
							} break;
						default:
							throw new CilboxInterpreterRuntimeException("Invalid comparison", parentClass.className, methodName, pc);
						}
						break;
					}
					case 0x45: // Switch
					{
						int nsw = (int)BytecodeAsU32( ref pc );
						int startpc = pc;
						pc += nsw * 4;
						StackElement s = stackBuffer[sp--];
						if( s.type > StackType.Ulong )
							throw new CilboxInterpreterRuntimeException("Stack type invalid for switch statement", parentClass.className, methodName, pc);

						if( s.u < nsw )
						{
							int smatch = (int)(s.u * 4 + startpc);
							int ofs = (int)BytecodeAsU32( ref smatch );
							pc += ofs;
						}
						// Otherwise fall through
						break;
					}

					case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D:
					case 0x5E: case 0x5F: case 0x60: case 0x61: case 0x62: case 0x63:
					case 0x64:
					{
						StackElement sb = stackBuffer[sp--];
						StackElement sa = stackBuffer[sp];
						StackType promoted = StackElement.StackTypeMaxPromote( sa.type, sb.type );

						switch( b-0x58 )
						{
							case 0: // Add
								switch( promoted )
								{
									case StackType.Int:		stackBuffer[sp].LoadInt( sa.i + sb.i ); break;
									case StackType.Uint:	stackBuffer[sp].LoadUint( sa.u + sb.u ); break;
									case StackType.Long:	stackBuffer[sp].LoadLong( sa.l + sb.l ); break;
									case StackType.Ulong:	stackBuffer[sp].LoadUlong( sa.e + sb.e ); break;
									case StackType.Float:	stackBuffer[sp].LoadFloat( sa.f + sb.f ); break;
									case StackType.Double:	stackBuffer[sp].LoadDouble( sa.d + sb.d ); break;
								} break;
							case 1: // Sub
								switch( promoted )
								{
									case StackType.Int:		stackBuffer[sp].LoadInt( sa.i - sb.i ); break;
									case StackType.Uint:	stackBuffer[sp].LoadUint( sa.u - sb.u ); break;
									case StackType.Long:	stackBuffer[sp].LoadLong( sa.l - sb.l ); break;
									case StackType.Ulong:	stackBuffer[sp].LoadUlong( sa.e - sb.e ); break;
									case StackType.Float:	stackBuffer[sp].LoadFloat( sa.f - sb.f ); break;
									case StackType.Double:	stackBuffer[sp].LoadDouble( sa.d - sb.d ); break;
								} break;
							case 2: // Mul
								switch( promoted )
								{
									case StackType.Int:		stackBuffer[sp].LoadInt( sa.i * sb.i ); break;
									case StackType.Uint:	stackBuffer[sp].LoadUint( sa.u * sb.u ); break;
									case StackType.Long:	stackBuffer[sp].LoadLong( sa.l * sb.l ); break;
									case StackType.Ulong:	stackBuffer[sp].LoadUlong( sa.e * sb.e ); break;
									case StackType.Float:	stackBuffer[sp].LoadFloat( sa.f * sb.f ); break;
									case StackType.Double:	stackBuffer[sp].LoadDouble( sa.d * sb.d ); break;
								} break;
							case 3: // Div
							{
								switch (promoted)
								{
									case StackType.Int:
										if (sb.i == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadInt(sa.i / sb.i);
										break;
									case StackType.Uint:
										if (sb.u == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUint(sa.u / sb.u);
										break;
									case StackType.Long:
										if (sb.l == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadLong(sa.l / sb.l);
										break;
									case StackType.Ulong:
										if (sb.e == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUlong(sa.e / sb.e);
										break;
									case StackType.Float:
										// Floating point division returns Infinity/NaN, does not throw.
										stackBuffer[sp].LoadFloat(sa.f / sb.f);
										break;
									case StackType.Double:
										stackBuffer[sp].LoadDouble(sa.d / sb.d);
										break;
									default: throw new CilboxInterpreterRuntimeException($"Unexpected div instruction behavior {promoted}", parentClass.className, methodName, pc);
								}
								break;
							}
							case 4: // Div.un
								switch( promoted )
								{
									case StackType.Int:
										if (sb.u == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUint( sa.u / sb.u );
										break;
									case StackType.Uint:
										if (sb.u == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUint( sa.u / sb.u );
										break;
									case StackType.Long:
										if (sb.e == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUlong( sa.e / sb.e );
										break;
									case StackType.Ulong:
										if (sb.e == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUlong( sa.e / sb.e );
										break;
									default: throw new CilboxInterpreterRuntimeException($"Unexpected div.un instruction behavior {promoted}", parentClass.className, methodName, pc);
								} break;
							case 5: // rem
								switch( promoted )
								{
									case StackType.Int:
										if (sb.i == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadInt(sa.i % sb.i);
										break;
									case StackType.Uint:
										if (sb.u == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUint(sa.u % sb.u);
										break;
									case StackType.Long:
										if (sb.l == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadLong(sa.l % sb.l);
										break;
									case StackType.Ulong:
										if (sb.e == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUlong(sa.e % sb.e);
										break;
									default: throw new CilboxInterpreterRuntimeException($"Unexpected rem instruction behavior {promoted}", parentClass.className, methodName, pc);
								} break;
							case 6: // rem.un
								switch( promoted )
								{
									case StackType.Int:
										if (sb.u == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUint(sa.u % sb.u);
										break;
									case StackType.Uint:
										if (sb.u == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUint(sa.u % sb.u);
										break;
									case StackType.Long:
										if (sb.e == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUlong(sa.e % sb.e);
										break;
									case StackType.Ulong:
										if (sb.e == 0) { interpretedThrow(pc - 1, new DivideByZeroException()); break; }
										stackBuffer[sp].LoadUlong(sa.e % sb.e);
										break;
									default: throw new CilboxInterpreterRuntimeException($"Unexpected rem.un instruction behavior {promoted}", parentClass.className, methodName, pc);
								} break;
							case 7: stackBuffer[sp].LoadUlongType( sa.e & sb.e, promoted ); break; // and
							case 8: stackBuffer[sp].LoadUlongType( sa.e | sb.e, promoted ); break; // or
							case 9: stackBuffer[sp].LoadUlongType( sa.e ^ sb.e, promoted ); break; // xor
							case 10: stackBuffer[sp].LoadUlongType( sa.e << sb.i, promoted ); break; // shl
							case 11: // shr
								switch( sa.type )
								{
								case StackType.Sbyte: // TODO: Is this right? Do we consider all unsigned types signed?
								case StackType.Byte:
								case StackType.Short:
								case StackType.Ushort:
								case StackType.Int:
								case StackType.Uint: stackBuffer[sp].LoadLongType( sa.i >> sb.i, promoted ); break;
								case StackType.Long:
								case StackType.Ulong: stackBuffer[sp].LoadLongType( sa.l >> sb.i, promoted ); break;
								}
								break;
							case 12: stackBuffer[sp].LoadUlongType( sa.e >> sb.i, promoted ); break; // shr.un
						}
						break;
					}

					case 0x65: stackBuffer[sp].l = -stackBuffer[sp].l; break;
					case 0x66: stackBuffer[sp].e ^= 0xffffffffffffffff; break;

					// XXX TODO: Perf improvement, detect float-to-int conversions and fast-path them.
					// C# Does not want you to blindly interpret these.
					case 0x67: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadSByte( ((se.type < StackType.Float) ? (sbyte)se.u  : (sbyte)se.CoerceToObject(typeof(sbyte)))  ); break; } // conv.i1
					case 0x68: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadShort( ((se.type < StackType.Float) ? (short)se.i  : (short)se.CoerceToObject(typeof(short)))  ); break; } // conv.i2
					case 0x69: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadInt(   ((se.type < StackType.Float) ? (int)se.i    : (int)se.CoerceToObject(typeof(int)))      ); break; } // conv.i4
					case 0x6A: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadLong(  ( se.type <= StackType.Int ? (long)se.i   : se.type == StackType.Uint ? (long) se.u   : se.type == StackType.Long ? (long)se.l   : se.type == StackType.Ulong ? (long)se.e   : (long)se.CoerceToObject(typeof(long)))    ); break; } // conv.i8
					case 0x6B: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadFloat( ( se.type <= StackType.Int ? (float)se.i  : se.type == StackType.Uint ? (float) se.u  : se.type == StackType.Long ? (float)se.l  : se.type == StackType.Ulong ? (float)se.e  : se.type == StackType.Double ? (float)se.d : (float)se.CoerceToObject(typeof(float)))  ); break; } // conv.r4
					case 0x6C: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadDouble(( se.type <= StackType.Int ? (double)se.i : se.type == StackType.Uint ? (double) se.u : se.type == StackType.Long ? (double)se.l : se.type == StackType.Ulong ? (double)se.e : se.type == StackType.Float ? (double)se.f : (double)se.CoerceToObject(typeof(double)))); break; } // conv.r8
					case 0x6D: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadUint(  ((se.type < StackType.Float) ?(uint)se.u    : (uint)se.CoerceToObject(typeof(uint)))      ); break; } // conv.u4
					case 0x6E: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadUlong( ( se.type <= StackType.Int ? (ulong)se.i   : se.type == StackType.Uint ? (ulong)se.u  : se.type == StackType.Long ? (ulong)se.l  : se.type == StackType.Ulong ? (ulong)se.e  : (ulong)se.CoerceToObject(typeof(ulong)))); break; } // conv.u8
					case 0xD1: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadUshort(((se.type < StackType.Float) ? (ushort)se.u : (ushort)se.CoerceToObject(typeof(ushort)))); break; } // conv.u2
					case 0xD2: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadByte(  ((se.type < StackType.Float) ? (byte)se.u   : (byte)se.CoerceToObject(typeof(byte)))    ); break; } // conv.u1

					case 0x72:
					{
						uint bc = BytecodeAsU32( ref pc );
						stackBuffer[++sp].Load( box.metadatas[bc].Name );
						break; //ldstr
					}
					case 0x74: //castclass
					case 0x75: //isinst
					{
						uint bc = BytecodeAsU32( ref pc );
						StackElement se = stackBuffer[sp--];
						CilMetadataTokenInfo ti = box.metadatas[bc];
						object oRet = null;
						if( ti.nativeTypeIsCilboxProxy )
						{
							if( se.o is CilboxProxy )
							{
								// Both are proxies. Check name.
								if( ((CilboxProxy)(se.o)).className == ti.Name )
									oRet = se.o;
							}
						}
						else if( ti.nativeTypeIsStackType )
						{
							if( ti.nativeTypeStackType == StackElement.TypeToStackType[ti.Name] )
								stackBuffer[++sp] = se;
						}
						else if( se.o.GetType() == ti.nativeType )
							stackBuffer[++sp].LoadObject( se.o );

						stackBuffer[++sp].LoadObject( oRet );

						if( b == 0x74 && oRet == null )
						{
							throw new CilboxInterpreterRuntimeException($"Error: casting class invalid to {ti.Name}", parentClass.className, methodName, pc);
						}
						break;
					}

					case 0x7a: //throw
					{
						object throwable = stackBuffer[sp--].AsObject();
						// todo: check if cilbox has access to the type?
						interpretedThrow(pc - 1, throwable);
						break;
					}
					case 0x7b: // ldfld
					{
						// Tricky:  Do not allow host-fields without great care. For instance, getting access to PlatformActual.DelegateRepackage would all the program out.
						uint bc = BytecodeAsU32( ref pc );

						object opths = stackBuffer[sp--].AsObject(box);
						if (opths == null) {
							interpretedThrow(pc - 1, new NullReferenceException());
							break;
						}

						if( opths is CilboxProxy proxy )
						{
							stackBuffer[++sp] = proxy.fields[box.metadatas[bc].fieldIndex];
							break;
						}

						CilMetadataTokenInfo ldfldMeta = box.metadatas[bc];
						if(!ldfldMeta.isFieldWhiteListed)
						{
							throw new CilboxInterpreterRuntimeException($"Can not access non-whitelisted field {ldfldMeta.Name} on type {ldfldMeta.nativeType.FullName}", parentClass.className, methodName, pc);
						}

						if (ldfldMeta.nativeField == null)
						{
							interpretedThrow(pc - 1, new MissingFieldException($"Field {ldfldMeta.Name} on type {ldfldMeta.nativeType.FullName} does not exist or is not accessible."));
							break;
						}

						object val = ldfldMeta.nativeField.GetValue( opths );
						stackBuffer[++sp].Load( val );
						break;
					}
					case 0x7c: // ldflda
					{
						uint bc = BytecodeAsU32( ref pc );
						object opths = stackBuffer[sp--].AsObject(box);
						if (opths == null) {
							interpretedThrow(pc - 1, new NullReferenceException());
							break;
						}

						if( opths is CilboxProxy proxy )
						{
							stackBuffer[++sp] = StackElement.CreateAddressReference((Array)(proxy.fields), (uint)box.metadatas[bc].fieldIndex);
							break;
						}

						stackBuffer[++sp] = StackElement.CreateNativeHandleReference( opths, bc );
						break;
					}
					case 0x7d: // stfld
					{
						uint bc = BytecodeAsU32( ref pc );
						StackElement se = stackBuffer[sp--];
						object opths = stackBuffer[sp--].AsObject(box);
						if (opths == null)
						{
							interpretedThrow(pc - 1, new NullReferenceException());
							break;
						}

						if( opths is CilboxProxy proxy )
						{
							proxy.fields[box.metadatas[bc].fieldIndex] = se;
							//Debug.Log( "Type: " + ((CilboxProxy)opths).fields[box.metadatas[bc].fieldIndex].type );
							break;
						}

						CilMetadataTokenInfo ldfldMeta = box.metadatas[bc];

						if (!ldfldMeta.isFieldWhiteListed)
						{
							throw new CilboxInterpreterRuntimeException($"Can not access non-whitelisted field {ldfldMeta.Name} on type {ldfldMeta?.nativeType?.FullName}", parentClass.className, methodName, pc);
						}

						if (ldfldMeta.nativeField == null)
						{
							interpretedThrow(pc - 1, new MissingFieldException($"Field {ldfldMeta.Name} on type {ldfldMeta.nativeType.FullName} does not exist or is not accessible."));
							break;
						}

						ldfldMeta.nativeField.SetValue( opths, se.CoerceToObject( ldfldMeta.nativeType ) );
						break;
					}
					case 0x46: case 0x47: case 0x48: case 0x49: case 0x4a: // ldind
					case 0x4b: case 0x4c: case 0x4d: case 0x4e: case 0x4f: case 0x50:
					{
						StackElement se = stackBuffer[sp--];
						object obj = null;
						if (se.type == StackType.Address)
						{
							obj = se.DereferenceAddress();
						}
						else if (se.type == StackType.NativeHandle)
						{
							obj = se.DereferenceNativeHandle(box);
						}
						else
						{
							throw new CilboxInterpreterRuntimeException("Invalid stack type for ldind instruction", parentClass.className, methodName, pc);
						}

						if (obj == null)
						{
							interpretedThrow(pc - 1, new NullReferenceException());
							break;
						}

						switch (b - 0x46)
						{
							case 0: // ldind.i1
							{
								stackBuffer[++sp].LoadSByte( Convert.ToSByte(obj) );
								break;
							}
							case 1: // ldind.u1
							{
								stackBuffer[++sp].LoadByte( Convert.ToByte(obj) );
								break;
							}
							case 2: // ldind.i2
							{
								stackBuffer[++sp].LoadShort( Convert.ToInt16(obj) );
								break;
							}
							case 3: // ldind.u2
							{
								stackBuffer[++sp].LoadUshort( Convert.ToUInt16(obj) );
								break;
							}
							case 4: // ldind.i4
							{
								stackBuffer[++sp].LoadInt( Convert.ToInt32(obj) );
								break;
							}
							case 5: // ldind.u4
							{
								stackBuffer[++sp].LoadUint( Convert.ToUInt32(obj) );
								break;
							}
							case 6: // ldind.i8 / ldind.u8
							{
								stackBuffer[++sp].LoadLong( Convert.ToInt64(obj) );
								break;
							}
							case 7: // ldind.i
							{
								stackBuffer[++sp].LoadLong( Convert.ToInt64(obj) );
								break;
							}
							case 8: // ldind.r4
							{
								stackBuffer[++sp].LoadFloat( Convert.ToSingle(obj) );
								break;
							}
							case 9: // ldind.r8
							{
								stackBuffer[++sp].LoadDouble( Convert.ToDouble(obj) );
								break;
							}
							case 10: // ldind.ref
							{
								stackBuffer[++sp].LoadObject(obj);
								break;
							}
						}
						break;
					}
					case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: // stind
					case 0x56: case 0x57:
					{
						StackElement val = stackBuffer[sp--];
						StackElement addr = stackBuffer[sp--];
						object obj = val.AsObject();
						if (addr.type == StackType.Address)
						{
							addr.DereferenceLoadAddress(obj);
						}
						else if (addr.type == StackType.NativeHandle)
						{
							addr.DereferenceLoadNativeHandle(box, obj);
						}
						else
						{
							throw new CilboxInterpreterRuntimeException("Invalid stack type for stind instruction", parentClass.className, methodName, pc);
						}
						break;
					}
					case 0x7e: // ldsfld
					{
						uint bc = BytecodeAsU32( ref pc );
						stackBuffer[++sp].Load( parentClass.staticFields[box.metadatas[bc].fieldIndex] );
						break;
					}
					case 0x7f: // ldsflda
					{
						uint bc = BytecodeAsU32( ref pc );
						stackBuffer[++sp] = StackElement.CreateAddressReference( (Array)(parentClass.staticFields), (uint)box.metadatas[bc].fieldIndex );
						break;
					}
					case 0x80: // stsfld
					{
						uint bc = BytecodeAsU32( ref pc );
						object obj = stackBuffer[sp--].AsObject();
						parentClass.staticFields[box.metadatas[bc].fieldIndex] = obj;
						break;
					}
					case 0x8C: // box (This pulls off a type)
					{
						uint otyp = BytecodeAsU32( ref pc );
						stackBuffer[sp].LoadObject( stackBuffer[sp].AsObject() );//(metaType.nativeType)stackBuffer[sp-1].AsObject();
						break;
					}
					case 0x8d: // newarr <etype>
					{
						uint otyp = BytecodeAsU32( ref pc );
						if( stackBuffer[sp].type > StackType.Ulong )
							throw new CilboxInterpreterRuntimeException("Invalid type, processing new array", parentClass.className, methodName, pc);
						int size = stackBuffer[sp].i;
						CilMetadataTokenInfo arrMeta = box.metadatas[otyp];
						if( arrMeta.nativeTypeIsCilboxProxy )
							stackBuffer[sp].LoadObject( new object[size] );
						else
							stackBuffer[sp].LoadObject( Array.CreateInstance( arrMeta.nativeType, size ) );
						break;
					}
					case 0x8e: // ldlen
					{
						stackBuffer[sp].LoadInt( ((Array)(stackBuffer[sp].o)).Length );
						break;
					}
					case 0x8f: // ldelema
					{
						/*uint whichClass = */BytecodeAsU32( ref pc ); // (For now, ignored)
						int index = stackBuffer[sp--].i;
						Array a = (Array)(stackBuffer[sp--].AsObject());
						if (index < 0 || index >= a.Length)
						{
							interpretedThrow(pc - 1, new IndexOutOfRangeException());
							break;
						}
						stackBuffer[++sp] = StackElement.CreateAddressReference( a, (uint)index );
						break;
					}
					case 0x90: case 0x91: case 0x92: case 0x93: case 0x94: // ldelem
					case 0x95: case 0x96: case 0x97: case 0x98: case 0x99:
					{
						if( stackBuffer[sp].type > StackType.Uint ) throw new CilboxInterpreterRuntimeException("Invalid index type" + stackBuffer[sp].type + " " + stackBuffer[sp].o, parentClass.className, methodName, pc);
						int index = stackBuffer[sp--].i;
						if (index < 0 || index >= ((Array)(stackBuffer[sp].o)).Length)
						{
							interpretedThrow(pc - 1, new IndexOutOfRangeException());
							break;
						}
//						Array a = ((Array)(stackBuffer[sp].o));
						switch( b - 0x90 )
						{
						// Does this way work universally?  Can we assume the compiler knows what it's doing?
						// Previously it looked more like a.GetValue( index ).
						case 0: stackBuffer[sp].LoadSByte( (sbyte)(((sbyte[])stackBuffer[sp].o)[index]) ); break; // ldelem.i1
						case 1: stackBuffer[sp].LoadByte( (byte)(((byte[])stackBuffer[sp].o)[index]) ); break; // ldelem.u1
						case 2: stackBuffer[sp].LoadShort( (short)(((short[])stackBuffer[sp].o)[index]) ); break; // ldelem.i2
						case 3: stackBuffer[sp].LoadUshort( (ushort)(((ushort[])stackBuffer[sp].o)[index]) ); break; // ldelem.u2
						case 4: stackBuffer[sp].LoadInt( (int)(((int[])stackBuffer[sp].o)[index]) ); break; // ldelem.i4
						case 5: stackBuffer[sp].LoadUint( (uint)(((uint[])stackBuffer[sp].o)[index]) ); break; // ldelem.u4
						case 6: stackBuffer[sp].LoadUlong( (ulong)(((ulong[])stackBuffer[sp].o)[index]) ); break; // ldelem.u8 / ldelem.i8
						case 7: stackBuffer[sp].LoadInt( (int)(((int[])stackBuffer[sp].o)[index]) ); break; // ldelem.i
						case 8: stackBuffer[sp].LoadFloat( (float)(((float[])stackBuffer[sp].o)[index]) ); break; // ldelem.r4
						case 9: stackBuffer[sp].LoadDouble( (double)(((double[])stackBuffer[sp].o)[index]) ); break; // ldelem.r8

						}
						break;
					}
					case 0x9a: // Ldelem_Ref
					{
						if( stackBuffer[sp].type > StackType.Uint ) throw new CilboxInterpreterRuntimeException("Invalid index type" + stackBuffer[sp].type + " " + stackBuffer[sp].o, parentClass.className, methodName, pc);
						int index = stackBuffer[sp--].i;
						Array a = ((Array)(stackBuffer[sp--].o));
						if (index < 0 || index >= a.Length)
						{
							interpretedThrow(pc - 1, new IndexOutOfRangeException());
							break;
						}
						stackBuffer[++sp].LoadObject( a.GetValue(index) );
						break;
					}
					case 0x9c: // stelem.i1
					{
						SByte val = (SByte)stackBuffer[sp--].i;
						if( stackBuffer[sp].type > StackType.Uint ) throw new CilboxInterpreterRuntimeException("Invalid index type" + stackBuffer[sp].type + " " + stackBuffer[sp].o, parentClass.className, methodName, pc);
						int index = stackBuffer[sp--].i;
						((Array)(stackBuffer[sp--].o)).SetValue( (byte)val, index );
						break;
					}
					case 0xa0: // stelem.r4
					{
						float val;
						val = stackBuffer[sp--].f;
						if( stackBuffer[sp].type > StackType.Uint ) throw new CilboxInterpreterRuntimeException("Invalid index type" + stackBuffer[sp].type + " " + stackBuffer[sp].o, parentClass.className, methodName, pc);
						int index = stackBuffer[sp--].i;
						float [] array = (float[])stackBuffer[sp--].AsObject();
						array[index] = val;
						break;
					}
					case 0xa2: // stelem.ref
					{
						object val = stackBuffer[sp--].AsObject();
						if( stackBuffer[sp].type > StackType.Uint ) throw new CilboxInterpreterRuntimeException("Invalid index type", parentClass.className, methodName, pc);
						int index = stackBuffer[sp--].i;
						object [] array = (object[])stackBuffer[sp--].AsObject();
						array[index] = val;
						break;
					}
					case 0xa4: // stelem
					{
						uint otyp = BytecodeAsU32( ref pc );
						object val = stackBuffer[sp--].AsObject();
						if( stackBuffer[sp].type > StackType.Uint ) throw new CilboxInterpreterRuntimeException("Invalid index type", parentClass.className, methodName, pc);
						int index = stackBuffer[sp--].i;
						object [] array = (object[])stackBuffer[sp--].AsObject();
						Type t = box.metadatas[otyp].nativeType;
						array[index] = Convert.ChangeType( val, t );  // This shouldn't be type changing.s
						break;
					}
					case 0xA5: // unbox.any
					{
						uint otyp = BytecodeAsU32( ref pc ); // Let's hope that somehow this isn't needed?
						CilMetadataTokenInfo metaType = box.metadatas[otyp];
						if( metaType.nativeTypeIsStackType )
						{
							stackBuffer[sp].Unbox( stackBuffer[sp].AsObject(), metaType.nativeTypeStackType );
						}
						else
						{
							throw new CilboxInterpreterRuntimeException($"Scary Unbox (that we don't have code for) from {otyp} ORIG {metaType.ToString()}", parentClass.className, methodName, pc);
						}
						break;
					}
					case 0xD0: // ldtoken <token>
					{
						uint md = BytecodeAsU32( ref pc ); // Let's hope that somehow this isn't needed?
						CilMetadataTokenInfo mi = box.metadatas[md];
						object loadedObject = null;
						switch( mi.type )
						{
						case MetaTokenType.mtField: // Get type of field.
							loadedObject = mi.fieldIsStatic ?
								parentClass.staticFieldTypes[mi.fieldIndex] :
								parentClass.instanceFieldTypes[mi.fieldIndex];
							break;
						case MetaTokenType.mtArrayInitializer: // Get type of field.
							loadedObject = mi.arrayInitializerData;
							break;
						default: throw new CilboxInterpreterRuntimeException("Error: opcode 0xD0 called on token ID " + md.ToString( "X8" ) + " Which is not currently handled.", parentClass.className, methodName, pc);
						}

						stackBuffer[++sp].LoadObject( loadedObject );

						break;
					}

					case 0xfe: // Extended opcodes
						b = byteCode[pc++];
						switch( b )
						{
						case 0x01:
						case 0x02:
						case 0x03:
						case 0x04:
						case 0x05:
						{
							StackElement sb = stackBuffer[sp--];
							StackElement sa = stackBuffer[sp];
							StackType promoted = StackElement.StackTypeMaxPromote( sa.type, sb.type );
							switch( b )
							{
							case 0x01: // CEQ
								switch( promoted )
								{
									case StackType.Boolean: stackBuffer[sp].LoadInt( sa.i == sb.i ? 1 : 0 ); break;
									case StackType.Int:		stackBuffer[sp].LoadInt( sa.i == sb.i ? 1 : 0 ); break;
									case StackType.Uint:	stackBuffer[sp].LoadInt( sa.i == sb.i ? 1 : 0 ); break;
									case StackType.Long:	stackBuffer[sp].LoadInt( sa.l == sb.l ? 1 : 0 ); break;
									case StackType.Ulong:	stackBuffer[sp].LoadInt( sa.l == sb.l ? 1 : 0 ); break;
									case StackType.Float:	stackBuffer[sp].LoadInt( sa.f == sb.f ? 1 : 0 ); break;
									case StackType.Double:	stackBuffer[sp].LoadInt( sa.d == sb.d ? 1 : 0 ); break;
									case StackType.Object:
										if( sa.type == StackType.Object && sb.type == StackType.Object )
											stackBuffer[sp].LoadInt( sa.o == sb.o ? 1 : 0 );
										else
											throw new CilboxInterpreterRuntimeException($"CEQ Unimplemented type promotion unequal {sa.type} != {sb.type}", parentClass.className, methodName, pc);
										break;
									default: throw new CilboxInterpreterRuntimeException($"CEQ Unimplemented type promotion ({promoted})", parentClass.className, methodName, pc);
								} break;
							case 0x02: // CGT
								switch( promoted )
								{
									case StackType.Int:		stackBuffer[sp].LoadInt( sa.i > sb.i ? 1 : 0 ); break;
									case StackType.Uint:	stackBuffer[sp].LoadInt( sa.i > sb.i ? 1 : 0 ); break;
									case StackType.Long:	stackBuffer[sp].LoadInt( sa.l > sb.l ? 1 : 0 ); break;
									case StackType.Ulong:	stackBuffer[sp].LoadInt( sa.l > sb.l ? 1 : 0 ); break;
									case StackType.Float:	stackBuffer[sp].LoadInt( sa.f > sb.f ? 1 : 0 ); break;
									case StackType.Double:	stackBuffer[sp].LoadInt( sa.d > sb.d ? 1 : 0 ); break;
									default: throw new CilboxInterpreterRuntimeException($"CEQ Unimplemented type promotion ({promoted})", parentClass.className, methodName, pc);
								} break;
							case 0x03: // CGT.UN
								switch( promoted )
								{
									case StackType.Int:		stackBuffer[sp].LoadInt( sa.u > sb.u ? 1 : 0 ); break;
									case StackType.Uint:	stackBuffer[sp].LoadInt( sa.u > sb.u ? 1 : 0 ); break;
									case StackType.Long:	stackBuffer[sp].LoadInt( sa.e > sb.e ? 1 : 0 ); break;
									case StackType.Ulong:	stackBuffer[sp].LoadInt( sa.e > sb.e ? 1 : 0 ); break;
									case StackType.Float:	stackBuffer[sp].LoadInt( sa.f > sb.f ? 1 : 0 ); break;
									case StackType.Double:	stackBuffer[sp].LoadInt( sa.d > sb.d ? 1 : 0 ); break;
									default: throw new CilboxInterpreterRuntimeException($"CEQ Unimplemented type promotion ({promoted})", parentClass.className, methodName, pc);
								} break;
							case 0x04: // CLT
								switch( promoted )
								{
									case StackType.Int:		stackBuffer[sp].LoadInt( sa.i < sb.i ? 1 : 0 ); break;
									case StackType.Uint:	stackBuffer[sp].LoadInt( sa.i < sb.i ? 1 : 0 ); break;
									case StackType.Long:	stackBuffer[sp].LoadInt( sa.l < sb.l ? 1 : 0 ); break;
									case StackType.Ulong:	stackBuffer[sp].LoadInt( sa.l < sb.l ? 1 : 0 ); break;
									case StackType.Float:	stackBuffer[sp].LoadInt( sa.f < sb.f ? 1 : 0 ); break;
									case StackType.Double:	stackBuffer[sp].LoadInt( sa.d < sb.d ? 1 : 0 ); break;
									default: throw new CilboxInterpreterRuntimeException($"CEQ Unimplemented type promotion ({promoted})", parentClass.className, methodName, pc);
								} break;
							case 0x05: // CLT.UN
								switch( promoted )
								{
									case StackType.Int:		stackBuffer[sp].LoadInt( sa.u < sb.u ? 1 : 0 ); break;
									case StackType.Uint:	stackBuffer[sp].LoadInt( sa.u < sb.u ? 1 : 0 ); break;
									case StackType.Long:	stackBuffer[sp].LoadInt( sa.e < sb.e ? 1 : 0 ); break;
									case StackType.Ulong:	stackBuffer[sp].LoadInt( sa.e < sb.e ? 1 : 0 ); break;
									case StackType.Float:	stackBuffer[sp].LoadInt( sa.f < sb.f ? 1 : 0 ); break;
									case StackType.Double:	stackBuffer[sp].LoadInt( sa.d < sb.d ? 1 : 0 ); break;
									default: throw new CilboxInterpreterRuntimeException($"CEQ Unimplemented type promotion ({promoted})", parentClass.className, methodName, pc);
								} break;
							}
							break;
						}
						case 0x06: // ldftn <method>
							uint bc = BytecodeAsU32( ref pc );
							CilMetadataTokenInfo dt = box.metadatas[bc];
							// Right now, we don't have any way of generating references to functions outside this cilbox.
							if( dt.isNative )
								throw new CilboxInterpreterRuntimeException($"Cannot create references to functions outside this cilbox ({dt.Name})", parentClass.className, methodName, pc);
							stackBuffer[++sp].LoadObject( box.classesList[dt.interpretiveMethodClass].methods[dt.interpretiveMethod] );
							break;
						case 0x16: // constrained.
							// handled by reflection so discard the type token
							BytecodeAsU32( ref pc );
							break;
						default:
							throw new CilboxInterpreterRuntimeException($"Opcode 0xfe 0x{b.ToString("X2")} unimplemented", parentClass.className, methodName, pc);
						}
						break;

					default: throw new CilboxInterpreterRuntimeException($"Opcode 0x{b.ToString("X2")} unimplemented", parentClass.className, methodName, pc);
					}
#if PER_INSTRUCTION_PROFILING
spiperf.End();
#endif
				}
				while( cont );
			}
			catch( Exception e )
			{
				string fullError = $"Breakwarn: {e.ToString()} Class: {parentClass.className}, Function: {methodName}, Bytecode: {pc}";
				Debug.LogError( fullError );
				box.disabledReason = fullError;
				box.disabled = true;
				//box.InterpreterExit();

				if (e is CilboxInterpreterRuntimeException)
				{
					throw;
				}

				throw new CilboxInterpreterRuntimeException($"Breakwarn: Unhandled exception", e, parentClass.className, methodName, pc);
			}
#if UNITY_EDITOR
			perfMarkerInterpret.End();
#endif

			//box.InterpreterExit();

			return ( sp == -1 ) ? StackElement.nil : stackBuffer[sp--];

			void interpretedThrow(int currentInstruction, object thrownObj)
			{
				sp = -1;
				exceptionRegister = new StackElement() { type = StackType.Object, o = thrownObj };
				if (!hasExceptionClauses)
				{
					// todo: figure out how to re-throw to outer interpreter.
					throw new CilboxInterpreterRuntimeException("Exception thrown with no handlers: " + thrownObj.ToString(), parentClass.className, methodName, currentInstruction);
				}

				CilboxExceptionHandlingClause found = null;
				for (int i = exceptionClauses.Length - 1; i >= 0; i--)
				{
					CilboxExceptionHandlingClause c = exceptionClauses[i];

					// Check we are in bounds of the Try block.
					if (currentInstruction < c.TryOffset || currentInstruction >= c.TryEndOffset)
					{
						continue;
					}

					// Only Clause and Filter handlers can catch exceptions.
					if (c.Flags != ExceptionHandlingClauseOptions.Clause && c.Flags != ExceptionHandlingClauseOptions.Filter)
					{
						continue;
					}

					// todo: implement filter handling.
					if (c.Flags == ExceptionHandlingClauseOptions.Filter)
					{
						continue;
					}

					// Check exception type matches.
					Type catchType = c.CatchType;
					if (catchType != null)
					{
						if (!catchType.IsInstanceOfType(thrownObj))
						{
							continue;
						}
					}
					else if (c.CatchTypeName != null)
					{
						// Cilboxable type match
						// todo: it isn't actually possible to throw a Cilboxable type (yet?)
						if (!(thrownObj is CilboxProxy && ((CilboxProxy)thrownObj).className == c.CatchTypeName))
						{
							continue;
						}
					}
					else
					{
						continue;
					}

					found = c;
					break;
				}

				if (found == null)
				{
					// how do I handle this?
					throw new CilboxInterpreterRuntimeException("No handlers matched exception: " + thrownObj.ToString(), parentClass.className, methodName, currentInstruction);
				}

				leaveRegionEnqueueFinallys(currentInstruction, found.HandlerOffset, true);
			}

			void leaveRegionEnqueueFinallys(int currentInstruction, int leaveTarget, bool allowFault = false)
			{
				// early out if no exception clauses.
				if (!hasExceptionClauses)
				{
					pc = leaveTarget;
					return;
				}

				if (handlerClauseStack == null)
				{
					handlerClauseStack = new Stack<int>();
				}

				handlerClauseStack.Push(leaveTarget);
				for( int i = 0; i < exceptionClauses.Length; i++ )
				{
					CilboxExceptionHandlingClause c = exceptionClauses[i];

					// only handling Finally clauses here.
					if (
						(c.Flags != ExceptionHandlingClauseOptions.Finally) &&
						(!(allowFault && c.Flags == ExceptionHandlingClauseOptions.Fault))
						)
					{
						continue;
					}

					// Check we are in bounds of the Try block.
					if (currentInstruction < c.TryOffset || currentInstruction >= c.TryEndOffset)
					{
						continue;
					}

					// Verify leaveTarget is outside the try block.
					if (leaveTarget >= c.TryOffset && leaveTarget < c.TryEndOffset)
					{
						continue;
					}

					handlerClauseStack.Push(c.HandlerOffset);
				}

				// Continue to the leave target or innermost handler.
				jumpToNextHandlerDestination();
			}

			void jumpToNextHandlerDestination()
			{
				if (handlerClauseStack == null || handlerClauseStack.Count == 0)
				{
					throw new CilboxInterpreterRuntimeException("No more handler clauses to jump to.", parentClass.className, methodName, pc);
				}

				pc = handlerClauseStack.Pop();
				if (handlerOffsetToClauseMap.TryGetValue(pc, out CilboxExceptionHandlingClause ehc))
				{
					if (ehc.Flags == ExceptionHandlingClauseOptions.Clause && exceptionRegister.HasValue)
					{
						stackBufferIn.AsSpan()[++sp] = exceptionRegister.Value;
						exceptionRegister = null;
					}
				}
			}
		}

		uint BytecodeAs16( ref int i )
		{
			return (uint)CilboxUtil.BytecodePullLiteral( byteCode, ref i, 2 );
		}
		uint BytecodeAsU32( ref int i )
		{
			return (uint)CilboxUtil.BytecodePullLiteral( byteCode, ref i, 4 );
		}
		int BytecodeAsI32( ref int i )
		{
			return (int)CilboxUtil.BytecodePullLiteral( byteCode, ref i, 4 );
		}
		ulong BytecodeAs64( ref int i )
		{
			return CilboxUtil.BytecodePullLiteral( byteCode, ref i, 8 );
		}
	}

	public class CilboxClass
	{
		public Cilbox box;
		public String className;

		public object[] staticFields;
		public String[] staticFieldNames;
		public Type[] staticFieldTypes;

		public String[] instanceFieldNames;
		public Type[] instanceFieldTypes;

		public Dictionary< String, uint > methodNameToIndex;
		public Dictionary< String, uint > methodFullSignatureToIndex;

		public CilboxMethod [] methods;

		public uint [] importFunctionToId; // from ImportFunctionID

		public bool LoadCilboxClass( Cilbox box, String className, Serializee classData )
		{
			this.box = box;
			this.className = className;

			Dictionary<String, Serializee> classProps = classData.AsMap();

			uint id = 0;
			Serializee [] staticFields = classProps["staticFields"].AsArray();
			int sfnum = staticFields.Length;
			this.staticFields = new object[sfnum];
			staticFieldNames = new String[sfnum];
			staticFieldTypes = new Type[sfnum];
			for( int k = 0; k < sfnum; k++ )
			{
				Dictionary< String, Serializee > field = staticFields[k].AsMap();
				String fieldName = staticFieldNames[id] = field["name"].AsString();
				Type t = staticFieldTypes[id] = box.usage.GetNativeTypeFromSerializee( field["type"] );

				//staticFieldIDs[id] = Cilbox.FindInternalMetadataID( className, 4, fieldName );
				this.staticFields[id] = CilboxUtil.DeserializeDataForProxyField( t, "" );
				id++;
			}

			Serializee [] instanceFields = classProps["instanceFields"].AsArray();
			int ifnum = instanceFields.Length;
			instanceFieldNames = new String[ifnum];
			instanceFieldTypes = new Type[ifnum];

			id = 0;
			for( int k = 0; k < ifnum; k++ )
			{
				Dictionary< String, Serializee > field = instanceFields[k].AsMap();
				String fieldName = instanceFieldNames[id] = field["name"].AsString();
				instanceFieldTypes[id] = box.usage.GetNativeTypeFromSerializee( field["type"] );
				id++;
			}

			id = 0;
			Dictionary< String, Serializee > deserMethods = classProps["methods"].AsMap();
			int mnum = deserMethods.Count;
			methods = new CilboxMethod[mnum];
			methodNameToIndex = new Dictionary< String, uint >();
			methodFullSignatureToIndex = new Dictionary< String, uint >();
			foreach( var k in deserMethods )
			{
				methods[id] = new CilboxMethod();
				methods[id].Load( this, k.Key, k.Value );
				methodNameToIndex[(String)k.Key] = id;
				methodFullSignatureToIndex[methods[id].fullSignature] = id;
				id++;
			}

			// These imports are for things like Start(), Update(), Awake(), etc...
			// so that we can call back into the class.
			int numImportFunctions = Enum.GetNames(typeof(ImportFunctionID)).Length;
			importFunctionToId = new uint[numImportFunctions];
			for( int i = 0; i < numImportFunctions; i++ )
			{
				String fn = Enum.GetName(typeof(ImportFunctionID), i);
				if( i == 0 ) fn = ".ctor";
				uint idx = 0;
				importFunctionToId[i] = 0xffffffff;
				if( methodNameToIndex.TryGetValue(fn, out idx ) )
				{
					importFunctionToId[i] = idx;
				}
			}

			return true;
		}
	}

	public class CilMetadataTokenInfo
	{
		public CilMetadataTokenInfo( MetaTokenType type ) { this.type = type; }
		public MetaTokenType type;
		public bool isValid;
		public int fieldIndex; // Only used for fields of cilbox objects.
		public bool isFieldWhiteListed = false;
		public FieldInfo nativeField; // For whitelisted fields on non-cilbox objects.

		public bool fieldIsStatic;

		public Type nativeType; // Used for types.
		public bool nativeTypeIsStackType;
		public bool nativeTypeIsCilboxProxy;
		public StackType nativeTypeStackType;

		public byte[] arrayInitializerData;

		// Todo handle interpreted types.
		public bool isNative;
		public MethodBase nativeMethod;
		public int interpretiveMethod; // If nativeToken is 0, then it's a interpreted call.
		public int interpretiveMethodClass; // If nativeToken is 0, then it's a interpreted call class

		// For string, type = 0x70, string is in fields[0] (escaped) and Name, unescaped.
		// For methods, type = 10, Declaring Type is in fields[0], Method is in fields[1], Full name is in fields[2] assembly name is in fields[3]
		// For fields, type = 4, Declaring Type is in fields[0], Name is in fields[1], Type is in fields[2]
		//public String [] fields;

		public String Name;
		public String declaringTypeName;
		//public String ToString() { return Name; }

		public delegate StackElement DelegateOverride( CilMetadataTokenInfo ths, ArraySegment<StackElement> stackBufferIn, ArraySegment<StackElement> parametersIn );
		public object opaque;
		public DelegateOverride shim = null;
		public bool shimIsVoid;
		public bool shimIsStatic;
		public int  shimParameterCount;
	}

	public enum MetaTokenType
	{
		mtType = 1,
		mtField = 4,
		mtString = 0x70,
		mtMethod = 10,
		mtArrayInitializer = 13, // Made-up type. 13 is unused in HandleKind.
	}

	abstract public class Cilbox : MonoBehaviour
	{
		public Dictionary< String, int > classes;
		public CilboxClass [] classesList;
		public CilMetadataTokenInfo [] metadatas;
		public String assemblyData;
		private bool initialized = false;

		public static readonly int defaultStackSize = 1024;

		public bool showFunctionProfiling;
		public bool exportDebuggingData;
		public CilboxUsage usage;

		public String disabledReason = "";
		public bool disabled = false;

		public long timeoutLengthUs = 500000; // 500ms Can be changed by specific Cilbox application.
		[HideInInspector] public uint interpreterAccountingDepth = 0;
		[HideInInspector] public long interpreterAccountingDropDead = 0;
		[HideInInspector] public long interpreterAccountingCumulitiveTicks = 0;
		[HideInInspector] public long interpreterInstructionsCount = 0;
		[HideInInspector] public long interpreterTicksInUs = System.Diagnostics.Stopwatch.Frequency / 1000000;

		public long usSpentLastFrame = 0;

		public Cilbox()
		{
			initialized = false;
			usage = new CilboxUsage( this );
		}

		abstract public bool CheckMethodAllowed( out MethodInfo mi, Type declaringType, String name, Serializee [] parametersIn, Serializee [] genericArgumentsIn, String fullSignature );
		abstract public bool CheckTypeAllowed( String sType );
		abstract public bool CheckFieldAllowed( String sType, String sFieldName );

		public void ForceReinit()
		{
			initialized = false;
		}

		public void BoxInitialize( bool bSimulate = false )
		{
#if UNITY_EDITOR
			var pfm = new ProfilerMarker( "Initialize Cilbox" );
			pfm.Auto();
#endif

			if( initialized ) return;
			initialized = true;
			//Debug.Log( "Cilbox Initialize Metadata:" + assemblyData.Length );

			Dictionary< String, Serializee > assemblyRoot = new Serializee( Convert.FromBase64String( assemblyData ), Serializee.ElementType.Map ).AsMap();
			Dictionary< String, Serializee > classData = assemblyRoot["classes"].AsMap();
			Dictionary< String, Serializee > metaData = assemblyRoot["metadata"].AsMap();

			metadatas = new CilMetadataTokenInfo[metaData.Count+1]; // element 0 is invalid.
			metadatas[0] = new CilMetadataTokenInfo( 0 );
			metadatas[0].Name = "<INVALID>";

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
				classesList[clsid++].LoadCilboxClass( this, v.Key, v.Value );

			foreach( var v in metaData )
			{
				int mid = Convert.ToInt32((String)v.Key);
				Dictionary< String, Serializee > st = v.Value.AsMap();
				MetaTokenType metatype = (MetaTokenType)Convert.ToInt32(st["mt"].AsString());
				CilMetadataTokenInfo t = metadatas[mid] = new CilMetadataTokenInfo( metatype );

				t.type = metatype;
				t.Name = "<UNKNOWN>";

				switch( metatype )
				{
				case MetaTokenType.mtString:
					t.Name = st["s"].AsString();
					break;
				case MetaTokenType.mtArrayInitializer:
					t.arrayInitializerData = st["data"].AsBlob();
					break;
				case MetaTokenType.mtField:
					// The type has been "sealed" so-to-speak. In that we have an index for it.

					t.Name = st["name"].AsString();
					t.declaringTypeName = usage.GetNativeTypeNameFromSerializee( st["dt"] );
					t.fieldIsStatic = Convert.ToInt32(st["isStatic"].AsString()) != 0;

					if( st.ContainsKey("index") )
					{
						t.fieldIndex = Convert.ToInt32(st["index"].AsString());
					}
					else
					{
						bool bAllowed = CheckFieldAllowed( t.declaringTypeName, t.Name );
						if( !bAllowed )
						{
							throw new CilboxException( $"Illegal field reference outside of the cilbox. {t.declaringTypeName}.{t.Name} in {v.Key}." );
						}
						t.isFieldWhiteListed = true;

						Serializee typ = st["dt"];
						Type ty = usage.GetNativeTypeFromSerializee( typ );
						if( ty == null )
						{
							throw new CilboxException( $"Could not get allowed type for checking field, {t.declaringTypeName} in {v.Key}." );
						}

						// We have a type for the declaring type, but, we need a field.
						FieldInfo f = ty.GetField( t.Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.Instance );

						if( f == null )
						{
							throw new CilboxException( $"Could not find field for object type {t.declaringTypeName}.{t.Name} in {v.Key}." );
						}

						if( !CheckTypeAllowed( f.FieldType.ToString() ) )
						{
							throw new CilboxException( $"Field for {t.declaringTypeName}.{t.Name} in {v.Key} of type {f.FieldType.ToString()} not allowed." );
						}

						t.isFieldWhiteListed = true;
						t.fieldIsStatic = f.IsStatic;
						t.nativeType = f.FieldType;
						t.nativeField = f;
						t.isValid = true;

						StackType seType = StackElement.StackTypeFromType( t.nativeType );

						if( seType < StackType.Object )
						{
							t.nativeTypeIsStackType = true;
							t.nativeTypeStackType = seType;
						}
						else
						{
							t.nativeTypeIsStackType = false;
						}
						//foreach(var i in st)
						//{
						//	Debug.Log( $"SS: {t.Name} {t.declaringTypeName} {i.Key}" );
						//}
						//throw new CilboxException( $"Currently cannot reference fields outside of the cilbox. {t.declaringTypeName} in {v.Key}.  Use properties." );
					}

					t.isValid = true;
					break;
				case MetaTokenType.mtType:
				{
					Serializee typ = st["dt"];
					t.nativeType = usage.GetNativeTypeFromSerializee( typ );
					StackType seType = StackElement.StackTypeFromType( t.nativeType );
					if( seType < StackType.Object )
					{
						t.nativeTypeIsStackType = true;
						t.nativeTypeStackType = seType;
						t.Name = t.nativeType.ToString();
					}
					else if( t.nativeType != null )
					{
						t.isValid = true;
						t.Name = "Type: " + typ.AsMap()["n"].AsString();
					}
					else
					{
						// Maybe it's a type inside our cilbox?
						t.isValid = false;
						foreach( CilboxClass c in classesList )
						{
							if( c.className == typ.AsMap()["n"].AsString() )
							{
								t.Name = c.className;
								t.nativeTypeIsCilboxProxy = true;
								t.isValid = true;
							}
						}

						if( !t.isValid )
							Debug.LogError( $"Error: Could not find type: {typ.AsMap()["n"].AsString()}" );
					}
					break;
				}
				case MetaTokenType.mtMethod:
				{
					String name = st["name"].AsString();
					String fullSignature = st["fullSignature"].AsString();
					bool isStatic = Convert.ToInt32( st["isStatic"].AsString() ) != 0;
					String useAssembly = st["assembly"].AsString();
					Serializee [] genericArguments = null;
					t.Name = "Method: " + name;

					//Possibly get genericArguments
					Serializee temp;
					if( st.TryGetValue( "ga", out temp ) )
						genericArguments = temp.AsArray();
					else
						genericArguments = new Serializee[0];

					if( usage.OptionallyOverride( name, st["dt"], fullSignature, isStatic, genericArguments, ref t ) )
					{
						break;
					}

					Serializee stDt;
					(name, stDt) = usage.HandleEarlyMethodRewrite( name, st["dt"], genericArguments );

					string declaringTypeName = t.declaringTypeName = usage.GetNativeTypeNameFromSerializee( stDt );


					Serializee [] parametersSer = null;
					if( st.TryGetValue( "parameters", out temp ) )
						parametersSer = temp.AsArray();
					else
						parametersSer = new Serializee[0];

					// First, see if this is to a class we are responsible for. Like does it come from _this_ class?
					int classid;
					if( declaringTypeName == null )
					{
						Debug.LogError( $"Error: Could not find internal type in {fullSignature}" );
					}
					else if( classes.TryGetValue( declaringTypeName, out classid ) )
					{
						CilboxClass matchingClass = classesList[classid];
						uint imid = 0;
						if( matchingClass.methodFullSignatureToIndex.TryGetValue( fullSignature, out imid ) )
						{
							//t.nativeToken = 0; // Sentinel for saying it's a cilbox'd class.
							t.isNative = false;
							t.interpretiveMethod = (int)imid;
							t.interpretiveMethodClass = classid;
							t.isValid = true;
						}
						else
						{
							t.isValid = false;
							throw new CilboxException( $"Error: Could not find internal method {declaringTypeName}:{fullSignature}" );
						}
					}
					else
					{
						Type declaringType = usage.GetNativeTypeFromSerializee( stDt );
						if( declaringType == null )
							throw new CilboxException( $"Error: Could not find referenced type {useAssembly}/{declaringTypeName}/" );

						MethodBase m = usage.GetNativeMethodFromTypeAndName( declaringType, name, parametersSer,
							genericArguments, fullSignature );

						if( m != null )
						{
							t.nativeMethod = m;
							t.isNative = true;
							t.isValid = true;
						} else if( !t.isNative )
						{
							throw new CilboxException( "Error: Could not find reference to: [" + useAssembly + "][" + declaringType.FullName + "][" + fullSignature + "] Type from:" + declaringTypeName );
						}
					}
					break;
				}
				}
			}

			if( !bSimulate )
			{
				foreach( var c in classesList )
				{
					// This class is loaded as it can be.  Time to call the class ctor, if one exists.
					uint cctorIndex = 0;
					if( c.methodFullSignatureToIndex.TryGetValue( "Void .cctor()", out cctorIndex ) )
					{
						if( c.methods[cctorIndex].isStatic )
						{
							c.methods[cctorIndex].Interpret( null, new object[0] );
						}
					}
				}
			}
		}

		public CilboxClass GetClass( String className )
		{
			if( className == null ) return null;
			int clsid;
			if( classes.TryGetValue(className, out clsid)) return classesList[clsid];
			return null;
		}

		public object InterpretIID( CilboxClass cls, CilboxProxy ths, ImportFunctionID iid, object [] parameters )
		{
			if( cls == null ) return null;
			uint index = cls.importFunctionToId[(uint)iid];
			if( index == 0xffffffff ) return null;

			object ret = cls.methods[index].Interpret( ths, parameters );

			return ret;
		}

		public bool InterpreterEntry( CilboxMethod m )
		{
			// Use of Monitor.Lock's here slows the whole emulator down by about 8%
			// TODO: Consider some sort of lockless approach.  This is tricky because
			// you need to make sure you interlock both depth, and, time accounting.
			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			Monitor.Enter( this );
			if( ++interpreterAccountingDepth == 1 )
			{
				// First entry, if we've been disabled, quiety abort.
				// this is normal if
				if( disabled )
				{
					--interpreterAccountingDepth;
					Monitor.Exit( this );
					return false;
				}
				interpreterInstructionsCount = 0;
				interpreterAccountingDropDead = now + timeoutLengthUs * interpreterTicksInUs - interpreterAccountingCumulitiveTicks;
				Monitor.Exit( this );
				return true;
			}
			else if( disabled )
			{
				// fault from within, abort now.
				Monitor.Exit( this );
				throw new CilboxException( $"Function interpreation happened while box was disabled. This should not be possible. Offender: {m.parentClass.className} {m.fullSignature}" );
			}
			else
			{
				if( now > interpreterAccountingDropDead )
				{
					interpreterAccountingCumulitiveTicks = now + timeoutLengthUs * interpreterTicksInUs - interpreterAccountingDropDead;
					--interpreterAccountingDepth;
					Monitor.Exit( this );
					throw new CilboxException( $"Function {m.parentClass.className} {m.fullSignature} timed out." );
				}

				// Otherwise we are recursively being called. All is well.
				Monitor.Exit( this );
				return true;
			}
		}

		public void InterpreterExit()
		{
			Monitor.Enter( this );
			if( --interpreterAccountingDepth == 0 )
			{
				long now = System.Diagnostics.Stopwatch.GetTimestamp();
				long elapsed = now + timeoutLengthUs * interpreterTicksInUs - interpreterAccountingDropDead - interpreterAccountingCumulitiveTicks;
				interpreterAccountingCumulitiveTicks = now + timeoutLengthUs * interpreterTicksInUs - interpreterAccountingDropDead;

				// For profiling
				if( showFunctionProfiling )
				{
					Monitor.Exit( this );
					Debug.Log( $"{interpreterInstructionsCount} in {elapsed/10}us or {interpreterInstructionsCount*10.0/(double)elapsed}MHz" );
					return;
				}
			}
			Monitor.Exit( this );
		}

		void Update()
		{
			usSpentLastFrame = Interlocked.Exchange( ref interpreterAccountingCumulitiveTicks, 0 ) / interpreterTicksInUs;
		}
	}



	///////////////////////////////////////////////////////////////////////////
	//  EXPORTING  ////////////////////////////////////////////////////////////
	///////////////////////////////////////////////////////////////////////////

	#if UNITY_EDITOR

	// Trigger the scene recompile.  Uuuughhhh someone who knows what they're doing need to rewrite
	// this part.  Also, see this discussion: https://discussions.unity.com/t/onprocessscene-sometimes-gets-skipped/943573/7
	//
	// IProcessSceneWithReport - runs before scene is compiled, against the play-mode tree
	// OnPostBuildPlayerScriptDLLs - it runs at the right time, in a blank scene, but that scene is not what is used.
	// IPostprocessBuildWithReport - happens after build is complete, but also dumped into a temporary scene.
	// IPreprocessBuildWithReport - Happens on the main scene, and outputs are preserved
	// BuildPlayerProcessor - same as IPreprocessBuildWithReport

	class CilboxCustomBuildProcessor : IProcessSceneWithReport
	{
		public int callbackOrder { get { return 0; } }
		public void OnProcessScene( UnityEngine.SceneManagement.Scene scene, UnityEditor.Build.Reporting.BuildReport report)
		{
			//Debug.Log( "IProcessSceneWithReport" );
			CilboxScenePostprocessor.OnPostprocessScene();
		}
	}

	class CilboxCustomBuildProcessor2 : IPreprocessBuildWithReport
	{
		public int callbackOrder { get { return 0; } }
		public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
		{
			MonoBehaviour [] allBehavioursThatNeedCilboxing = CilboxUtil.GetAllBehavioursThatNeedCilboxing();

			if( allBehavioursThatNeedCilboxing.Length == 0 )
				return;

			Debug.Log( $"Dirtying scene, found {allBehavioursThatNeedCilboxing.Length} cilboxable elements." );

			// PLEASE LET ME KNOW IF YOU KNOW A BETTER WAY https://discussions.unity.com/t/onprocessscene-sometimes-gets-skipped/943573/6
			GameObject dirtier = GameObject.Find( "/CilboxDirtier" );
			if( !dirtier )
				dirtier = new GameObject("CilboxDirtier");
			dirtier.hideFlags = HideFlags.HideInHierarchy;
			dirtier.transform.position = new Vector3(UnityEngine.Random.Range(-100,100),UnityEngine.Random.Range(-100,100),UnityEngine.Random.Range(-100,100));
			UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
				UnityEngine.SceneManagement.SceneManager.GetActiveScene() );
			UnityEditor.SceneManagement.EditorSceneManager.SaveScene( UnityEngine.SceneManagement.SceneManager.GetActiveScene() );
		}
	}
	public class CilboxScenePostprocessor {
		//[PostProcessSceneAttribute (2)] This is actually called by IProcessSceneWithReport
		public static void OnPostprocessScene() {

			ProfilerMarker perf = new ProfilerMarker("Initial Setup"); perf.Begin();

			MonoBehaviour [] allBehavioursThatNeedCilboxing = CilboxUtil.GetAllBehavioursThatNeedCilboxing();

			Debug.Log( $"Postprocessing scene. Cilbox scripts to do: {allBehavioursThatNeedCilboxing.Length}" );
			if( allBehavioursThatNeedCilboxing.Length == 0 ) return;


			Dictionary< String, Serializee > assemblyMetadata = new Dictionary< String, Serializee >();
			Dictionary< uint, String > originalMetaToFriendlyName = new Dictionary< uint, String >();
			Dictionary< int, uint> assemblyMetadataReverseOriginal = new Dictionary< int, uint >();

			uint mdcount = 1; // token 0 is invalid.
			int bytecodeLength = 0;
			Dictionary< String, Serializee > classes = new Dictionary<String, Serializee>();
			Dictionary< String, Serializee > allClassMethods = new Dictionary< String, Serializee >();

			perf.End(); perf = new ProfilerMarker( "Main Getting Types" ); perf.Begin();

			// Make sure the cilbox script is in use in the scene.
			HashSet<System.Type> TypesInUseInScene = null;

			UnityEngine.SceneManagement.Scene activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

			if( activeScene != null )
			{
				TypesInUseInScene = new HashSet<System.Type>();
				GameObject[] rootObjects = activeScene.GetRootGameObjects();
				foreach (GameObject root in rootObjects)
				{
					MonoBehaviour[] components = root.GetComponentsInChildren<MonoBehaviour>(true);

					foreach (MonoBehaviour component in components)
					{
						if( component != null )
						{
							Type t = component.GetType();
							if( !TypesInUseInScene.Contains( t ) )
							{
								TypesInUseInScene.Add( t);
							}
						}
					}
				}
			}
			else
			{
				Debug.LogWarning( "No scene loaded. Converting ALL Cilboxable scripts." );
			}

			System.Reflection.Assembly [] assys = AppDomain.CurrentDomain.GetAssemblies();
			foreach( System.Reflection.Assembly proxyAssembly in assys )
			{
				assemblyMetadataReverseOriginal.Clear();

				foreach (Type type in proxyAssembly.GetTypes())
				{
					if( type.GetCustomAttributes(typeof(CilboxableAttribute), true).Length <= 0 )
						continue;

					// Cilbox is not in use... But do ALL cilboxes if no scene is loaded.
					if( TypesInUseInScene != null && !TypesInUseInScene.Contains( type ) ) continue;

					ProfilerMarker perfType = new ProfilerMarker(type.ToString()); perfType.Begin();

					Dictionary< String, Serializee > methods = new Dictionary< String, Serializee >();

					int mtyp; // Which round of methods are we getting.
					// Iterate twice. Once for methods, then for constructors.
					for( mtyp = 0; mtyp < 2; mtyp++ )
					{
						MethodBase[] me;
						if( mtyp == 0 )
							me = type.GetMethods( BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static );
						else
							me = type.GetConstructors( BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static );

						foreach( MethodBase m in me )
						{
							if( m.DeclaringType.Assembly != proxyAssembly )
							{
								// We can't export things that are part of Unity.
								continue;
							}

							ProfilerMarker perfMethod = new ProfilerMarker(m.ToString()); perfMethod.Begin();

							String methodName = m.Name;
							Dictionary< String, Serializee > MethodProps = new Dictionary< String, Serializee >();
							MethodBody mb = m.GetMethodBody();
							if( mb == null )
							{
								Debug.Log( $"NOTE: {m.Name} does not have a body" );
								// Things like MemberwiseClone, etc.
								perfMethod.End();
								continue;
							}

							byte [] byteCodeIn = mb.GetILAsByteArray();
							byte [] byteCode = new byte[byteCodeIn.Length];
							Array.Copy( byteCodeIn, byteCode, byteCodeIn.Length );

							String sOpcodeStr = ""; int iOpcodeStrI = 0;
							//if( !ExtractAndTransformMetas( proxyAssembly, ref ba, ref assemblyMetadata, ref assemblyMetadataReverseOriginal, ref mdcount ) ) continue;
							//static bool ExtractAndTransformMetas( Assembly proxyAssembly, ref byte [] byteCode, ref OrderedDictionary od, ref Dictionary< uint, uint > assemblyMetadataReverseOriginal, ref int mdcount )
							{
								int i = 0;
								i = 0;
								try {
									do
									{
										int starti = i;
										for( ; iOpcodeStrI <= starti; iOpcodeStrI++ )
											sOpcodeStr += ((iOpcodeStrI < starti)?" ":"*") + byteCode[iOpcodeStrI].ToString("X2");

										CilboxUtil.OpCodes.OpCode oc;
										try {
											oc = CilboxUtil.OpCodes.ReadOpCode( byteCode, ref i );
										} catch( Exception e )
										{
											Debug.LogError( e.ToString() );
											sOpcodeStr += " XXXX ";
											for( ; iOpcodeStrI < byteCode.Length; iOpcodeStrI++ )
											{
												sOpcodeStr += byteCode[iOpcodeStrI].ToString("X2") + " ";
											}
											Debug.LogError( "Exception decoding opcode at address " + i + " in " + m.Name + "\n" + sOpcodeStr );
											throw;
										}
										int opLen = CilboxUtil.OpCodes.OperandLength[(int)oc.OperandType];
										int backupi = i;
										uint operand = (uint)CilboxUtil.BytecodePullLiteral( byteCode, ref i, opLen );

										bool changeOperand = true;
										uint writebackToken = mdcount;

										// Check to see if this is a meta that we care about.  Then rewrite in a new identifier.
										// ResolveField, ResolveMember, ResolveMethod, ResolveSignature, ResolveString, ResolveType
										// We sort of want to let the other end know what they are. So we mark them with the code
										// from here: https://github.com/jbevain/cecil/blob/master/Mono.Cecil.Metadata/TableHeap.cs#L16

										CilboxUtil.OpCodes.OperandType ot = oc.OperandType;

										if( ot == CilboxUtil.OpCodes.OperandType.InlineTok )
										{
											// Cheating: Just convert it to whatever we think it is.
											switch( operand>>24 )
											{
											case 0x04: // Special case handling for constant initializers.
												if( !assemblyMetadataReverseOriginal.TryGetValue( (int)operand, out writebackToken ) )
												{
													writebackToken = mdcount;
													// Special <PrivateImplementationDetails>+__StaticArrayInitTypeSize=24 instance.
													FieldInfo rf = proxyAssembly.ManifestModule.ResolveField( (int)operand );
													// Extract raw bytes from initializer type
													byte[] bytes = new byte[System.Runtime.InteropServices.Marshal.SizeOf(rf.FieldType)];
													GCHandle h = GCHandle.Alloc(rf.GetValue(null), GCHandleType.Pinned);
													Marshal.Copy(h.AddrOfPinnedObject(), bytes, 0, bytes.Length);
													h.Free();
													// Now, encode our array initializer to base64.
													Dictionary< String, Serializee > thisMeta = new Dictionary< String, Serializee >();
													thisMeta["mt"] = new Serializee(((int)MetaTokenType.mtArrayInitializer).ToString());
													thisMeta["data"] = Serializee.CreateFromBlob( bytes );
													originalMetaToFriendlyName[mdcount] = rf.Name;
													assemblyMetadata[(mdcount++).ToString()] = new Serializee( thisMeta );
												}
												break;
										/*
											case 0x02: // Inline Token for Type (typically used with typeof())
												if( !assemblyMetadataReverseOriginal.TryGetValue( (int)operand, out writebackToken ) )
												{
													// TODO: Actually investigate this.  See if we really need it.
													writebackToken = mdcount;
													Type ty = proxyAssembly.ManifestModule.ResolveType( (int)operand );
													Dictionary<String, Serializee> fieldProps = new Dictionary<String, Serializee>();
													fieldProps["mt"] = new Serializee( ((int)MetaTokenType.mtType).ToString() );
													fieldProps["dt"] = CilboxUtil.GetSerializeeFromNativeType( ty );
													assemblyMetadata[(mdcount++).ToString()] = new Serializee( fieldProps );
													originalMetaToFriendlyName[writebackToken] = ty.FullName;
												}
												break;
										*/
											default:
												throw new CilboxException( "Exception decoding opcode at address (confusing meta " + operand.ToString("X8") + ") " + i + " in " + m.Name );
											}
										}
										else if( ot == CilboxUtil.OpCodes.OperandType.InlineSwitch )
										{
											i += (int)operand*4;
											changeOperand = false;
										}
										else if( ot == CilboxUtil.OpCodes.OperandType.InlineString )
										{
											if( !assemblyMetadataReverseOriginal.TryGetValue( (int)operand, out writebackToken ) )
											{
												writebackToken = mdcount;
												Dictionary< String, String > thisMeta = new Dictionary< String, String >();
												String st = ((int)MetaTokenType.mtString).ToString();
												thisMeta["mt"] = st;
												thisMeta["s"] = proxyAssembly.ManifestModule.ResolveString( (int)operand );
												originalMetaToFriendlyName[mdcount] = st;
												assemblyMetadata[(mdcount++).ToString()] = new Serializee( thisMeta );
											}
										}
										else if( ot == CilboxUtil.OpCodes.OperandType.InlineMethod )
										{
											if( !assemblyMetadataReverseOriginal.TryGetValue( (int)operand, out writebackToken ) )
											{
												writebackToken = mdcount;
												MethodBase tmb = proxyAssembly.ManifestModule.ResolveMethod( (int)operand );

												Dictionary<String, Serializee> methodProps = new Dictionary<String, Serializee>();

												// "Generic constructors are not supported in the .NET Framework version 2.0"
												if( !tmb.IsConstructor )
												{
													Type[] templateArguments = tmb.GetGenericArguments();
													if( templateArguments.Length > 0 )
													{
														Serializee [] argtypes = new Serializee[templateArguments.Length];
														for( int a = 0; a < templateArguments.Length; a++ )
															argtypes[a] = CilboxUtil.GetSerializeeFromNativeType( templateArguments[a] );
														methodProps["ga"] = new Serializee( argtypes );
													}
												}

												methodProps["dt"] = CilboxUtil.GetSerializeeFromNativeType( tmb.DeclaringType );
												methodProps["name"] = new Serializee( tmb.Name );

												System.Reflection.ParameterInfo[] parameterInfos = tmb.GetParameters();
												if( parameterInfos.Length > 0 )
												{
													Serializee [] parametersSer = new Serializee[parameterInfos.Length];
													for( var j = 0; j < parameterInfos.Length; j++ )
													{
														Type ty = parameterInfos[j].ParameterType;
														parametersSer[j] = CilboxUtil.GetSerializeeFromNativeType( ty );
													}
													methodProps["parameters"] = new Serializee( parametersSer );
												}
												methodProps["fullSignature"] = new Serializee( tmb.ToString() );
												methodProps["isStatic"] = new Serializee( tmb.IsStatic?"1":"0" );
												methodProps["assembly"] = new Serializee( tmb.DeclaringType.Assembly.GetName().Name );
												methodProps["mt"] = new Serializee(((int)MetaTokenType.mtMethod).ToString());
												originalMetaToFriendlyName[writebackToken] = tmb.DeclaringType.ToString() + "." + tmb.ToString();
												assemblyMetadata[(mdcount++).ToString()] = new Serializee( methodProps );
											}
										}
										else if( ot == CilboxUtil.OpCodes.OperandType.InlineField )
										{
											if( !assemblyMetadataReverseOriginal.TryGetValue( (int)operand, out writebackToken ) )
											{
												writebackToken = mdcount;
												FieldInfo rf = proxyAssembly.ManifestModule.ResolveField( (int)operand );

												Dictionary<String, Serializee> fieldProps = new Dictionary<String, Serializee>();
												fieldProps["mt"] = new Serializee( ((int)MetaTokenType.mtField).ToString() );
												fieldProps["dt"] = CilboxUtil.GetSerializeeFromNativeType( rf.DeclaringType );
												fieldProps["name"] = new Serializee( rf.Name );
												//fieldProps["fullName"] = rf.FieldType.FullName;
												fieldProps["isStatic"] = new Serializee( (rf.IsStatic?1:0).ToString() );
												originalMetaToFriendlyName[writebackToken] = rf.Name;
												assemblyMetadata[(mdcount++).ToString()] = new Serializee(fieldProps);
											}
										}
										else if( ot == CilboxUtil.OpCodes.OperandType.InlineType )
										{
											if( !assemblyMetadataReverseOriginal.TryGetValue( (int)operand, out writebackToken ) )
											{
												writebackToken = mdcount;
												Type ty = proxyAssembly.ManifestModule.ResolveType( (int)operand );

												Dictionary<String, Serializee> fieldProps = new Dictionary<String, Serializee>();
												fieldProps["mt"] = new Serializee( ((int)MetaTokenType.mtType).ToString() );
												fieldProps["dt"] = CilboxUtil.GetSerializeeFromNativeType( ty );
												assemblyMetadata[(mdcount++).ToString()] = new Serializee( fieldProps );
												originalMetaToFriendlyName[writebackToken] = ty.FullName;
											}
										}
										else
											changeOperand = false;

										if( changeOperand )
										{
											i = backupi;
											assemblyMetadataReverseOriginal[(int)operand] = writebackToken;
											CilboxUtil.BytecodeReplaceLiteral( ref byteCode, ref i, opLen, writebackToken );
										}
										if( i >= byteCode.Length ) break;
									} while( true );
								}
								catch( Exception e )
								{
									Debug.LogError( e.ToString() );
									continue;
								}
							}

							bytecodeLength += byteCode.Length;
							MethodProps["body"] = Serializee.CreateFromBlob(byteCode);

							IList<ExceptionHandlingClause> exceptions = mb.ExceptionHandlingClauses;
							if( exceptions.Count > 0 )
							{
								Serializee [] excArray = new Serializee[exceptions.Count];
								for( int k = 0; k < exceptions.Count; k++ )
								{
									ExceptionHandlingClause c = exceptions[k];
									Dictionary< String, Serializee > exc = new Dictionary< String, Serializee >();
									exc["flags"] = new Serializee( ((int)c.Flags).ToString() );
									exc["tryOff"] = new Serializee( c.TryOffset.ToString() );
									exc["tryLen"] = new Serializee( c.TryLength.ToString() );
									exc["hOff"] = new Serializee( c.HandlerOffset.ToString() );
									exc["hLen"] = new Serializee( c.HandlerLength.ToString() );

									if( c.Flags == ExceptionHandlingClauseOptions.Clause && c.CatchType != null )
									{
										exc["cType"] = CilboxUtil.GetSerializeeFromNativeType( c.CatchType );
									}
									excArray[k] = new Serializee( exc );
								}
								MethodProps["eh"] = new Serializee( excArray );
							}

							Serializee [] localVars = new Serializee[mb.LocalVariables.Count];
							for( int i = 0; i < mb.LocalVariables.Count; i++ )
							{
								LocalVariableInfo lvi = mb.LocalVariables[i];
								Dictionary< String, Serializee > local = new Dictionary< String, Serializee >();
								local["name"] = new Serializee( lvi.ToString() );
								local["dt"] = CilboxUtil.GetSerializeeFromNativeType( lvi.LocalType );
								localVars[i] = new Serializee( local );
							}
							MethodProps["locals"] = new Serializee( localVars );

							ParameterInfo [] parameters = m.GetParameters();

							Serializee [] parameterList = new Serializee[parameters.Length];
							for( int i = 0; i < parameters.Length; i++ )
							{
								Dictionary< String, Serializee > tpi = new Dictionary< String, Serializee >();
								tpi["name"] = new Serializee( parameters[i].Name );
								tpi["dt"] = CilboxUtil.GetSerializeeFromNativeType( parameters[i].ParameterType );
								parameterList[i] = new Serializee( tpi );
							}
							MethodProps["parameters"] = new Serializee( parameterList );
							MethodProps["maxStack"] = new Serializee( mb.MaxStackSize.ToString() );
							MethodProps["isVoid"] = new Serializee( (m is MethodInfo)?(((MethodInfo)m).ReturnType == typeof(void) ? "1" : "0" ): "0" );
							MethodProps["isStatic"] = new Serializee( m.IsStatic ? "1" : "0" );
							MethodProps["fullSignature"] = new Serializee( m.ToString() );

							methods[methodName] = new Serializee( MethodProps );
							perfMethod.End();
						}
					}

					allClassMethods[type.FullName] = new Serializee( methods );
					perfType.End();
				}

				perf.End(); perf = new ProfilerMarker( "Secondary Getting Types" ); perf.Begin();

				// Now that we've iterated through all classes, and collected all possible uses of field IDs,
				// go through the classes again, collecting the fields themselves.

				foreach (Type type in proxyAssembly.GetTypes())
				{
					if( type.GetCustomAttributes(typeof(CilboxableAttribute), true).Length <= 0 )
						continue;

					ProfilerMarker perfType = new ProfilerMarker(type.ToString()); perfType.Begin();

					Dictionary< String, Serializee > classProps = new Dictionary< String, Serializee >();

					// This portion extracts the index information from the current type, and
					// Writes it back in where it was needed above in the Method call.
					//
					for( int lst = 0; lst < 2; lst++ )
					{
						List< Serializee > fields = new List< Serializee >();
						int sfid = 0;
						FieldInfo[] fi;
						if( lst == 0 )
							fi = type.GetFields( BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static );
						else
							fi = type.GetFields( BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance );
						foreach( var f in fi )
						{
							Dictionary< String, Serializee > dictField = new Dictionary< String, Serializee >();
							dictField["name"] = new Serializee( f.Name );
							dictField["type"] = CilboxUtil.GetSerializeeFromNativeType( f.FieldType );
							fields.Add( new Serializee( dictField ) );

							// Fill in our metadata with a class-specific field ID, if this field ID was used in code anywhere.
							uint mdid;
							if( assemblyMetadataReverseOriginal.TryGetValue(f.MetadataToken, out mdid) )
							{
								Serializee sOpen = assemblyMetadata[mdid.ToString()];
								Dictionary< String, Serializee > m = sOpen.AsMap();
								m["index"] = new Serializee( sfid.ToString() );
								assemblyMetadata[mdid.ToString()] = new Serializee( m );
							}
							sfid++;
						}
						classProps[(lst == 0 )?"staticFields":"instanceFields"] = new Serializee( fields.ToArray() );
					}

					classProps["methods"] = allClassMethods[type.FullName];
					classes[type.FullName] = new Serializee( classProps );
					perfType.End();
				}
			}

			perf.End(); perf = new ProfilerMarker( "Assembling" ); perf.Begin();

			Dictionary< String, Serializee > assemblyRoot = new Dictionary< String, Serializee >();
			assemblyRoot["classes"] = new Serializee( classes );
			assemblyRoot["metadata"] = new Serializee( assemblyMetadata );
			Serializee assemblySerializee = new Serializee( assemblyRoot );

			perf.End(); perf = new ProfilerMarker( "Serializing" ); perf.Begin();

			String sAllAssemblyData = Convert.ToBase64String(assemblySerializee.DumpAsMemory().ToArray() );

			perf.End(); perf = new ProfilerMarker( "Checking If Assembly Changed" ); perf.Begin();

			Cilbox [] se = Resources.FindObjectsOfTypeAll(typeof(Cilbox)) as Cilbox [];
			Cilbox tac;
			if( se.Length != 0 )
			{
				tac = se[0];
				if( tac.assemblyData != sAllAssemblyData ) EditorUtility.SetDirty( tac );
			}
			else
			{
				throw new CilboxException( "You must have an object with Cilbox (Scene or Avatar)" );
				//GameObject cilboxDataObject = new GameObject("CilboxData " + new System.Random().Next(0,10000000));
				//tac = cilboxDataObject.AddComponent( typeof(Cilbox) ) as Cilbox;
				//EditorUtility.SetDirty( tac );
			}

			perf.End(); perf = new ProfilerMarker( "Applying Assembly" ); perf.Begin();

			if( tac.exportDebuggingData )
			{
				GameObject gameObjectAsm = new GameObject("CilboxAsm " + new System.Random().Next(0,10000000));
				Cilbox b = gameObjectAsm.AddComponent( tac.GetType() ) as Cilbox;
				new Task( () => {
					CilboxUtil.AssemblyLoggerTask( Application.dataPath + "/CilboxLog.txt", sAllAssemblyData, b );
					UnityEngine.Events.UnityAction deleter = null;
					deleter = () => { GameObject.Destroy( gameObjectAsm ); Application.onBeforeRender -= deleter; };
					Application.onBeforeRender += deleter;
				} ).Start();
			}

			{
				MonoScript ms = MonoScript.FromMonoBehaviour(tac);
				String scriptPath = AssetDatabase.GetAssetPath( ms );
				if( scriptPath == null ) Debug.LogError( "Can't find path to cilbox for writing XML." );
				else
				{
					FileInfo fi = new FileInfo( scriptPath );
					String thisPath = fi.Directory.ToString();
					new Task( () => {
						// Tricky bits...
						//abstract public HashSet<String> GetWhiteListTypes();

						HashSet<String> allWhiteList = new HashSet<String>();

						System.Reflection.Assembly [] assys = AppDomain.CurrentDomain.GetAssemblies();
						foreach( System.Reflection.Assembly proxyAssembly in assys )
						{
							foreach (Type type in proxyAssembly.GetTypes())
							{
								if( type.GetCustomAttributes(typeof(CilboxTarget), true).Length <= 0 )
									continue;
								//HashSet<String> toAdd = (HashSet<String>)type.InvokeMember( "GetWhiteListTypes", BindingFlags.Static | BindingFlags.Public, null, null, null );
								MethodInfo mi = type.GetMethod( "GetWhiteListTypes" );
								HashSet<String> toAdd = (HashSet<String>)mi.Invoke( null, null );
								allWhiteList.UnionWith( toAdd );
							}
						}

						Dictionary< String, HashSet<String> > fullWhiteList = new Dictionary< String, HashSet<String> >();

						foreach( String s in allWhiteList )
						{
							//System.Reflection.Assembly [] assys = AppDomain.CurrentDomain.GetAssemblies();
							foreach( System.Reflection.Assembly a in assys )
							{
								Type typ = a.GetType( s );
								if( typ == null ) continue;
								AssemblyName assemName = a.GetName();
								HashSet<String> hs;
								if( !fullWhiteList.TryGetValue( assemName.Name, out hs ) )
									hs = fullWhiteList[assemName.Name] = new HashSet<String>();

								fullWhiteList[assemName.Name].Add( typ.ToString() );
								break;
							}
						}

						StreamWriter CLog = File.CreateText( thisPath + "/link.xml" );
						CLog.WriteLine( "<linker>" );
						foreach( var v in fullWhiteList )
						{
							CLog.WriteLine( $"\t<assembly fullname=\"{v.Key}\">" );
							foreach( String s in v.Value )
							{
								CLog.WriteLine( $"\t\t<type fullname=\"{s}\" preserve=\"all\"/>" );
							}
							CLog.WriteLine( "\t</assembly>" );
						}
						CLog.WriteLine( "</linker>" );
						CLog.Close();

					} ).Start();
				}
			}

			if( bytecodeLength == 0 )
			{
				// This happens the second time around.
			}
			else
			{
				tac.assemblyData = sAllAssemblyData;
				tac.ForceReinit();
			}

			Dictionary< MonoBehaviour, CilboxProxy > refToProxyMap = new Dictionary< MonoBehaviour, CilboxProxy >();
			List< MonoBehaviour > refProxiesOrig = new List< MonoBehaviour >();
			List< CilboxProxy > refProxies = new List< CilboxProxy >();

			perf.End(); perf = new ProfilerMarker( "Updating Game Objects" ); perf.Begin();

			// Iterate over all GameObjects, and find the ones that have Cilboxable scripts.
			object[] obj = GameObject.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
			foreach (object o in obj)
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

					CilboxProxy p = g.AddComponent<CilboxProxy>();
					refProxies.Add( p );
					refProxiesOrig.Add( m );
					refToProxyMap[m] = p;
				}
			}
			perf.End(); perf = new ProfilerMarker( "Setting Up Proxies" ); perf.Begin();

			var cnt = refProxies.Count;
			for( var i = 0; i < cnt; i++ )
			{
				CilboxProxy p = refProxies[i];
				MonoBehaviour m = refProxiesOrig[i];

				p.SetupProxy( tac, m, refToProxyMap );
			}

			perf.End(); perf = new ProfilerMarker( "Destroying Silboxable Scripts" ); perf.Begin();
			// re-attach the refrences to
			foreach (MonoBehaviour m in allBehavioursThatNeedCilboxing)
			{
				UnityEngine.Object.DestroyImmediate( m );
			}
			perf.End();
		}
	}
	#endif

	public enum ImportFunctionID
	{
		dotCtor, // Must be at index 0.
		FixedUpdate,
		Update,
		Start,
		Awake,
		OnTriggerEnter,
		OnTriggerExit,
		OnEnable,
		OnDisable,
	}
}

