using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Cilbox;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisCilboxProfilerTests
    {
        const string Probe = "ZZProfilerProbe";
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        static readonly Assembly Shims = typeof(CilboxBasisCommon).Assembly;
        static readonly Type Profiler = Shims.GetType("Cilbox.CilboxProfiler", true);
        static readonly Type Entry = Shims.GetType("Cilbox.CilboxProfileEntry", true);
        static readonly Type View = Shims.GetType("Cilbox.CilboxProfilerView", true);
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            Call(Profiler, "SetActive", false);
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        static object Call(Type type, string name, params object[] args)
        {
            MethodInfo method = type.GetMethod(name, Hidden);
            Assert.IsNotNull(method, $"{type.Name}.{name} moved; this test cannot drive the profiler.");
            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
        }

        static object Static(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(name, Hidden);
            if (property != null) return property.GetValue(null);
            FieldInfo field = type.GetField(name, Hidden);
            Assert.IsNotNull(field, $"{type.Name}.{name} moved; this test cannot read the profiler.");
            return field.GetValue(null);
        }

        static float Measure(string text)
        {
            return Regex.Replace(text, "<[^>]*>", string.Empty).Length * 10f;
        }

        static void AssertNoOverlap(string table, float width)
        {
            string[] lines = table.Split('\n');
            for (int l = 0; l < lines.Length; l++)
            {
                string[] parts = Regex.Split(lines[l], "<pos=([0-9.]+)>");
                float end = Measure(parts[0]);
                for (int p = 1; p + 1 < parts.Length; p += 2)
                {
                    float start = float.Parse(parts[p], CultureInfo.InvariantCulture);
                    Assert.LessOrEqual(end, start + 0.05f, $"Cell starting at {start} overlaps the one before it: {lines[l]}");
                    end = start + Measure(parts[p + 1]);
                }
                Assert.LessOrEqual(end, width + 0.05f, $"Line runs past the table: {lines[l]}");
            }
        }

        static void AssertColor(Color expected, Color actual, string what)
        {
            Assert.That(Mathf.Abs(expected.r - actual.r) < 0.02f && Mathf.Abs(expected.g - actual.g) < 0.02f && Mathf.Abs(expected.b - actual.b) < 0.02f && Mathf.Abs(expected.a - actual.a) < 0.02f, $"{what}: expected {expected}, got {actual}");
        }

        static object Field(object target, string name)
        {
            FieldInfo field = Entry.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"CilboxProfileEntry.{name} moved; this test cannot read the profiler.");
            return field.GetValue(target);
        }

        static object Find(string script)
        {
            IList entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Entry));
            Call(Profiler, "Collect", entries);
            foreach (object entry in entries)
            {
                if ((string)Field(entry, "Script") == Probe + "." + script) return entry;
            }
            return null;
        }

        static long Calls(string script)
        {
            object entry = Find(script);
            return entry == null ? 0 : (long)Field(entry, "TotalCalls");
        }

        static long Ticks(string script)
        {
            object entry = Find(script);
            return entry == null ? 0 : (long)Field(entry, "TotalTicks");
        }

        CilboxSceneBasis AddBox()
        {
            GameObject host = new GameObject("Cilbox profiler test");
            hosts.Add(host);
            CilboxSceneBasis box = host.AddComponent<CilboxSceneBasis>();
            box.assemblyData = BuildAssembly();
            box.BoxInitialize();
            return box;
        }

        static CilboxProxy AddProxy(CilboxSceneBasis box)
        {
            CilboxProxy proxy = box.gameObject.AddComponent<CilboxProxy>();
            proxy.box = box;
            proxy.cls = box.GetClass(Probe);
            proxy.fields = new StackElement[1];
            proxy.fields[0].LoadInt(0);
            return proxy;
        }

        static CilboxMethod Method(CilboxSceneBasis box, string name)
        {
            CilboxClass cls = box.GetClass(Probe);
            return cls.methods[cls.methodNameToIndex[name]];
        }

        static string BuildAssembly(bool native = false)
        {
            SerializedTypeDescriptor probe = new SerializedTypeDescriptor { assemblyName = "Assembly-CSharp", typeName = Probe };
            SerializedTypeDescriptor int32 = SerializedTypeDescriptorBuilder.FromNativeType(typeof(int));
            byte shift = (byte)(native ? 1 : 0), hits = (byte)(1 + shift), helperToken = (byte)(2 + shift), computeToken = (byte)(3 + shift), throwerToken = (byte)(4 + shift);
            byte[] spin = { 0x20, 0xD0, 0x07, 0x00, 0x00, 0x17, 0x59, 0x25, 0x2D, 0xFB, 0x26 };
            List<byte> update = new List<byte> { 0x02, 0x02, 0x7B, hits, 0, 0, 0, 0x17, 0x58, 0x7D, hits, 0, 0, 0 };
            update.AddRange(spin);
            update.AddRange(new byte[] { 0x02, 0x28, helperToken, 0, 0, 0, 0x2A });
            List<byte> helper = new List<byte>(spin) { 0x2A };
            List<SerializedMetadataToken> metadata = new List<SerializedMetadataToken>();
            if (native)
            {
                metadata.Add(new SerializedMetadataToken
                {
                    metaTokenIndex = 1,
                    metaTokenType = (byte)MetaTokenType.mtMethod,
                    typeDescriptor = SerializedTypeDescriptorBuilder.FromNativeType(typeof(Mathf)),
                    name = nameof(Mathf.Abs),
                    methodFullSignature = "Single Abs(Single)",
                    methodAssembly = typeof(Mathf).Assembly.GetName().Name,
                    isStatic = true,
                    methodParameters = new[] { SerializedTypeDescriptorBuilder.FromNativeType(typeof(float)) },
                    methodGenericArguments = Array.Empty<SerializedTypeDescriptor>(),
                });
            }
            metadata.Add(new SerializedMetadataToken { metaTokenIndex = hits, metaTokenType = (byte)MetaTokenType.mtField, typeDescriptor = probe, name = "hits", fieldHasIndex = true, fieldIndex = 0 });
            metadata.Add(MethodToken(helperToken, probe, "Helper", "Void Helper()"));
            metadata.Add(MethodToken(computeToken, probe, "Compute", "Int32 Compute(Int32)", int32));
            metadata.Add(MethodToken(throwerToken, probe, "Thrower", "Void Thrower()"));
            SerializedAssembly assembly = new SerializedAssembly
            {
                classes = new[]
                {
                    new SerializedClass
                    {
                        className = Probe,
                        staticFields = Array.Empty<SerializedField>(),
                        instanceFields = new[] { new SerializedField { name = "hits", type = int32 } },
                        baseClassNames = Array.Empty<string>(),
                        methods = new[]
                        {
                            ScriptMethod("Update", "Void Update()", 3, true, update.ToArray()),
                            ScriptMethod("Helper", "Void Helper()", 2, true, helper.ToArray()),
                            ScriptMethod("Compute", "Int32 Compute(Int32)", 2, false, new byte[] { 0x03, 0x19, 0x5A, 0x17, 0x58, 0x2A }, new SerializedField { name = "x", type = int32 }),
                            ScriptMethod("Thrower", "Void Thrower()", 1, true, new byte[] { 0x14, 0x7B, hits, 0, 0, 0, 0x26, 0x2A }),
                            ScriptMethod("Start", "Void Start()", 1, true, new byte[] { 0xFE, 0x06, helperToken, 0, 0, 0, 0x26, 0xFE, 0x06, computeToken, 0, 0, 0, 0x26, 0xFE, 0x06, throwerToken, 0, 0, 0, 0x26, 0x2A }),
                        },
                    },
                },
                metadata = metadata.ToArray(),
                enums = Array.Empty<SerializedEnum>(),
            };
            return assembly.SerializeString();
        }

        static SerializedMethod ScriptMethod(string name, string signature, int stack, bool isVoid, byte[] body, params SerializedField[] parameters)
        {
            return new SerializedMethod
            {
                methodName = name,
                fullSignature = signature,
                maxStack = stack,
                isVoid = isVoid,
                body = body,
                locals = Array.Empty<SerializedField>(),
                parameters = parameters,
                exceptionHandlers = Array.Empty<SerializedExceptionHandler>(),
            };
        }

        static SerializedMetadataToken MethodToken(int index, SerializedTypeDescriptor owner, string name, string signature, params SerializedTypeDescriptor[] parameters)
        {
            return new SerializedMetadataToken
            {
                metaTokenIndex = index,
                metaTokenType = (byte)MetaTokenType.mtMethod,
                typeDescriptor = owner,
                name = name,
                methodFullSignature = signature,
                methodAssembly = "Assembly-CSharp",
                methodParameters = parameters,
                methodGenericArguments = Array.Empty<SerializedTypeDescriptor>(),
            };
        }

        [Test]
        public void WrappedCallbacks_KeepTheirBehaviour_AndAreTimedPerCallback()
        {
            CilboxSceneBasis box = AddBox();
            CilboxProxy proxy = AddProxy(box);
            CilboxClass cls = box.GetClass(Probe);
            byte[] update = Method(box, "Update").byteCode;

            Call(Profiler, "SetActive", true);

            Assert.AreNotSame(update, Method(box, "Update").byteCode, "Update was not wrapped.");
            for (int i = 0; i < 3; i++) cls.InterpretMethod(proxy, ImportFunctionID.Update, null);
            Assert.AreEqual(3, proxy.fields[0].i, "The wrapped Update must still write its field.");
            Assert.AreEqual(16, Convert.ToInt32(cls.InterpretMethod(proxy, "Compute", new object[] { 5 })), "The wrapper must hand back the script's return value.");
            cls.InterpretMethod(proxy, "Helper", null);
            Call(Profiler, "Tick");

            Assert.AreEqual(3, Calls("Update"));
            Assert.Greater(Ticks("Update"), 0);
            Assert.AreEqual(1, Calls("Compute"));
            Assert.AreEqual(1, Calls("Helper"), "Update's own calls into Helper belong to Update, not to Helper.");
            Assert.Greater(Ticks("Update"), Ticks("Helper"));
        }

        [Test]
        public void TurningProfilingOff_RestoresTheOriginalCode_AndOnAgainReusesTheWrappers()
        {
            CilboxSceneBasis box = AddBox();
            CilboxProxy proxy = AddProxy(box);
            CilboxClass cls = box.GetClass(Probe);
            byte[] update = Method(box, "Update").byteCode;
            byte[] helper = Method(box, "Helper").byteCode;
            int methods = cls.methods.Length;

            Call(Profiler, "SetActive", true);
            int tokens = box.metadatas.Length;
            int wrapped = cls.methods.Length;
            Assert.Greater(tokens, 5);
            Assert.Greater(wrapped, methods);

            Call(Profiler, "SetActive", false);
            Assert.AreSame(update, Method(box, "Update").byteCode);
            Assert.AreSame(helper, Method(box, "Helper").byteCode);
            cls.InterpretMethod(proxy, ImportFunctionID.Update, null);
            Assert.AreEqual(1, proxy.fields[0].i);
            Assert.IsNull(Find("Update"));

            Call(Profiler, "SetActive", true);
            Assert.AreNotSame(update, Method(box, "Update").byteCode);
            Assert.AreEqual(tokens, box.metadatas.Length, "Turning profiling back on should reuse the wrappers instead of growing the box.");
            Assert.AreEqual(wrapped, cls.methods.Length);
            cls.InterpretMethod(proxy, ImportFunctionID.Update, null);
            Call(Profiler, "Tick");
            Assert.AreEqual(2, proxy.fields[0].i);
            Assert.AreEqual(1, Calls("Update"));
        }

        [Test]
        public void AThrowingCallback_StillDisablesOnlyItsProxy_AndTheNextCallbackIsTimed()
        {
            CilboxSceneBasis box = AddBox();
            CilboxProxy proxy = AddProxy(box);
            CilboxProxy thrower = AddProxy(box);
            CilboxClass cls = box.GetClass(Probe);

            Call(Profiler, "SetActive", true);

            Assert.Catch<NullReferenceException>(() => cls.InterpretMethod(thrower, "Thrower", null));
            Assert.IsTrue(thrower.disabled);
            Assert.IsFalse(proxy.disabled);
            Assert.IsFalse(box.disabled);
            cls.InterpretMethod(proxy, ImportFunctionID.Update, null);
            Call(Profiler, "Tick");

            Assert.AreEqual(1, proxy.fields[0].i);
            Assert.AreEqual(1, Calls("Update"), "A frame left open by the exception must not swallow the next callback.");
        }

        [Test]
        public void ABoxWrappedWhileLoading_GetsItsDelegateTargetsOnTheNextTick()
        {
            Call(Profiler, "SetActive", true);
            CilboxSceneBasis box = AddBox();
            byte[] update = Method(box, "Update").byteCode;
            byte[] helper = Method(box, "Helper").byteCode;

            Call(Profiler, "Watch", box, false);
            Assert.AreNotSame(update, Method(box, "Update").byteCode, "Named callbacks are wrapped while the box loads.");
            Assert.AreSame(helper, Method(box, "Helper").byteCode, "Delegate targets wait for the metadata to finish loading.");

            Call(Profiler, "Tick");
            Assert.AreNotSame(helper, Method(box, "Helper").byteCode);
            CilboxProxy proxy = AddProxy(box);
            box.GetClass(Probe).InterpretMethod(proxy, "Helper", null);
            Call(Profiler, "Tick");
            Assert.AreEqual(1, Calls("Helper"));
        }

        [Test]
        public void ABoxThatLoadsWhileProfiling_IsWrappedFromInsideItsOwnInitialize()
        {
            Call(Profiler, "SetActive", true);
            GameObject host = new GameObject("Cilbox profiler load test");
            hosts.Add(host);
            ZZProfiledLoadBox box = host.AddComponent<ZZProfiledLoadBox>();
            box.assemblyData = BuildAssembly(true);
            box.BoxInitialize();

            Assert.IsTrue(box.Hooked, "The native token never reached CheckMethodAllowed, so this test did not wrap anything mid-load.");
            Assert.IsNotNull(box.metadatas[2], "Tokens resolved after the wrapper grew the table must land in the grown table.");
            CilboxProxy proxy = AddProxy(box);
            CilboxClass cls = box.GetClass(Probe);
            byte[] helper = Method(box, "Helper").byteCode;
            cls.InterpretMethod(proxy, ImportFunctionID.Update, null);
            Assert.AreEqual(1, proxy.fields[0].i);
            Call(Profiler, "Tick");
            Assert.AreEqual(1, Calls("Update"), "Update was wrapped while the box loaded, so its first call is already timed.");

            Assert.AreNotSame(helper, Method(box, "Helper").byteCode, "The next tick wraps the delegate targets Start creates.");
            cls.InterpretMethod(proxy, "Helper", null);
            Call(Profiler, "Tick");
            Assert.AreEqual(1, Calls("Helper"));
        }

        [Test]
        public void Clear_ResetsTotals()
        {
            CilboxSceneBasis box = AddBox();
            CilboxProxy proxy = AddProxy(box);
            Call(Profiler, "SetActive", true);
            box.GetClass(Probe).InterpretMethod(proxy, ImportFunctionID.Update, null);
            Call(Profiler, "Tick");
            Assert.AreEqual(1, Calls("Update"));

            Call(Profiler, "Clear");
            Assert.AreEqual(0, Calls("Update"));
            Assert.AreEqual(0, Ticks("Update"));
        }

        [Test]
        public void Names_DropNamespacesAndCompilerNoise_AndCannotCarryMarkup()
        {
            Assert.AreEqual("Board.Update", Call(Profiler, "Pretty", "Example.Games.Board", "Update"));
            Assert.AreEqual("Board.[Start]b__5_0", Call(Profiler, "Pretty", "Example.Games.Board+<>c__DisplayClass5_0", "<Start>b__5_0"));
            Assert.AreEqual("Board.Cell.Tick", Call(Profiler, "Pretty", "Example.Board+Cell", "Tick"));
            Assert.AreEqual("[b]Big[/b]", Call(Profiler, "Clean", "<b>Big</b>", 40));
            Assert.AreEqual("A B", Call(Profiler, "Clean", "A\nB", 40));
            Assert.AreEqual("abcd…", Call(Profiler, "Clean", "abcdefgh", 5));
            Assert.AreEqual("?", Call(Profiler, "Clean", "", 5));
        }

        [Test]
        public void TheTable_ListsTheSlowestScriptFirst_AndNoCellsOverlap()
        {
            CilboxSceneBasis box = AddBox();
            CilboxProxy proxy = AddProxy(box);
            CilboxClass cls = box.GetClass(Probe);
            Call(Profiler, "SetActive", true);
            for (int i = 0; i < 3; i++) cls.InterpretMethod(proxy, ImportFunctionID.Update, null);
            cls.InterpretMethod(proxy, "Helper", null);
            Call(Profiler, "Tick");
            Func<string, float> measure = Measure;

            string wide = (string)Call(View, "RenderTable", "total", 5000f, measure);
            int update = wide.IndexOf(Probe + ".Update", StringComparison.Ordinal);
            int helper = wide.IndexOf(Probe + ".Helper", StringComparison.Ordinal);
            Assert.GreaterOrEqual(update, 0, wide);
            Assert.Greater(helper, update, wide);
            StringAssert.DoesNotContain(Probe + ".Thrower", wide, "A callback that never ran has no row.");
            AssertNoOverlap(wide, 5000f);

            string narrow = (string)Call(View, "RenderTable", "total", 900f, measure);
            AssertNoOverlap(narrow, float.MaxValue);
            StringAssert.Contains("…", narrow, "A column too narrow for its text truncates it instead of running into the next one.");
        }

        [Test]
        public void Fit_KeepsWhatFits_AndCutsTheRestWithAnEllipsis()
        {
            Func<string, float> measure = Measure;
            Assert.AreEqual("abcdefgh", Call(View, "Fit", "abcdefgh", 80f, false, measure));
            Assert.AreEqual("abcd…", Call(View, "Fit", "abcdefgh", 50f, false, measure));
            Assert.AreEqual("…", Call(View, "Fit", "abcdefgh", 5f, false, measure));
        }

        [Test]
        public void TheGraph_RecordsEachFrame_InTheRowOfTheScriptsColor()
        {
            CilboxSceneBasis box = AddBox();
            CilboxProxy proxy = AddProxy(box);
            CilboxClass cls = box.GetClass(Probe);
            int frames = (int)Static(Profiler, "GraphFrames"), slots = (int)Static(Profiler, "GraphSlots");
            Call(Profiler, "SetActive", true);
            cls.InterpretMethod(proxy, ImportFunctionID.Update, null);
            Call(Profiler, "Tick");

            float[] graph = (float[])Static(Profiler, "Graph");
            int head = (int)Static(Profiler, "GraphHead");
            Assert.Greater(graph[slots * frames + head], 0f, "Until the first ranking every script draws in the grey row.");

            Call(Profiler, "Snapshot", Time.realtimeSinceStartupAsDouble);
            object updateEntry = Find("Update");
            int slot = (int)Field(updateEntry, "Slot");
            Assert.That(slot, Is.InRange(0, slots - 1), "The slowest script takes a colored row at the next ranking.");
            cls.InterpretMethod(proxy, ImportFunctionID.Update, null);
            Call(Profiler, "Tick");
            head = (int)Static(Profiler, "GraphHead");
            Assert.Greater(graph[slot * frames + head], 0f);
            Assert.AreEqual(0f, graph[slots * frames + head], "Nothing else ran, so the grey row stays empty.");

            Call(Profiler, "Clear");
            Assert.AreEqual(-1, (int)Field(updateEntry, "Slot"), "Clear hands every colored row back.");
            Assert.AreEqual(0f, Sum(graph));
        }

        [Test]
        public void TheGraphScale_GrowsAtOnce_AndShrinksOnlyWhenWellBelow()
        {
            Type panel = Shims.GetType("Cilbox.CilboxProfilerPanel", true);
            Assert.AreEqual(2f, (float)Call(panel, "Rescale", 1f, 1.5f));
            Assert.AreEqual(2f, (float)Call(panel, "Rescale", 2f, 0.6f));
            Assert.AreEqual(0.5f, (float)Call(panel, "Rescale", 2f, 0.3f));
        }

        [Test]
        public void TheGraphShader_StacksEachRowInItsColorFromTheBottom()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("This run has no graphics device.");
            Shader shader = Resources.Load<Shader>("BasisCilboxProfilerGraph");
            Assert.IsNotNull(shader, "The graph shader did not load from Resources.");
            Assert.IsTrue(shader.isSupported, "The graph shader does not compile for this graphics device.");
            int frames = (int)Static(Profiler, "GraphFrames"), rows = (int)Static(Profiler, "GraphRows");
            Texture2D data = new Texture2D(frames, rows, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            Material material = new Material(shader);
            RenderTexture target = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Texture2D read = new Texture2D(64, 64, TextureFormat.RGBA32, false, true);
            try
            {
                float[] values = new float[frames * rows];
                for (int f = 0; f < frames; f++)
                {
                    values[f] = 1f;
                    values[(rows - 1) * frames + f] = 1f;
                }
                data.SetPixelData(values, 0);
                data.Apply(false, false);
                Vector4[] colors = new Vector4[rows];
                for (int i = 0; i < rows; i++) colors[i] = new Vector4(0f, 1f, 0f, 1f);
                colors[0] = new Vector4(1f, 0f, 0f, 1f);
                colors[rows - 1] = new Vector4(0f, 0f, 1f, 1f);
                material.SetVectorArray("_Colors", colors);
                material.SetVector("_Background", Vector4.zero);
                material.SetVector("_Grid", Vector4.zero);
                material.SetFloat("_Head", 0f);
                material.SetFloat("_Scale", 4f);
                target.Create();
                Graphics.Blit(data, target, material);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                read.Apply();
                RenderTexture.active = previous;

                AssertColor(Color.red, read.GetPixel(32, 6), "The first row fills the bottom of the stack");
                AssertColor(Color.blue, read.GetPixel(32, 24), "The grey row sits on top of the colored rows");
                AssertColor(Color.clear, read.GetPixel(32, 48), "Above the stack is background");
                AssertColor(Color.red, read.GetPixel(1, 6), "Every column of the history draws");
            }
            finally
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(read);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        static float Sum(float[] values)
        {
            float sum = 0f;
            for (int i = 0; i < values.Length; i++) sum += values[i];
            return sum;
        }
    }

    [NoAutoStaticsCleanup]
    public class ZZProfiledLoadBox : CilboxSceneBasis
    {
        public bool Hooked;

        public override bool CheckMethodAllowed(out MethodInfo mi, Type declaringType, string name, SerializedTypeDescriptor[] parametersIn, SerializedTypeDescriptor[] genericArgumentsIn, string fullSignature)
        {
            if (!Hooked)
            {
                Hooked = true;
                typeof(CilboxBasisCommon).Assembly.GetType("Cilbox.CilboxProfiler", true).GetMethod("Watch", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { this, false });
            }
            return base.CheckMethodAllowed(out mi, declaringType, name, parametersIn, genericArgumentsIn, fullSignature);
        }
    }
}
