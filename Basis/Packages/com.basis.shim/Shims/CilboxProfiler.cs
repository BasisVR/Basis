using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Drivers;
using Basis.Shims;

namespace Cilbox
{
	public abstract partial class CilboxBasisCommon
	{
		[NonSerialized] internal CilboxClass[] profilerClasses;
		[NonSerialized] internal CilboxProfileBox profilerState;
	}
	internal sealed class CilboxProfileEntry
	{
		internal int Slot = -1;
		internal int Kind, Live, FrameCalls, WindowCalls;
		internal string Source, Script;
		internal bool Touched;
		internal long FrameTicks, WindowTicks, TotalTicks, PeakTicks, TotalCalls;
		internal double FrameMs, CallsPerFrame;
	}
	internal sealed class CilboxProfilePatch
	{
		internal CilboxBasisCommon Box;
		internal CilboxClass Class;
		internal CilboxMethod Method, Body;
		internal CilboxProfileEntry Entry;
		internal byte[] Code;
		internal int Stack;
		internal bool Applied;
	}
	internal sealed class CilboxProfileBox
	{
		internal CilboxBasisCommon Box;
		internal CilboxClass[] Classes;
		internal readonly List<CilboxProfilePatch> Patches = new List<CilboxProfilePatch>();
		internal readonly HashSet<CilboxMethod> Patched = new HashSet<CilboxMethod>(), Wrapped = new HashSet<CilboxMethod>();
		internal readonly List<(int Index, CilMetadataTokenInfo Token)> Batches = new List<(int, CilMetadataTokenInfo)>();
		internal int Kind;
		internal string Name;
		internal bool Named, Scanned, Settled, Broken;
	}
	[AutoStaticsCleanup]
	internal static partial class CilboxProfiler
	{
		internal const int KindWorld = 0, KindProp = 1, KindAvatar = 2;
		internal const double WindowSeconds = 1d;
		internal const int GraphFrames = 240, GraphSlots = 8, GraphRows = GraphSlots + 1;
		const uint NoMethod = 0xffffffff;
		const int MaxArguments = 255;
		struct Frame
		{
			internal CilboxProfilePatch Patch;
			internal long Start, Child;
			internal uint Depth;
			internal bool Skip;
		}
		[NoAutoStaticsCleanup] static readonly string[] callbacks = { BasisBeforeRenderShim.CallbackName, BasisGraphicsSettingsEventShim.CallbackName, BasisJiggleEventShim.GrabCallbackName, BasisJiggleEventShim.TouchCallbackName, BasisLanguageEventShim.CallbackName, BasisPermissionEventShim.CallbackName, BasisPlatformEventShim.CallbackName };
		[NoAutoStaticsCleanup] static readonly CilMetadataTokenInfo.DelegateOverride enter = Enter, exit = Exit;
		static readonly List<CilboxProfileBox> boxes = new List<CilboxProfileBox>();
		static readonly Dictionary<string, CilboxProfileEntry> entries = new Dictionary<string, CilboxProfileEntry>(StringComparer.Ordinal);
		static readonly List<CilboxProfileEntry> touched = new List<CilboxProfileEntry>();
		static readonly List<(int Class, uint Method)> targets = new List<(int, uint)>();
		static readonly HashSet<CilboxMethod> queued = new HashSet<CilboxMethod>();
		static readonly float[] graph = new float[GraphRows * GraphFrames];
		static CilboxProfileEntry[] slots = new CilboxProfileEntry[GraphSlots], ranked = new CilboxProfileEntry[GraphSlots];
		static Frame[] frames = new Frame[32];
		static int top, windowFrames, graphHead, mainThread = -1;
		static bool active, subscribed, warned;
		static double windowStart;
		internal static int Version { get; private set; }
		internal static int GraphVersion { get; private set; }
		internal static bool Active => active;
		internal static int BoxCount => boxes.Count;
		internal static int GraphHead => graphHead;
		internal static float[] Graph => graph;
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		static void BindMainThread() => mainThread = Thread.CurrentThread.ManagedThreadId;
		internal static void SetActive(bool value)
		{
			if (value) mainThread = Thread.CurrentThread.ManagedThreadId;
			if (active == value) return;
			active = value;
			ResetStats();
			if (value)
			{
				CilboxBasisCommon[] found = UnityEngine.Object.FindObjectsByType<CilboxBasisCommon>(FindObjectsInactive.Include);
				for (int i = 0; i < found.Length; i++) if (found[i].classesList != null && found[i].consentScan == null) Guard(found[i], true);
				Subscribe();
			}
			Maintain();
			Version++;
		}
		internal static void Resolving(CilboxBasisCommon box)
		{
			if (!active) return;
			CilboxClass[] classes = box.classesList;
			if (classes == null || ReferenceEquals(box.profilerClasses, classes) || box.consentScan != null || Thread.CurrentThread.ManagedThreadId != mainThread) return;
			for (int i = 0; i < classes.Length; i++) if (classes[i] == null || classes[i].importFunctionToId == null || classes[i].methods == null) return;
			box.profilerClasses = classes;
			if (Application.isPlaying) Guard(box, false);
		}
		internal static CilboxProfileBox Watch(CilboxBasisCommon box, bool scan)
		{
			for (int i = boxes.Count - 1; i >= 0; i--) if (ReferenceEquals(boxes[i].Box, box)) Drop(i);
			CilboxClass[] classes = box.classesList;
			box.profilerClasses = classes;
			CilboxProfileBox state = box.profilerState;
			if (state == null || !ReferenceEquals(state.Classes, classes))
			{
				if (state != null) Restore(state);
				state = new CilboxProfileBox { Box = box, Classes = classes };
				Describe(state);
				box.profilerState = state;
			}
			for (int i = 0; i < state.Patches.Count; i++)
			{
				CilboxProfilePatch patch = state.Patches[i];
				patch.Entry = EntryFor(state, patch.Class, patch.Method);
				patch.Entry.Live++;
			}
			state.Settled = false;
			boxes.Add(state);
			if (active) Patch(state, scan);
			return state;
		}
		internal static void Tick()
		{
			top = 0;
			graphHead = (graphHead + 1) % GraphFrames;
			for (int row = 0; row < GraphRows; row++) graph[row * GraphFrames + graphHead] = 0f;
			double toMs = 1000d / Stopwatch.Frequency;
			for (int i = 0; i < touched.Count; i++)
			{
				CilboxProfileEntry e = touched[i];
				long ticks = e.FrameTicks;
				e.WindowTicks += ticks;
				e.TotalTicks += ticks;
				e.WindowCalls += e.FrameCalls;
				e.TotalCalls += e.FrameCalls;
				if (ticks > e.PeakTicks) e.PeakTicks = ticks;
				graph[(e.Slot >= 0 ? e.Slot : GraphSlots) * GraphFrames + graphHead] += (float)(ticks * toMs);
				e.FrameTicks = 0;
				e.FrameCalls = 0;
				e.Touched = false;
			}
			touched.Clear();
			GraphVersion++;
			windowFrames++;
			double now = Time.realtimeSinceStartupAsDouble;
			if (now - windowStart >= WindowSeconds) Snapshot(now);
			Maintain();
		}
		internal static void Clear()
		{
			ResetStats();
			List<string> dead = null;
			foreach (KeyValuePair<string, CilboxProfileEntry> pair in entries) if (pair.Value.Live <= 0) (dead ??= new List<string>()).Add(pair.Key);
			if (dead != null) for (int i = 0; i < dead.Count; i++) entries.Remove(dead[i]);
			Version++;
		}
		internal static void Collect(List<CilboxProfileEntry> into)
		{
			into.Clear();
			foreach (CilboxProfileEntry e in entries.Values) if (e.TotalCalls > 0) into.Add(e);
		}
		internal static double TicksToMs(long ticks) => ticks * 1000d / Stopwatch.Frequency;
		internal static string Pretty(string className, string methodName)
		{
			string type = className ?? string.Empty;
			int nested = type.IndexOf('+');
			int cut = nested < 0 ? type.Length : nested;
			int dot = cut > 0 ? type.LastIndexOf('.', cut - 1) : -1;
			string[] parts = type.Substring(dot + 1).Split('+');
			string shown = null;
			for (int i = 0; i < parts.Length; i++) if (parts[i].Length > 0 && parts[i][0] != '<') shown = shown == null ? parts[i] : shown + "." + parts[i];
			return Clean((shown ?? parts[0]) + "." + methodName, 34);
		}
		internal static string Clean(string value, int max)
		{
			if (string.IsNullOrEmpty(value)) return "?";
			int slashes = 0;
			for (int i = 0; i < value.Length; i++) if (value[i] == '\\') slashes++;
			char[] chars = new char[value.Length + slashes];
			for (int i = 0, n = 0; i < value.Length; i++)
			{
				char c = value[i];
				chars[n++] = c == '<' ? '[' : c == '>' ? ']' : char.IsControl(c) ? ' ' : c;
				if (c == '\\') chars[n++] = CilboxMenus.Joiner;
			}
			string clean = new string(chars).Trim();
			if (clean.Length == 0) return "?";
			return clean.Length <= max ? clean : clean.Substring(0, max - 1) + "…";
		}
		static void Guard(CilboxBasisCommon box, bool scan)
		{
			try
			{
				Watch(box, scan);
			}
			catch (Exception e)
			{
				if (box.profilerState != null)
				{
					box.profilerState.Broken = true;
					Restore(box.profilerState);
				}
				Warn($"[Cilbox] Script profiling skipped {box.name}: {e.Message}");
			}
		}
		static void Warn(string message)
		{
			if (warned) return;
			warned = true;
			BasisDebug.LogWarning(message);
		}
		static void Subscribe()
		{
			if (subscribed) return;
			subscribed = true;
			BasisFrameClock.OnTick += Tick;
			BasisFrameClock.AddRequest();
		}
		static void Unsubscribe()
		{
			if (!subscribed) return;
			subscribed = false;
			BasisFrameClock.OnTick -= Tick;
			BasisFrameClock.RemoveRequest();
		}
		static void ResetStats()
		{
			foreach (CilboxProfileEntry e in entries.Values)
			{
				e.FrameCalls = e.WindowCalls = 0;
				e.FrameTicks = e.WindowTicks = e.TotalTicks = e.PeakTicks = e.TotalCalls = 0;
				e.FrameMs = e.CallsPerFrame = 0d;
				e.Touched = false;
			}
			touched.Clear();
			top = 0;
			windowFrames = 0;
			windowStart = Time.realtimeSinceStartupAsDouble;
			ResetGraph();
		}
		static void ResetGraph()
		{
			Array.Clear(graph, 0, graph.Length);
			for (int s = 0; s < GraphSlots; s++)
			{
				if (slots[s] != null) slots[s].Slot = -1;
				slots[s] = null;
			}
			GraphVersion++;
		}
		static void Rank()
		{
			int count = 0;
			foreach (CilboxProfileEntry e in entries.Values)
			{
				if (e.FrameMs <= 0d || (count == GraphSlots && ranked[GraphSlots - 1].FrameMs >= e.FrameMs)) continue;
				int at = count < GraphSlots ? count++ : GraphSlots - 1;
				while (at > 0 && ranked[at - 1].FrameMs < e.FrameMs)
				{
					ranked[at] = ranked[at - 1];
					at--;
				}
				ranked[at] = e;
			}
			for (int s = 0; s < GraphSlots; s++) if (slots[s] != null && Array.IndexOf(ranked, slots[s], 0, count) < 0) Release(s);
			for (int i = 0; i < count; i++)
			{
				if (ranked[i].Slot >= 0) continue;
				int free = Array.IndexOf(slots, null);
				if (free < 0) break;
				slots[free] = ranked[i];
				ranked[i].Slot = free;
			}
			Array.Clear(ranked, 0, GraphSlots);
		}
		static void Release(int slot)
		{
			int row = slot * GraphFrames, other = GraphSlots * GraphFrames;
			for (int f = 0; f < GraphFrames; f++)
			{
				graph[other + f] += graph[row + f];
				graph[row + f] = 0f;
			}
			slots[slot].Slot = -1;
			slots[slot] = null;
		}
		static void Snapshot(double now)
		{
			int count = Math.Max(1, windowFrames);
			double scale = 1000d / Stopwatch.Frequency / count;
			foreach (CilboxProfileEntry e in entries.Values)
			{
				e.FrameMs = e.WindowTicks * scale;
				e.CallsPerFrame = e.WindowCalls / (double)count;
				e.WindowTicks = 0;
				e.WindowCalls = 0;
			}
			windowFrames = 0;
			windowStart = now;
			Rank();
			Version++;
		}
		static void Maintain()
		{
			bool applied = false;
			for (int i = boxes.Count - 1; i >= 0; i--)
			{
				CilboxProfileBox state = boxes[i];
				CilboxBasisCommon box = state.Box;
				try
				{
					if (box == null)
					{
						Drop(i);
						continue;
					}
					if (!ReferenceEquals(box.classesList, state.Classes))
					{
						Drop(i);
						if (active && box.classesList != null && box.consentScan == null) Watch(box, true);
						continue;
					}
					if (active)
					{
						if (!state.Settled && Verify(state)) Patch(state, true);
						continue;
					}
					if (Unpatch(state)) Drop(i);
					else applied = true;
				}
				catch (Exception e)
				{
					state.Broken = state.Settled = true;
					Restore(state);
					Warn($"[Cilbox] Script profiling stopped for {state.Name}: {e.Message}");
				}
			}
			if (active) return;
			if (!applied) Unsubscribe();
			if (boxes.Count > 0) return;
			entries.Clear();
			ResetGraph();
		}
		static void Drop(int index)
		{
			CilboxProfileBox state = boxes[index];
			boxes.RemoveAt(index);
			for (int i = 0; i < state.Patches.Count; i++) if (state.Patches[i].Entry != null) state.Patches[i].Entry.Live--;
			if (state.Box == null || !ReferenceEquals(state.Box.classesList, state.Classes)) Restore(state);
		}
		static bool Verify(CilboxProfileBox state)
		{
			if (state.Broken) return false;
			CilMetadataTokenInfo[] tokens = state.Box.metadatas;
			for (int i = 0; i < state.Batches.Count; i++)
			{
				(int index, CilMetadataTokenInfo token) = state.Batches[i];
				if (tokens != null && index < tokens.Length && ReferenceEquals(tokens[index], token)) continue;
				state.Broken = state.Settled = true;
				Restore(state);
				Warn($"[Cilbox] Script profiling stopped for {state.Name}: the box replaced its metadata table after the profiler wrapped it.");
				return false;
			}
			return true;
		}
		static bool Unpatch(CilboxProfileBox state)
		{
			CilboxBasisCommon box = state.Box;
			lock (box)
			{
				if (box.interpreterAccountingDepth != 0) return false;
				Restore(state);
			}
			return true;
		}
		static void Restore(CilboxProfileBox state)
		{
			for (int i = 0; i < state.Patches.Count; i++)
			{
				CilboxProfilePatch patch = state.Patches[i];
				if (!patch.Applied) continue;
				CilboxMethod method = patch.Method, body = patch.Body;
				method.byteCode = body.byteCode;
				method.MaxStackSize = body.MaxStackSize;
				method.methodLocals = body.methodLocals;
				method.typeLocals = body.typeLocals;
				method.hasExceptionClauses = body.hasExceptionClauses;
				patch.Applied = false;
			}
		}
		static void Apply(CilboxProfilePatch patch)
		{
			CilboxMethod method = patch.Method;
			method.byteCode = patch.Code;
			method.MaxStackSize = patch.Stack;
			method.methodLocals = Array.Empty<string>();
			method.typeLocals = Type.EmptyTypes;
			method.hasExceptionClauses = false;
			patch.Applied = true;
		}
		static void Patch(CilboxProfileBox state, bool scan)
		{
			if (state.Broken) return;
			CilboxBasisCommon box = state.Box;
			CilboxClass[] classes = state.Classes;
			bool complete = Complete(box);
			bool scanning = scan && !state.Scanned && complete;
			targets.Clear();
			queued.Clear();
			for (int c = 0; c < classes.Length; c++)
			{
				CilboxClass cls = classes[c];
				if (!state.Named)
				{
					uint[] imports = cls.importFunctionToId;
					bool behaviour = false;
					for (int id = 1; id < imports.Length; id++)
					{
						if (imports[id] == NoMethod) continue;
						behaviour = true;
						Add(state, c, imports[id]);
					}
					if (behaviour && imports.Length > 0 && imports[0] != NoMethod) Add(state, c, imports[0]);
					for (int n = 0; n < callbacks.Length; n++) if (cls.methodNameToIndex.TryGetValue(callbacks[n], out uint index)) Add(state, c, index);
				}
				if (scanning) for (int m = 0; m < cls.methods.Length; m++) Scan(state, cls.methods[m]);
			}
			lock (box)
			{
				if (box.interpreterAccountingDepth != 0) return;
				if (targets.Count > 0) Wrap(state);
				for (int i = 0; i < state.Patches.Count; i++) if (!state.Patches[i].Applied) Apply(state.Patches[i]);
			}
			state.Named = true;
			if (scanning || (scan && !complete)) state.Scanned = true;
			state.Settled = state.Scanned;
		}
		static bool Complete(CilboxBasisCommon box)
		{
			CilMetadataTokenInfo[] tokens = box.metadatas;
			if (tokens == null) return false;
			for (int i = 1; i < tokens.Length; i++) if (tokens[i] == null) return false;
			return true;
		}
		static void Add(CilboxProfileBox state, int cls, uint index)
		{
			CilboxClass owner = state.Classes[cls];
			if (owner == null || owner.methods == null || index >= owner.methods.Length) return;
			CilboxMethod method = owner.methods[index];
			if (method == null || method.byteCode == null || state.Patched.Contains(method) || !queued.Add(method)) return;
			if ((method.isStatic ? 0 : 1) + (method.signatureParameters?.Length ?? 0) > MaxArguments) return;
			targets.Add((cls, index));
		}
		static void Scan(CilboxProfileBox state, CilboxMethod method)
		{
			byte[] code = method?.byteCode;
			CilMetadataTokenInfo[] tokens = state.Box.metadatas;
			if (code == null || tokens == null || state.Wrapped.Contains(method)) return;
			try
			{
				int i = 0;
				while (i < code.Length)
				{
					CilboxUtil.OpCodes.OpCode op = CilboxUtil.OpCodes.ReadOpCode(code, ref i);
					CilboxUtil.OpCodes.OperandType type = op.OperandType;
					uint operand = (uint)CilboxUtil.BytecodePullLiteral(code, ref i, CilboxUtil.OpCodes.OperandLength[(int)type]);
					if (type == CilboxUtil.OpCodes.OperandType.InlineSwitch)
					{
						if (operand > (uint)(code.Length - i) / 4) return;
						i += (int)operand * 4;
						continue;
					}
					bool virtualTarget = op.Code == CilboxUtil.OpCodes.Code.Ldvirtftn;
					if ((!virtualTarget && op.Code != CilboxUtil.OpCodes.Code.Ldftn) || operand >= tokens.Length) continue;
					CilMetadataTokenInfo token = tokens[operand];
					if (token == null || !token.isValid || token.isNative || token.shim != null || token.interpretiveMethodClass < 0 || token.interpretiveMethodClass >= state.Classes.Length) continue;
					Add(state, token.interpretiveMethodClass, (uint)token.interpretiveMethod);
					CilboxClass declaring = state.Classes[token.interpretiveMethodClass];
					if (!virtualTarget || declaring?.methods == null || token.interpretiveMethod < 0 || token.interpretiveMethod >= declaring.methods.Length || declaring.methods[token.interpretiveMethod] == null) continue;
					string signature = declaring.methods[token.interpretiveMethod].fullSignature;
					for (int c = 0; c < state.Classes.Length; c++) if (state.Classes[c]?.methodFullSignatureToIndex != null && state.Classes[c].methodFullSignatureToIndex.TryGetValue(signature, out uint over)) Add(state, c, over);
				}
			}
			catch (Exception)
			{
			}
		}
		static void Wrap(CilboxProfileBox state)
		{
			CilboxBasisCommon box = state.Box;
			CilboxClass[] classes = state.Classes;
			CilMetadataTokenInfo[] tokens = box.metadatas;
			int first = tokens.Length;
			CilMetadataTokenInfo[] grown = new CilMetadataTokenInfo[first + targets.Count * 3];
			Array.Copy(tokens, grown, first);
			int[] next = new int[classes.Length];
			CilboxMethod[][] methods = new CilboxMethod[classes.Length][];
			for (int i = 0; i < targets.Count; i++) next[targets[i].Class]++;
			for (int c = 0; c < classes.Length; c++)
			{
				if (next[c] == 0) continue;
				CilboxMethod[] old = classes[c].methods;
				methods[c] = new CilboxMethod[old.Length + next[c]];
				Array.Copy(old, methods[c], old.Length);
				next[c] = old.Length;
			}
			for (int i = 0; i < targets.Count; i++)
			{
				int c = targets[i].Class;
				CilboxClass cls = classes[c];
				CilboxMethod method = cls.methods[targets[i].Method];
				CilboxMethod body = Copy(cls, method);
				int bodyIndex = next[c]++;
				methods[c][bodyIndex] = body;
				CilboxProfilePatch patch = new CilboxProfilePatch { Box = box, Class = cls, Method = method, Body = body, Entry = EntryFor(state, cls, method) };
				patch.Entry.Live++;
				int slot = first + i * 3;
				grown[slot] = new CilMetadataTokenInfo(MetaTokenType.mtMethod) { isValid = true, shim = enter, shimIsVoid = true, shimIsStatic = true, opaque = patch, Name = "Method: Enter", declaringTypeName = "Cilbox.CilboxProfiler" };
				grown[slot + 1] = new CilMetadataTokenInfo(MetaTokenType.mtMethod) { isValid = true, interpretiveMethodClass = c, interpretiveMethod = bodyIndex, Name = "Method: " + method.methodName, declaringTypeName = cls.className };
				grown[slot + 2] = new CilMetadataTokenInfo(MetaTokenType.mtMethod) { isValid = true, shim = exit, shimIsVoid = true, shimIsStatic = true, opaque = patch, Name = "Method: Exit", declaringTypeName = "Cilbox.CilboxProfiler" };
				int arguments = (method.isStatic ? 0 : 1) + (method.signatureParameters?.Length ?? 0);
				patch.Code = Trampoline(slot, slot + 1, slot + 2, arguments);
				patch.Stack = Math.Max(1, arguments);
				state.Patches.Add(patch);
				state.Patched.Add(method);
				state.Patched.Add(body);
				state.Wrapped.Add(method);
			}
			for (int c = 0; c < classes.Length; c++) if (methods[c] != null) classes[c].methods = methods[c];
			box.metadatas = grown;
			state.Batches.Add((first, grown[first]));
		}
		static CilboxMethod Copy(CilboxClass cls, CilboxMethod method)
		{
			CilboxMethod body = new CilboxMethod();
			body.Load(cls, new SerializedMethod
			{
				methodName = method.methodName,
				fullSignature = method.fullSignature,
				maxStack = method.MaxStackSize,
				isVoid = method.isVoid,
				isStatic = method.isStatic,
				isCtor = method.isConstructor,
				body = method.byteCode,
				locals = Array.Empty<SerializedField>(),
				parameters = Array.Empty<SerializedField>(),
				exceptionHandlers = Array.Empty<SerializedExceptionHandler>(),
			});
			body.methodLocals = method.methodLocals;
			body.typeLocals = method.typeLocals;
			body.signatureParameters = method.signatureParameters;
			body.typeParameters = method.typeParameters;
			body.exceptionClauses = method.exceptionClauses;
			body.hasExceptionClauses = method.hasExceptionClauses;
			body.handlerOffsetToClauseMap = method.handlerOffsetToClauseMap;
			return body;
		}
		static byte[] Trampoline(int enterToken, int bodyToken, int exitToken, int arguments)
		{
			List<byte> code = new List<byte>(16 + arguments * 2);
			Call(code, enterToken);
			for (int a = 0; a < arguments; a++)
			{
				if (a < 4) code.Add((byte)(0x02 + a));
				else
				{
					code.Add(0x0E);
					code.Add((byte)a);
				}
			}
			Call(code, bodyToken);
			Call(code, exitToken);
			code.Add(0x2A);
			return code.ToArray();
		}
		static void Call(List<byte> code, int token)
		{
			code.Add(0x28);
			code.Add((byte)token);
			code.Add((byte)(token >> 8));
			code.Add((byte)(token >> 16));
			code.Add((byte)(token >> 24));
		}
		static CilboxProfileEntry EntryFor(CilboxProfileBox state, CilboxClass cls, CilboxMethod method)
		{
			string key = state.Kind + "\n" + state.Name + "\n" + cls.className + "\n" + method.methodName;
			if (!entries.TryGetValue(key, out CilboxProfileEntry entry))
			{
				entry = new CilboxProfileEntry { Kind = state.Kind, Source = state.Name, Script = Pretty(cls.className, method.methodName) };
				entries[key] = entry;
			}
			return entry;
		}
		static void Describe(CilboxProfileBox state)
		{
			CilboxBasisCommon box = state.Box;
			state.Kind = box is CilboxAvatarBasis ? KindAvatar : box is CilboxPropBasis ? KindProp : KindWorld;
			string name = BasisContent.TryResolve(box, out BasisContentBase content) ? content.BasisBundleDescription?.AssetBundleName : null;
			if (string.IsNullOrEmpty(name)) name = box.gameObject.scene.name;
			if (string.IsNullOrEmpty(name)) name = box.name;
			state.Name = Clean(name, 14);
		}
		static StackElement Enter(CilMetadataTokenInfo token, ArraySegment<StackElement> stack, ArraySegment<StackElement> parameters)
		{
			if (!active || Thread.CurrentThread.ManagedThreadId != mainThread) return default;
			CilboxProfilePatch patch = (CilboxProfilePatch)token.opaque;
			CilboxBasisCommon box = patch.Box;
			uint depth = box.interpreterAccountingDepth;
			if (depth == 1 && box.interpreterInstructionsCount == 1) while (top > 0 && ReferenceEquals(frames[top - 1].Patch.Box, box)) top--;
			if (top == frames.Length) Array.Resize(ref frames, top * 2);
			bool skip = top > 0 && ReferenceEquals(frames[top - 1].Patch.Box, box) && frames[top - 1].Depth == depth;
			ref Frame frame = ref frames[top++];
			frame.Patch = patch;
			frame.Depth = depth;
			frame.Skip = skip;
			frame.Child = 0;
			frame.Start = skip ? 0 : Stopwatch.GetTimestamp();
			return default;
		}
		static StackElement Exit(CilMetadataTokenInfo token, ArraySegment<StackElement> stack, ArraySegment<StackElement> parameters)
		{
			if (!active || Thread.CurrentThread.ManagedThreadId != mainThread) return default;
			CilboxProfilePatch patch = (CilboxProfilePatch)token.opaque;
			int index = top - 1;
			while (index >= 0 && !ReferenceEquals(frames[index].Patch, patch)) index--;
			if (index < 0) return default;
			Frame frame = frames[index];
			top = index;
			if (frame.Skip) return default;
			long elapsed = Stopwatch.GetTimestamp() - frame.Start;
			long self = elapsed - frame.Child;
			CilboxProfileEntry entry = patch.Entry;
			if (entry != null)
			{
				entry.FrameTicks += self > 0 ? self : 0;
				entry.FrameCalls++;
				if (!entry.Touched)
				{
					entry.Touched = true;
					touched.Add(entry);
				}
			}
			for (int i = index - 1; i >= 0; i--)
			{
				if (frames[i].Skip) continue;
				frames[i].Child += elapsed;
				break;
			}
			return default;
		}
	}
}
