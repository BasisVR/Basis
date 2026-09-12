//#define PER_INSTRUCTION_PROFILING

using UnityEngine;
using UnityEngine.Serialization;
using System.Collections.Generic;
using System;
using System.Collections.Specialized;
using System.Collections;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading; // At runtime, only used for a lock (Monitor)
using System.Buffers;

#if UNITY_EDITOR
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using System.IO;
using System.Linq;
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

	public class CilboxHeapInstance
	{
		public string className;
		public CilboxClass cls;
		public StackElement[] fields;
	}

	public class CilboxMethod
	{
		public CilboxClass parentClass;
		public int MaxStackSize;
		public String methodName;
		public String fullSignature;
		public String[] methodLocals;
		public bool isStatic;
		public bool isConstructor;
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

		public void Load( CilboxClass cclass, SerializedMethod sm )
		{
			parentClass = cclass;
			methodName = sm.methodName;
			byteCode = sm.body;
			MaxStackSize = sm.maxStack;
			isVoid = sm.isVoid;
			isStatic = sm.isStatic;
			fullSignature = sm.fullSignature;
			isConstructor = sm.isCtor;

			methodLocals = new String[sm.locals.Length];
			typeLocals = new Type[sm.locals.Length];
			for( int i = 0; i < sm.locals.Length; i++ )
			{
				methodLocals[i] = sm.locals[i].name;
				typeLocals[i] = parentClass.box.usage.GetNativeTypeFromDescriptor( sm.locals[i].type );
			}
			signatureParameters = new String[sm.parameters.Length];
			typeParameters = new Type[sm.parameters.Length];
			for( int p = 0; p < sm.parameters.Length; p++ )
			{
				signatureParameters[p] = sm.parameters[p].name;
				typeParameters[p] = parentClass.box.usage.GetNativeTypeFromDescriptor( sm.parameters[p].type );
			}

			if (sm.exceptionHandlers.Length > 0)
			{
				exceptionClauses = new CilboxExceptionHandlingClause[sm.exceptionHandlers.Length];
				handlerOffsetToClauseMap = new Dictionary<int, CilboxExceptionHandlingClause>();
				for (int e = 0; e < sm.exceptionHandlers.Length; e++)
				{
					SerializedExceptionHandler seh = sm.exceptionHandlers[e];
					CilboxExceptionHandlingClause clause = new CilboxExceptionHandlingClause();
					clause.Flags = (ExceptionHandlingClauseOptions)seh.flags;
					clause.TryOffset = seh.tryOffset;
					clause.TryLength = seh.tryLength;
					clause.TryEndOffset = clause.TryOffset + clause.TryLength;
					clause.HandlerOffset = seh.handlerOffset;
					clause.HandlerLength = seh.handlerLength;
					clause.HandlerEndOffset = clause.HandlerOffset + clause.HandlerLength;
					if (seh.hasCatchType)
					{
						clause.CatchType = parentClass.box.usage.GetNativeTypeFromDescriptor(seh.catchType);
						if (clause.CatchType == null)
						{
							// Check if it's a Cilboxable type
							String typeName = seh.catchType.typeName;
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
			if( ths != null && ths.disabled ) return null;

			int plen = parametersIn?.Length ?? 0;
			int thisOffset = isStatic ? 0 : 1;

			StackElement [] parameters = new StackElement[plen+thisOffset];
			StackElement [] stackBuffer = new StackElement[Cilbox.defaultStackSize];

			if( isStatic )
			{
				for( int p = 0; p < plen; p++ )
					parameters[p].Load( parametersIn[p] );
			}
			else
			{
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
			}
			catch( Exception e )
			{
				parentClass.box.InterpreterExit();
				if( ths != null ) ths.DisableProxy();
				else parentClass.box.DisableWithReason(e.ToString());
				if( e is CilboxUnhandledInterpretedException uhe && uhe.Throwee is System.Exception te ) throw te;
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
			CilMetadataTokenInfo constrainedMeta = null;
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
					case 0x10: parameters[byteCode[pc++]] = stackBuffer[sp--]; break; // starg.s <uint8 (argNum)> -- mirror of stloc.s (0x13) but stores into a parameter slot
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
						int currentInstruction = pc - 1;
						uint bc = (b == 0x29) ? stackBuffer[sp--].u : BytecodeAsU32( ref pc );
						object iko = null; // Returned value.
						CilMetadataTokenInfo dt = box.metadatas[bc];
						bool isVoid = false;
						MethodBase st;
						bool isNewObj = b == 0x73;
						bool isJmp = b == 0x27;

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
								if( targetMethod == null )
									throw new CilboxInterpreterRuntimeException($"Function {dt.Name} not found", parentClass.className, methodName, pc);

								// callvirt (0x6F) dispatches on the receiver's runtime type; plain call (0x28) and base.X() must stay
								// bound to the token's method, and statics/ctors have no virtual receiver -- so only re-resolve here.
								if( b == 0x6F && !targetMethod.isStatic && !targetMethod.isConstructor )
								{
									// Args are still on the stack, so the receiver 'this' sits just below them, at sp - (parameter count).
									int vparams = targetMethod.signatureParameters.Length;
									object oThis = stackBuffer[sp - vparams].AsObject( box );
									// Receiver's actual runtime class: interpreted proxy or heap object (null for a native/null receiver).
									CilboxClass rtClass = (oThis as CilboxProxy)?.cls;
									if( rtClass == null ) rtClass = (oThis as CilboxHeapInstance)?.cls;
									// If that class overrides this method (same signature, more-derived), re-bind the call to the override.
									if( rtClass != null && rtClass != targetClass &&
										rtClass.methodFullSignatureToIndex.TryGetValue( targetMethod.fullSignature, out uint vidx ) )
									{
										targetClass = rtClass;
										targetMethod = targetClass.methods[(int)vidx];
									}
								}

								isVoid = targetMethod.isVoid;
								int staticOffset = (targetMethod.isStatic?0:1);
								int numParams = targetMethod.signatureParameters.Length;
								int nextParameterStart = stackContinues;
								int nextStackHead = nextParameterStart + numParams + staticOffset;

								for( int i = numParams - 1; i >= 0; i-- )
									stackBuffer[nextParameterStart+i+staticOffset] = stackBuffer[sp--];

								bool ctorAsNewObj = targetMethod.isConstructor && isNewObj;
								bool ctorAsCall = targetMethod.isConstructor && !isNewObj;

								if( ctorAsNewObj )
								{
									CilboxHeapInstance newObj = CreateDefaultInternalObject( targetClass );
									stackBuffer[nextParameterStart].LoadObject( newObj );
									try
									{
										targetMethod.InterpretInner(stackBufferIn.Slice(nextStackHead), stackBufferIn.Slice(nextParameterStart, numParams + staticOffset));
									}
									catch (CilboxUnhandledInterpretedException e)
									{
										interpretedThrow(currentInstruction, e.Throwee);
									}
									stackBuffer[++sp].LoadObject( newObj );
								}
								else
								{
									if( !targetMethod.isStatic )
										stackBuffer[nextParameterStart] = stackBuffer[sp--];

									if( !targetMethod.isStatic && stackBuffer[nextParameterStart].AsObject() is CilboxProxy dispatchProxy && dispatchProxy.disabled )
									{
										interpretedThrow( currentInstruction, new System.Exception( "Attempted to invoke a method on a disabled interpreted behaviour" ) );
									}
									else
									{
										try
										{
											if (!isVoid && !ctorAsCall)
												stackBuffer[++sp] = targetMethod.InterpretInner(stackBufferIn.Slice(nextStackHead), stackBufferIn.Slice(nextParameterStart, numParams + staticOffset));
											else
												targetMethod.InterpretInner(stackBufferIn.Slice(nextStackHead), stackBufferIn.Slice(nextParameterStart, numParams + staticOffset));
										}
										catch (CilboxUnhandledInterpretedException e)
										{
											interpretedThrow(currentInstruction, e.Throwee);
										}
									}
								}

								if( isJmp )
								{
									// This is returning from a jump, so immediately abort.
									if( isVoid || ctorAsCall ) stackBuffer[++sp] = StackElement.nil; /// ?? Please check me! If wrong, fix above, too.
									cont = false;
								}
							}
						}
						else
						{
							st = dt.nativeMethod;
							isVoid = dt.nativeIsVoid;
							object callthis = null;
							Type[] paTypes = dt.nativeParameterTypes;
							int numFields = paTypes.Length;
							object [] callpar = new object[numFields];
							StackElement [] callpar_se = new StackElement[numFields];

							int ik;
							for( ik = 0; ik < numFields; ik++ )
							{
								StackElement se = stackBuffer[sp--];
								callpar_se[numFields-ik-1] = se;
								object o = se.AsObject(box);
								Type t = paTypes[numFields-ik-1];

								if( t.IsByRef )
								{
									// out parameters can be uninintialized, so we have to initialize them first
									Type elementType = t.GetElementType();
									if( o != null && !elementType.IsAssignableFrom(o.GetType()) )
									{
										if( elementType.IsValueType )
											o = Activator.CreateInstance(elementType);
										else
											o = null;
									}
								}
								// XXX TODO: Copy mechanism below from ResolveToStackElement and Coerce
								else if( se.type < StackType.Object )
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
								ConstructorInfo ctor = (ConstructorInfo)st;
								if( isNewObj )
								{
									try
									{
										iko = ctor.Invoke( callpar );
									}
									catch( TargetInvocationException e )
									{
										interpretedThrow(pc - 1, e.InnerException ?? e);
										break;
									}
									isVoid = false; // newobj always pushes a reference/value.
								}
								else
								{
									StackElement ctorThisSe = stackBuffer[sp--];
									Type ctorDeclaringType = ctor.DeclaringType;

									if( ctorDeclaringType != null && ctorDeclaringType.IsValueType )
									{
										object newStruct = ctor.Invoke( callpar );
										if( ctorThisSe.type == StackType.Address )
											ctorThisSe.DereferenceLoadAddress( newStruct );
										else if( ctorThisSe.type == StackType.NativeHandle )
											ctorThisSe.DereferenceLoadNativeHandle( box, newStruct );
										else
											throw new CilboxInterpreterRuntimeException(
												$"Unsupported target for native value-type constructor: {ctorDeclaringType.FullName}",
												parentClass.className, methodName, pc);
										isVoid = true;
									}
									else
									{
										object ctorThis = ctorThisSe.AsObject(box);
										if (ctorThis == null)
										{
											interpretedThrow(pc - 1, new NullReferenceException());
											break;
										}

										if( ctorDeclaringType == null || ( ctorDeclaringType != typeof(object) && ctorDeclaringType != typeof(MonoBehaviour) ) )
										{
											throw new CilboxInterpreterRuntimeException(
												$"Unsupported native constructor call on existing instance: {ctorDeclaringType?.FullName}",
												parentClass.className, methodName, pc);
										}

										isVoid = true;
									}
								}
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
								else if( constrainedMeta != null && se.type < StackType.Object )
								{
									if( constrainedMeta.cilboxEnum != null )
										callthis = constrainedMeta.cilboxEnum.BoxValue( se.l );
									else
										callthis = se.CoerceToObject( constrainedMeta.nativeType );
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
								constrainedMeta = null;

								if (callthis == null)
								{
									interpretedThrow(pc - 1, new NullReferenceException());
									break;
								}

								try
								{
									iko = st.Invoke( callthis, callpar );
								}
								catch( TargetInvocationException e )
								{
									interpretedThrow(pc - 1, e.InnerException ?? e);
									break;
								}
								if( seorig.type == StackType.Address  && callthis is not BoxedCilboxEnum ) // enums are immutable
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
								try
								{
									iko = st.Invoke( null, callpar );
								}
								catch( TargetInvocationException e )
								{
									interpretedThrow(pc - 1, e.InnerException ?? e);
									break;
								}
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
								if( iko is char retChar )
									stackBuffer[++sp].LoadUshort( (ushort)retChar );
								else
									stackBuffer[++sp].Load( iko );
							}
							if( isJmp )
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

					case 0x65: // neg
					{
						ref StackElement s = ref stackBuffer[sp];
						switch (s.type)
						{
							case StackType.Float:
								s.f = -s.f;
								break;
							case StackType.Double:
								s.d = -s.d;
								break;
							default:
								s.l = -s.l;
								break;
						}
						break;
					}

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
					case 0xD3: { StackElement se = stackBuffer[sp]; stackBuffer[sp].LoadNint(  ( se.type <= StackType.Int ? (nint)se.i    : se.type == StackType.Uint ? (nint)se.u   : se.type == StackType.Long ? (nint)se.l  : se.type == StackType.Ulong ? (nint)se.e  : (nint)Convert.ToInt64(se.CoerceToObject(typeof(long)))) ); break; } // conv.i

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
							if( TryGetInternalObjectData( se.o, out string seClassName, out _ ) )
							{
								if( seClassName == ti.Name )
									oRet = se.o;
							}
						}
						else if( ti.nativeTypeIsStackType )
						{
							if( StackElement.TypeToStackType.TryGetValue( ti.Name, out StackType stackType ) && ti.nativeTypeStackType == stackType )
							{
								if( se.type == StackType.Object ) // boxed value: keep it only if it's genuinely the target type; else oRet stays null (isinst -> pushes null, castclass -> throws below)
								{
									if( se.o != null && ti.nativeType != null && ti.nativeType.IsInstanceOfType( se.o ) )
										oRet = se.o;
								}
								else if( se.type == stackType )
								{
									oRet = se.AsObject();
								}
							}
						}
						else if( se.o != null && se.o.GetType() == ti.nativeType )
							oRet = se.o;

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

						if( opths is CilboxProxy fieldProxy && fieldProxy.disabled )
						{
							interpretedThrow( pc - 1, new System.Exception( "Attempted to access a field on a disabled interpreted behaviour" ) );
							break;
						}

						if( TryGetInternalObjectData( opths, out _, out StackElement[] internalFields ) )
						{
							stackBuffer[++sp] = internalFields[box.metadatas[bc].fieldIndex];
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

						if( opths is CilboxProxy fieldProxy && fieldProxy.disabled )
						{
							interpretedThrow( pc - 1, new System.Exception( "Attempted to access a field on a disabled interpreted behaviour" ) );
							break;
						}

						if( TryGetInternalObjectData( opths, out _, out StackElement[] internalFields ) )
						{
							stackBuffer[++sp] = StackElement.CreateAddressReference((Array)(internalFields), (uint)box.metadatas[bc].fieldIndex);
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

						if( opths is CilboxProxy fieldProxy && fieldProxy.disabled )
						{
							interpretedThrow( pc - 1, new System.Exception( "Attempted to access a field on a disabled interpreted behaviour" ) );
							break;
						}

						if( TryGetInternalObjectData( opths, out _, out StackElement[] internalFields ) )
						{
							internalFields[box.metadatas[bc].fieldIndex] = se;
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
						CilMetadataTokenInfo ldsm = box.metadatas[bc];
						if (!ldsm.fieldIsStatic)
						{
							throw new CilboxInterpreterRuntimeException($"Field {ldsm.Name} on type {ldsm?.nativeType?.FullName} is not static and can not be accessed with ldsfld", parentClass.className, methodName, pc);
						}
						if( ldsm.isFieldWhiteListed && ldsm.nativeField != null )
						{
							stackBuffer[++sp].Load( ldsm.nativeField.GetValue( null ) );
						}
						else
						{
							CilboxClass declaringClass = ldsm.interpretiveFieldClass >= 0 ? box.classesList[ldsm.interpretiveFieldClass] : parentClass;
							stackBuffer[++sp].Load( declaringClass.staticFields[ldsm.fieldIndex] );
						}
						break;
					}
					case 0x7f: // ldsflda
					{
						uint bc = BytecodeAsU32( ref pc );
						CilMetadataTokenInfo ldsam = box.metadatas[bc];
						if (!ldsam.fieldIsStatic)
						{
							throw new CilboxInterpreterRuntimeException($"Field {ldsam.Name} on type {ldsam?.nativeType?.FullName} is not static and can not be accessed with ldsfld", parentClass.className, methodName, pc);
						}
						if( ldsam.isFieldWhiteListed && ldsam.nativeField != null )
						{
							stackBuffer[++sp] = StackElement.CreateNativeHandleReference( null, bc );
						}
						else
						{
							CilboxClass declaringClass = ldsam.interpretiveFieldClass >= 0 ? box.classesList[ldsam.interpretiveFieldClass] : parentClass;
							stackBuffer[++sp] = StackElement.CreateAddressReference( (Array)(declaringClass.staticFields), (uint)ldsam.fieldIndex );
						}
						break;
					}
					case 0x80: // stsfld
					{
						uint bc = BytecodeAsU32( ref pc );
						object obj = stackBuffer[sp--].AsObject();
						CilMetadataTokenInfo stsm = box.metadatas[bc];
						if (!stsm.fieldIsStatic)
						{
							throw new CilboxInterpreterRuntimeException($"Field {stsm.Name} on type {stsm?.nativeType?.FullName} is not static and can not be accessed with ldsfld", parentClass.className, methodName, pc);
						}
						if( stsm.isFieldWhiteListed && stsm.nativeField != null )
						{
							stsm.nativeField.SetValue( null, obj );
						}
						else
						{
							CilboxClass declaringClass = stsm.interpretiveFieldClass >= 0 ? box.classesList[stsm.interpretiveFieldClass] : parentClass;
							declaringClass.staticFields[stsm.fieldIndex] = obj;
						}
						break;
					}
					case 0x81: // stobj
					{
						uint typeToken = BytecodeAsU32( ref pc );
						CilMetadataTokenInfo stobjMeta = box.metadatas[typeToken];
						StackElement value = stackBuffer[sp--];
						StackElement addr = stackBuffer[sp--];
						object obj = ( stobjMeta.nativeType != null && value.type < StackType.Object ) ?
							value.CoerceToObject( stobjMeta.nativeType ) :
							value.AsObject( box );

						if( addr.type == StackType.Address )
						{
							addr.DereferenceLoadAddress( obj );
						}
						else if( addr.type == StackType.NativeHandle )
						{
							addr.DereferenceLoadNativeHandle( box, obj );
						}
						else
						{
							throw new CilboxInterpreterRuntimeException("Invalid stack type for stobj instruction", parentClass.className, methodName, pc);
						}
						break;
					}
					case 0x8C: // box (This pulls off a type)
					{
						uint otyp = BytecodeAsU32( ref pc );
						CilMetadataTokenInfo meta = box.metadatas[otyp];
						if( meta.cilboxEnum != null )
							stackBuffer[sp].LoadObject( meta.cilboxEnum.BoxValue( stackBuffer[sp].l ) );
						else if( meta.nativeType != null && meta.nativeType.IsEnum )
							stackBuffer[sp].LoadObject( Enum.ToObject(meta.nativeType, stackBuffer[sp].l) );
						else if( meta.nativeTypeIsStackType && meta.nativeType != null )
							stackBuffer[sp].LoadObject( stackBuffer[sp].CoerceToObject( meta.nativeType ) );
						else
							stackBuffer[sp].LoadObject( stackBuffer[sp].AsObject() );
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
						{
							// If it's a native enum, it will try to create an array of the enum type.
							// We want to force it to create an array of the underlying type (enum stack elements are always stored as their underlying type)
							Type elemType = arrMeta.nativeType.IsEnum ? Enum.GetUnderlyingType( arrMeta.nativeType ) : arrMeta.nativeType;
							stackBuffer[sp].LoadObject( Array.CreateInstance( elemType, size ) );
						}
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
						int index = stackBuffer[sp--].i;
						if (stackBuffer[sp].o == null)
						{
							interpretedThrow(pc - 1, new NullReferenceException());
							break;
						}
						Array arr = (Array)stackBuffer[sp].o;
						if (index < 0 || index >= arr.Length)
						{
							interpretedThrow(pc - 1, new IndexOutOfRangeException());
							break;
						}
						switch( b - 0x90 )
						{
						// The opcode determines the stack type.  The hard casts also accept CLR-compatible
						// arrays (signedness variants like int[]/uint[], enum arrays as their underlying
						// type), so they need no per-array-type handling.
						case 0: stackBuffer[sp].LoadSByte( ((sbyte[])arr)[index] ); break; // ldelem.i1
						case 1: stackBuffer[sp].LoadByte( ((byte[])arr)[index] ); break; // ldelem.u1
						case 2: stackBuffer[sp].LoadShort( ((short[])arr)[index] ); break; // ldelem.i2
						case 3: // ldelem.u2 (used for UInt16/Char element arrays; char[] does not cast to ushort[])
							if( arr is char[] charArr )
								stackBuffer[sp].LoadUshort( charArr[index] );
							else
								stackBuffer[sp].LoadUshort( ((ushort[])arr)[index] );
							break;
						case 4: stackBuffer[sp].LoadInt( ((int[])arr)[index] ); break; // ldelem.i4
						case 5: stackBuffer[sp].LoadUint( ((uint[])arr)[index] ); break; // ldelem.u4
						case 6: // ldelem.i8: the only variant without an unsigned counterpart, emitted for
							// long[] and ulong[] alike.  A (ulong[]) cast also succeeds on a long[], so
							// only here the element type must decide the signedness of the stack type.
							if( Type.GetTypeCode( arr.GetType().GetElementType() ) == TypeCode.UInt64 )
								stackBuffer[sp].LoadUlong( ((ulong[])arr)[index] );
							else
								stackBuffer[sp].LoadLong( ((long[])arr)[index] );
							break;
						case 7: stackBuffer[sp].LoadNint( ((nint[])arr)[index] ); break; // ldelem.i
						case 8: stackBuffer[sp].LoadFloat( ((float[])arr)[index] ); break; // ldelem.r4
						case 9: stackBuffer[sp].LoadDouble( ((double[])arr)[index] ); break; // ldelem.r8
						}
						break;
					}
					case 0x9a: // Ldelem_Ref
					{
						int index = stackBuffer[sp--].i;
						StackElement arrSE = stackBuffer[sp--];
						if (arrSE.o == null)
						{
							interpretedThrow(pc - 1, new NullReferenceException());
							break;
						}
						Array a = (Array)(arrSE.o);
						if (index < 0 || index >= a.Length)
						{
							interpretedThrow(pc - 1, new IndexOutOfRangeException());
							break;
						}
						stackBuffer[++sp].LoadObject( a.GetValue(index) );
						break;
					}
					case 0xa3: // ldelem <typeTok>
					{
						uint otyp = BytecodeAsU32( ref pc );
						int index = stackBuffer[sp--].i;
						StackElement arrSE = stackBuffer[sp--];

						if (arrSE.o == null)
						{
							interpretedThrow(pc - 1, new NullReferenceException());
							break;
						}
						Array array = (Array)arrSE.AsObject();
						if (index < 0 || index >= array.Length)
						{
							interpretedThrow(pc - 1, new IndexOutOfRangeException());
							break;
						}

						CilMetadataTokenInfo elemMeta = box.metadatas[otyp];
						var value = array.GetValue( index );
						var targetElementType = elemMeta.nativeType;

						if( targetElementType != null && targetElementType != typeof(object) && value != null && !targetElementType.IsInstanceOfType( value ) )
						{
							value = Convert.ChangeType( value, targetElementType );
						}

						stackBuffer[++sp].LoadObject( value );
						break;
					}
					case 0x9b: case 0x9c: case 0x9d: case 0x9e: case 0x9f: // stelem
					case 0xa0: case 0xa1: case 0xa2:
					{
						StackElement valSE = stackBuffer[sp--];
						int index = stackBuffer[sp--].i;
						StackElement arrSE = stackBuffer[sp--];
						if (arrSE.o == null)
						{
							interpretedThrow(pc - 1, new NullReferenceException());
							break;
						}
						Array asArr = (Array)(arrSE.o);
						if (index < 0 || index >= asArr.Length)
						{
							interpretedThrow(pc - 1, new IndexOutOfRangeException());
							break;
						}
						switch( b - 0x9b )
						{
						case 0: asArr.SetValue( (nint)valSE.l, index ); break; // stelem.i
						case 1: asArr.SetValue( (byte)(SByte)valSE.i, index ); break; // stelem.i1
						case 2: // stelem.i2 (used for Int16/UInt16/Char element arrays)
							if( arrSE.o is ushort[] ushortArr )
								ushortArr[index] = (ushort)valSE.u;
							else if( arrSE.o is char[] charArr )
								charArr[index] = (char)valSE.u;
							else
								asArr.SetValue( (short)valSE.i, index );
							break;
						case 3: // stelem.i4 (used for Int32/UInt32 element arrays)
							if( arrSE.o is uint[] uintArr )
								uintArr[index] = valSE.u;
							else
								asArr.SetValue( valSE.i, index );
							break;
						case 4: // stelem.i8 (used for Int64/UInt64 element arrays; SetValue can't
							// put a boxed long into a ulong[], so store through the array cast)
							if( Type.GetTypeCode( asArr.GetType().GetElementType() ) == TypeCode.UInt64 )
								((ulong[])asArr)[index] = valSE.e;
							else
								((long[])asArr)[index] = valSE.l;
							break;
						case 5: ((float[])arrSE.AsObject())[index] = valSE.f; break; // stelem.r4
						case 6: ((double[])arrSE.AsObject())[index] = valSE.d; break; // stelem.r8
						case 7: ((object[])arrSE.AsObject())[index] = valSE.AsObject(); break; // stelem.ref
						}
						break;
					}
					case 0xa4: // stelem <typeTok>
					{
						uint otyp = BytecodeAsU32( ref pc );
						StackElement valSE = stackBuffer[sp--];
						int index = stackBuffer[sp--].i;
						StackElement arrSE = stackBuffer[sp--];
						if (arrSE.o == null)
						{
							interpretedThrow(pc - 1, new NullReferenceException());
							break;
						}
						Array array = (Array)arrSE.AsObject();
						if (index < 0 || index >= array.Length)
						{
							interpretedThrow(pc - 1, new IndexOutOfRangeException());
							break;
						}
						CilMetadataTokenInfo elemMeta = box.metadatas[otyp];
						object value;
						if( elemMeta.nativeTypeIsCilboxProxy || elemMeta.nativeType == null )
						{
							// This actually gets the value in valSE, and converts it to the int/float/native handle, etc. based on "this" box.
							value = valSE.AsObject( box );
						}
						else
						{
							value = valSE.AsObject();
						}

						Type targetElementType = array.GetType().GetElementType();
						if( targetElementType != null && targetElementType != typeof(object) && value != null && !targetElementType.IsInstanceOfType( value ) )
						{
							if( targetElementType.IsEnum )
							{
								value = Enum.ToObject( targetElementType, value );
							}
							else
							{
								if( value.GetType().IsEnum )
									value = Convert.ChangeType( value, Enum.GetUnderlyingType( value.GetType() ) );
								value = Convert.ChangeType( value, targetElementType );
							}
						}

						array.SetValue( value, index );
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
						else if( metaType.nativeType != null && !metaType.nativeType.IsEnum )
						{
							object ubObj = stackBuffer[sp].AsObject();
							if( ubObj != null && !metaType.nativeType.IsInstanceOfType( ubObj ) )
								throw new InvalidCastException( $"unbox.any: {ubObj.GetType()} is not assignable to {metaType.nativeType}" );
							stackBuffer[sp].LoadObject( ubObj );
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
									case StackType.Object:
										// cgt.un on object refs is how Roslyn emits `x != null` (an identity test, not an ordering); mirror ceq's guarded object case, fail on anything else
										if( sa.type == StackType.Object && sb.type == StackType.Object )
											stackBuffer[sp].LoadInt( sa.o != sb.o ? 1 : 0 );
										else
											throw new CilboxInterpreterRuntimeException($"CGT.UN Unimplemented type promotion unequal {sa.type} != {sb.type}", parentClass.className, methodName, pc);
										break;
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
						case 0x07: // ldvirtftn: function pointer to the receiver's runtime-class override (for a delegate over a virtual method)
						{
							// ldvirtftn is like ldftn (0x06) but resolves the target by the receiver's runtime type: it pushes a
							// pointer to the virtual override of <method> for the object on top of the stack (used to build a delegate).
							uint bcv = BytecodeAsU32( ref pc );
							CilMetadataTokenInfo dtv = box.metadatas[bcv];
							// We can only make pointers to methods that live inside this cilbox.
							if( dtv.isNative )
								throw new CilboxInterpreterRuntimeException($"Cannot create references to functions outside this cilbox ({dtv.Name})", parentClass.className, methodName, pc);
							// Start from the token's method (the base declaration) -- the fallback if the receiver doesn't override it.
							CilboxClass vClass = box.classesList[dtv.interpretiveMethodClass];
							CilboxMethod vMethod = vClass.methods[dtv.interpretiveMethod];
							// Pop the receiver and find its runtime class.
							object vThis = stackBuffer[sp--].AsObject( box );
							CilboxClass vrtClass = (vThis as CilboxProxy)?.cls;
							if( vrtClass == null ) vrtClass = (vThis as CilboxHeapInstance)?.cls;
							// If the runtime class overrides the method (same signature, more-derived), point vMethod at the override.
							if( vrtClass != null && vrtClass != vClass && vMethod != null &&
								vrtClass.methodFullSignatureToIndex.TryGetValue( vMethod.fullSignature, out uint vmidx ) )
							{
								vMethod = vrtClass.methods[(int)vmidx];
							}
							// Push the resolved method as the function pointer (later consumed to build the delegate).
							stackBuffer[++sp].LoadObject( vMethod );
							break;
						}
						case 0x15: // initobj <typeTok>
						{
							uint typeToken = BytecodeAsU32( ref pc );
							CilMetadataTokenInfo initMeta = box.metadatas[typeToken];
							StackElement addr = stackBuffer[sp--];
							object defaultValue = CreateDefaultValueForType( initMeta );

							if( addr.type == StackType.Address )
							{
								addr.DereferenceLoadAddress( defaultValue );
							}
							else if( addr.type == StackType.NativeHandle )
							{
								addr.DereferenceLoadNativeHandle( box, defaultValue );
							}
							else
							{
								throw new CilboxInterpreterRuntimeException("Invalid stack type for initobj instruction", parentClass.className, methodName, pc);
							}
							break;
						}
						case 0x16: // constrained.
							constrainedMeta = box.metadatas[BytecodeAsU32( ref pc )];
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
#if UNITY_EDITOR
			perfMarkerInterpret.End();
#endif

			//box.InterpreterExit();

			return ( sp == -1 ) ? StackElement.nil : stackBuffer[sp--];

			object CreateDefaultValueForType( CilMetadataTokenInfo typeMeta )
			{
				if( typeMeta.nativeTypeIsCilboxProxy )
				{
					if( !box.classes.TryGetValue( typeMeta.Name, out int classId ) )
						throw new CilboxInterpreterRuntimeException($"Could not find internal type for initobj: {typeMeta.Name}", parentClass.className, methodName, pc);
					return CreateDefaultInternalObject( box.classesList[classId] );
				}

				if( typeMeta.nativeType != null )
				{
					if( !typeMeta.nativeType.IsValueType )
						return null;
					try
					{
						return Activator.CreateInstance( typeMeta.nativeType );
					}
					catch
					{
						return null;
					}
				}

				return null;
			}

			CilboxHeapInstance CreateDefaultInternalObject( CilboxClass cls )
			{
				CilboxHeapInstance newObj = new CilboxHeapInstance();
				newObj.className = cls.className;
				newObj.cls = cls;
				newObj.fields = new StackElement[cls.instanceFieldNames.Length];

				for( int i = 0; i < cls.instanceFieldNames.Length; i++ )
				{
					Type fieldType = cls.instanceFieldTypes[i];
					if( fieldType == null )
					{
						newObj.fields[i].LoadObject( null );
						continue;
					}

					StackType fieldStackType = StackElement.StackTypeFromType( fieldType );
					if( fieldStackType < StackType.Object )
					{
						newObj.fields[i].type = fieldStackType;
					}
					else if( fieldType.IsValueType )
					{
						try
						{
							newObj.fields[i].LoadObject( Activator.CreateInstance( fieldType ) );
						}
						catch
						{
							newObj.fields[i].LoadObject( null );
						}
					}
					else
					{
						newObj.fields[i].LoadObject( null );
					}
				}

				return newObj;
			}

			void interpretedThrow(int currentInstruction, object thrownObj)
			{
				sp = -1;
				exceptionRegister = new StackElement() { type = StackType.Object, o = thrownObj };
				if (!hasExceptionClauses)
				{
					throw new CilboxUnhandledInterpretedException("Exception thrown with no handlers: " + (thrownObj?.ToString() ?? "(null)"), thrownObj, parentClass.className, methodName, currentInstruction);
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
						if (!IsInternalObjectInstanceOf(thrownObj, c.CatchTypeName))
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
					throw new CilboxUnhandledInterpretedException("No handlers matched exception: " + (thrownObj?.ToString() ?? "(null)"), thrownObj, parentClass.className, methodName, currentInstruction);
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

		private static bool TryGetInternalObjectData( object candidate, out string className, out StackElement[] fields )
		{
			if( candidate is CilboxProxy proxy )
			{
				className = proxy.className;
				fields = proxy.fields;
				return true;
			}
			if( candidate is CilboxHeapInstance heap )
			{
				className = heap.className;
				fields = heap.fields;
				return true;
			}

			className = string.Empty;
			fields = Array.Empty<StackElement>();
			return false;
		}

		private static bool IsInternalObjectInstanceOf( object candidate, string className )
		{
			return TryGetInternalObjectData( candidate, out string candidateClassName, out _ ) && candidateClassName == className;
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

		public String [] baseClassNames = new String[0];

		public bool LoadCilboxClass( Cilbox box, SerializedClass sc )
		{
			this.box = box;
			this.className = sc.className;
			this.baseClassNames = sc.baseClassNames;

			int sfnum = sc.staticFields.Length;
			this.staticFields = new object[sfnum];
			staticFieldNames = new String[sfnum];
			staticFieldTypes = new Type[sfnum];
			for( int k = 0; k < sfnum; k++ )
			{
				staticFieldNames[k] = sc.staticFields[k].name;
				Type t = staticFieldTypes[k] = box.usage.GetNativeTypeFromDescriptor( sc.staticFields[k].type );
				this.staticFields[k] = CilboxUtil.DeserializeDataForProxyField( t, "" );
			}

			int ifnum = sc.instanceFields.Length;
			instanceFieldNames = new String[ifnum];
			instanceFieldTypes = new Type[ifnum];
			for( int k = 0; k < ifnum; k++ )
			{
				instanceFieldNames[k] = sc.instanceFields[k].name;
				instanceFieldTypes[k] = box.usage.GetNativeTypeFromDescriptor( sc.instanceFields[k].type );
			}

			int mnum = sc.methods.Length;
			methods = new CilboxMethod[mnum];
			methodNameToIndex = new Dictionary< String, uint >();
			methodFullSignatureToIndex = new Dictionary< String, uint >();
			for( uint k = 0; k < mnum; k++ )
			{
				methods[k] = new CilboxMethod();
				methods[k].Load( this, sc.methods[k] );
				methodNameToIndex[methods[k].methodName] = k;
				methodFullSignatureToIndex[methods[k].fullSignature] = k;
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

	public class CilboxEnum
	{
		public string enumName;
		public StackType underlyingType;
		public Dictionary<long, string> valueToName;

		public string GetName(long value)
		{
			if (valueToName.TryGetValue(value, out string name))
				return name;
			return value.ToString();
		}

		public BoxedCilboxEnum BoxValue(long value)
		{
			return new BoxedCilboxEnum(this, value);
		}
	}

	public class BoxedCilboxEnum
	{
		public CilboxEnum enumDef;
		public long value;

		public BoxedCilboxEnum(CilboxEnum enumDef, long value)
		{
			this.enumDef = enumDef;
			this.value = value;
		}

		public override string ToString() => enumDef.GetName(value);
		public override bool Equals(object obj) => obj is BoxedCilboxEnum other && enumDef == other.enumDef && value == other.value;
		public override int GetHashCode() => value.GetHashCode();
	}

	public class CilMetadataTokenInfo
	{
		public CilMetadataTokenInfo( MetaTokenType type ) { this.type = type; }
		public MetaTokenType type;
		public bool isValid;
		public int fieldIndex; // Only used for fields of cilbox objects.
		public int interpretiveFieldClass = -1;
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
		public Type[] nativeParameterTypes;
		public bool nativeIsVoid;
		public int interpretiveMethod; // If nativeToken is 0, then it's a interpreted call.
		public int interpretiveMethodClass; // If nativeToken is 0, then it's a interpreted call class

		// For string, type = 0x70, string is in fields[0] (escaped) and Name, unescaped.
		// For methods, type = 10, Declaring Type is in fields[0], Method is in fields[1], Full name is in fields[2] assembly name is in fields[3]
		// For fields, type = 4, Declaring Type is in fields[0], Name is in fields[1], Type is in fields[2]
		//public String [] fields;

		public String Name = "<UNKNOWN>";
		public String declaringTypeName;
		//public String ToString() { return Name; }

		public CilboxEnum cilboxEnum;

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
		public Dictionary<string, CilboxEnum> cilboxEnums;
		public String assemblyData;
		private bool initialized = false;

		public static readonly int defaultStackSize = 1024;

		public bool showFunctionProfiling;
		public bool exportDebuggingData;
		public bool verboseLogging = false;
		public CilboxUsage usage;

		public String disabledReason = "";
		public bool disabled = false;

		[SerializeField][FormerlySerializedAs("timeoutLengthUs")] private long desiredTimeoutLengthUs = 500000; // 500ms Can be changed by specific Cilbox instance.
		public long timeoutLengthUs
		{
			get => desiredTimeoutLengthUs;
			set => desiredTimeoutLengthUs = Math.Min(value, MaxTimeoutLengthUs);
		}

		public virtual long MaxTimeoutLengthUs => 1000000; // 1 second. Can be overridden by specific Cilbox application.

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

		abstract public bool CheckMethodAllowed( out MethodInfo mi, Type declaringType, String name, SerializedTypeDescriptor [] parametersIn, SerializedTypeDescriptor [] genericArgumentsIn, String fullSignature );
		abstract public bool CheckTypeAllowed( String sType );
		abstract public bool CheckFieldAllowed( String sType, String sFieldName );
		abstract public bool GetTypeOverride( String sType, out Type t );

		public delegate void CilboxDisabledEvent( Cilbox box, string reason );

		public static CilboxDisabledEvent OnCilboxDisabled;

		public void ForceReinit()
		{
			initialized = false;
		}

		public void BoxInitialize( bool bSimulate = false )
		{
#if UNITY_EDITOR
			var pfm = new ProfilerMarker( "Initialize Cilbox" );
			using var pfmScope = pfm.Auto();
#endif

			if( initialized ) return;
			initialized = true;
			//Debug.Log( "Cilbox Initialize Metadata:" + assemblyData.Length );
			timeoutLengthUs = desiredTimeoutLengthUs; // make sure min is applied once.

			SerializedAssembly assembly = SerializedAssembly.DeserializeString( assemblyData );
			SerializedClass[] classData = assembly.classes;
			SerializedMetadataToken[] metaData = assembly.metadata;

			metadatas = new CilMetadataTokenInfo[metaData.Length + 1]; // element 0 is invalid.
			metadatas[0] = new CilMetadataTokenInfo( 0 );
			metadatas[0].Name = "<INVALID>";

			int clsid = 0;
			classes = new Dictionary< String, int >();
			classesList = new CilboxClass[classData.Length];
			foreach( var sc in classData )
			{
				classesList[clsid] = new CilboxClass();
				classes[sc.className] = clsid;
				clsid++;
			}

			// Actually load classes in a 2nd pass so we know which classes are Cilbox types first
			for (int i = 0; i < clsid; i++)
			{
				classesList[i].LoadCilboxClass( this, classData[i] );
			}

			cilboxEnums = new Dictionary<string, CilboxEnum>();
			if (assembly.enums.Length > 0)
			{
				foreach (var se in assembly.enums)
				{
					CilboxEnum ce = new CilboxEnum();
					ce.enumName = se.enumName;
					ce.underlyingType = StackElement.StackTypeFromType(usage.GetNativeTypeFromDescriptor(se.underlyingType));
					ce.valueToName = new Dictionary<long, string>();
					foreach (var entry in se.values)
					{
						ce.valueToName[entry.value] = entry.name;
					}
					cilboxEnums[se.enumName] = ce;
				}
			}

			foreach( var st in metaData )
			{
				MetaTokenType metatype = (MetaTokenType)st.metaTokenType;
				CilMetadataTokenInfo t = metadatas[st.metaTokenIndex] = new CilMetadataTokenInfo( metatype );

				switch( metatype )
				{
				case MetaTokenType.mtString:
					t.Name = st.stringValue;
					break;
				case MetaTokenType.mtArrayInitializer:
					t.arrayInitializerData = st.arrayInitData;
					break;
				case MetaTokenType.mtField:
					// The type has been "sealed" so-to-speak. In that we have an index for it.
					t.Name = st.name;
					t.declaringTypeName = usage.GetNativeTypeNameFromDescriptor( st.typeDescriptor );
					t.fieldIsStatic = st.isStatic;

					if( st.fieldHasIndex )
					{
						t.fieldIndex = st.fieldIndex;
						if( classes.TryGetValue( t.declaringTypeName, out int fieldClassId ) )
							t.interpretiveFieldClass = fieldClassId;
					}
					else
					{
						bool bAllowed = CheckFieldAllowed( t.declaringTypeName, t.Name );
						if( !bAllowed )
						{
							throw new CilboxException( $"Illegal field reference outside of the cilbox. {t.declaringTypeName}.{t.Name} in meta {st.metaTokenIndex}." );
						}
						t.isFieldWhiteListed = true;

						Type ty = usage.GetNativeTypeFromDescriptor( st.typeDescriptor );
						if( ty == null )
						{
							throw new CilboxException( $"Could not get allowed type for checking field, {t.declaringTypeName} in meta {st.metaTokenIndex}." );
						}

						// We have a type for the declaring type, but, we need a field.
						FieldInfo f = ty.GetField( t.Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.Instance );

						if( f == null )
						{
							throw new CilboxException( $"Could not find field for object type {t.declaringTypeName}.{t.Name} in meta {st.metaTokenIndex}." );
						}

						if( !usage.CheckTypeSecurityRecursive( f.FieldType ) )
						{
							throw new CilboxException( $"Field for {t.declaringTypeName}.{t.Name} in meta {st.metaTokenIndex} of type {f.FieldType.ToString()} not allowed." );
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
					}

					t.isValid = true;
					break;
				case MetaTokenType.mtType:
				{
					SerializedTypeDescriptor td = st.typeDescriptor;
					t.nativeType = usage.GetNativeTypeFromDescriptor( td );
					StackType seType = StackElement.StackTypeFromType( t.nativeType );
					if( seType < StackType.Object )
					{
						t.nativeTypeIsStackType = true;
						t.nativeTypeStackType = seType;
						t.Name = t.nativeType.ToString();

						// Link to CilboxEnum if this was a cilboxable enum serialized by underlying type.
						string origName = td.typeName;
						if (cilboxEnums != null && cilboxEnums.TryGetValue(origName, out CilboxEnum cilboxEnumDef))
						{
							t.cilboxEnum = cilboxEnumDef;
							t.Name = origName;
						}
					}
					else if( t.nativeType != null )
					{
						t.isValid = true;
						t.Name = "Type: " + td.typeName;
					}
					else
					{
						// Maybe it's a type inside our cilbox?
						t.isValid = false;
						foreach( CilboxClass c in classesList )
						{
							if( c.className == td.typeName )
							{
								t.Name = c.className;
								t.nativeTypeIsCilboxProxy = true;
								t.isValid = true;
							}
						}

						if( !t.isValid )
							Debug.LogError( $"Error: Could not find type: {td.typeName}" );
					}
					break;
				}
				case MetaTokenType.mtMethod:
				{
					String name = st.name;
					String fullSignature = st.methodFullSignature;
					bool isStatic = st.isStatic;
					String useAssembly = st.methodAssembly;
					SerializedTypeDescriptor [] genericArguments = st.methodGenericArguments;
					t.Name = "Method: " + name;

					if( usage.OptionallyOverride( name, st.typeDescriptor, fullSignature, isStatic, genericArguments, ref t ) )
					{
						break;
					}

					SerializedTypeDescriptor stDt;
					(name, stDt) = usage.HandleEarlyMethodRewrite( name, st.typeDescriptor, genericArguments );

					string declaringTypeName = t.declaringTypeName = usage.GetNativeTypeNameFromDescriptor( stDt );

					SerializedTypeDescriptor [] parametersSer = st.methodParameters;

					// First, see if this is to a class we are responsible for. Like does it come from _this_ class?
					if( declaringTypeName == null )
					{
						Debug.LogError( $"Error: Could not find internal type in {fullSignature}" );
					}
					else if( classes.TryGetValue( declaringTypeName, out int classid ) )
					{
						CilboxClass matchingClass = classesList[classid];
						uint imid = 0;
						if( matchingClass.methodFullSignatureToIndex.TryGetValue( fullSignature, out imid ) )
						{
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
						Type declaringType = usage.GetNativeTypeFromDescriptor( stDt );
						if( declaringType == null )
							throw new CilboxException( $"Error: Could not find referenced type {useAssembly}/{declaringTypeName}/" );

						MethodBase m = usage.GetNativeMethodFromTypeAndName( declaringType, name, parametersSer, genericArguments, fullSignature );

						if( m != null )
						{
							t.nativeMethod = m;
							t.isNative = true;
							t.isValid = true;
							ParameterInfo[] mp = m.GetParameters();
							Type[] mpt = new Type[mp.Length];
							for( int mpi = 0; mpi < mp.Length; mpi++ )
							{
								mpt[mpi] = mp[mpi].ParameterType;
							}
							t.nativeParameterTypes = mpt;
							t.nativeIsVoid = (m is MethodInfo mInfo) && mInfo.ReturnType == typeof(void);
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

		internal void DisableWithReason(string reason)
		{
			Debug.LogError( reason );
			this.disabledReason = reason;
			this.disabled = true;
			//this.InterpreterExit();
			OnCilboxDisabled?.Invoke(this, reason);
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
			CilboxScenePostprocessor.OnPostprocessScene(scene);
		}
	}

	class CilboxCustomBuildProcessor2 : IPreprocessBuildWithReport
	{
		public int callbackOrder { get { return 0; } }
		public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
		{
			UnityEngine.SceneManagement.Scene activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
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
				activeScene );
			UnityEditor.SceneManagement.EditorSceneManager.SaveScene( activeScene );
		}
	}
	public class CilboxScenePostprocessor {
		//[PostProcessSceneAttribute (2)] This is actually called by IProcessSceneWithReport
		public static void OnPostprocessScene(UnityEngine.SceneManagement.Scene? scene) {

			ProfilerMarker perf = new ProfilerMarker("Initial Setup"); perf.Begin();

			MonoBehaviour [] allBehavioursThatNeedCilboxing = CilboxUtil.GetAllBehavioursThatNeedCilboxing(scene);
			Debug.Log( $"Postprocessing scene. Cilbox scripts to do: {allBehavioursThatNeedCilboxing.Length}" );
			if( allBehavioursThatNeedCilboxing.Length == 0 )
			{
				perf.End();
				return;
			}


			Dictionary< uint, SerializedMetadataToken > assemblyMetadata = new Dictionary< uint, SerializedMetadataToken >();
			Dictionary< uint, String > originalMetaToFriendlyName = new Dictionary< uint, String >();
			// This is used for remapping tokens in the bytecode to point to our own metadata.
			// Since tokens are per-assembly, we need prevent collisions by keying per assembly as well.
			Dictionary< (System.Reflection.Assembly, int), uint> assemblyMetadataReverseOriginal = new Dictionary< (System.Reflection.Assembly, int), uint >();

			uint mdcount = 1; // token 0 is invalid.
			int bytecodeLength = 0;
			List<SerializedClass> classes = new List<SerializedClass>();
			Dictionary< String, SerializedMethod[] > allClassMethods = new Dictionary< String, SerializedMethod[] >();

			perf.End(); perf = new ProfilerMarker( "Main Getting Types" ); perf.Begin();

			// Make sure the cilbox script is in use in the scene or we have no scene loaded.
			List<System.Type> TypesInUseInSceneList = new List<System.Type>();
			HashSet<System.Type> TypesInUseInScene = new HashSet<System.Type>();;


			System.Reflection.Assembly [] assys = AppDomain.CurrentDomain.GetAssemblies();

			if( scene != null )
			{
				GameObject[] rootObjects =((UnityEngine.SceneManagement.Scene) scene).GetRootGameObjects();
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
								TypesInUseInScene.Add( t );
								TypesInUseInSceneList.Add( t );
							}
						}
					}
				}
			}
			else
			{
				// Collect ALL cilboxable classes if no scene active.
				foreach( System.Reflection.Assembly proxyAssembly in assys )
				{
					foreach (Type t in proxyAssembly.GetTypes())
					{
						if( CilboxUtil.HasCilboxableAttribute( t ) && !TypesInUseInScene.Contains( t ) )
						{
							TypesInUseInScene.Add( t );
							TypesInUseInSceneList.Add( t );
						}
					}
				}
			}
			{
				for( int typeIndex = 0; typeIndex < TypesInUseInSceneList.Count; typeIndex++ )
				{
					Type type = TypesInUseInSceneList[typeIndex];
					System.Reflection.Assembly proxyAssembly = type.Assembly;

					if( !CilboxUtil.HasCilboxableAttribute( type ) )
						continue;
					if( type.IsEnum )
						continue;

					ProfilerMarker perfType = new ProfilerMarker(type.ToString()); perfType.Begin();

					List< SerializedMethod > methods = new List< SerializedMethod >();

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

							SerializedMethod serializedMethod = new SerializedMethod();
							serializedMethod.methodName = m.Name;
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
												if( !assemblyMetadataReverseOriginal.TryGetValue( (proxyAssembly, (int)operand), out writebackToken ) )
												{
													writebackToken = mdcount;
													// Special <PrivateImplementationDetails>+__StaticArrayInitTypeSize=24 instance.
													FieldInfo rf = proxyAssembly.ManifestModule.ResolveField( (int)operand );
													// Extract raw bytes from initializer type
													byte[] bytes = new byte[System.Runtime.InteropServices.Marshal.SizeOf(rf.FieldType)];
													GCHandle h = GCHandle.Alloc(rf.GetValue(null), GCHandleType.Pinned);
													Marshal.Copy(h.AddrOfPinnedObject(), bytes, 0, bytes.Length);
													h.Free();
													// Now, encode our array initializer.
													SerializedMetadataToken thisMeta = new SerializedMetadataToken
													{
														metaTokenIndex = (int)mdcount,
														metaTokenType = (int)MetaTokenType.mtArrayInitializer,
														arrayInitData = bytes,
													};
													originalMetaToFriendlyName[mdcount] = rf.Name;
													assemblyMetadata[mdcount++] = thisMeta;
												}
												break;
										/*
											case 0x02: // Inline Token for Type (typically used with typeof())
												if( !assemblyMetadataReverseOriginal.TryGetValue( (proxyAssembly, (int)operand), out writebackToken ) )
												{
													// TODO: Actually investigate this.  See if we really need it.
													writebackToken = mdcount;
													Type ty = proxyAssembly.ManifestModule.ResolveType( (int)operand );
													SerializedMetadataToken thisMeta = new SerializedMetadataToken();
													thisMeta.mid = (int)mdcount;
													thisMeta.metaTokenType = (int)MetaTokenType.mtType;
													thisMeta.typeDescriptor = SerializedTypeDescriptorBuilder.FromNativeType( ty );
													originalMetaToFriendlyName[writebackToken] = ty.FullName;
													assemblyMetadata[mdcount++] = thisMeta;
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
											if( !assemblyMetadataReverseOriginal.TryGetValue( (proxyAssembly, (int)operand), out writebackToken ) )
											{
												writebackToken = mdcount;
												SerializedMetadataToken thisMeta = new SerializedMetadataToken
												{
													metaTokenIndex = (int)mdcount,
													metaTokenType = (int)MetaTokenType.mtString,
													stringValue = proxyAssembly.ManifestModule.ResolveString( (int)operand ),
												};
												originalMetaToFriendlyName[mdcount] = thisMeta.stringValue;
												assemblyMetadata[mdcount++] = thisMeta;
											}
										}
										else if( ot == CilboxUtil.OpCodes.OperandType.InlineMethod )
										{
											if( !assemblyMetadataReverseOriginal.TryGetValue( (proxyAssembly, (int)operand), out writebackToken ) )
											{
												writebackToken = mdcount;
												MethodBase tmb = proxyAssembly.ManifestModule.ResolveMethod( (int)operand );

												SerializedMetadataToken thisMeta = new SerializedMetadataToken();
												thisMeta.metaTokenIndex = (int)mdcount;
												thisMeta.metaTokenType = (int)MetaTokenType.mtMethod;

												// "Generic constructors are not supported in the .NET Framework version 2.0"
												if( !tmb.IsConstructor )
												{
													Type[] templateArguments = tmb.GetGenericArguments();
													if( templateArguments.Length > 0 )
													{
														thisMeta.methodGenericArguments = new SerializedTypeDescriptor[templateArguments.Length];
														for( int a = 0; a < templateArguments.Length; a++ )
															thisMeta.methodGenericArguments[a] = SerializedTypeDescriptorBuilder.FromNativeType( templateArguments[a] );
													}
												}

												// If we are using another type here, make sure it gets in our list.
												// We only need to do this here, because either the script is in-use or we are creating it.
												if( !TypesInUseInScene.Contains( tmb.DeclaringType ) )
												{
													TypesInUseInScene.Add( tmb.DeclaringType );
													TypesInUseInSceneList.Add( tmb.DeclaringType );
												}

												thisMeta.typeDescriptor = SerializedTypeDescriptorBuilder.FromNativeType( tmb.DeclaringType );
												thisMeta.name = tmb.Name;

												System.Reflection.ParameterInfo[] parameterInfos = tmb.GetParameters();
												if( parameterInfos.Length > 0 )
												{
													thisMeta.methodParameters = new SerializedTypeDescriptor[parameterInfos.Length];
													for( var j = 0; j < parameterInfos.Length; j++ )
													{
														thisMeta.methodParameters[j] = SerializedTypeDescriptorBuilder.FromNativeType( parameterInfos[j].ParameterType );
													}
												}
												thisMeta.methodFullSignature = tmb.ToString();
												thisMeta.isStatic = tmb.IsStatic;
												thisMeta.methodAssembly = tmb.DeclaringType.Assembly.GetName().Name;
												originalMetaToFriendlyName[writebackToken] = tmb.DeclaringType.ToString() + "." + tmb.ToString();
												assemblyMetadata[mdcount++] = thisMeta;
											}
										}
										else if( ot == CilboxUtil.OpCodes.OperandType.InlineField )
										{
											if( !assemblyMetadataReverseOriginal.TryGetValue( (proxyAssembly, (int)operand), out writebackToken ) )
											{
												writebackToken = mdcount;
												FieldInfo rf = proxyAssembly.ManifestModule.ResolveField( (int)operand );

												// Field references can pull in nested/internal value types that are not
												// directly attached to a scene object. Make sure we serialize those
												// declaring types just like we already do for referenced methods.
												if( !TypesInUseInScene.Contains( rf.DeclaringType ) )
												{
													TypesInUseInScene.Add( rf.DeclaringType );
													TypesInUseInSceneList.Add( rf.DeclaringType );
													changeOperand = true; // We need to rewrite the operand to point to our new metadata for the field, which will have a reference to the declaring type.
												}

												SerializedMetadataToken thisMeta = new SerializedMetadataToken();
												thisMeta.metaTokenIndex = (int)mdcount;
												thisMeta.metaTokenType = (int)MetaTokenType.mtField;
												thisMeta.typeDescriptor = SerializedTypeDescriptorBuilder.FromNativeType( rf.DeclaringType );
												thisMeta.name = rf.Name;
												thisMeta.isStatic = rf.IsStatic;
												originalMetaToFriendlyName[writebackToken] = rf.Name;
												assemblyMetadata[mdcount++] = thisMeta;
											}
										}
										else if( ot == CilboxUtil.OpCodes.OperandType.InlineType )
										{
											if( !assemblyMetadataReverseOriginal.TryGetValue( (proxyAssembly, (int)operand), out writebackToken ) )
											{
												writebackToken = mdcount;
												Type ty = proxyAssembly.ManifestModule.ResolveType( (int)operand );
												SerializedMetadataToken thisMeta = new SerializedMetadataToken();
												thisMeta.metaTokenIndex = (int)mdcount;
												thisMeta.metaTokenType = (int)MetaTokenType.mtType;
												thisMeta.typeDescriptor = SerializedTypeDescriptorBuilder.FromNativeType( ty );
												assemblyMetadata[mdcount++] = thisMeta;
												originalMetaToFriendlyName[writebackToken] = ty.FullName;
											}
										}
										else
											changeOperand = false;

										if( changeOperand )
										{
											i = backupi;
											assemblyMetadataReverseOriginal[(proxyAssembly, (int)operand)] = writebackToken;
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
							serializedMethod.body = byteCode;

							IList<ExceptionHandlingClause> exceptions = mb.ExceptionHandlingClauses;
							if( exceptions.Count > 0 )
							{
								SerializedExceptionHandler[] excArray = new SerializedExceptionHandler[exceptions.Count];
								for( int k = 0; k < exceptions.Count; k++ )
								{
									ExceptionHandlingClause c = exceptions[k];
									SerializedExceptionHandler seh =  new SerializedExceptionHandler();
									seh.flags = (int)c.Flags;
									seh.tryOffset = c.TryOffset;
									seh.tryLength = c.TryLength;
									seh.handlerOffset = c.HandlerOffset;
									seh.handlerLength = c.HandlerLength;

									if( c.Flags == ExceptionHandlingClauseOptions.Clause && c.CatchType != null )
									{
										seh.hasCatchType = true;
										seh.catchType = SerializedTypeDescriptorBuilder.FromNativeType( c.CatchType );
									}
									excArray[k] = seh;
								}
								serializedMethod.exceptionHandlers = excArray;
							}

							SerializedField[] localVars = new SerializedField[mb.LocalVariables.Count];
							for( int i = 0; i < mb.LocalVariables.Count; i++ )
							{
								LocalVariableInfo lvi = mb.LocalVariables[i];
								SerializedField local = new SerializedField();
								local.name = lvi.ToString();
								local.type = SerializedTypeDescriptorBuilder.FromNativeType( lvi.LocalType );
								localVars[i] = local;
							}
							serializedMethod.locals = localVars;

							ParameterInfo [] parameters = m.GetParameters();

							SerializedField[] parameterList = new SerializedField[parameters.Length];
							for( int i = 0; i < parameters.Length; i++ )
							{
								SerializedField tpi = new SerializedField();
								tpi.name = parameters[i].Name;
								tpi.type = SerializedTypeDescriptorBuilder.FromNativeType( parameters[i].ParameterType );
								parameterList[i] = tpi;
							}
							serializedMethod.parameters = parameterList;
							serializedMethod.maxStack = mb.MaxStackSize;
							bool isCtor = m.IsConstructor;
							serializedMethod.isVoid = m is MethodInfo ? ((MethodInfo)m).ReturnType == typeof(void) : isCtor;
							serializedMethod.isCtor = isCtor;
							serializedMethod.isStatic = m.IsStatic;
							serializedMethod.fullSignature = m.ToString();

							methods.Add( serializedMethod );
							perfMethod.End();
						}
					}

					allClassMethods[type.FullName] = methods.ToArray();
					perfType.End();
				}
			}


			perf.End(); perf = new ProfilerMarker( "Secondary Getting Types" ); perf.Begin();

			// Now that we've iterated through all classes, and collected all possible uses of field IDs,
			// go through the classes again, collecting the fields themselves.

			{
				for( int typeIndex = 0; typeIndex < TypesInUseInSceneList.Count; typeIndex++ )
				{
					Type type = TypesInUseInSceneList[typeIndex];

					if( !CilboxUtil.HasCilboxableAttribute( type ) )
						continue;
					if( type.IsEnum )
						continue;

					ProfilerMarker perfType = new ProfilerMarker(type.ToString()); perfType.Begin();

					SerializedClass serializedClass  = new SerializedClass();
					serializedClass.className = type.FullName;

					// This portion extracts the index information from the current type, and
					// Writes it back in where it was needed above in the Method call.
					//
					for( int lst = 0; lst < 2; lst++ )
					{
						List< SerializedField > fields = new List< SerializedField >();
						int sfid = 0;
						FieldInfo[] fi;
						if( lst == 0 )
							fi = type.GetFields( BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static );
						else
							fi = CilboxUtil.GetInstanceFieldsBaseFirst( type );
						foreach( var f in fi )
						{
							SerializedField dictField = new SerializedField();
							dictField.name = f.Name;
							dictField.type = SerializedTypeDescriptorBuilder.FromNativeType( f.FieldType );
							fields.Add( dictField );

							// Fill in our metadata with a class-specific field ID, if this field ID was used in code anywhere.
							if( assemblyMetadataReverseOriginal.TryGetValue((type.Assembly, f.MetadataToken), out uint mdid) )
							{
								assemblyMetadata[mdid].fieldHasIndex = true;
								assemblyMetadata[mdid].fieldIndex = sfid;
							}
							sfid++;
						}
						if( lst == 0 )
							serializedClass.staticFields = fields.ToArray();
						else
							serializedClass.instanceFields = fields.ToArray();
					}

					serializedClass.methods = allClassMethods[type.FullName];

					// Only [Cilboxable] ancestors are recorded: a native/prohibited base is never emitted, so GetComponent<T>
					// base-matching can never name a non-sandboxed class (and a match only ever yields an interpreted proxy).
					List< string > baseClassChain = new List< string >();
					for( Type bt = type.BaseType; bt != null && bt != typeof( UnityEngine.MonoBehaviour ) && bt != typeof( object ); bt = bt.BaseType )
						if( CilboxUtil.HasCilboxableAttribute( bt ) )
							baseClassChain.Add( bt.FullName );
					serializedClass.baseClassNames = baseClassChain.ToArray();
					classes.Add(serializedClass);
					perfType.End();
				}
			}

			perf.End(); perf = new ProfilerMarker( "Assembling" ); perf.Begin();

			List<SerializedEnum> enums = new List<SerializedEnum>();
			foreach( System.Reflection.Assembly proxyAssembly in assys )
			{
				foreach (Type type in proxyAssembly.GetTypes())
				{
					if (!type.IsEnum || !CilboxUtil.HasCilboxableAttribute(type))
						continue;
					SerializedEnum serializedEnum  = new SerializedEnum();
					serializedEnum.enumName = type.FullName;
					serializedEnum.underlyingType = SerializedTypeDescriptorBuilder.FromNativeType(type.GetEnumUnderlyingType());
					string[] names = Enum.GetNames(type);
					Array values = Enum.GetValues(type);
					SerializedEnumValue[] entries = new  SerializedEnumValue[names.Length];
					for (int i = 0; i < names.Length; i++)
					{
						SerializedEnumValue entry = new SerializedEnumValue();
						entry.name = names[i];
						entry.value = Convert.ToInt64(values.GetValue(i));
						entries[i] = entry;
					}
					serializedEnum.values = entries;
					enums.Add(serializedEnum);
				}
			}

			SerializedAssembly assembly = new SerializedAssembly
			{
				classes = classes.ToArray(),
				metadata = assemblyMetadata.Values.ToArray(),
				enums = enums.ToArray(),
			};

			perf.End(); perf = new ProfilerMarker( "Serializing" ); perf.Begin();

			String sAllAssemblyData = assembly.SerializeString();

			perf.End(); perf = new ProfilerMarker( "Checking If Assembly Changed" ); perf.Begin();

			Cilbox [] se = Resources.FindObjectsOfTypeAll(typeof(Cilbox)) as Cilbox [];
			Cilbox tac = null;

			foreach ( var tacCandidate in se ) {
				if( !tacCandidate.gameObject.scene.IsValid() || EditorUtility.IsPersistent(tacCandidate) )
					continue;

				// When processing a specific scene, assembly data and generated proxies must use a
				// Cilbox from that scene. Falling through to an arbitrary loaded scene mutates live
				// content and can bind proxies to the wrong sandbox/permission model.
				if( scene != null && tacCandidate.gameObject.scene != scene.Value )
					continue;

				tac = tacCandidate;
				break;
			}

			if( tac != null )
			{
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
			foreach (MonoBehaviour m in allBehavioursThatNeedCilboxing)
			{
				GameObject g = m.gameObject;
				// Skip null objects.
				if (m == null)
					continue;
				if( !CilboxUtil.HasCilboxableAttribute( m.GetType() ) )
					continue;

				CilboxProxy p = g.AddComponent<CilboxProxy>();
				refProxies.Add( p );
				refProxiesOrig.Add( m );
				refToProxyMap[m] = p;
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
		OnEnable,
		OnDisable,
		OnDestroy,
		OnTriggerEnter,
		OnTriggerExit,
		OnCollisionEnter,
		OnCollisionExit,
		LateUpdate,
		OnTriggerStay,
		OnCollisionStay,
		OnRenderObject,
		OnWillRenderObject
	}
}
