using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Cilbox;
using Basis.Shims;

namespace Basis.Shims
{
	public static class BasisComputeShim
	{
		public const int MaxThreadGroups = 65535;
		public const long MaxBufferBytes = 64L << 20;
		public const long WorldBytes = 256L << 20;
		public const long PropBytes = 64L << 20;
		public const long AvatarBytes = 16L << 20;
		public const long TotalBytes = 1L << 30;
		public const long WorldThreadsPerFrame = 1L << 25;
		public const long PropThreadsPerFrame = 1L << 23;
		public const long AvatarThreadsPerFrame = 1L << 20;
		public const long TotalThreadsPerFrame = 1L << 26;
		public static bool IsSupported => SystemInfo.supportsComputeShaders;
		public static long RemainingBytes(object self) => CilboxCompute.RemainingBytes(self);
		public static long RemainingThreads(object self) => CilboxCompute.RemainingThreads(self);
		public static bool TryGetDataAsync(ComputeBuffer buffer, Array data) => CilboxCompute.TryGetDataAsync(buffer, data);
		public static bool TryGetDataAsync(GraphicsBuffer buffer, Array data) => CilboxCompute.TryGetDataAsync(buffer, data);
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxCompute
	{
		private static readonly List<CilboxBasisCommon> boxes = new List<CilboxBasisCommon>();
		private static int frame = -1;
		private static long frameThreads;
		private static bool warned;
		internal const int BlockingWarnFrames = 120;
		[NoAutoStaticsCleanup] private static readonly ConditionalWeakTable<object, ComputeRead> reads = new ConditionalWeakTable<object, ComputeRead>();

		private sealed class ComputeRead
		{
			private byte[] latest;
			private int length;
			private bool fresh;
			public bool Pending;
			public readonly Action<AsyncGPUReadbackRequest> Done;
			public ComputeRead() => Done = Completed;

			private void Completed(AsyncGPUReadbackRequest request)
			{
				Pending = false;
				if (request.hasError) return;
				NativeArray<byte> bytes = request.GetData<byte>();
				if (latest == null || latest.Length < bytes.Length) latest = new byte[bytes.Length];
				NativeArray<byte>.Copy(bytes, latest, bytes.Length);
				length = bytes.Length;
				fresh = true;
			}

			public bool Take(Array data)
			{
				if (!fresh) return false;
				fresh = false;
				long capacity = (long)data.Length * UnsafeUtility.SizeOf(data.GetType().GetElementType());
				GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
				try { Marshal.Copy(latest, 0, pin.AddrOfPinnedObject(), (int)Math.Min(length, capacity)); }
				finally { pin.Free(); }
				return true;
			}
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Hook()
		{
			SceneManager.sceneUnloaded -= Sweep;
			SceneManager.sceneUnloaded += Sweep;
			Application.quitting -= Unhook;
			Application.quitting += Unhook;
		}

		private static void Unhook()
		{
			SceneManager.sceneUnloaded -= Sweep;
			Application.quitting -= Unhook;
		}

		private static void Sweep(Scene scene) => Prune();

		internal static bool IsEntryPoint(string name) => name == "get_" + nameof(BasisComputeShim.IsSupported) || name == nameof(BasisComputeShim.RemainingBytes) || name == nameof(BasisComputeShim.RemainingThreads) || name == nameof(BasisComputeShim.TryGetDataAsync);

		internal static bool IsRedirect(Type declaringType, string name)
		{
			if (declaringType == typeof(ComputeShader)) return name == nameof(ComputeShader.Dispatch);
			return (declaringType == typeof(ComputeBuffer) || declaringType == typeof(GraphicsBuffer)) && (name == ".ctor" || name == nameof(ComputeBuffer.CopyCount) || name == nameof(ComputeBuffer.GetData));
		}

		internal static MethodInfo Resolve(CilboxBasisCommon box, Type declaringType, string name, SerializedTypeDescriptor[] parametersIn)
		{
			if (box.usage == null) return null;
			Type[] parameters = box.usage.DescriptorsToArrayOfNativeTypes(parametersIn);
			foreach (Type parameter in parameters) if (parameter == null) return null;
			string target = name;
			if (name == ".ctor") target = declaringType == typeof(ComputeBuffer) ? nameof(NewComputeBuffer) : nameof(NewGraphicsBuffer);
			else if (name == nameof(ComputeShader.Dispatch) || name == nameof(ComputeBuffer.GetData))
			{
				Type[] withTarget = new Type[parameters.Length + 1];
				withTarget[0] = declaringType;
				Array.Copy(parameters, 0, withTarget, 1, parameters.Length);
				parameters = withTarget;
			}
			MethodInfo method = typeof(CilboxCompute).GetMethod(target, BindingFlags.Static | BindingFlags.NonPublic, null, parameters, null);
			if (method != null && !boxes.Contains(box)) boxes.Add(box);
			return method;
		}

		internal static ComputeBuffer NewComputeBuffer(int count, int stride) => NewComputeBuffer(count, stride, ComputeBufferType.Default, ComputeBufferMode.Immutable);

		internal static ComputeBuffer NewComputeBuffer(int count, int stride, ComputeBufferType type) => NewComputeBuffer(count, stride, type, ComputeBufferMode.Immutable);

		internal static ComputeBuffer NewComputeBuffer(int count, int stride, ComputeBufferType type, ComputeBufferMode usage)
		{
			CilboxBasisCommon box = Charge(count, stride);
			ComputeBuffer buffer = new ComputeBuffer(count, stride, type, usage);
			Track(box, buffer, (long)count * stride);
			return buffer;
		}

		internal static GraphicsBuffer NewGraphicsBuffer(GraphicsBuffer.Target target, int count, int stride) => NewGraphicsBuffer(target, GraphicsBuffer.UsageFlags.None, count, stride);

		internal static GraphicsBuffer NewGraphicsBuffer(GraphicsBuffer.Target target, GraphicsBuffer.UsageFlags usageFlags, int count, int stride)
		{
			CilboxBasisCommon box = Charge(count, stride);
			GraphicsBuffer buffer = new GraphicsBuffer(target, usageFlags, count, stride);
			Track(box, buffer, (long)count * stride);
			return buffer;
		}

		internal static void Dispatch(ComputeShader shader, int kernelIndex, int threadGroupsX, int threadGroupsY, int threadGroupsZ)
		{
			if (ReferenceEquals(shader, null)) throw new NullReferenceException();
			CheckGroups(threadGroupsX, nameof(threadGroupsX));
			CheckGroups(threadGroupsY, nameof(threadGroupsY));
			CheckGroups(threadGroupsZ, nameof(threadGroupsZ));
			shader.GetKernelThreadGroupSizes(kernelIndex, out uint sizeX, out uint sizeY, out uint sizeZ);
			if (threadGroupsX == 0 || threadGroupsY == 0 || threadGroupsZ == 0) return;
			long threads = (long)threadGroupsX * threadGroupsY * threadGroupsZ * Math.Max(1L, (long)sizeX * sizeY * sizeZ);
			CilboxBasisCommon box = Running();
			if (!Spend(box, threads))
			{
				Warn(box);
				return;
			}
			shader.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);
		}

		internal static void CopyCount(ComputeBuffer src, ComputeBuffer dst, int dstOffsetBytes)
		{
			CheckCount(src, dst, dst == null ? 0 : (long)dst.count * dst.stride, dstOffsetBytes);
			ComputeBuffer.CopyCount(src, dst, dstOffsetBytes);
		}

		internal static void CopyCount(ComputeBuffer src, GraphicsBuffer dst, int dstOffsetBytes)
		{
			CheckCount(src, dst, dst == null ? 0 : (long)dst.count * dst.stride, dstOffsetBytes);
			GraphicsBuffer.CopyCount(src, dst, dstOffsetBytes);
		}

		internal static void CopyCount(GraphicsBuffer src, ComputeBuffer dst, int dstOffsetBytes)
		{
			CheckCount(src, dst, dst == null ? 0 : (long)dst.count * dst.stride, dstOffsetBytes);
			GraphicsBuffer.CopyCount(src, dst, dstOffsetBytes);
		}

		internal static void CopyCount(GraphicsBuffer src, GraphicsBuffer dst, int dstOffsetBytes)
		{
			CheckCount(src, dst, dst == null ? 0 : (long)dst.count * dst.stride, dstOffsetBytes);
			GraphicsBuffer.CopyCount(src, dst, dstOffsetBytes);
		}

		internal static void GetData(ComputeBuffer buffer, Array data)
		{
			NoteBlockingRead();
			buffer.GetData(data);
		}

		internal static void GetData(ComputeBuffer buffer, Array data, int managedBufferStartIndex, int computeBufferStartIndex, int count)
		{
			NoteBlockingRead();
			buffer.GetData(data, managedBufferStartIndex, computeBufferStartIndex, count);
		}

		internal static void GetData(GraphicsBuffer buffer, Array data)
		{
			NoteBlockingRead();
			buffer.GetData(data);
		}

		internal static void GetData(GraphicsBuffer buffer, Array data, int managedBufferStartIndex, int computeBufferStartIndex, int count)
		{
			NoteBlockingRead();
			buffer.GetData(data, managedBufferStartIndex, computeBufferStartIndex, count);
		}

		internal static bool TryGetDataAsync(ComputeBuffer buffer, Array data)
		{
			if (buffer == null) throw new ArgumentNullException(nameof(buffer));
			CheckReadTarget(data);
			if (!SystemInfo.supportsAsyncGPUReadback)
			{
				buffer.GetData(data);
				return true;
			}
			ComputeRead read = reads.GetOrCreateValue(buffer);
			if (!read.Pending)
			{
				AsyncGPUReadback.Request(buffer, read.Done);
				read.Pending = true;
			}
			return read.Take(data);
		}

		internal static bool TryGetDataAsync(GraphicsBuffer buffer, Array data)
		{
			if (buffer == null) throw new ArgumentNullException(nameof(buffer));
			CheckReadTarget(data);
			if (!SystemInfo.supportsAsyncGPUReadback)
			{
				buffer.GetData(data);
				return true;
			}
			ComputeRead read = reads.GetOrCreateValue(buffer);
			if (!read.Pending)
			{
				AsyncGPUReadback.Request(buffer, read.Done);
				read.Pending = true;
			}
			return read.Take(data);
		}

		private static void CheckReadTarget(Array data)
		{
			if (data == null) throw new ArgumentNullException(nameof(data));
			if (!UnsafeUtility.IsBlittable(data.GetType().GetElementType())) throw new ArgumentException("A compute buffer can only be read into an array of plain values.", nameof(data));
		}

		private static void NoteBlockingRead()
		{
			CilboxBasisCommon box = Running();
			if (box == null) return;
			int now = Time.frameCount;
			if (box.computeBlockingFrame == now) return;
			box.computeBlockingFrames = box.computeBlockingFrame == now - 1 ? box.computeBlockingFrames + 1 : 1;
			box.computeBlockingFrame = now;
			if (box.computeBlockingFrames < BlockingWarnFrames || box.computeBlockingWarned) return;
			box.computeBlockingWarned = true;
			Debug.LogWarning("[Cilbox] " + CilboxBasisConsent.NameOf(box) + " reads a compute buffer with GetData every frame, which stops the game until the GPU catches up. BasisComputeShim.TryGetDataAsync reads it without waiting.");
		}

		internal static long RemainingBytes(object self)
		{
			CilboxBasisCommon box = Owner(self);
			if (box == null) return 0;
			long total = Prune();
			return Math.Max(0, Math.Min(Bytes(box) - box.computeBytes, BasisComputeShim.TotalBytes - total));
		}

		internal static long RemainingThreads(object self)
		{
			CilboxBasisCommon box = Owner(self);
			if (box == null) return 0;
			int now = Time.frameCount;
			long used = box.computeFrame == now ? box.computeFrameThreads : 0, all = frame == now ? frameThreads : 0;
			return Math.Max(0, Math.Min(Threads(box) - used, BasisComputeShim.TotalThreadsPerFrame - all));
		}

		internal static void Release(CilboxBasisCommon box)
		{
			boxes.Remove(box);
			Free(box);
		}

		private static CilboxBasisCommon Owner(object self) => self is CilboxProxy proxy && proxy != null && proxy.box is CilboxBasisCommon box && box != null ? box : null;

		private static long Bytes(CilboxBasisCommon box) => box is CilboxAvatarBasis ? BasisComputeShim.AvatarBytes : box is CilboxPropBasis ? BasisComputeShim.PropBytes : BasisComputeShim.WorldBytes;

		private static long Threads(CilboxBasisCommon box) => box is CilboxAvatarBasis ? BasisComputeShim.AvatarThreadsPerFrame : box is CilboxPropBasis ? BasisComputeShim.PropThreadsPerFrame : BasisComputeShim.WorldThreadsPerFrame;

		private static void CheckGroups(int groups, string name)
		{
			if (groups < 0 || groups > BasisComputeShim.MaxThreadGroups) throw new ArgumentOutOfRangeException(name, groups, "A thread group count must be between 0 and " + BasisComputeShim.MaxThreadGroups + ".");
		}

		private static void CheckCount(object src, object dst, long dstBytes, int dstOffsetBytes)
		{
			if (src == null) throw new ArgumentNullException(nameof(src));
			if (dst == null) throw new ArgumentNullException(nameof(dst));
			if (dstOffsetBytes < 0 || (dstOffsetBytes & 3) != 0 || dstOffsetBytes > dstBytes - 4) throw new ArgumentOutOfRangeException(nameof(dstOffsetBytes), dstOffsetBytes, "The count is 4 bytes and has to land 4-byte aligned inside the destination buffer.");
		}

		private static CilboxBasisCommon Charge(int count, int stride)
		{
			if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), count, "A compute buffer needs at least one element.");
			if (stride <= 0) throw new ArgumentOutOfRangeException(nameof(stride), stride, "A compute buffer stride has to be positive.");
			long bytes = (long)count * stride;
			if (bytes > BasisComputeShim.MaxBufferBytes) throw new InvalidOperationException("A compute buffer can hold at most " + BasisComputeShim.MaxBufferBytes + " bytes; this one asks for " + bytes + ".");
			CilboxBasisCommon box = Running();
			if (box == null) throw new InvalidOperationException("Basis could not tell which content is creating this compute buffer.");
			long total = Prune();
			if (box.computeBytes + bytes > Bytes(box)) throw new InvalidOperationException(CilboxBasisConsent.NameOf(box) + " already holds " + box.computeBytes + " bytes of compute buffers and can hold " + Bytes(box) + ".");
			if (total + bytes > BasisComputeShim.TotalBytes) throw new InvalidOperationException("All content together already holds " + total + " bytes of compute buffers and can hold " + BasisComputeShim.TotalBytes + ".");
			return box;
		}

		private static void Track(CilboxBasisCommon box, IDisposable buffer, long bytes)
		{
			box.computeBuffers ??= new List<(WeakReference<IDisposable> Buffer, long Bytes)>();
			box.computeBuffers.Add((new WeakReference<IDisposable>(buffer), bytes));
			box.computeBytes += bytes;
		}

		private static CilboxBasisCommon Running()
		{
			CilboxBasisCommon running = null;
			int found = 0;
			for (int i = boxes.Count - 1; i >= 0; i--)
			{
				CilboxBasisCommon box = boxes[i];
				if (box == null)
				{
					boxes.RemoveAt(i);
					Free(box);
					continue;
				}
				if (box.disabled || box.interpreterAccountingDepth == 0) continue;
				running = box;
				found++;
			}
			return found == 1 ? running : null;
		}

		private static bool Spend(CilboxBasisCommon box, long threads)
		{
			int now = Time.frameCount;
			if (frame != now)
			{
				frame = now;
				frameThreads = 0;
			}
			if (frameThreads + threads > BasisComputeShim.TotalThreadsPerFrame) return false;
			if (box != null)
			{
				if (box.computeFrame != now)
				{
					box.computeFrame = now;
					box.computeFrameThreads = 0;
				}
				if (box.computeFrameThreads + threads > Threads(box)) return false;
				box.computeFrameThreads += threads;
			}
			frameThreads += threads;
			return true;
		}

		private static void Warn(CilboxBasisCommon box)
		{
			if (box != null ? box.computeWarned : warned) return;
			if (box != null) box.computeWarned = true;
			else warned = true;
			Debug.LogWarning("[Cilbox] " + (box != null ? CilboxBasisConsent.NameOf(box) : "A script") + " went over the compute thread budget for this frame, so a Dispatch was skipped. BasisComputeShim.RemainingThreads(this) says how much is left.");
		}

		private static long Prune()
		{
			long total = 0;
			for (int i = boxes.Count - 1; i >= 0; i--)
			{
				CilboxBasisCommon box = boxes[i];
				if (box == null)
				{
					boxes.RemoveAt(i);
					Free(box);
					continue;
				}
				List<(WeakReference<IDisposable> Buffer, long Bytes)> owned = box.computeBuffers;
				if (owned != null)
				{
					for (int j = owned.Count - 1; j >= 0; j--)
					{
						if (Alive(owned[j].Buffer)) continue;
						box.computeBytes -= owned[j].Bytes;
						owned.RemoveAt(j);
					}
				}
				total += box.computeBytes;
			}
			return total;
		}

		private static void Free(CilboxBasisCommon box)
		{
			if (ReferenceEquals(box, null) || box.computeBuffers == null) return;
			foreach ((WeakReference<IDisposable> owned, long _) in box.computeBuffers)
			{
				if (!owned.TryGetTarget(out IDisposable buffer)) continue;
				try { buffer.Dispose(); } catch (Exception) { }
			}
			box.computeBuffers = null;
			box.computeBytes = 0;
		}

		private static bool Alive(WeakReference<IDisposable> owned) => owned.TryGetTarget(out IDisposable buffer) && (buffer is ComputeBuffer compute ? compute.IsValid() : buffer is GraphicsBuffer graphics && graphics.IsValid());
	}
}
