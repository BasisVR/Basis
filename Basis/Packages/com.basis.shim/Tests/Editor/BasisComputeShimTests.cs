using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisComputeShimTests
    {
        const string TestShaderPath = "Packages/com.basis.imagepickup/Resources/BasisImageDepthVisibility.compute";

        static readonly Type[] BoxTypes = { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) };

        static readonly Type[] Exposed = { typeof(ComputeShader), typeof(ComputeBuffer), typeof(GraphicsBuffer), typeof(LocalKeyword), typeof(LocalKeywordSpace) };

        static readonly Type[] Values = { typeof(ComputeBufferType), typeof(ComputeBufferMode), typeof(GraphicsBuffer.Target), typeof(GraphicsBuffer.UsageFlags), typeof(ShaderKeywordType), typeof(RenderTextureSubElement), typeof(BasisComputeShim) };

        static readonly HashSet<(Type, string)> HeldBack = new HashSet<(Type, string)>
        {
            (typeof(ComputeShader), nameof(ComputeShader.DispatchIndirect)),
            (typeof(ComputeShader), nameof(ComputeShader.SetTextureFromGlobal)),
            (typeof(ComputeShader), nameof(ComputeShader.SetRayTracingAccelerationStructure)),
            (typeof(ComputeBuffer), nameof(ComputeBuffer.GetNativeBufferPtr)),
            (typeof(ComputeBuffer), nameof(ComputeBuffer.BeginWrite)),
            (typeof(ComputeBuffer), nameof(ComputeBuffer.EndWrite)),
            (typeof(GraphicsBuffer), nameof(GraphicsBuffer.GetNativeBufferPtr)),
            (typeof(GraphicsBuffer), nameof(GraphicsBuffer.LockBufferForWrite)),
            (typeof(GraphicsBuffer), nameof(GraphicsBuffer.UnlockBufferAfterWrite)),
            (typeof(GraphicsBuffer), "get_" + nameof(GraphicsBuffer.bufferHandle)),
        };

        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<CilboxBasisCommon> running = new List<CilboxBasisCommon>();
        readonly List<IDisposable> buffers = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            foreach (CilboxBasisCommon box in running) box.interpreterAccountingDepth = 0;
            running.Clear();
            foreach (IDisposable buffer in buffers) buffer?.Dispose();
            buffers.Clear();
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        CilboxBasisCommon Box(Type boxType)
        {
            GameObject host = new GameObject(boxType.Name + " compute test");
            hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }

        static CilboxProxy Proxy(CilboxBasisCommon box)
        {
            CilboxProxy proxy = box.gameObject.AddComponent<CilboxProxy>();
            proxy.box = box;
            return proxy;
        }

        void Run(CilboxBasisCommon box)
        {
            box.interpreterAccountingDepth = 1;
            running.Add(box);
        }

        ComputeBuffer Create(MethodInfo create, params object[] arguments)
        {
            ComputeBuffer buffer = (ComputeBuffer)create.Invoke(null, arguments);
            buffers.Add(buffer);
            return buffer;
        }

        static SerializedTypeDescriptor[] Descriptors(IEnumerable<Type> types) => types.Select(SerializedTypeDescriptorBuilder.FromNativeType).ToArray();

        static MethodBase Resolve(CilboxBasisCommon box, MethodBase method) =>
            new CilboxUsage(box).GetNativeMethodFromTypeAndName(method.DeclaringType, method.IsConstructor ? ".ctor" : method.Name, Descriptors(method.GetParameters().Select(p => p.ParameterType)), Array.Empty<SerializedTypeDescriptor>(), method.ToString());

        static bool Allowed(CilboxBasisCommon box, Type declaringType, string name) =>
            box.CheckMethodAllowed(out _, declaringType, name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), name);

        static MethodInfo Redirect(CilboxBasisCommon box, Type declaringType, string name, params Type[] parameters)
        {
            Assert.IsTrue(box.CheckMethodAllowed(out MethodInfo method, declaringType, name, Descriptors(parameters), Array.Empty<SerializedTypeDescriptor>(), name),
                $"{box.GetType().Name} refuses {declaringType.Name}.{name}({string.Join(", ", parameters.Select(p => p.Name))}).");
            Assert.IsNotNull(method, $"{declaringType.Name}.{name} is allowed without going through the Basis compute limits.");
            Assert.AreEqual("CilboxCompute", method.DeclaringType.Name);
            return method;
        }

        static Exception Refusal(MethodInfo method, params object[] arguments) =>
            Assert.Throws<TargetInvocationException>(() => method.Invoke(null, arguments)).InnerException;

        [Test]
        public void TheComputeTypes_AreVisibleToEveryBox()
        {
            foreach (Type boxType in BoxTypes)
            {
                CilboxBasisCommon box = Box(boxType);
                foreach (Type type in Exposed.Concat(Values))
                {
                    Assert.IsTrue(box.CheckTypeAllowed(type), $"{boxType.Name} rejects {type.FullName}, so any script naming it fails to load.");
                }
            }
        }

        [Test]
        public void EveryExposedType_HasAMethodPin()
        {
            FieldInfo field = typeof(CilboxBasisCommon).GetField("commonMethodWhitelist", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "CilboxBasisCommon no longer has a commonMethodWhitelist field; this test cannot see what it restricts.");
            var pins = (Dictionary<Type, HashSet<string>>)field.GetValue(null);
            foreach (Type type in Exposed)
            {
                Assert.IsTrue(pins.ContainsKey(type), $"{type.Name} is type-whitelisted with no method pin. The gate is default-allow, so its whole surface would be callable from every box.");
            }
        }

        [Test]
        public void ThePublicSurface_LoadsOrIsHeldBack_InEveryBox()
        {
            foreach (Type boxType in BoxTypes)
            {
                CilboxBasisCommon box = Box(boxType);
                foreach (Type type in Exposed.Append(typeof(BasisComputeShim)))
                {
                    MethodInfo[] open = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    foreach (MethodInfo method in open)
                    {
                        if (HeldBack.Contains((type, method.Name)))
                        {
                            Assert.IsFalse(Allowed(box, type, method.Name), $"{boxType.Name}: {type.Name}.{method.Name} became callable from a script.");
                        }
                        else if (!method.IsGenericMethodDefinition)
                        {
                            Assert.IsNotNull(Resolve(box, method), $"{boxType.Name}: {type.Name}.{method} does not load in a script. Pin it in CilboxBasisCommon, or list it in {nameof(HeldBack)}.");
                        }
                    }
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (open.Any(m => m.Name == method.Name)) continue;
                        Assert.IsFalse(Allowed(box, type, method.Name), $"{boxType.Name}: {type.Name}.{method.Name} is not public, yet a bundle that names it passes the method gate.");
                    }
                }
                Assert.IsNotNull(Resolve(box, typeof(LocalKeyword).GetConstructor(new[] { typeof(ComputeShader), typeof(string) })), boxType.Name);
                Assert.IsNotNull(Resolve(box, typeof(LocalKeyword).GetConstructor(new[] { typeof(Shader), typeof(string) })), boxType.Name);
            }
        }

        [Test]
        public void BufferCreation_Dispatch_AndCopyCount_GoThroughTheBasisLimits()
        {
            foreach (Type boxType in BoxTypes)
            {
                CilboxBasisCommon box = Box(boxType);
                Redirect(box, typeof(ComputeBuffer), ".ctor", typeof(int), typeof(int));
                Redirect(box, typeof(ComputeBuffer), ".ctor", typeof(int), typeof(int), typeof(ComputeBufferType));
                Redirect(box, typeof(ComputeBuffer), ".ctor", typeof(int), typeof(int), typeof(ComputeBufferType), typeof(ComputeBufferMode));
                Redirect(box, typeof(GraphicsBuffer), ".ctor", typeof(GraphicsBuffer.Target), typeof(int), typeof(int));
                Redirect(box, typeof(GraphicsBuffer), ".ctor", typeof(GraphicsBuffer.Target), typeof(GraphicsBuffer.UsageFlags), typeof(int), typeof(int));
                MethodInfo dispatch = Redirect(box, typeof(ComputeShader), nameof(ComputeShader.Dispatch), typeof(int), typeof(int), typeof(int), typeof(int));
                CollectionAssert.AreEqual(new[] { typeof(ComputeShader), typeof(int), typeof(int), typeof(int), typeof(int) }, dispatch.GetParameters().Select(p => p.ParameterType), "Dispatch must take the shader the script called it on first.");
                Redirect(box, typeof(ComputeBuffer), nameof(ComputeBuffer.CopyCount), typeof(ComputeBuffer), typeof(ComputeBuffer), typeof(int));
                Redirect(box, typeof(GraphicsBuffer), nameof(GraphicsBuffer.CopyCount), typeof(GraphicsBuffer), typeof(ComputeBuffer), typeof(int));
                Assert.IsTrue(box.CheckMethodAllowed(out MethodInfo probe, typeof(ComputeShader), nameof(ComputeShader.Dispatch), null, null, "Void Dispatch(Int32, Int32, Int32, Int32)"), $"{boxType.Name}: the SDK window would show Dispatch as blocked.");
                Assert.IsNull(probe);
            }
        }

        [Test]
        public void GlobalBufferBindings_StayClosed_AndMaterialBindingsOpen()
        {
            foreach (Type boxType in BoxTypes)
            {
                CilboxBasisCommon box = Box(boxType);
                Assert.IsFalse(Allowed(box, typeof(Shader), nameof(Shader.SetGlobalBuffer)), $"{boxType.Name}: a global buffer binding outlives the content and reaches Basis's own shaders.");
                Assert.IsFalse(Allowed(box, typeof(Shader), nameof(Shader.SetGlobalConstantBuffer)), $"{boxType.Name}: a global buffer binding outlives the content and reaches Basis's own shaders.");
                Assert.IsNotNull(Resolve(box, typeof(Material).GetMethod(nameof(Material.SetBuffer), new[] { typeof(string), typeof(ComputeBuffer) })), boxType.Name);
                Assert.IsNotNull(Resolve(box, typeof(MaterialPropertyBlock).GetMethod(nameof(MaterialPropertyBlock.SetBuffer), new[] { typeof(string), typeof(GraphicsBuffer) })), boxType.Name);
            }
        }

        [Test]
        public void TheShim_OnlyOpensItsEntryPoints()
        {
            foreach (Type boxType in BoxTypes)
            {
                CilboxBasisCommon box = Box(boxType);
                Assert.IsTrue(Allowed(box, typeof(BasisComputeShim), "get_" + nameof(BasisComputeShim.IsSupported)), boxType.Name);
                Assert.IsTrue(Allowed(box, typeof(BasisComputeShim), nameof(BasisComputeShim.RemainingBytes)), boxType.Name);
                Assert.IsTrue(Allowed(box, typeof(BasisComputeShim), nameof(BasisComputeShim.RemainingThreads)), boxType.Name);
                Assert.IsFalse(Allowed(box, typeof(BasisComputeShim), "Charge"), boxType.Name);
            }
        }

        [Test]
        public void EachContentKind_StartsWithItsOwnBudget()
        {
            (Type Box, long Bytes, long Threads)[] kinds =
            {
                (typeof(CilboxSceneBasis), BasisComputeShim.WorldBytes, BasisComputeShim.WorldThreadsPerFrame),
                (typeof(CilboxPropBasis), BasisComputeShim.PropBytes, BasisComputeShim.PropThreadsPerFrame),
                (typeof(CilboxAvatarBasis), BasisComputeShim.AvatarBytes, BasisComputeShim.AvatarThreadsPerFrame),
            };
            foreach ((Type boxType, long bytes, long threads) in kinds)
            {
                CilboxProxy proxy = Proxy(Box(boxType));
                Assert.AreEqual(bytes, BasisComputeShim.RemainingBytes(proxy), boxType.Name);
                Assert.AreEqual(threads, BasisComputeShim.RemainingThreads(proxy), boxType.Name);
            }
            Assert.AreEqual(0, BasisComputeShim.RemainingBytes(new object()));
            Assert.AreEqual(0, BasisComputeShim.RemainingThreads(null));
        }

        [Test]
        public void ABuffer_NeedsOneRunningOwner_AndAPlausibleSize()
        {
            CilboxBasisCommon world = Box(typeof(CilboxSceneBasis)), prop = Box(typeof(CilboxPropBasis));
            MethodInfo create = Redirect(world, typeof(ComputeBuffer), ".ctor", typeof(int), typeof(int));
            Redirect(prop, typeof(ComputeBuffer), ".ctor", typeof(int), typeof(int));
            Assert.IsInstanceOf<InvalidOperationException>(Refusal(create, 4, 4), "A buffer was created with no script running.");
            Run(world);
            Assert.IsInstanceOf<InvalidOperationException>(Refusal(create, (int)(BasisComputeShim.MaxBufferBytes / 4) + 1, 4), "A buffer went over the per-buffer limit.");
            Assert.IsInstanceOf<ArgumentOutOfRangeException>(Refusal(create, 0, 4));
            Assert.IsInstanceOf<ArgumentOutOfRangeException>(Refusal(create, 4, -4));
            Run(prop);
            Assert.IsInstanceOf<InvalidOperationException>(Refusal(create, 4, 4), "A buffer was charged to one of two running contents at random.");
        }

        [Test]
        public void Buffers_CountAgainstTheirContent_AndAreFreedWithIt()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("This graphics device has no compute shaders.");
            CilboxBasisCommon avatar = Box(typeof(CilboxAvatarBasis));
            CilboxProxy proxy = Proxy(avatar);
            MethodInfo create = Redirect(avatar, typeof(ComputeBuffer), ".ctor", typeof(int), typeof(int));
            Run(avatar);
            ComputeBuffer first = Create(create, 1024, 16);
            Assert.IsTrue(first.IsValid());
            Assert.AreEqual(BasisComputeShim.AvatarBytes - 16384, BasisComputeShim.RemainingBytes(proxy));
            Assert.IsInstanceOf<InvalidOperationException>(Refusal(create, (int)(BasisComputeShim.AvatarBytes / 16), 16), "An avatar went over its compute memory budget.");
            first.Release();
            Assert.AreEqual(BasisComputeShim.AvatarBytes, BasisComputeShim.RemainingBytes(proxy), "A released buffer still counts against its content.");
            ComputeBuffer second = Create(create, 256, 4);
            avatar.interpreterAccountingDepth = 0;
            UnityEngine.Object.DestroyImmediate(avatar.gameObject);
            BasisComputeShim.RemainingBytes(Proxy(Box(typeof(CilboxPropBasis))));
            Assert.IsFalse(second.IsValid(), "A compute buffer outlived the content that created it.");
        }

        [Test]
        public void ADispatchOverTheFrameBudget_IsSkipped()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("This graphics device has no compute shaders.");
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(TestShaderPath);
            Assert.IsNotNull(shader, TestShaderPath + " moved; this test needs any compute shader with a kernel 0.");
            if (!shader.IsSupported(0)) Assert.Ignore("The test kernel does not run on this graphics device.");
            CilboxBasisCommon avatar = Box(typeof(CilboxAvatarBasis));
            CilboxProxy proxy = Proxy(avatar);
            MethodInfo dispatch = Redirect(avatar, typeof(ComputeShader), nameof(ComputeShader.Dispatch), typeof(int), typeof(int), typeof(int), typeof(int));
            Run(avatar);
            LogAssert.Expect(LogType.Warning, new Regex("compute thread budget"));
            dispatch.Invoke(null, new object[] { shader, 0, BasisComputeShim.MaxThreadGroups, BasisComputeShim.MaxThreadGroups, 1 });
            Assert.AreEqual(BasisComputeShim.AvatarThreadsPerFrame, BasisComputeShim.RemainingThreads(proxy), "A skipped Dispatch was still charged.");
            Assert.IsInstanceOf<ArgumentOutOfRangeException>(Refusal(dispatch, shader, 0, -1, 1, 1));
            Assert.IsInstanceOf<ArgumentOutOfRangeException>(Refusal(dispatch, shader, 0, 1, BasisComputeShim.MaxThreadGroups + 1, 1));
            Assert.IsInstanceOf<NullReferenceException>(Refusal(dispatch, null, 0, 1, 1, 1));
        }

        [Test]
        public void CopyCount_StaysInsideTheDestination()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("This graphics device has no compute shaders.");
            CilboxBasisCommon prop = Box(typeof(CilboxPropBasis));
            MethodInfo create = Redirect(prop, typeof(ComputeBuffer), ".ctor", typeof(int), typeof(int), typeof(ComputeBufferType));
            MethodInfo copy = Redirect(prop, typeof(ComputeBuffer), nameof(ComputeBuffer.CopyCount), typeof(ComputeBuffer), typeof(ComputeBuffer), typeof(int));
            Run(prop);
            ComputeBuffer append = Create(create, 16, 4, ComputeBufferType.Append);
            ComputeBuffer counts = Create(create, 2, 4, ComputeBufferType.Raw);
            foreach (int offset in new[] { -4, 2, 8 })
            {
                Assert.IsInstanceOf<ArgumentOutOfRangeException>(Refusal(copy, append, counts, offset), "offset " + offset);
            }
            Assert.IsInstanceOf<ArgumentNullException>(Refusal(copy, append, null, 0));
        }
    }
}
