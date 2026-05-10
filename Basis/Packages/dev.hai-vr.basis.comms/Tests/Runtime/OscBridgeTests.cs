using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Shims;
using Basis.Scripts.BasisSdk;
using Cilbox;
using HVR.Basis.Comms;
using HVR.Basis.Comms.OSC;
using HVR.Basis.Comms.OSC.Lyuma;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HVR.Basis.Comms.Tests
{
    public class OscBridgeTests
    {
        [TearDown]
        public void TearDown()
        {
            DestroySceneInstance();
        }

        [Test]
        public void SimpleOsc_EncodesAndDecodes_SymbolAndMidi()
        {
            byte[] buffer = new byte[1024];
            int offset = 0;
            SimpleOSC.OSCMessage outbound = new SimpleOSC.OSCMessage
            {
                path = "/test",
                arguments = new object[]
                {
                    new SimpleOSC.OSCSymbol { value = "symbolic" },
                    new SimpleOSC.OSCMidi { port = 1, status = 2, data1 = 3, data2 = 4 },
                }
            };

            SimpleOSC.EncodeOSCInto(buffer, ref offset, outbound);

            ConcurrentQueue<SimpleOSC.OSCMessage> queue = new ConcurrentQueue<SimpleOSC.OSCMessage>();
            SimpleOSC.DecodeOSCInto(queue, buffer, 0, offset);

            Assert.That(queue.TryDequeue(out SimpleOSC.OSCMessage inbound), Is.True);
            Assert.That(inbound.typeTag, Is.EqualTo(",Sm"));
            Assert.That(((SimpleOSC.OSCSymbol)inbound.arguments[0]).value, Is.EqualTo("symbolic"));

            SimpleOSC.OSCMidi midi = (SimpleOSC.OSCMidi)inbound.arguments[1];
            Assert.That(midi.port, Is.EqualTo(1));
            Assert.That(midi.status, Is.EqualTo(2));
            Assert.That(midi.data1, Is.EqualTo(3));
            Assert.That(midi.data2, Is.EqualTo(4));
        }

        [Test]
        public void OscMessage_ConvertsAllSupportedDataKinds()
        {
            SimpleOSC.OSCMessage raw = new SimpleOSC.OSCMessage
            {
                path = "/avatar/parameters/Test",
                typeTag = ",TFNiftsSbrhdcm[is]I",
                arguments = new object[]
                {
                    true,
                    false,
                    null,
                    7,
                    2.5f,
                    new SimpleOSC.TimeTag { secs = 1, nsecs = 2 },
                    "text",
                    new SimpleOSC.OSCSymbol { value = "symbol" },
                    new byte[] { 9, 8 },
                    new SimpleOSC.OSCColor { r = 1, g = 2, b = 3, a = 4 },
                    99L,
                    10.5d,
                    (uint)65,
                    new SimpleOSC.OSCMidi { port = 5, status = 6, data1 = 7, data2 = 8 },
                    new object[] { 3, "nested" },
                    SimpleOSC.Impulse.IMPULSE,
                }
            };

            OscMessage message = OscMessage.FromRaw(raw);

            Assert.That(message.Path, Is.EqualTo("/avatar/parameters/Test"));
            Assert.That(message.NormalizedPath, Is.EqualTo("Test"));
            Assert.That(message.Arguments.Length, Is.EqualTo(16));
            Assert.That(message.Arguments[0].Kind, Is.EqualTo(OscDataKind.Boolean));
            Assert.That(message.Arguments[1].Kind, Is.EqualTo(OscDataKind.Boolean));
            Assert.That(message.Arguments[2].Kind, Is.EqualTo(OscDataKind.Nil));
            Assert.That(message.Arguments[3].Kind, Is.EqualTo(OscDataKind.Int32));
            Assert.That(message.Arguments[4].Kind, Is.EqualTo(OscDataKind.Float32));
            Assert.That(message.Arguments[5].Kind, Is.EqualTo(OscDataKind.TimeTag));
            Assert.That(message.Arguments[6].Kind, Is.EqualTo(OscDataKind.String));
            Assert.That(message.Arguments[7].Kind, Is.EqualTo(OscDataKind.Symbol));
            Assert.That(message.Arguments[8].Kind, Is.EqualTo(OscDataKind.Blob));
            Assert.That(message.Arguments[9].Kind, Is.EqualTo(OscDataKind.Color));
            Assert.That(message.Arguments[10].Kind, Is.EqualTo(OscDataKind.Int64));
            Assert.That(message.Arguments[11].Kind, Is.EqualTo(OscDataKind.Float64));
            Assert.That(message.Arguments[12].Kind, Is.EqualTo(OscDataKind.Char));
            Assert.That(message.Arguments[13].Kind, Is.EqualTo(OscDataKind.Midi));
            Assert.That(message.Arguments[14].Kind, Is.EqualTo(OscDataKind.Array));
            Assert.That(message.Arguments[15].Kind, Is.EqualTo(OscDataKind.Impulse));
            Assert.That(message.Arguments[14].Elements[1].StringValue, Is.EqualTo("nested"));
        }

        [Test]
        public void BasisOsc_FiltersExactAndPrefixSubscriptions()
        {
            GameObject go = new GameObject("OscShimTest");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.OnMessage = (message, arguments) => callCount++;
                shim.Subscribe("/exact", (message, arguments) => { });
                shim.SubscribePrefix("/prefix/", (message, arguments) => { });

                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/nope", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/exact", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/prefix/value", arguments = Array.Empty<object>() }) });

                Assert.That(callCount, Is.EqualTo(2));

                shim.enabled = false;
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/exact", arguments = Array.Empty<object>() }) });

                Assert.That(callCount, Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_NormalizesAvatarParameterSubscriptions()
        {
            GameObject go = new GameObject("OscShimNormalizeTest");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.OnMessage = (message, arguments) => callCount++;
                shim.Subscribe("Face/Smile", (message, arguments) => { });
                shim.SubscribePrefix("FT/", (message, arguments) => { });

                Assert.That(shim.IsSubscribed("/avatar/parameters/Face/Smile"), Is.True);
                Assert.That(shim.IsPrefixSubscribed("/avatar/parameters/FT/"), Is.True);

                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/parameters/Face/Smile", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/parameters/FT/v2/JawOpen", arguments = Array.Empty<object>() }) });

                Assert.That(callCount, Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_SubscribeWithCallback_InvokesExactHandler()
        {
            GameObject go = new GameObject("OscShimCallbackTest");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.Subscribe("/avatar/parameters/Callback", (message, arguments) =>
                {
                    callCount++;
                    Assert.That(message.Path, Is.EqualTo("/avatar/parameters/Callback"));
                    Assert.That(arguments[0].FloatValue, Is.EqualTo(2.5f));
                });

                publish.Invoke(null, new object[]
                {
                    OscMessage.FromRaw(new SimpleOSC.OSCMessage
                    {
                        path = "/avatar/parameters/Callback",
                        arguments = new object[] { 2.5f }
                    })
                });

                Assert.That(callCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_SubmitRawMessages_DeliversRepeatedPackets()
        {
            GameObject go = new GameObject("OscShimRepeatedRawCallbackTest");
            MethodInfo submitRawMessages = typeof(BasisOscService).GetMethod("SubmitRawMessages", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(submitRawMessages, Is.Not.Null);

            try
            {
                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;
                float lastValue = 0f;

                shim.Subscribe("/avatar/parameters/Callback", (message, arguments) =>
                {
                    callCount++;
                    lastValue = arguments[0].FloatValue;
                });

                submitRawMessages.Invoke(null, new object[]
                {
                    new List<SimpleOSC.OSCMessage>
                    {
                        new SimpleOSC.OSCMessage
                        {
                            path = "/avatar/parameters/Callback",
                            arguments = new object[] { 1f }
                        }
                    }
                });

                submitRawMessages.Invoke(null, new object[]
                {
                    new List<SimpleOSC.OSCMessage>
                    {
                        new SimpleOSC.OSCMessage
                        {
                            path = "/avatar/parameters/Callback",
                            arguments = new object[] { 2f }
                        }
                    }
                });

                Assert.That(callCount, Is.EqualTo(2));
                Assert.That(lastValue, Is.EqualTo(2f));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_SubscribeValue_InvokesFirstArgument()
        {
            GameObject go = new GameObject("OscShimValueCallbackTest");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisOsc shim = go.AddComponent<BasisOsc>();
                OscData received = null;

                shim.SubscribeValue("/avatar/parameters/ValueCallback", value => received = value);

                publish.Invoke(null, new object[]
                {
                    OscMessage.FromRaw(new SimpleOSC.OSCMessage
                    {
                        path = "/avatar/parameters/ValueCallback",
                        arguments = new object[] { "text", 5 }
                    })
                });

                Assert.That(received, Is.Not.Null);
                Assert.That(received.Kind, Is.EqualTo(OscDataKind.String));
                Assert.That(received.StringValue, Is.EqualTo("text"));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_SubscribeValue_DoesNotInvokeForEmptyArgumentMessages()
        {
            GameObject go = new GameObject("OscShimEmptyValueCallbackTest");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisOsc shim = go.AddComponent<BasisOsc>();
                bool called = false;

                shim.SubscribeValue("/avatar/parameters/ValueCallback", value => called = true);

                publish.Invoke(null, new object[]
                {
                    OscMessage.FromRaw(new SimpleOSC.OSCMessage
                    {
                        path = "/avatar/parameters/ValueCallback",
                        arguments = Array.Empty<object>()
                    })
                });

                Assert.That(called, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void OscData_Factories_DefensivelyCopyInputArrays()
        {
            byte[] blob = { 1, 2, 3 };
            OscData[] elements = { OscData.Int32(7) };

            OscData blobData = OscData.Blob(blob);
            OscData arrayData = OscData.ArrayValue(elements);

            blob[0] = 99;
            elements[0] = OscData.Int32(11);

            Assert.That(blobData.BlobValue[0], Is.EqualTo(1));
            Assert.That(arrayData.Elements[0].IntValue, Is.EqualTo(7));
        }

        [Test]
        public void OscData_ArrayConversions_HandleNullNestedElements()
        {
            OscData data = OscData.ArrayValue(OscData.Int32(7), null, OscData.String("x"));
            MethodInfo toQueryValue = typeof(OscData).GetMethod("ToQueryValue", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo toOscArgument = typeof(OscData).GetMethod("ToOscArgument", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(toQueryValue, Is.Not.Null);
            Assert.That(toOscArgument, Is.Not.Null);

            object[] queryValue = (object[])toQueryValue.Invoke(data, null);
            object[] oscArgument = (object[])toOscArgument.Invoke(data, null);

            Assert.That(queryValue.Length, Is.EqualTo(3));
            Assert.That(queryValue[0], Is.EqualTo(7));
            Assert.That(queryValue[1], Is.Null);
            Assert.That(queryValue[2], Is.EqualTo("x"));

            Assert.That(oscArgument.Length, Is.EqualTo(3));
            Assert.That(oscArgument[0], Is.EqualTo(7));
            Assert.That(oscArgument[1], Is.Null);
            Assert.That(oscArgument[2], Is.EqualTo("x"));
        }

        [Test]
        public void CilboxWhitelists_AllowDirectOscTypes()
        {
            Assert.That(new CilboxSceneBasis().CheckTypeAllowed("HVR.Basis.Comms.OSC.OscMessage"), Is.True);
            Assert.That(new CilboxPropBasis().CheckTypeAllowed("HVR.Basis.Comms.OSC.OscData"), Is.True);
            Assert.That(new CilboxAvatarBasis().CheckTypeAllowed("HVR.Basis.Comms.OSC.OscDataKind"), Is.True);
            Assert.That(new CilboxAvatarBasis().CheckTypeAllowed("Basis.Shims.BasisOsc"), Is.True);
            Assert.That(new CilboxAvatarBasis().CheckTypeAllowed("Basis.Shims.BasisOsc+OscValueEvent"), Is.True);
        }

        [Test]
        public void BasisOscService_PublishesValuesIntoNodeMap()
        {
            DestroySceneInstance();
            BasisOscService.PublishValues("Custom/TestNode", new[]
            {
                OscData.Float32(0.5f),
                OscData.String("hello"),
            });

            System.Type serverType = GetServerType();
            Assert.That(serverType, Is.Not.Null);

            FieldInfo sceneInstanceField = GetSceneInstanceField(serverType);
            Assert.That(sceneInstanceField, Is.Not.Null);

            MonoBehaviour sceneInstance = (MonoBehaviour)sceneInstanceField.GetValue(null);
            Assert.That(sceneInstance, Is.Not.Null);

            try
            {
                FieldInfo rootField = serverType.GetField("_oscQueryRoot", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(rootField, Is.Not.Null);
                object rootNode = rootField.GetValue(sceneInstance);
                Assert.That(rootNode, Is.Not.Null);

                object leaf = ResolveNode(rootNode, "avatar", "parameters", "Custom", "TestNode");
                Assert.That(leaf, Is.Not.Null);

                System.Type nodeType = leaf.GetType();
                Assert.That((string)nodeType.GetField("FULL_PATH").GetValue(leaf), Is.EqualTo("/avatar/parameters/Custom/TestNode"));
                Assert.That((string)nodeType.GetField("TYPE").GetValue(leaf), Is.EqualTo(",fs"));

                IList values = (IList)nodeType.GetField("VALUE").GetValue(leaf);
                Assert.That(values.Count, Is.EqualTo(2));
                Assert.That(values[0], Is.EqualTo(0.5f));
                Assert.That(values[1], Is.EqualTo("hello"));
            }
            finally
            {
                Object.DestroyImmediate(sceneInstance.gameObject);
                sceneInstanceField.SetValue(null, null);
            }
        }

        [Test]
        public void BasisOsc_Subscribe_RegistersExactAddressInNodeMap()
        {
            DestroySceneInstance();
            GameObject go = new GameObject("OscExactQueryRegistration");

            try
            {
                BasisOsc shim = go.AddComponent<BasisOsc>();
                shim.Subscribe("Face/Smile", (message, arguments) => { });

                object leaf = ResolveNode(GetQueryRoot(), "avatar", "parameters", "Face", "Smile");
                Assert.That(leaf, Is.Not.Null);
                Assert.That((string)leaf.GetType().GetField("FULL_PATH").GetValue(leaf), Is.EqualTo("/avatar/parameters/Face/Smile"));
                Assert.That((int)leaf.GetType().GetField("ACCESS").GetValue(leaf), Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(go);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_SubscribeWithCallback_ReturnsResolvedRelativeAddress()
        {
            GameObject go = new GameObject("OscResolvedSubscribe");

            try
            {
                BasisOsc shim = go.AddComponent<BasisOsc>();
                shim.Subscribe("Face/Smile", (message, arguments) => { }, out string resolvedAddress);

                Assert.That(resolvedAddress, Is.EqualTo("/avatar/parameters/Face/Smile"));
                Assert.That(shim.IsSubscribed(resolvedAddress), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_Subscribe_LocalOnlyOnRemoteAvatar_DoesNotRegisterOrInvoke()
        {
            GameObject go = new GameObject("RemoteAvatarLocalOnlySubscribe");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = false;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.Subscribe("Blocked", (message, arguments) => callCount++, true, out string resolvedAddress);

                Assert.That(resolvedAddress, Is.Null);
                Assert.That(shim.IsSubscribed("/avatar/public/Blocked"), Is.False);

                publish.Invoke(null, new object[]
                {
                    OscMessage.FromRaw(new SimpleOSC.OSCMessage
                    {
                        path = "/avatar/public/Blocked",
                        arguments = Array.Empty<object>()
                    })
                });

                Assert.That(callCount, Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_SubscribeValue_LocalOnlyOnRemoteAvatar_DoesNotRegisterOrInvoke()
        {
            GameObject go = new GameObject("RemoteAvatarLocalOnlyValueSubscribe");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = false;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                bool called = false;

                shim.SubscribeValue("Blocked", value => called = true, true, out string resolvedAddress);

                Assert.That(resolvedAddress, Is.Null);
                Assert.That(shim.IsSubscribed("/avatar/public/Blocked"), Is.False);

                publish.Invoke(null, new object[]
                {
                    OscMessage.FromRaw(new SimpleOSC.OSCMessage
                    {
                        path = "/avatar/public/Blocked",
                        arguments = new object[] { 1f }
                    })
                });

                Assert.That(called, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_RemoteAvatarSubscription_RegistersNormalizedPublicAddressInNodeMap()
        {
            DestroySceneInstance();
            GameObject go = new GameObject("RemoteAvatarQueryRegistration");

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = false;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                shim.Subscribe("/avatar/parameters/Blocked", (message, arguments) => { });

                Assert.That(ResolveNode(GetQueryRoot(), "avatar", "parameters", "Blocked"), Is.Null);

                object leaf = ResolveNode(GetQueryRoot(), "avatar", "public", "Blocked");
                Assert.That(leaf, Is.Not.Null);
                Assert.That((string)leaf.GetType().GetField("FULL_PATH").GetValue(leaf), Is.EqualTo("/avatar/public/Blocked"));
                Assert.That((int)leaf.GetType().GetField("ACCESS").GetValue(leaf), Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(go);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_RemoteAvatarSubscriptionWithCallback_ReturnsNormalizedPublicAddress()
        {
            GameObject go = new GameObject("RemoteAvatarResolvedSubscribe");

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = false;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                shim.Subscribe("/avatar/parameters/Blocked", (message, arguments) => { }, out string resolvedAddress);

                Assert.That(resolvedAddress, Is.EqualTo("/avatar/public/Blocked"));
                Assert.That(shim.IsSubscribed(resolvedAddress), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_LocalAvatarPublishesIntoAvatarNamespace()
        {
            DestroySceneInstance();
            GameObject go = new GameObject("AvatarPublisher");

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = true;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                shim.PublishValue("Face/Smile", OscData.Float32(1f));

                object leaf = ResolveNode(GetQueryRoot(), "avatar", "parameters", "Face", "Smile");
                Assert.That(leaf, Is.Not.Null);
                Assert.That((string)leaf.GetType().GetField("FULL_PATH").GetValue(leaf), Is.EqualTo("/avatar/parameters/Face/Smile"));
            }
            finally
            {
                Object.DestroyImmediate(go);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_LocalAvatarPublishValue_SubmitsFloatIntoVixxyVariableStore()
        {
            DestroySceneInstance();
            GameObject go = new GameObject("AvatarVixxyPublisher");

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = true;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                shim.PublishValue("Face/Smile", OscData.Float32(0.75f));

                int addressId = HVRAddress.AddressToId("Face/Smile");
                Assert.That(AcquisitionService.SceneInstance.VariableStore.GetValue(addressId), Is.EqualTo(0.75f));
            }
            finally
            {
                Object.DestroyImmediate(go);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_PublishValue_ReturnsResolvedRelativeAddress()
        {
            DestroySceneInstance();
            GameObject go = new GameObject("AvatarResolvedPublisher");

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = true;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                shim.PublishValue("Face/Smile", OscData.Float32(1f), out string resolvedAddress);

                Assert.That(resolvedAddress, Is.EqualTo("/avatar/parameters/Face/Smile"));
            }
            finally
            {
                Object.DestroyImmediate(go);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_RemoteAvatarDoesNotPublish()
        {
            DestroySceneInstance();
            GameObject go = new GameObject("RemoteAvatarPublisher");

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = false;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                shim.PublishValue("Blocked", OscData.Float32(1f));

                Assert.That(GetSceneInstanceField(GetServerType()).GetValue(null), Is.Not.Null);
                Assert.That(ResolveNode(GetQueryRoot(), "avatar", "parameters", "Blocked"), Is.Null);
                Assert.That(ResolveNode(GetQueryRoot(), "avatar", "public", "Blocked"), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(go);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_OnEnable_InitializesOscAcquisitionServer_WithoutFaceTracking()
        {
            DestroySceneInstance();
            GameObject go = new GameObject("OscShimOnly");

            try
            {
                Assert.That(GetSceneInstanceField(GetServerType()).GetValue(null), Is.Null);

                go.AddComponent<BasisOsc>();

                Assert.That(GetSceneInstanceField(GetServerType()).GetValue(null), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(go);
                DestroySceneInstance();
            }
        }

        [Test]
        public void OSCAcquisition_ReusesExistingOscAcquisitionServer()
        {
            DestroySceneInstance();
            GameObject avatarRoot = new GameObject("AvatarWithOscAcquisition");

            try
            {
                BasisOscService.EnsureInitialized();

                System.Type serverType = GetServerType();
                FieldInfo sceneInstanceField = GetSceneInstanceField(serverType);
                MonoBehaviour existingServer = (MonoBehaviour)sceneInstanceField.GetValue(null);
                Assert.That(existingServer, Is.Not.Null);

                BasisAvatar avatar = avatarRoot.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = true;
                avatarRoot.AddComponent<OSCAcquisition>();

                avatar.NotifyAvatarReady(true);

                MonoBehaviour resolvedServer = (MonoBehaviour)sceneInstanceField.GetValue(null);
                Assert.That(resolvedServer, Is.SameAs(existingServer));

                Object[] allServers = Resources.FindObjectsOfTypeAll(serverType);
                Assert.That(allServers.Length, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(avatarRoot);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_RemoteAvatarSubscriptions_OnlyReceiveAvatarPublic()
        {
            GameObject go = new GameObject("RemoteAvatarSubscriber");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = false;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.OnMessage = (message, arguments) => callCount++;
                shim.Subscribe("/avatar/parameters/Blocked", (message, arguments) => { });

                Assert.That(shim.IsSubscribed("/avatar/public/Blocked"), Is.True);
                Assert.That(shim.IsSubscribed("/avatar/parameters/Blocked"), Is.True);

                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/parameters/Blocked", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/public/Blocked", arguments = Array.Empty<object>() }) });

                Assert.That(callCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_RemoteAvatarPrefixSubscriptions_OnlyReceiveAvatarPublic()
        {
            GameObject go = new GameObject("RemoteAvatarPrefixSubscriber");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = false;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.OnMessage = (message, arguments) => callCount++;
                shim.SubscribePrefix("/avatar/parameters/", (message, arguments) => { });

                Assert.That(shim.IsPrefixSubscribed("/avatar/public/"), Is.True);
                Assert.That(shim.IsPrefixSubscribed("/avatar/parameters/"), Is.True);

                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/parameters/FT/v2/JawOpen", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/public/FT/v2/JawOpen", arguments = Array.Empty<object>() }) });

                Assert.That(callCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_ReceiveAll_LocalAvatar_IsScopedToAvatarParameters()
        {
            GameObject go = new GameObject("ReceiveAllLocalAvatar");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = true;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.OnMessage = (message, arguments) => callCount++;
                shim.ReceiveAll = true;

                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/parameters/Allowed", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/public/Blocked", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/scene/test/parameters/Blocked", arguments = Array.Empty<object>() }) });

                Assert.That(callCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_ReceiveAll_RemoteAvatar_IsScopedToAvatarPublic()
        {
            GameObject go = new GameObject("ReceiveAllRemoteAvatar");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = false;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.OnMessage = (message, arguments) => callCount++;
                shim.ReceiveAll = true;

                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/public/Allowed", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/parameters/Blocked", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/prop/test/parameters/Blocked", arguments = Array.Empty<object>() }) });

                Assert.That(callCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_PrefixSubscriptions_RequireSegmentBoundary()
        {
            GameObject go = new GameObject("PrefixBoundarySubscriber");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.OnMessage = (message, arguments) => callCount++;
                shim.SubscribePrefix("/avatar/parameters", (message, arguments) => { });

                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/parametersExtra", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/parameters/Face/Smile", arguments = Array.Empty<object>() }) });

                Assert.That(callCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_PropPublishesWithInstanceScopedPath()
        {
            DestroySceneInstance();
            GameObject goA = new GameObject("Prop");
            GameObject goB = new GameObject("Prop");

            try
            {
                BasisProp propA = goA.AddComponent<BasisProp>();
                propA.AssignNetworkGUIDIdentifier("prop-one");
                BasisOsc shimA = goA.AddComponent<BasisOsc>();

                BasisProp propB = goB.AddComponent<BasisProp>();
                propB.AssignNetworkGUIDIdentifier("prop-two");
                BasisOsc shimB = goB.AddComponent<BasisOsc>();

                shimA.PublishValue("Status", OscData.String("alpha"));
                shimB.PublishValue("Status", OscData.String("beta"));

                object leafA = ResolveNode(GetQueryRoot(), "prop", "prop-one", "parameters", "Status");
                object leafB = ResolveNode(GetQueryRoot(), "prop", "prop-two", "parameters", "Status");

                Assert.That(leafA, Is.Not.Null);
                Assert.That(leafB, Is.Not.Null);
                Assert.That(leafA, Is.Not.SameAs(leafB));
            }
            finally
            {
                Object.DestroyImmediate(goA);
                Object.DestroyImmediate(goB);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_PropUnderRemoteAvatar_PublishesToPropNamespace()
        {
            DestroySceneInstance();
            GameObject avatarRoot = new GameObject("RemoteAvatarRoot");
            GameObject propChild = new GameObject("PropChild");
            propChild.transform.SetParent(avatarRoot.transform, false);

            try
            {
                BasisAvatar avatar = avatarRoot.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = false;

                BasisProp prop = propChild.AddComponent<BasisProp>();
                prop.AssignNetworkGUIDIdentifier("prop-under-remote-avatar");

                BasisOsc shim = propChild.AddComponent<BasisOsc>();
                shim.PublishValue("Status", OscData.String("held"));

                object propLeaf = ResolveNode(GetQueryRoot(), "prop", "prop-under-remote-avatar", "parameters", "Status");
                Assert.That(propLeaf, Is.Not.Null);

                object avatarLeaf = ResolveNode(GetQueryRoot(), "avatar", "parameters", "Status");
                Assert.That(avatarLeaf, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(avatarRoot);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_PropSubscriptions_OnlyReceiveAvatarPublic()
        {
            GameObject go = new GameObject("PropSubscriber");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                go.AddComponent<BasisProp>();
                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.OnMessage = (message, arguments) => callCount++;
                shim.Subscribe("Face/Smile", (message, arguments) => { });
                shim.Subscribe("/avatar/parameters/Blocked", (message, arguments) => { });

                Assert.That(shim.IsSubscribed("/avatar/public/Face/Smile"), Is.True);
                Assert.That(shim.IsSubscribed("/avatar/parameters/Face/Smile"), Is.False);
                Assert.That(shim.IsSubscribed("/avatar/parameters/Blocked"), Is.False);

                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/parameters/Face/Smile", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/public/Face/Smile", arguments = Array.Empty<object>() }) });

                Assert.That(callCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_SceneSubscriptions_OnlyReceiveAvatarPublic()
        {
            GameObject go = new GameObject("SceneSubscriber");
            MethodInfo publish = typeof(BasisOscService).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(publish, Is.Not.Null);

            try
            {
                go.AddComponent<BasisScene>();
                BasisOsc shim = go.AddComponent<BasisOsc>();
                int callCount = 0;

                shim.OnMessage = (message, arguments) => callCount++;
                shim.SubscribePrefix("FT/", (message, arguments) => { });
                shim.SubscribePrefix("/avatar/parameters/", (message, arguments) => { });

                Assert.That(shim.IsPrefixSubscribed("/avatar/public/FT/"), Is.True);
                Assert.That(shim.IsPrefixSubscribed("/avatar/parameters/"), Is.False);

                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/parameters/FT/v2/JawOpen", arguments = Array.Empty<object>() }) });
                publish.Invoke(null, new object[] { OscMessage.FromRaw(new SimpleOSC.OSCMessage { path = "/avatar/public/FT/v2/JawOpen", arguments = Array.Empty<object>() }) });

                Assert.That(callCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(go);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_ResolvePublishAddress_RequiresSegmentBoundary()
        {
            GameObject go = new GameObject("PropPublisher");

            try
            {
                BasisProp prop = go.AddComponent<BasisProp>();
                prop.AssignNetworkGUIDIdentifier("prop-one");

                BasisOsc shim = go.AddComponent<BasisOsc>();
                MethodInfo resolvePublishAddress = typeof(BasisOsc).GetMethod("ResolvePublishAddress", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(resolvePublishAddress, Is.Not.Null);

                // Absolute-looking paths that miss the scoped prefix on a segment boundary are intentionally treated as relative containment.
                string resolved = (string)resolvePublishAddress.Invoke(shim, new object[] { "/prop/prop-one/parametersExtra" });
                Assert.That(resolved, Is.EqualTo("/prop/prop-one/parameters/prop/prop-one/parametersExtra"));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BasisOsc_ScenePublishesWithScopedPath_AndQueryBranchResolves()
        {
            DestroySceneInstance();
            GameObject go = new GameObject("ScenePublisher");

            try
            {
                BasisScene scene = go.AddComponent<BasisScene>();
                scene.AssignNetworkGUIDIdentifier("scene-one");
                BasisOsc shim = go.AddComponent<BasisOsc>();

                shim.PublishValue("Environment/Ambient", OscData.String("night"));

                object leaf = ResolveNode(GetQueryRoot(), "scene", "scene-one", "parameters", "Environment", "Ambient");
                Assert.That(leaf, Is.Not.Null);

                object sceneInstance = GetSceneInstanceField(GetServerType()).GetValue(null);
                MethodInfo responseResolver = GetServerType().GetMethod("GetOscQueryResponse", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(responseResolver, Is.Not.Null);

                string json = (string)responseResolver.Invoke(sceneInstance, new object[] { "/scene/scene-one" });
                StringAssert.Contains("\"FULL_PATH\": \"/scene/scene-one\"", json);
                StringAssert.Contains("\"Ambient\"", json);
            }
            finally
            {
                Object.DestroyImmediate(go);
                DestroySceneInstance();
            }
        }

        [Test]
        public void BasisOsc_LocalAvatarCanPublishToAvatarPublicNamespace()
        {
            DestroySceneInstance();
            GameObject go = new GameObject("AvatarPublicPublisher");

            try
            {
                BasisAvatar avatar = go.AddComponent<BasisAvatar>();
                avatar.IsOwnedLocally = true;

                BasisOsc shim = go.AddComponent<BasisOsc>();
                shim.PublishValue("/avatar/public/Status", OscData.String("shareable"));

                object leaf = ResolveNode(GetQueryRoot(), "avatar", "public", "Status");
                Assert.That(leaf, Is.Not.Null);
                Assert.That((string)leaf.GetType().GetField("FULL_PATH").GetValue(leaf), Is.EqualTo("/avatar/public/Status"));
            }
            finally
            {
                Object.DestroyImmediate(go);
                DestroySceneInstance();
            }
        }

        [Test]
        public void PublishValues_BuildsSimpleOscCompatibleArguments()
        {
            System.Type serverType = typeof(BasisOscService).Assembly.GetType("HVR.Basis.Comms.OSCAcquisitionServer");
            Assert.That(serverType, Is.Not.Null);

            MethodInfo buildOscArguments = serverType.GetMethod("BuildOscArguments", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(buildOscArguments, Is.Not.Null);

            object[] arguments = (object[])buildOscArguments.Invoke(null, new object[]
            {
                new[]
                {
                    OscData.Float32(1.25f),
                    OscData.Symbol("sym"),
                    OscData.Midi(1, 2, 3, 4),
                    OscData.ArrayValue(OscData.Int32(7), OscData.String("nested")),
                    OscData.Impulse(),
                    OscData.Nil(),
                }
            });

            byte[] buffer = new byte[1024];
            int offset = 0;
            SimpleOSC.OSCMessage outbound = new SimpleOSC.OSCMessage
            {
                path = "/publish/test",
                arguments = arguments,
            };

            SimpleOSC.EncodeOSCInto(buffer, ref offset, outbound);

            ConcurrentQueue<SimpleOSC.OSCMessage> queue = new ConcurrentQueue<SimpleOSC.OSCMessage>();
            SimpleOSC.DecodeOSCInto(queue, buffer, 0, offset);

            Assert.That(queue.TryDequeue(out SimpleOSC.OSCMessage inbound), Is.True);
            Assert.That(inbound.typeTag, Is.EqualTo(",fSm[is]IN"));
            Assert.That(((SimpleOSC.OSCSymbol)inbound.arguments[1]).value, Is.EqualTo("sym"));
            Assert.That(((object[])inbound.arguments[3]).Length, Is.EqualTo(2));
        }

        private static object ResolveNode(object rootNode, params string[] path)
        {
            object current = rootNode;
            int pathCount = path.Length;
            for (int i = 0; i < pathCount; i++)
            {
                FieldInfo contentsField = current.GetType().GetField("CONTENTS");
                if (contentsField == null)
                    return null;
                IDictionary contents = (IDictionary)contentsField.GetValue(current);
                if (contents == null)
                    return null;
                current = contents[path[i]];
                if (current == null)
                    return null;

            }

            return current;
        }

        private static System.Type GetServerType()
        {
            return typeof(BasisOscService).Assembly.GetType("HVR.Basis.Comms.OSCAcquisitionServer");
        }

        private static FieldInfo GetSceneInstanceField(System.Type serverType)
        {
            return serverType.GetField("_sceneInstance", BindingFlags.NonPublic | BindingFlags.Static);
        }

        private static object GetQueryRoot()
        {
            System.Type serverType = GetServerType();
            MonoBehaviour sceneInstance = (MonoBehaviour)GetSceneInstanceField(serverType).GetValue(null);
            if (sceneInstance == null)
            {
                return null;
            }

            FieldInfo rootField = serverType.GetField("_oscQueryRoot", BindingFlags.NonPublic | BindingFlags.Instance);
            if (rootField == null)
            {
                return null;
            }
            return rootField.GetValue(sceneInstance);
        }

        private static void DestroySceneInstance()
        {
            System.Type serverType = GetServerType();
            if (serverType == null)
            {
                return;
            }

            FieldInfo sceneInstanceField = GetSceneInstanceField(serverType);
            if (sceneInstanceField == null)
            {
                return;
            }

            MonoBehaviour sceneInstance = (MonoBehaviour)sceneInstanceField.GetValue(null);
            if (sceneInstance != null)
            {
                Object.DestroyImmediate(sceneInstance.gameObject);
                sceneInstanceField.SetValue(null, null);
            }
        }

    }
}
