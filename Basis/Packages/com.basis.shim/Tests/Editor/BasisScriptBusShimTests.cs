using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisScriptBusShimTests
    {
        const string Probe = "ZZBusProbe";
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        static readonly Type Bus = typeof(CilboxBasisCommon).Assembly.GetType("Cilbox.CilboxScriptBus", true);
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            Call("Reset");
            LogAssert.ignoreFailingMessages = false;
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        static object Call(string name)
        {
            MethodInfo method = Bus.GetMethod(name, Hidden, null, Type.EmptyTypes, null);
            Assert.IsNotNull(method, $"CilboxScriptBus.{name} moved; this test cannot drive the bus.");
            try
            {
                return method.Invoke(null, null);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
        }

        static void Tick() => Call("Tick");
        static int Queued => (int)Call("get_QueuedCount");

        T Box<T>(string name) where T : CilboxBasisCommon
        {
            GameObject host = new GameObject(name);
            hosts.Add(host);
            T box = host.AddComponent<T>();
            box.assemblyData = BuildAssembly();
            box.BoxInitialize();
            return box;
        }

        static CilboxProxy Proxy(CilboxBasisCommon box)
        {
            CilboxProxy proxy = box.gameObject.AddComponent<CilboxProxy>();
            proxy.box = box;
            proxy.cls = box.GetClass(Probe);
            proxy.fields = new StackElement[2];
            proxy.fields[0].LoadInt(0);
            proxy.fields[1].LoadObject(null);
            return proxy;
        }

        static int Clicks(CilboxProxy proxy) => (int)proxy.fields[0].AsObject();
        static string Last(CilboxProxy proxy) => proxy.fields[1].AsObject() as string;

        static T Running<T>(CilboxBasisCommon box, Func<T> call)
        {
            box.interpreterAccountingDepth++;
            try
            {
                return call();
            }
            finally
            {
                box.interpreterAccountingDepth--;
            }
        }

        static Action<BasisScriptMessage> Handler(CilboxProxy proxy, string method)
        {
            CilboxClass cls = proxy.cls;
            return (Action<BasisScriptMessage>)CilboxPlatform.ProxyForGeneratingActions<Action<BasisScriptMessage>>(proxy, cls.methods[cls.methodNameToIndex[method]]);
        }

        static bool Listen(CilboxProxy proxy, string channel, string method, int from = BasisScriptBus.FromAnyone) => Running((CilboxBasisCommon)proxy.box, () => BasisScriptBus.Listen(proxy, channel, from, Handler(proxy, method)));

        static bool Method(CilboxBasisCommon box, Type type, string name) =>
            box.CheckTypeAllowed(type) &&
            box.CheckMethodAllowed(out MethodInfo _, type, name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), name + "()");

        static string BuildAssembly()
        {
            SerializedTypeDescriptor probe = new SerializedTypeDescriptor { assemblyName = "Assembly-CSharp", typeName = Probe };
            SerializedTypeDescriptor int32 = SerializedTypeDescriptorBuilder.FromNativeType(typeof(int));
            SerializedTypeDescriptor text = SerializedTypeDescriptorBuilder.FromNativeType(typeof(string));
            SerializedTypeDescriptor message = SerializedTypeDescriptorBuilder.FromNativeType(typeof(BasisScriptMessage));
            const byte clicks = 1, last = 2, getText = 3, reply = 4, chat = 5, hi = 6, broadcast = 7, pong = 8;
            SerializedField arg = new SerializedField { name = "message", type = message };
            SerializedAssembly assembly = new SerializedAssembly
            {
                classes = new[]
                {
                    new SerializedClass
                    {
                        className = Probe,
                        staticFields = Array.Empty<SerializedField>(),
                        instanceFields = new[]
                        {
                            new SerializedField { name = "clicks", type = int32 },
                            new SerializedField { name = "last", type = text },
                        },
                        baseClassNames = Array.Empty<string>(),
                        methods = new[]
                        {
                            ScriptMethod("OnMessage", "Void OnMessage(Basis.Shims.BasisScriptMessage)", 3, new byte[] { 0x02, 0x03, 0x6F, getText, 0, 0, 0, 0x7D, last, 0, 0, 0, 0x02, 0x02, 0x7B, clicks, 0, 0, 0, 0x17, 0x58, 0x7D, clicks, 0, 0, 0, 0x2A }, arg),
                            ScriptMethod("Thrower", "Void Thrower(Basis.Shims.BasisScriptMessage)", 1, new byte[] { 0x14, 0x7B, clicks, 0, 0, 0, 0x26, 0x2A }, arg),
                            ScriptMethod("Ping", "Void Ping(Basis.Shims.BasisScriptMessage)", 3, new byte[] { 0x03, 0x72, pong, 0, 0, 0, 0x6F, reply, 0, 0, 0, 0x26, 0x02, 0x02, 0x7B, clicks, 0, 0, 0, 0x17, 0x58, 0x7D, clicks, 0, 0, 0, 0x2A }, arg),
                            ScriptMethod("SendHi", "Void SendHi()", 3, new byte[] { 0x02, 0x72, chat, 0, 0, 0, 0x72, hi, 0, 0, 0, 0x28, broadcast, 0, 0, 0, 0x26, 0x2A }),
                        },
                    },
                },
                metadata = new[]
                {
                    FieldToken(clicks, probe, "clicks", 0),
                    FieldToken(last, probe, "last", 1),
                    NativeToken(getText, typeof(BasisScriptMessage), "get_" + nameof(BasisScriptMessage.Text), false),
                    NativeToken(reply, typeof(BasisScriptMessage), nameof(BasisScriptMessage.Reply), false, typeof(string)),
                    new SerializedMetadataToken { metaTokenIndex = chat, metaTokenType = (byte)MetaTokenType.mtString, stringValue = "chat" },
                    new SerializedMetadataToken { metaTokenIndex = hi, metaTokenType = (byte)MetaTokenType.mtString, stringValue = "hi" },
                    NativeToken(broadcast, typeof(BasisScriptBus), nameof(BasisScriptBus.Broadcast), true, typeof(object), typeof(string), typeof(string)),
                    new SerializedMetadataToken { metaTokenIndex = pong, metaTokenType = (byte)MetaTokenType.mtString, stringValue = "pong" },
                },
                enums = Array.Empty<SerializedEnum>(),
            };
            return assembly.SerializeString();
        }

        static SerializedMetadataToken FieldToken(int index, SerializedTypeDescriptor owner, string name, int field)
        {
            return new SerializedMetadataToken { metaTokenIndex = index, metaTokenType = (byte)MetaTokenType.mtField, typeDescriptor = owner, name = name, fieldHasIndex = true, fieldIndex = field };
        }

        static SerializedMetadataToken NativeToken(int index, Type owner, string name, bool isStatic, params Type[] parameters)
        {
            SerializedTypeDescriptor[] descriptors = new SerializedTypeDescriptor[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) descriptors[i] = SerializedTypeDescriptorBuilder.FromNativeType(parameters[i]);
            return new SerializedMetadataToken
            {
                metaTokenIndex = index,
                metaTokenType = (byte)MetaTokenType.mtMethod,
                typeDescriptor = SerializedTypeDescriptorBuilder.FromNativeType(owner),
                name = name,
                methodFullSignature = owner.GetMethod(name, parameters).ToString(),
                methodAssembly = owner.Assembly.GetName().Name,
                isStatic = isStatic,
                methodParameters = descriptors,
                methodGenericArguments = Array.Empty<SerializedTypeDescriptor>(),
            };
        }

        static SerializedMethod ScriptMethod(string name, string signature, int stack, byte[] body, params SerializedField[] parameters)
        {
            return new SerializedMethod
            {
                methodName = name,
                fullSignature = signature,
                maxStack = stack,
                isVoid = true,
                body = body,
                locals = Array.Empty<SerializedField>(),
                parameters = parameters,
                exceptionHandlers = Array.Empty<SerializedExceptionHandler>(),
            };
        }

        [Test]
        public void TheEntryPoints_ResolveInEveryBox_AndNothingElseOnTheBusTypesDoes()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                GameObject host = new GameObject(boxType.Name + " gate test");
                hosts.Add(host);
                CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
                box.classes = new Dictionary<string, int>();
                Assert.IsTrue(Method(box, typeof(BasisScriptBus), nameof(BasisScriptBus.Send)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptBus), nameof(BasisScriptBus.Listen)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptBus), "get_" + nameof(BasisScriptBus.World)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptTarget), nameof(BasisScriptTarget.GetString)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptTarget), "get_" + nameof(BasisScriptTarget.OwnerPlayerId)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptMessage), nameof(BasisScriptMessage.Reply)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptMessage), "get_" + nameof(BasisScriptMessage.Sender)), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisScriptTarget), ".ctor"), boxType.Name + " must not forge a target.");
                Assert.IsFalse(Method(box, typeof(BasisScriptMessage), ".ctor"), boxType.Name + " must not forge a message.");
                Assert.IsFalse(Method(box, typeof(BasisScriptMessage), "set_" + nameof(BasisScriptMessage.Sender)), boxType.Name + " must not rewrite who sent a message.");
                Assert.IsFalse(Method(box, typeof(BasisScriptBus), "Reset"), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptBus), nameof(BasisScriptBus.SendToScript)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptMessage), nameof(BasisScriptMessage.GetSharedTransform)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisSharedTransform), nameof(BasisSharedTransform.SetPosition)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptBus), nameof(BasisScriptBus.Share)), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisSharedTransform), ".ctor"), boxType.Name + " must not forge a shared transform.");
                Assert.IsTrue(Method(box, typeof(BasisScriptMessage), "get_" + nameof(BasisScriptMessage.SenderScript)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptTarget), nameof(BasisScriptTarget.GetScript)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptReference), nameof(BasisScriptReference.Resolve)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptReference), "get_" + nameof(BasisScriptReference.ClassName)), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisScriptReference), ".ctor"), boxType.Name + " must not forge a script reference.");
                Assert.IsTrue(Method(box, typeof(BasisSharedTexture), nameof(BasisSharedTexture.GetTexture)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisSharedTexture), "get_" + nameof(BasisSharedTexture.Width)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptMessage), nameof(BasisScriptMessage.GetSharedTexture)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisScriptTarget), nameof(BasisScriptTarget.GetMatrix)), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisSharedTexture), ".ctor"), boxType.Name + " must not forge a shared texture.");
            }
        }

        [Test]
        public void ASend_ArrivesOnTheNextTick_WithTheRealSender_AndItsOwnCopyOfTheData()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            propBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            Assert.IsTrue(Listen(world, "score", "OnMessage"));

            BasisScriptTarget worldTarget = Running(worldBox, () => BasisScriptBus.Self(world));
            BasisScriptTarget propTarget = Running(propBox, () => BasisScriptBus.Self(prop));
            Assert.IsNotNull(worldTarget);
            Assert.AreEqual(BasisScriptBus.KindProp, propTarget.Kind);
            Assert.IsTrue(worldTarget.Listens("score"));
            Assert.IsFalse(propTarget.Listens("score"));

            byte[] data = { 1, 2, 3 };
            int id = Running(propBox, () => BasisScriptBus.Send(prop, worldTarget, "score", "goal", data));
            Assert.Greater(id, 0);
            data[0] = 9;
            Assert.AreEqual(0, Clicks(world), "delivery must never run inside the sender's call");
            object pending = ((System.Collections.IList)Bus.GetField("queue", Hidden).GetValue(null))[0];
            BasisScriptMessage queued = (BasisScriptMessage)pending.GetType().GetField("Message", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pending);
            Assert.AreEqual(1, queued.Data[0], "the sender changing its array after Send must not reach the receiver");
            Assert.AreSame(propTarget, queued.Sender);
            Assert.AreEqual(id, queued.Id);
            Assert.AreEqual("score", queued.Channel);

            Tick();
            Assert.AreEqual(1, Clicks(world));
            Assert.AreEqual("goal", Last(world));
            Assert.AreEqual(0, Queued);
            Assert.AreEqual(0, Running(propBox, () => BasisScriptBus.Send(prop, propTarget, "score", "nobody")), "a target with no listener on the channel reports 0");
        }

        [Test]
        public void ABroadcast_SkipsTheSendingScript_AndRespectsTheFromMask()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            propBox.gameObject.AddComponent<BasisProp>();
            CilboxPropBasis otherBox = Box<CilboxPropBasis>("Other prop");
            otherBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox), other = Proxy(otherBox);
            Assert.IsTrue(Listen(world, "chat", "OnMessage", BasisScriptBus.FromProps));
            Assert.IsTrue(Listen(prop, "chat", "OnMessage"));
            Assert.IsTrue(Listen(other, "chat", "OnMessage", BasisScriptBus.FromWorld));

            Assert.Greater(Running(propBox, () => BasisScriptBus.Broadcast(prop, "chat", "hello")), 0);
            Tick();
            Assert.AreEqual(1, Clicks(world));
            Assert.AreEqual(0, Clicks(prop), "the sender does not hear its own broadcast");
            Assert.AreEqual(0, Clicks(other), "this listener only takes messages from the world");
            Assert.AreEqual(3, BasisScriptBus.Listening("chat").Length);
            Assert.AreEqual(3, BasisScriptBus.All.Length);
        }

        [Test]
        public void AnInterpretedScript_Broadcasts_ThroughTheRealNativeCall()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            Assert.IsTrue(Listen(world, "chat", "OnMessage"));

            prop.cls.InterpretMethod(prop, "SendHi", null);
            Assert.IsFalse(prop.disabled, "the script must not have faulted");
            Tick();
            Assert.AreEqual(1, Clicks(world));
            Assert.AreEqual("hi", Last(world));
        }

        [Test]
        public void AReply_GoesBackToTheSender_WithTheOriginalId()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            Assert.IsTrue(Listen(world, "ping", "Ping"));
            Assert.IsTrue(Listen(prop, "ping", "OnMessage"));
            BasisScriptTarget worldTarget = Running(worldBox, () => BasisScriptBus.Self(world));

            int id = Running(propBox, () => BasisScriptBus.Send(prop, worldTarget, "ping", "ping?"));
            Tick();
            Assert.AreEqual(1, Clicks(world));
            Assert.AreEqual(0, Clicks(prop), "the reply waits for the next tick");
            Tick();
            Assert.AreEqual(1, Clicks(prop));
            Assert.AreEqual("pong", Last(prop));
            Assert.Greater(id, 0);
        }

        [Test]
        public void OnlyARunningScript_WithItsOwnHandler_CanListenOrSend()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);

            Assert.IsFalse(BasisScriptBus.Listen(world, "chat", Handler(world, "OnMessage")), "idle box");
            Assert.IsFalse(Running(worldBox, () => BasisScriptBus.Listen(world, "chat", Handler(prop, "OnMessage"))), "a handler from another box must be refused");
            Assert.IsFalse(Running(worldBox, () => BasisScriptBus.Listen(world, "chat", m => { })), "a host delegate must be refused");
            Assert.IsFalse(Running(worldBox, () => BasisScriptBus.Listen(world, new string('x', BasisScriptBus.MaxNameLength + 1), Handler(world, "OnMessage"))));
            Assert.IsFalse(Running(worldBox, () => BasisScriptBus.Listen(world, "bad\nname", Handler(world, "OnMessage"))));
            Assert.IsTrue(Listen(world, "chat", "OnMessage"));
            Assert.AreEqual(0, BasisScriptBus.Broadcast(prop, "chat", "idle"), "a box that is not running cannot send");
            Assert.AreEqual(0, Running(propBox, () => BasisScriptBus.Broadcast(prop, "chat", new string('x', BasisScriptBus.MaxTextLength + 1))));
            Assert.AreEqual(0, Running(propBox, () => BasisScriptBus.Broadcast(prop, "chat", "big", new byte[BasisScriptBus.MaxDataLength + 1])));
        }

        [Test]
        public void SendsAreRateLimited_AndQueuesAreCapped()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            Assert.IsTrue(Listen(world, "spam", "OnMessage"));
            int accepted = 0;
            for (int i = 0; i < BasisScriptBus.MaxSendsPerSecond + 10; i++)
            {
                if (Running(propBox, () => BasisScriptBus.Broadcast(prop, "spam", "x")) > 0) accepted++;
            }
            Assert.AreEqual(BasisScriptBus.MaxSendsPerSecond, accepted);
            Assert.LessOrEqual(Queued, BasisScriptBus.MaxQueued);
            Tick();
            Assert.AreEqual(BasisScriptBus.MaxSendsPerSecond, Clicks(world));
        }

        [Test]
        public void AThrowingHandler_IsContained_AndStopsReceiving()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            Assert.IsTrue(Listen(world, "boom", "Thrower"));
            LogAssert.ignoreFailingMessages = true;
            Assert.Greater(Running(propBox, () => BasisScriptBus.Broadcast(prop, "boom", "1")), 0);
            Assert.DoesNotThrow(Tick);
            Assert.IsTrue(world.disabled, "the interpreter disables the script that threw");
            Assert.AreEqual(0, Running(propBox, () => BasisScriptBus.Broadcast(prop, "boom", "2")), "a disabled script is no longer a listener");
            Assert.IsFalse(prop.disabled);
        }

        [Test]
        public void PublishedValues_AreReadableByOthers_AndTyped()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            BasisProp content = propBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "team", "red")));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "score", 12)));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "speed", 1.5f)));
            Assert.IsFalse(BasisScriptBus.Publish(prop, "idle", 1), "only a running script may publish");
            Assert.IsFalse(Running(propBox, () => BasisScriptBus.Publish(prop, "nan", float.NaN)));

            BasisScriptTarget target = BasisScriptBus.Of(propBox.gameObject);
            Assert.AreSame(Running(propBox, () => BasisScriptBus.Self(prop)), target, "one handle per content, so == works in scripts");
            Assert.AreEqual("red", target.GetString("team", "none"));
            Assert.AreEqual(12, target.GetInt("score", -1));
            Assert.AreEqual(1.5f, target.GetFloat("speed", 0f));
            Assert.AreEqual(-1, target.GetInt("team", -1), "a text value is not a number");
            Assert.AreEqual("12", target.GetString("score", null));
            CollectionAssert.AreEqual(new[] { "score", "speed", "team" }, target.Keys);
            Running(propBox, () => { BasisScriptBus.Unpublish(prop, "team"); return 0; });
            Assert.IsFalse(target.HasValue("team"));

            for (int i = 0; i < BasisScriptBus.MaxValues; i++) Running(propBox, () => BasisScriptBus.Publish(prop, "k" + i, i));
            Assert.LessOrEqual(target.Keys.Length, BasisScriptBus.MaxValues);

            UnityEngine.Object.DestroyImmediate(content);
            Assert.IsFalse(target.IsValid, "a handle dies with its content");
            Assert.AreEqual("none", target.GetString("score", "none"));
        }

        [Test]
        public void TypedValues_AreCopiedIn_AndCopiedOutForReaders()
        {
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            propBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy prop = Proxy(propBox);
            byte[] bytes = { 1, 2, 3 };
            float[] floats = { 0.5f, 2f };
            string[] names = { "a", null, "c" };
            Vector3[] path = { Vector3.one, Vector3.up };
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "blob", bytes)));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "weights", floats)));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "names", names)));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "path", path)));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "spawn", new Vector3(1f, 2f, 3f))));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "facing", Quaternion.Euler(0f, 90f, 0f))));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "tint", Color.red)));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "open", true)));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "ids", new[] { 4, 5 })));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "big", 9876543210L)));
            Assert.IsFalse(Running(propBox, () => BasisScriptBus.Publish(prop, "bad", new Vector3(float.NaN, 0f, 0f))));
            Assert.IsFalse(Running(propBox, () => BasisScriptBus.Publish(prop, "object", new object())), "only the listed types cross");
            Assert.IsFalse(Running(propBox, () => BasisScriptBus.Publish(prop, "transform", propBox.transform)), "no Unity objects cross");
            Assert.IsFalse(Running(propBox, () => BasisScriptBus.Publish(prop, "huge", new byte[BasisScriptBus.MaxDataLength + 1])));
            bytes[0] = 9;
            floats[0] = 9f;

            BasisScriptTarget target = BasisScriptBus.Of(propBox.gameObject);
            Assert.AreEqual(BasisScriptBus.TypeBytes, target.TypeOf("blob"));
            byte[] read = target.GetBytes("blob");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, read, "publisher edits after Publish must not leak in");
            read[1] = 7;
            Assert.AreEqual(2, target.GetBytes("blob")[1], "one reader's edits must not reach the next");
            CollectionAssert.AreEqual(new[] { 0.5f, 2f }, target.GetFloats("weights"));
            CollectionAssert.AreEqual(new[] { "a", "", "c" }, target.GetStrings("names"));
            CollectionAssert.AreEqual(path, target.GetVector3s("path"));
            Assert.AreEqual(new Vector3(1f, 2f, 3f), target.GetVector3("spawn", Vector3.zero));
            Assert.AreEqual(Vector3.zero, target.GetVector3("tint", Vector3.zero), "wrong type gives the fallback");
            Assert.AreEqual(Color.red, target.GetColor("tint", Color.clear));
            Assert.IsTrue(Quaternion.Angle(Quaternion.Euler(0f, 90f, 0f), target.GetQuaternion("facing", Quaternion.identity)) < 0.01f);
            Assert.IsTrue(target.GetBool("open", false));
            CollectionAssert.AreEqual(new[] { 4, 5 }, target.GetInts("ids"));
            Assert.AreEqual(9876543210d, (double)target.GetFloat("big", 0f), 1000d);
            Assert.IsNull(target.GetBytes("weights"));
        }

        [Test]
        public void AMessage_CarriesATypedValue_AndEachReceiverGetsItsOwnArray()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            CilboxPropBasis otherBox = Box<CilboxPropBasis>("Other prop");
            otherBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox), other = Proxy(otherBox);
            Assert.IsTrue(Listen(world, "path", "OnMessage"));
            Assert.IsTrue(Listen(other, "path", "OnMessage"));
            Vector3[] path = { Vector3.one };
            Assert.Greater(Running(propBox, () => BasisScriptBus.Broadcast(prop, "path", "route", path)), 0);
            path[0] = Vector3.zero;

            System.Collections.IList pending = (System.Collections.IList)Bus.GetField("queue", Hidden).GetValue(null);
            Assert.AreEqual(2, pending.Count);
            FieldInfo field = pending[0].GetType().GetField("Message", BindingFlags.NonPublic | BindingFlags.Instance);
            BasisScriptMessage a = (BasisScriptMessage)field.GetValue(pending[0]), b = (BasisScriptMessage)field.GetValue(pending[1]);
            Assert.AreEqual(BasisScriptBus.TypeVector3s, a.ValueType);
            Assert.AreEqual(Vector3.one, a.GetVector3s()[0]);
            Assert.AreNotSame(a.GetVector3s(), b.GetVector3s());
            Assert.IsNull(a.Data);
            Assert.AreEqual(0, Running(propBox, () => BasisScriptBus.Broadcast(prop, "path", "x", new object())), "an unsupported value refuses the whole send");
            Assert.AreEqual(0, Running(propBox, () => BasisScriptBus.Broadcast(prop, "path", "x", new byte[BasisScriptBus.MaxDataLength + 1])));
            Tick();
            Assert.AreEqual(1, Clicks(world));
            Assert.AreEqual(1, Clicks(other));
        }

        [Test]
        public void SendsAreCappedByBytesPerSecondToo()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            Assert.IsTrue(Listen(world, "bulk", "OnMessage"));
            byte[] chunk = new byte[BasisScriptBus.MaxDataLength];
            int accepted = 0;
            for (int i = 0; i < 40; i++)
            {
                if (Running(propBox, () => BasisScriptBus.Broadcast(prop, "bulk", null, chunk)) > 0) accepted++;
            }
            Assert.AreEqual(BasisScriptBus.MaxBytesPerSecond / (BasisScriptBus.MaxDataLength + 16), accepted);
        }

        [Test]
        public void TagsAndDescriptions_TellPeopleAndScriptsWhatContentIsFor()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            propBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Tag(prop, "Ball")));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Tag(prop, "sport.football")));
            Assert.IsFalse(Running(propBox, () => BasisScriptBus.Tag(prop, "has space")));
            Assert.IsFalse(Running(propBox, () => BasisScriptBus.Tag(prop, new string('a', BasisScriptBus.MaxTagLength + 1))));
            Assert.IsFalse(BasisScriptBus.Tag(prop, "idle"), "only a running script may tag its content");
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Describe(prop, "A football. Kick it into a goal.")));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Describe(prop, "goal", "Sent to the world when the ball crosses a line. Text is the team.")));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Describe(prop, "speed", "Metres per second, as a number.")));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Describe(prop, "bad", "<b>bold</b>\u202Eflip")));
            Assert.IsTrue(Listen(prop, "goal", "OnMessage"));

            BasisScriptTarget ball = BasisScriptBus.Of(propBox.gameObject);
            CollectionAssert.AreEqual(new[] { "ball", "sport.football" }, ball.Tags);
            Assert.IsTrue(ball.HasTag("BALL"), "tags ignore case");
            Assert.AreEqual(1, BasisScriptBus.Tagged("ball").Length);
            Assert.AreSame(ball, BasisScriptBus.Tagged("ball")[0]);
            Assert.AreEqual(0, BasisScriptBus.Tagged("world").Length);
            Assert.AreEqual("A football. Kick it into a goal.", ball.Description);
            Assert.AreEqual("Metres per second, as a number.", ball.DescriptionOf("speed"));
            Assert.AreEqual(string.Empty, ball.DescriptionOf("missing"));
            StringAssert.DoesNotContain("<", ball.DescriptionOf("bad"), "descriptions are shown to people, so no rich text");
            Assert.AreEqual(-1, ball.DescriptionOf("bad").IndexOf('\u202E'), "bidi overrides are stripped");
            CollectionAssert.AreEqual(new[] { "goal" }, ball.Channels);
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Untag(prop, "ball")));
            Assert.AreEqual(0, BasisScriptBus.Tagged("ball").Length);
            Assert.IsTrue(Running(worldBox, () => BasisScriptBus.Tag(world, "arena")));
            Assert.IsFalse(ball.HasTag("arena"), "tagging only ever touches the caller's own content");
        }

        [Test]
        public void TheClock_IsOnlyHeldWhileMessagesWait()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            FieldInfo requests = typeof(Basis.Scripts.Drivers.BasisFrameClock).GetField("_requestCount", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(requests, "BasisFrameClock._requestCount moved");
            int before = (int)requests.GetValue(null);
            Assert.IsTrue(Listen(world, "chat", "OnMessage"));
            Assert.AreEqual(before, (int)requests.GetValue(null), "listening alone costs nothing per frame");
            Running(propBox, () => BasisScriptBus.Broadcast(prop, "chat", "x"));
            Assert.AreEqual(before + 1, (int)requests.GetValue(null));
            Tick();
            Assert.AreEqual(before, (int)requests.GetValue(null));
        }

        static BasisScriptMessage Peek(int index)
        {
            object pending = ((System.Collections.IList)Bus.GetField("queue", Hidden).GetValue(null))[index];
            return (BasisScriptMessage)pending.GetType().GetField("Message", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pending);
        }

        [Test]
        public void ASharedTransform_IsAHandle_ReadOnlyUnlessTheOwnerSaysSo()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            BasisProp content = propBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            Transform handle = new GameObject("Handle").transform;
            handle.SetParent(propBox.transform, false);
            Assert.IsTrue(Listen(world, "share", "OnMessage"));
            BasisScriptTarget worldTarget = Running(worldBox, () => BasisScriptBus.Self(world));
            BasisScriptTarget propTarget = Running(propBox, () => BasisScriptBus.Self(prop));

            foreach (MemberInfo member in typeof(BasisSharedTransform).GetMembers(BindingFlags.Public | BindingFlags.Instance))
            {
                Type type = member is PropertyInfo property ? property.PropertyType : member is MethodInfo method ? method.ReturnType : null;
                Assert.IsFalse(type != null && typeof(UnityEngine.Object).IsAssignableFrom(type), member.Name + " must not hand out a Unity object");
            }

            Assert.Greater(Running(propBox, () => BasisScriptBus.Send(prop, worldTarget, "share", "handle", handle)), 0);
            BasisScriptMessage message = Peek(0);
            Assert.AreEqual(BasisScriptBus.TypeTransform, message.ValueType);
            BasisSharedTransform readOnly = message.GetSharedTransform();
            Assert.IsNotNull(readOnly);
            Assert.IsFalse(readOnly.Writable, "a raw Transform crosses read-only");
            Assert.AreEqual("Handle", readOnly.Name);
            Assert.AreSame(propTarget, readOnly.Owner);
            Assert.IsFalse(readOnly.SetPosition(new Vector3(5f, 0f, 0f)));
            Assert.IsFalse(readOnly.SetActive(false));
            Assert.AreEqual(Vector3.zero, handle.localPosition);
            Tick();
            Assert.Greater(Running(propBox, () => BasisScriptBus.Send(prop, worldTarget, "share", "go", handle.gameObject)), 0);
            Assert.AreSame(readOnly, Peek(0).GetSharedTransform(), "one read-only handle per Transform");
            Tick();

            BasisSharedTransform writable = Running(propBox, () => BasisScriptBus.Share(prop, handle, true));
            Assert.IsNotNull(writable);
            Assert.IsTrue(writable.Writable);
            Assert.AreNotSame(readOnly, writable);
            Assert.IsTrue(writable.SetPosition(new Vector3(1f, 2f, 3f)));
            Assert.AreEqual(new Vector3(1f, 2f, 3f), handle.position);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), readOnly.Position);
            Assert.IsTrue(writable.SetLocalRotation(new Quaternion(0f, 0f, 0f, 2f)), "rotations are normalised");
            Assert.AreEqual(Quaternion.identity, handle.localRotation);
            Assert.IsFalse(writable.SetPosition(new Vector3(float.NaN, 0f, 0f)));
            Assert.IsFalse(writable.SetRotation(new Quaternion(0f, 0f, 0f, 0f)));
            Assert.IsFalse(writable.SetLocalScale(new Vector3(float.PositiveInfinity, 1f, 1f)));
            Assert.IsTrue(writable.SetActive(false));
            Assert.IsTrue(handle.gameObject.activeSelf, "SetActive waits for the next tick so the owner's OnDisable never runs inside the caller");
            Tick();
            Assert.IsFalse(handle.gameObject.activeSelf);
            Assert.IsFalse(readOnly.ActiveSelf);

            Assert.AreEqual(0, Running(propBox, () => BasisScriptBus.Send(prop, worldTarget, "share", "theirs", worldBox.transform)), "a prop cannot share a Transform it does not own");
            Assert.IsNull(Running(propBox, () => BasisScriptBus.Share(prop, worldBox.transform, true)));
            Assert.IsNull(Running(worldBox, () => BasisScriptBus.Share(world, handle, true)), "the world cannot share the prop's Transform");
            Assert.IsNull(BasisScriptBus.Share(prop, handle, true), "only a running script can share");
            ScriptableObject asset = ScriptableObject.CreateInstance<ScriptableObject>();
            Assert.AreEqual(0, Running(propBox, () => BasisScriptBus.Send(prop, worldTarget, "share", "asset", asset)), "only scene objects can be shared");
            UnityEngine.Object.DestroyImmediate(asset);
            Assert.Greater(Running(propBox, () => BasisScriptBus.Send(prop, worldTarget, "share", "root", content)), 0, "a component shares its Transform, never itself");
            Assert.AreEqual(propBox.transform.position, Peek(0).GetSharedTransform().Position);
            Tick();

            Running(propBox, () => { BasisScriptBus.Unshare(prop, handle); return 0; });
            Assert.IsFalse(writable.IsValid);
            Assert.IsFalse(readOnly.IsValid);
            Assert.IsFalse(writable.SetPosition(Vector3.zero), "the owner can take a share back");

            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "handle", handle)));
            BasisSharedTransform published = propTarget.GetSharedTransform("handle");
            Assert.IsNotNull(published);
            Assert.IsFalse(published.Writable);
            Assert.AreEqual(BasisScriptBus.TypeTransform, propTarget.TypeOf("handle"));
            UnityEngine.Object.DestroyImmediate(handle.gameObject);
            Assert.IsNull(propTarget.GetSharedTransform("handle"), "a destroyed Transform reads back as null");
            Assert.IsFalse(published.IsValid);
        }

        [Test]
        public void MoreValueTypes_CrossAndComeBackTyped()
        {
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            propBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy prop = Proxy(propBox);
            Matrix4x4 matrix = Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 45f, 0f), Vector3.one * 2f);
            Vector2[] uvs = { new Vector2(0f, 1f), new Vector2(0.5f, 0.25f) };
            Color32[] palette = { new Color32(255, 0, 0, 255) };
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "pose", matrix)));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "uvs", uvs)));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "planes", new[] { new Vector4(0f, 1f, 0f, -2f) })));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "turns", new[] { Quaternion.identity })));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "palette", palette)));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "tint", new Color32(0, 255, 0, 255))));
            Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "time", 1234567.890123d)));
            Matrix4x4 broken = Matrix4x4.identity;
            broken[5] = float.NaN;
            Assert.IsFalse(Running(propBox, () => BasisScriptBus.Publish(prop, "broken", broken)));
            Assert.IsFalse(Running(propBox, () => BasisScriptBus.Publish(prop, "far", new[] { new Vector2(float.PositiveInfinity, 0f) })));
            Assert.IsFalse(Running(propBox, () => BasisScriptBus.Publish(prop, "long", new Vector2[BasisScriptBus.MaxArrayLength + 1])));
            uvs[0] = Vector2.zero;

            BasisScriptTarget target = BasisScriptBus.Of(propBox.gameObject);
            Assert.AreEqual(BasisScriptBus.TypeMatrix, target.TypeOf("pose"));
            Assert.AreEqual(matrix, target.GetMatrix("pose", Matrix4x4.identity));
            Assert.AreEqual(Matrix4x4.identity, target.GetMatrix("uvs", Matrix4x4.identity), "wrong type gives the fallback");
            CollectionAssert.AreEqual(new[] { new Vector2(0f, 1f), new Vector2(0.5f, 0.25f) }, target.GetVector2s("uvs"), "publisher edits after Publish must not leak in");
            Assert.AreEqual(new Vector4(0f, 1f, 0f, -2f), target.GetVector4s("planes")[0]);
            Assert.AreEqual(Quaternion.identity, target.GetQuaternions("turns")[0]);
            Assert.AreEqual(BasisScriptBus.TypeColors, target.TypeOf("palette"), "Color32 arrays arrive as Color arrays");
            Assert.AreEqual(Color.red, target.GetColors("palette")[0]);
            Assert.AreEqual(Color.green, target.GetColor("tint", Color.clear));
            Assert.AreEqual(1234567.890123d, target.GetDouble("time", 0d), "GetDouble keeps full precision");
            target.GetColors("palette")[0] = Color.blue;
            Assert.AreEqual(Color.red, target.GetColors("palette")[0], "one reader's edits must not reach the next");
        }

        static Color ReadPixel(Texture texture)
        {
            RenderTexture active = RenderTexture.active;
            RenderTexture rt = texture as RenderTexture;
            Assert.IsNotNull(rt, "a copy is a RenderTexture");
            Texture2D read = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                read.Apply();
                return read.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = active;
                UnityEngine.Object.DestroyImmediate(read);
            }
        }

        static Texture2D Solid(string name, Color color)
        {
            Texture2D texture = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = name };
            Color[] pixels = new Color[16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        [Test]
        public void ASharedTexture_GivesEachReceiverItsOwnLiveCopy()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            CilboxPropBasis otherBox = Box<CilboxPropBasis>("Other prop");
            BasisProp content = propBox.gameObject.AddComponent<BasisProp>();
            otherBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox), other = Proxy(otherBox);
            Texture2D source = Solid("Screen", Color.red);
            try
            {
                Assert.IsTrue(Listen(world, "screen", "OnMessage"));
                BasisScriptTarget worldTarget = Running(worldBox, () => BasisScriptBus.Self(world));
                BasisScriptTarget propTarget = Running(propBox, () => BasisScriptBus.Self(prop));

                Assert.Greater(Running(propBox, () => BasisScriptBus.Send(prop, worldTarget, "screen", "tv", source)), 0);
                BasisScriptMessage message = Peek(0);
                Assert.AreEqual(BasisScriptBus.TypeTexture, message.ValueType);
                BasisSharedTexture shared = message.GetSharedTexture();
                Assert.IsNotNull(shared);
                Assert.AreEqual("Screen", shared.Name);
                Assert.AreEqual(4, shared.Width);
                Assert.AreEqual(4, shared.Height);
                Assert.IsFalse(shared.IsRenderTexture);
                Assert.AreSame(propTarget, shared.Owner);
                Assert.AreSame(shared, Running(propBox, () => BasisScriptBus.Share(prop, source)), "one handle per texture and owner, so == works");
                Assert.IsNull(shared.GetTexture(world), "only a running script can ask for its copy");
                Tick();

                Assert.AreSame(source, Running(propBox, () => shared.GetTexture(prop)), "the owner gets its own texture back, no copy");
                Texture worldCopy = Running(worldBox, () => shared.GetTexture(world));
                Assert.IsNotNull(worldCopy);
                Assert.AreNotSame(source, worldCopy, "a receiver never holds the sender's texture");
                Assert.AreSame(worldCopy, Running(worldBox, () => shared.GetTexture(world)), "one copy per receiving content");
                Texture otherCopy = Running(otherBox, () => shared.GetTexture(other));
                Assert.AreNotSame(worldCopy, otherCopy, "each receiver spoils only its own copy");
                Assert.AreEqual(2, (int)Call("get_CopyCount"));
                Assert.AreEqual(Color.red, ReadPixel(worldCopy));

                Color[] blue = new Color[16];
                for (int i = 0; i < blue.Length; i++) blue[i] = Color.blue;
                source.SetPixels(blue);
                source.Apply();
                Tick();
                Assert.AreEqual(Color.blue, ReadPixel(worldCopy), "copies follow the sender's texture");

                Running(otherBox, () => { shared.Release(other); return 0; });
                Assert.IsTrue(otherCopy == null, "Release frees the caller's copy");
                Assert.AreEqual(1, (int)Call("get_CopyCount"));

                Running(propBox, () => { BasisScriptBus.Unshare(prop, source); return 0; });
                Assert.IsFalse(shared.IsValid);
                Assert.IsTrue(worldCopy == null, "taking the share back frees every copy");
                Assert.AreEqual(0, (int)Call("get_CopyCount"));
                Assert.IsNull(Running(worldBox, () => shared.GetTexture(world)));

                Assert.IsTrue(Running(propBox, () => BasisScriptBus.Publish(prop, "screen", source)));
                BasisSharedTexture published = propTarget.GetSharedTexture("screen");
                Assert.IsNotNull(published);
                Texture publishedCopy = Running(worldBox, () => published.GetTexture(world));
                Assert.IsNotNull(publishedCopy);
                UnityEngine.Object.DestroyImmediate(content);
                Tick();
                Assert.IsFalse(published.IsValid, "a share dies with its content");
                Assert.IsTrue(publishedCopy == null);
                Assert.AreEqual(0, (int)Call("get_CopyCount"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void TextureShares_RefuseWhatTheyCannotCopy_AndCopiesAreCapped()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            propBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy world = Proxy(worldBox), prop = Proxy(propBox);
            Texture2D wide = new Texture2D(BasisScriptBus.MaxTextureSize + 1, 1, TextureFormat.RGBA32, false);
            Cubemap cube = new Cubemap(4, TextureFormat.RGBA32, false);
            List<Texture2D> many = new List<Texture2D>();
            try
            {
                Assert.IsNull(Running(propBox, () => BasisScriptBus.Share(prop, wide)), "too big to copy");
                Assert.IsNull(Running(propBox, () => BasisScriptBus.Share(prop, cube)), "only 2D textures cross");
                Assert.IsFalse(Running(propBox, () => BasisScriptBus.Publish(prop, "cube", cube)), "a refused texture refuses the whole call");
                Assert.IsNull(BasisScriptBus.Share(prop, wide), "only a running script can share");

                for (int i = 0; i <= BasisScriptBus.MaxTextureCopies; i++) many.Add(Solid("Tile " + i, Color.white));
                int copied = 0;
                foreach (Texture2D tile in many)
                {
                    BasisSharedTexture shared = Running(propBox, () => BasisScriptBus.Share(prop, tile));
                    Assert.IsNotNull(shared);
                    if (Running(worldBox, () => shared.GetTexture(world)) != null) copied++;
                }
                Assert.AreEqual(BasisScriptBus.MaxTextureCopies, copied, "a receiving content holds a capped number of copies");
            }
            finally
            {
                Call("Reset");
                UnityEngine.Object.DestroyImmediate(wide);
                UnityEngine.Object.DestroyImmediate(cube);
                foreach (Texture2D tile in many) UnityEngine.Object.DestroyImmediate(tile);
            }
        }

        [Test]
        public void AnAvatar_CanShareItsBones_EvenUnderAPlayerRig()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxAvatarBasis avatarBox = Box<CilboxAvatarBasis>("Avatar script");
            avatarBox.gameObject.AddComponent<BasisAvatar>();
            GameObject rig = new GameObject("Player rig");
            hosts.Add(rig);
            avatarBox.transform.SetParent(rig.transform, false);
            Transform hand = new GameObject("Hand").transform;
            hand.SetParent(avatarBox.transform, false);
            CilboxProxy world = Proxy(worldBox), avatar = Proxy(avatarBox);
            Assert.IsTrue(Listen(world, "bone", "OnMessage"));
            BasisScriptTarget worldTarget = Running(worldBox, () => BasisScriptBus.Self(world));

            BasisSharedTransform shared = Running(avatarBox, () => BasisScriptBus.Share(avatar, hand, true));
            Assert.IsNotNull(shared, "an avatar's own bone is the avatar's to share");
            Assert.AreEqual(BasisScriptBus.KindAvatar, shared.Owner.Kind);
            Assert.Greater(Running(avatarBox, () => BasisScriptBus.Send(avatar, worldTarget, "bone", "hand", shared)), 0);
            Assert.AreSame(shared, Peek(0).GetSharedTransform());
            Tick();
            Assert.IsTrue(shared.SetLocalPosition(new Vector3(0f, 1f, 0f)));
            Assert.AreEqual(new Vector3(0f, 1f, 0f), hand.localPosition);
            Assert.IsNull(Running(avatarBox, () => BasisScriptBus.Share(avatar, rig.transform, false)), "the rig above the avatar is not the avatar's");
        }

        [Test]
        public void AScriptReference_ResolvesOnlyInItsOwnBox_AndTargetsJustThatScript()
        {
            CilboxSceneBasis worldBox = Box<CilboxSceneBasis>("World script");
            CilboxPropBasis propBox = Box<CilboxPropBasis>("Prop script");
            propBox.gameObject.AddComponent<BasisProp>();
            CilboxProxy world = Proxy(worldBox), first = Proxy(propBox), second = Proxy(propBox);
            Assert.IsTrue(Listen(world, "share", "OnMessage"));
            Assert.IsTrue(Listen(first, "ping", "OnMessage"));
            Assert.IsTrue(Listen(second, "ping", "OnMessage"));
            BasisScriptTarget worldTarget = Running(worldBox, () => BasisScriptBus.Self(world));
            BasisScriptTarget propTarget = Running(propBox, () => BasisScriptBus.Self(first));

            Assert.Greater(Running(propBox, () => BasisScriptBus.Send(second, worldTarget, "share", "here", first)), 0);
            BasisScriptMessage message = Peek(0);
            Assert.AreEqual(BasisScriptBus.TypeScript, message.ValueType);
            BasisScriptReference script = message.GetScript();
            Assert.IsNotNull(script);
            Assert.IsTrue(script.IsValid);
            Assert.AreEqual(Probe, script.ClassName);
            Assert.AreSame(propTarget, script.Target);
            Assert.IsTrue(script.Listens("ping"));
            Assert.AreNotSame(script, message.SenderScript, "the sender is the second script, the value is the first");
            Assert.IsNull(Running(worldBox, () => script.Resolve(world)), "another box only ever sees the handle");
            Assert.AreSame(first, Running(propBox, () => script.Resolve(second)), "the script's own box gets the real object back");
            Assert.IsNull(script.Resolve(second), "only a running script can resolve");
            Tick();

            Assert.Greater(Running(worldBox, () => BasisScriptBus.SendToScript(world, script, "ping", "just you")), 0);
            Tick();
            Assert.AreEqual(1, Clicks(first));
            Assert.AreEqual("just you", Last(first));
            Assert.AreEqual(0, Clicks(second), "SendToScript skips the content's other listeners");
            Assert.AreEqual(0, Running(worldBox, () => BasisScriptBus.SendToScript(world, script, "nothing", "x")), "no listener on that channel");

            Assert.AreEqual(0, Running(worldBox, () => BasisScriptBus.Send(world, propTarget, "ping", "steal", first)), "a box cannot share another box's script as its own");
            Assert.Greater(Running(worldBox, () => BasisScriptBus.Send(world, propTarget, "ping", "forward", script)), 0, "a handle it was given can be passed on");
            Assert.AreSame(script, Peek(0).GetScript());
            Tick();
            Assert.Greater(Running(propBox, () => BasisScriptBus.Send(second, worldTarget, "share", "again", first)), 0);
            Assert.AreSame(script, Peek(0).GetScript(), "one handle per script, so == works");
            Assert.AreSame(Peek(0).SenderScript, message.SenderScript);
            Tick();

            first.DisableProxy();
            Assert.IsFalse(script.IsValid);
            Assert.AreEqual(string.Empty, script.ClassName);
            Assert.IsNull(script.Target);
            Assert.AreEqual(0, Running(worldBox, () => BasisScriptBus.SendToScript(world, script, "ping", "gone")));
        }
    }
}
