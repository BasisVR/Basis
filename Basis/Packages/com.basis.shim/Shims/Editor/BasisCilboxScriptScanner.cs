#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Basis.Shims.Editor
{
    /// <summary>One thing a script touches, and how often.</summary>
    internal sealed class CilboxReference
    {
        public MemberInfo Member;
        public Type DeclaringType;
        public string Display;
        public int Count = 1;

        /// <summary>Sort key: the declaring type, then the member.</summary>
        public string SortKey => (DeclaringType?.FullName ?? string.Empty) + "\0" + (Member?.Name ?? string.Empty);
    }

    /// <summary>A problem with the script itself, independent of any whitelist.</summary>
    internal sealed class CilboxScriptIssue
    {
        public string TitleKey;
        public string Detail;
        public bool IsError;
    }

    /// <summary>Everything the analyzer found in one script.</summary>
    internal sealed class CilboxScriptScan
    {
        public Type ScriptType;
        public bool HasCilboxableAttribute;

        public List<CilboxReference> Types = new List<CilboxReference>();
        public List<CilboxReference> Fields = new List<CilboxReference>();
        public List<CilboxReference> Methods = new List<CilboxReference>();

        /// <summary>Other types from the script's own assembly — content, not host API.</summary>
        public List<Type> SameAssembly = new List<Type>();

        public List<CilboxScriptIssue> Issues = new List<CilboxScriptIssue>();

        /// <summary>Methods whose IL could not be read, so their calls are missing from the lists.</summary>
        public int UnreadableMethods;
    }

    /// <summary>
    /// Works out what a script actually touches by walking the IL of its compiled method bodies.
    ///
    /// <para>Reading the method bodies rather than the type's public surface is the whole point: the
    /// sandbox refuses calls, and a call only appears in the body. A signature-level pass would say
    /// a script is fine while every line inside it is blocked.</para>
    ///
    /// <para>Uses nothing but System.Reflection — <c>GetILAsByteArray</c> plus
    /// <c>Module.ResolveMember</c> — so there is no IL library to vendor. The opcode table is read
    /// off <see cref="OpCodes"/> rather than hand-written.</para>
    /// </summary>
    internal static class BasisCilboxScriptScanner
    {
        // Everything CilboxProxy forwards to an interpreted script.
        private static readonly HashSet<string> ForwardedMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake", "Start", "Update", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy",
            "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit",
            "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit",
        };

        // Unity messages a script might reasonably define that the proxy never calls. Kept as a
        // list rather than "anything starting with On" so ordinary helper methods stay quiet.
        private static readonly HashSet<string> UnforwardedMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "LateUpdate", "OnGUI", "OnAnimatorIK", "OnAnimatorMove", "OnParticleCollision",
            "OnParticleTrigger", "OnBecameVisible", "OnBecameInvisible", "OnWillRenderObject",
            "OnPreRender", "OnPostRender", "OnRenderObject", "OnRenderImage", "OnPreCull",
            "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit",
            "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D",
            "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D",
            "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit", "OnMouseOver", "OnMouseDrag",
            "OnValidate", "Reset", "OnDrawGizmos", "OnDrawGizmosSelected",
            "OnJointBreak", "OnControllerColliderHit", "OnTransformChildrenChanged",
            "OnTransformParentChanged", "OnBeforeTransformParentChanged",
        };

        private static readonly HashSet<string> UnsupportedInheritedNetworkFields =
            new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(Basis.BasisNetworkBehaviour.HasNetworkID),
                nameof(Basis.BasisNetworkBehaviour.IsOwnedLocallyOnServer),
                nameof(Basis.BasisNetworkBehaviour.IsOwnedLocallyOnClient),
                nameof(Basis.BasisNetworkBehaviour.CurrentOwnerId),
                nameof(Basis.BasisNetworkBehaviour.currentOwnedPlayer),
            };

        private enum ReceiverKind
        {
            Unknown,
            ScriptInstance,
            Other,
        }

        private static Dictionary<short, OpCode> _opcodes;

        private static void BuildOpcodes()
        {
            if (_opcodes != null) return;
            _opcodes = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(OpCode)) continue;
                var code = (OpCode)field.GetValue(null);
                _opcodes[code.Value] = code;
            }
        }

        public static CilboxScriptScan Scan(Type scriptType)
        {
            var scan = new CilboxScriptScan { ScriptType = scriptType };
            if (scriptType == null) return scan;

            BuildOpcodes();

            foreach (object attribute in scriptType.GetCustomAttributes(false))
            {
                if (attribute.GetType().Name == "CilboxableAttribute" ||
                    attribute.GetType().Name == "Cilboxable")
                {
                    scan.HasCilboxableAttribute = true;
                    break;
                }
            }

            var types = new Dictionary<Type, CilboxReference>();
            var fields = new Dictionary<FieldInfo, CilboxReference>();
            var methods = new Dictionary<MethodBase, CilboxReference>();

            // The script's own class plus anything nested in it is interpreted, not called out to.
            var own = new HashSet<Type> { scriptType };
            foreach (Type nested in scriptType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                own.Add(nested);
            }

            CheckShape(scriptType, scan);

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (Type holder in own)
            {
                // Serialized fields are checked per object slot when the proxy is set up, so their
                // types matter as much as anything the body calls.
                foreach (FieldInfo field in holder.GetFields(all))
                {
                    Record(types, fields, methods, own, scan, field.FieldType, null, null);
                }

                var bodies = new List<MethodBase>();
                bodies.AddRange(holder.GetMethods(all));
                bodies.AddRange(holder.GetConstructors(all));

                foreach (MethodBase body in bodies)
                {
                    foreach (ParameterInfo parameter in body.GetParameters())
                    {
                        Record(types, fields, methods, own, scan, parameter.ParameterType, null, null);
                    }
                    if (body is MethodInfo info && info.ReturnType != typeof(void))
                    {
                        Record(types, fields, methods, own, scan, info.ReturnType, null, null);
                    }

                    if (!WalkBody(body, types, fields, methods, own, scan))
                    {
                        scan.UnreadableMethods++;
                    }
                }
            }

            scan.Types.AddRange(types.Values);
            scan.Fields.AddRange(fields.Values);
            scan.Methods.AddRange(methods.Values);

            AddUnsupportedInheritedNetworkFieldIssue(scriptType, scan);

            scan.Types.Sort((a, b) => string.Compare(a.SortKey, b.SortKey, StringComparison.Ordinal));
            scan.Fields.Sort((a, b) => string.Compare(a.SortKey, b.SortKey, StringComparison.Ordinal));
            scan.Methods.Sort((a, b) => string.Compare(a.SortKey, b.SortKey, StringComparison.Ordinal));
            scan.SameAssembly.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));

            return scan;
        }

        internal static bool UsesUnsupportedInheritedNetworkStateField(
            Type scriptType,
            out string fields)
        {
            List<string> names = FindUnsupportedInheritedNetworkStateFields(scriptType);
            if (names.Count == 0)
            {
                fields = null;
                return false;
            }

            fields = string.Join(", ", names);
            return true;
        }

        private static void AddUnsupportedInheritedNetworkFieldIssue(
            Type scriptType,
            CilboxScriptScan scan)
        {
            List<string> names = FindUnsupportedInheritedNetworkStateFields(scriptType);
            if (names.Count == 0)
            {
                return;
            }

            scan.Issues.Add(new CilboxScriptIssue
            {
                TitleKey = "sdk.cilbox.scan.issue.networkStateField",
                Detail = string.Join(", ", names),
                IsError = true,
            });
        }

        private static List<string> FindUnsupportedInheritedNetworkStateFields(Type scriptType)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (scriptType == null ||
                !typeof(Basis.BasisNetworkBehaviour).IsAssignableFrom(scriptType))
            {
                return new List<string>();
            }

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            var holders = new List<Type> { scriptType };
            holders.AddRange(scriptType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

            for (int holderIndex = 0; holderIndex < holders.Count; holderIndex++)
            {
                Type holder = holders[holderIndex];
                var bodies = new List<MethodBase>();
                bodies.AddRange(holder.GetMethods(all));
                bodies.AddRange(holder.GetConstructors(all));

                for (int methodIndex = 0; methodIndex < bodies.Count; methodIndex++)
                {
                    FindUnsupportedInheritedNetworkStateFields(
                        bodies[methodIndex],
                        scriptType,
                        result);
                }
            }

            var names = new List<string>(result);
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static void FindUnsupportedInheritedNetworkStateFields(
            MethodBase method,
            Type scriptType,
            HashSet<string> result)
        {
            MethodBody body;
            byte[] il;
            try
            {
                body = method.GetMethodBody();
                if (body == null)
                {
                    return;
                }

                il = body.GetILAsByteArray();
                if (il == null || il.Length == 0)
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }

            var stack = new List<ReceiverKind>();
            var locals = new ReceiverKind[body.LocalVariables.Count];
            ParameterInfo[] parameters = method.GetParameters();
            int argumentCount = parameters.Length + (method.IsStatic ? 0 : 1);
            var arguments = new ReceiverKind[argumentCount];

            int argumentOffset = 0;
            if (!method.IsStatic)
            {
                arguments[0] = method.DeclaringType == scriptType
                    ? ReceiverKind.ScriptInstance
                    : ReceiverKind.Other;
                argumentOffset = 1;
            }

            for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
            {
                arguments[parameterIndex + argumentOffset] =
                    IsScriptInstanceType(parameters[parameterIndex].ParameterType, scriptType)
                        ? ReceiverKind.ScriptInstance
                        : ReceiverKind.Other;
            }

            Module module = method.Module;
            Type[] typeArgs = null;
            Type[] methodArgs = null;
            try
            {
                if (method.DeclaringType != null && method.DeclaringType.IsGenericType)
                {
                    typeArgs = method.DeclaringType.GetGenericArguments();
                }
                if (method.IsGenericMethod)
                {
                    methodArgs = method.GetGenericArguments();
                }
            }
            catch (Exception)
            {
                typeArgs = null;
                methodArgs = null;
            }

            int i = 0;
            while (i < il.Length)
            {
                int instructionOffset = i;
                short key;
                byte first = il[i++];
                if (first == 0xFE && i < il.Length)
                {
                    key = unchecked((short)(0xFE00 | il[i++]));
                }
                else
                {
                    key = first;
                }

                if (!_opcodes.TryGetValue(key, out OpCode code))
                {
                    return;
                }

                int operandStart = i;
                int operandSize = OperandSize(code.OperandType, il, operandStart);
                if (operandSize < 0 || operandStart + operandSize > il.Length)
                {
                    return;
                }

                if (TryHandleArgumentOrLocalInstruction(
                        code,
                        il,
                        operandStart,
                        arguments,
                        locals,
                        stack))
                {
                    i += operandSize;
                    continue;
                }

                if (code == OpCodes.Dup)
                {
                    stack.Add(stack.Count > 0 ? stack[stack.Count - 1] : ReceiverKind.Unknown);
                    i += operandSize;
                    continue;
                }

                if (code == OpCodes.Pop)
                {
                    Pop(stack);
                    i += operandSize;
                    continue;
                }

                if (code == OpCodes.Ldfld || code == OpCodes.Ldflda || code == OpCodes.Stfld ||
                    code == OpCodes.Ldsfld || code == OpCodes.Ldsflda || code == OpCodes.Stsfld)
                {
                    FieldInfo field = ResolveField(module, il, operandStart, typeArgs, methodArgs);

                    if (code == OpCodes.Stfld)
                    {
                        Pop(stack);
                        ReceiverKind receiver = Pop(stack);
                        RecordUnsupportedField(field, receiver, result);
                    }
                    else if (code == OpCodes.Ldfld || code == OpCodes.Ldflda)
                    {
                        ReceiverKind receiver = Pop(stack);
                        RecordUnsupportedField(field, receiver, result);
                        stack.Add(FieldResultKind(field, scriptType));
                    }
                    else if (code == OpCodes.Stsfld)
                    {
                        Pop(stack);
                    }
                    else
                    {
                        stack.Add(FieldResultKind(field, scriptType));
                    }

                    i += operandSize;
                    continue;
                }

                if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj)
                {
                    MethodBase called = ResolveMethod(module, il, operandStart, typeArgs, methodArgs);
                    if (called == null)
                    {
                        stack.Clear();
                        i += operandSize;
                        continue;
                    }

                    ParameterInfo[] calledParameters = called.GetParameters();
                    for (int parameterIndex = calledParameters.Length - 1; parameterIndex >= 0; parameterIndex--)
                    {
                        Pop(stack);
                    }

                    if (code != OpCodes.Newobj && !called.IsStatic)
                    {
                        Pop(stack);
                    }

                    if (code == OpCodes.Newobj)
                    {
                        Type constructed = called.DeclaringType;
                        stack.Add(IsScriptInstanceType(constructed, scriptType)
                            ? ReceiverKind.ScriptInstance
                            : ReceiverKind.Other);
                    }
                    else if (called is MethodInfo calledMethod && calledMethod.ReturnType != typeof(void))
                    {
                        stack.Add(IsScriptInstanceType(calledMethod.ReturnType, scriptType)
                            ? ReceiverKind.ScriptInstance
                            : ReceiverKind.Other);
                    }

                    i += operandSize;
                    continue;
                }

                if (code == OpCodes.Castclass || code == OpCodes.Isinst)
                {
                    ReceiverKind value = Pop(stack);
                    stack.Add(value);
                    i += operandSize;
                    continue;
                }

                if (code.FlowControl == FlowControl.Return ||
                    code.FlowControl == FlowControl.Throw ||
                    code == OpCodes.Leave ||
                    code == OpCodes.Leave_S)
                {
                    stack.Clear();
                    i += operandSize;
                    continue;
                }

                ApplyGenericStackEffect(code, stack);

                // Branch targets produced by the C# compiler normally start with a balanced
                // evaluation stack. Clear our symbolic approximation after an unconditional
                // branch so a value from the lexical fall-through cannot be mistaken for the
                // receiver on the target path.
                if (code.FlowControl == FlowControl.Branch)
                {
                    stack.Clear();
                }

                i += operandSize;
            }
        }

        private static bool TryHandleArgumentOrLocalInstruction(
            OpCode code,
            byte[] il,
            int operandStart,
            ReceiverKind[] arguments,
            ReceiverKind[] locals,
            List<ReceiverKind> stack)
        {
            int index;
            if (TryGetLoadArgumentIndex(code, il, operandStart, out index))
            {
                stack.Add(index >= 0 && index < arguments.Length
                    ? arguments[index]
                    : ReceiverKind.Unknown);
                return true;
            }

            if (TryGetStoreArgumentIndex(code, il, operandStart, out index))
            {
                ReceiverKind value = Pop(stack);
                if (index >= 0 && index < arguments.Length)
                {
                    arguments[index] = value;
                }
                return true;
            }

            if (TryGetLoadLocalIndex(code, il, operandStart, out index))
            {
                stack.Add(index >= 0 && index < locals.Length
                    ? locals[index]
                    : ReceiverKind.Unknown);
                return true;
            }

            if (TryGetStoreLocalIndex(code, il, operandStart, out index))
            {
                ReceiverKind value = Pop(stack);
                if (index >= 0 && index < locals.Length)
                {
                    locals[index] = value;
                }
                return true;
            }

            if (code == OpCodes.Ldarga || code == OpCodes.Ldarga_S ||
                code == OpCodes.Ldloca || code == OpCodes.Ldloca_S)
            {
                stack.Add(ReceiverKind.Other);
                return true;
            }

            return false;
        }

        private static bool TryGetLoadArgumentIndex(OpCode code, byte[] il, int operandStart, out int index)
        {
            if (code == OpCodes.Ldarg_0) { index = 0; return true; }
            if (code == OpCodes.Ldarg_1) { index = 1; return true; }
            if (code == OpCodes.Ldarg_2) { index = 2; return true; }
            if (code == OpCodes.Ldarg_3) { index = 3; return true; }
            if (code == OpCodes.Ldarg_S) { index = il[operandStart]; return true; }
            if (code == OpCodes.Ldarg) { index = BitConverter.ToUInt16(il, operandStart); return true; }
            index = -1;
            return false;
        }

        private static bool TryGetStoreArgumentIndex(OpCode code, byte[] il, int operandStart, out int index)
        {
            if (code == OpCodes.Starg_S) { index = il[operandStart]; return true; }
            if (code == OpCodes.Starg) { index = BitConverter.ToUInt16(il, operandStart); return true; }
            index = -1;
            return false;
        }

        private static bool TryGetLoadLocalIndex(OpCode code, byte[] il, int operandStart, out int index)
        {
            if (code == OpCodes.Ldloc_0) { index = 0; return true; }
            if (code == OpCodes.Ldloc_1) { index = 1; return true; }
            if (code == OpCodes.Ldloc_2) { index = 2; return true; }
            if (code == OpCodes.Ldloc_3) { index = 3; return true; }
            if (code == OpCodes.Ldloc_S) { index = il[operandStart]; return true; }
            if (code == OpCodes.Ldloc) { index = BitConverter.ToUInt16(il, operandStart); return true; }
            index = -1;
            return false;
        }

        private static bool TryGetStoreLocalIndex(OpCode code, byte[] il, int operandStart, out int index)
        {
            if (code == OpCodes.Stloc_0) { index = 0; return true; }
            if (code == OpCodes.Stloc_1) { index = 1; return true; }
            if (code == OpCodes.Stloc_2) { index = 2; return true; }
            if (code == OpCodes.Stloc_3) { index = 3; return true; }
            if (code == OpCodes.Stloc_S) { index = il[operandStart]; return true; }
            if (code == OpCodes.Stloc) { index = BitConverter.ToUInt16(il, operandStart); return true; }
            index = -1;
            return false;
        }

        private static FieldInfo ResolveField(
            Module module,
            byte[] il,
            int operandStart,
            Type[] typeArgs,
            Type[] methodArgs)
        {
            try
            {
                int token = BitConverter.ToInt32(il, operandStart);
                return module.ResolveField(token, typeArgs, methodArgs);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static MethodBase ResolveMethod(
            Module module,
            byte[] il,
            int operandStart,
            Type[] typeArgs,
            Type[] methodArgs)
        {
            try
            {
                int token = BitConverter.ToInt32(il, operandStart);
                return module.ResolveMethod(token, typeArgs, methodArgs);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void RecordUnsupportedField(
            FieldInfo field,
            ReceiverKind receiver,
            HashSet<string> result)
        {
            if (receiver == ReceiverKind.ScriptInstance &&
                field != null &&
                field.DeclaringType == typeof(Basis.BasisNetworkBehaviour) &&
                UnsupportedInheritedNetworkFields.Contains(field.Name))
            {
                result.Add(field.Name);
            }
        }

        private static ReceiverKind FieldResultKind(FieldInfo field, Type scriptType)
        {
            return field != null && IsScriptInstanceType(field.FieldType, scriptType)
                ? ReceiverKind.ScriptInstance
                : ReceiverKind.Other;
        }

        private static bool IsScriptInstanceType(Type type, Type scriptType)
        {
            if (type == null || scriptType == null)
            {
                return false;
            }

            while (type.IsByRef || type.IsPointer)
            {
                type = type.GetElementType();
                if (type == null)
                {
                    return false;
                }
            }

            return scriptType.IsAssignableFrom(type);
        }

        private static ReceiverKind Pop(List<ReceiverKind> stack)
        {
            if (stack.Count == 0)
            {
                return ReceiverKind.Unknown;
            }

            int index = stack.Count - 1;
            ReceiverKind value = stack[index];
            stack.RemoveAt(index);
            return value;
        }

        private static void ApplyGenericStackEffect(OpCode code, List<ReceiverKind> stack)
        {
            int popCount = StackPopCount(code.StackBehaviourPop);
            if (popCount < 0)
            {
                stack.Clear();
            }
            else
            {
                for (int i = 0; i < popCount; i++)
                {
                    Pop(stack);
                }
            }

            int pushCount = StackPushCount(code.StackBehaviourPush);
            if (pushCount < 0)
            {
                stack.Clear();
                return;
            }

            for (int i = 0; i < pushCount; i++)
            {
                stack.Add(ReceiverKind.Other);
            }
        }

        private static int StackPopCount(StackBehaviour behaviour)
        {
            switch (behaviour)
            {
                case StackBehaviour.Pop0:
                    return 0;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref:
                    return 1;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi:
                    return 2;
                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_pop1:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref:
                    return 3;
                case StackBehaviour.Varpop:
                    return -1;
                default:
                    return -1;
            }
        }

        private static int StackPushCount(StackBehaviour behaviour)
        {
            switch (behaviour)
            {
                case StackBehaviour.Push0:
                    return 0;
                case StackBehaviour.Push1:
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref:
                    return 1;
                case StackBehaviour.Push1_push1:
                    return 2;
                case StackBehaviour.Varpush:
                    return -1;
                default:
                    return -1;
            }
        }

        /// <summary>The problems that are about the script's shape rather than about a whitelist.</summary>
        private static void CheckShape(Type scriptType, CilboxScriptScan scan)
        {
            if (!scan.HasCilboxableAttribute)
            {
                scan.Issues.Add(new CilboxScriptIssue
                {
                    TitleKey = "sdk.cilbox.scan.issue.notCilboxable",
                    IsError = true,
                });
            }

            const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.DeclaredOnly;

            var unforwarded = new List<string>();
            var coroutines = new List<string>();

            foreach (MethodInfo method in scriptType.GetMethods(declared))
            {
                if (UnforwardedMessages.Contains(method.Name)) unforwarded.Add(method.Name);
                if (typeof(IEnumerator).IsAssignableFrom(method.ReturnType)) coroutines.Add(method.Name);

                // OnEnable is forwarded, but the first one is dropped, which is its own trap.
                if (method.Name == "OnEnable")
                {
                    scan.Issues.Add(new CilboxScriptIssue
                    {
                        TitleKey = "sdk.cilbox.scan.issue.onEnable",
                        IsError = false,
                    });
                }
            }

            if (unforwarded.Count > 0)
            {
                scan.Issues.Add(new CilboxScriptIssue
                {
                    TitleKey = "sdk.cilbox.scan.issue.unforwarded",
                    Detail = string.Join(", ", unforwarded),
                    IsError = true,
                });
            }

            if (coroutines.Count > 0)
            {
                scan.Issues.Add(new CilboxScriptIssue
                {
                    TitleKey = "sdk.cilbox.scan.issue.coroutine",
                    Detail = string.Join(", ", coroutines),
                    IsError = true,
                });
            }
        }

        /// <summary>
        /// Walks one method body, resolving every metadata token it references. Returns false when
        /// the body could not be read, so the caller can say the picture is incomplete rather than
        /// implying a clean result.
        /// </summary>
        private static bool WalkBody(MethodBase method,
            Dictionary<Type, CilboxReference> types,
            Dictionary<FieldInfo, CilboxReference> fields,
            Dictionary<MethodBase, CilboxReference> methods,
            HashSet<Type> own, CilboxScriptScan scan)
        {
            byte[] il;
            try
            {
                MethodBody body = method.GetMethodBody();
                if (body == null) return true; // abstract or extern: nothing to read, not a failure
                il = body.GetILAsByteArray();
                if (il == null) return true;

                foreach (LocalVariableInfo local in body.LocalVariables)
                {
                    Record(types, fields, methods, own, scan, local.LocalType, null, null);
                }
            }
            catch (Exception)
            {
                return false;
            }

            Type[] typeArgs = null;
            Type[] methodArgs = null;
            try
            {
                if (method.DeclaringType != null && method.DeclaringType.IsGenericType)
                {
                    typeArgs = method.DeclaringType.GetGenericArguments();
                }
                if (method.IsGenericMethod) methodArgs = method.GetGenericArguments();
            }
            catch (Exception)
            {
                // Leave both null; ResolveMember just gets less context.
            }

            Module module = method.Module;
            int i = 0;

            while (i < il.Length)
            {
                short key;
                byte first = il[i++];
                if (first == 0xFE && i < il.Length)
                {
                    key = unchecked((short)(0xFE00 | il[i++]));
                }
                else
                {
                    key = first;
                }

                if (!_opcodes.TryGetValue(key, out OpCode code)) return false;

                int operand = OperandSize(code.OperandType, il, i);
                if (operand < 0 || i + operand > il.Length) return false;

                switch (code.OperandType)
                {
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        int token = BitConverter.ToInt32(il, i);
                        TryResolve(module, token, typeArgs, methodArgs, types, fields, methods, own, scan);
                        break;
                }

                i += operand;
            }

            return true;
        }

        private static void TryResolve(Module module, int token, Type[] typeArgs, Type[] methodArgs,
            Dictionary<Type, CilboxReference> types,
            Dictionary<FieldInfo, CilboxReference> fields,
            Dictionary<MethodBase, CilboxReference> methods,
            HashSet<Type> own, CilboxScriptScan scan)
        {
            MemberInfo member;
            try
            {
                member = module.ResolveMember(token, typeArgs, methodArgs);
            }
            catch (Exception)
            {
                // String literals and standalone signatures share the token space; not resolving
                // one is normal, not a failure.
                return;
            }

            if (member == null) return;

            if (member is Type type)
            {
                Record(types, fields, methods, own, scan, type, null, null);
            }
            else if (member is FieldInfo field)
            {
                Record(types, fields, methods, own, scan, null, field, null);
            }
            else if (member is MethodBase method)
            {
                Record(types, fields, methods, own, scan, null, null, method);
            }
        }

        private static void Record(
            Dictionary<Type, CilboxReference> types,
            Dictionary<FieldInfo, CilboxReference> fields,
            Dictionary<MethodBase, CilboxReference> methods,
            HashSet<Type> own, CilboxScriptScan scan,
            Type type, FieldInfo field, MethodBase method)
        {
            if (type != null)
            {
                type = Unwrap(type);
                if (type == null || own.Contains(type)) return;
                if (type.IsGenericParameter) return;

                if (SameContentAssembly(type, scan))
                {
                    if (!scan.SameAssembly.Contains(type)) scan.SameAssembly.Add(type);
                    return;
                }

                if (types.TryGetValue(type, out CilboxReference existing)) existing.Count++;
                else types[type] = new CilboxReference { Member = type, DeclaringType = type, Display = type.FullName };
                return;
            }

            if (field != null)
            {
                Type declaring = field.DeclaringType;
                if (declaring == null || own.Contains(declaring)) return;
                if (SameContentAssembly(declaring, scan)) return;

                if (fields.TryGetValue(field, out CilboxReference existing)) existing.Count++;
                else
                {
                    fields[field] = new CilboxReference
                    {
                        Member = field,
                        DeclaringType = declaring,
                        Display = declaring.FullName + "." + field.Name,
                    };
                }

                Record(types, fields, methods, own, scan, declaring, null, null);
                Record(types, fields, methods, own, scan, field.FieldType, null, null);
                return;
            }

            if (method != null)
            {
                Type declaring = method.DeclaringType;
                if (declaring == null || own.Contains(declaring)) return;
                if (SameContentAssembly(declaring, scan)) return;

                if (methods.TryGetValue(method, out CilboxReference existing)) existing.Count++;
                else
                {
                    methods[method] = new CilboxReference
                    {
                        Member = method,
                        DeclaringType = declaring,
                        Display = declaring.FullName + "." + method.Name,
                    };
                }

                Record(types, fields, methods, own, scan, declaring, null, null);
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Record(types, fields, methods, own, scan, parameter.ParameterType, null, null);
                }
                if (method is MethodInfo info && info.ReturnType != typeof(void))
                {
                    Record(types, fields, methods, own, scan, info.ReturnType, null, null);
                }
            }
        }

        private static bool SameContentAssembly(Type type, CilboxScriptScan scan) =>
            scan.ScriptType != null && type.Assembly == scan.ScriptType.Assembly;

        /// <summary>
        /// Reduces a type to the one the sandbox actually checks: by-ref and array wrappers are
        /// stripped and a generic type is checked as its definition, matching
        /// <c>CheckTypeSecurityRecursive</c>.
        /// </summary>
        private static Type Unwrap(Type type)
        {
            while (type != null && (type.IsByRef || type.IsArray || type.IsPointer))
            {
                type = type.GetElementType();
            }
            if (type != null && type.IsConstructedGenericType)
            {
                return type.GetGenericTypeDefinition();
            }
            return type;
        }

        private static int OperandSize(OperandType operandType, byte[] il, int position)
        {
            switch (operandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    if (position + 4 > il.Length) return -1;
                    int count = BitConverter.ToInt32(il, position);
                    if (count < 0) return -1;
                    return 4 + count * 4;
                default:
                    return -1;
            }
        }
    }
}
#endif
