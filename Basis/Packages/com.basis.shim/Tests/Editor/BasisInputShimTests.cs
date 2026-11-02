using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisInputShimTests
    {
        static readonly Type Requests = typeof(BasisInputShim).Assembly.GetType("Cilbox.CilboxInputRequests", true);
        static readonly MethodInfo PollMethod = Requests.GetMethod("Poll", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly PropertyInfo ChannelCountProperty = Requests.GetProperty("ChannelCount", BindingFlags.NonPublic | BindingFlags.Static);
        static readonly PropertyInfo LeaseCountProperty = Requests.GetProperty("LeaseCount", BindingFlags.NonPublic | BindingFlags.Static);
        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<BasisInputRequest> requests = new List<BasisInputRequest>();
        Keyboard keyboard;
        Gamepad gamepad;
        int baseChannels, baseLeases;

        static int ChannelCount => (int)ChannelCountProperty.GetValue(null);
        static int LeaseCount => (int)LeaseCountProperty.GetValue(null);

        [SetUp]
        public void SetUp()
        {
            keyboard = InputSystem.AddDevice<Keyboard>();
            gamepad = InputSystem.AddDevice<Gamepad>();
            keyboard.MakeCurrent();
            gamepad.MakeCurrent();
            baseChannels = ChannelCount;
            baseLeases = LeaseCount;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (BasisInputRequest request in requests) request?.Release();
            requests.Clear();
            foreach (GameObject host in hosts) if (host != null) UnityEngine.Object.DestroyImmediate(host);
            hosts.Clear();
            InputSystem.RemoveDevice(keyboard);
            InputSystem.RemoveDevice(gamepad);
        }

        CilboxProxy Proxy(uint depth = 1)
        {
            GameObject host = new GameObject("input shim test");
            hosts.Add(host);
            CilboxSceneBasis box = host.AddComponent<CilboxSceneBasis>();
            box.interpreterAccountingDepth = depth;
            CilboxProxy proxy = host.AddComponent<CilboxProxy>();
            proxy.box = box;
            return proxy;
        }

        BasisInputRequest Request()
        {
            BasisInputRequest request = BasisInputShim.Request(Proxy());
            Assert.IsNotNull(request);
            requests.Add(request);
            return request;
        }

        static void Poll(bool menu = false, bool hover = false, bool typing = false, bool focused = true) => PollMethod.Invoke(null, new object[] { menu, hover, typing, focused });

        void Press(params Key[] keys)
        {
            InputState.Change(keyboard, new KeyboardState(keys));
        }

        [Test]
        public void Refused_WithoutInterpreterCaller()
        {
            Assert.IsNull(BasisInputShim.Request(null));
            Assert.IsNull(BasisInputShim.Request(new object()));
            Assert.IsNull(BasisInputShim.Request(Proxy(0)));
        }

        [Test]
        public void Changed_OnlyWhenARequestedInputMoves()
        {
            BasisInputRequest request = Request();
            int space = request.AddKey(Key.Space);
            float[] values = new float[2];
            Poll();
            request.Read(values);
            Press(Key.A);
            Poll();
            Assert.IsFalse(request.Changed, "A key nobody asked for must not wake the script");
            Press(Key.A, Key.Space);
            Poll();
            Assert.IsTrue(request.Changed);
            Assert.AreEqual(1, request.Read(values));
            Assert.AreEqual(1f, values[space]);
            Assert.IsFalse(request.Changed);
            Poll();
            Assert.IsFalse(request.Changed, "A held key must not wake the script again");
        }

        [Test]
        public void Edges_SurviveATapBetweenReads_AndArriveOnce()
        {
            BasisInputRequest request = Request();
            int space = request.AddKey(Key.Space);
            Poll();
            Press(Key.Space);
            Poll();
            Press();
            Poll();
            Assert.AreEqual(0, request.Held);
            Assert.AreEqual(1 << space, request.TakeDown());
            Assert.AreEqual(1 << space, request.TakeUp());
            Assert.AreEqual(0, request.TakeDown());
            Assert.AreEqual(0, request.TakeUp());
        }

        [Test]
        public void SameInput_SharesOneChannel_AndReleaseDropsIt()
        {
            BasisInputRequest first = Request(), second = Request();
            first.AddKey(Key.Space);
            first.AddKey(Key.Space);
            second.AddKey(Key.Space);
            Assert.AreEqual(baseChannels + 1, ChannelCount);
            Assert.AreEqual(1, first.Count);
            second.AddKey(Key.Enter);
            Assert.AreEqual(baseChannels + 2, ChannelCount);
            second.Release();
            Assert.AreEqual(baseChannels + 1, ChannelCount);
            first.Release();
            Assert.AreEqual(baseChannels, ChannelCount);
            Assert.AreEqual(baseLeases, LeaseCount);
            Assert.IsTrue(first.IsReleased);
            Assert.AreEqual(-1, first.AddKey(Key.Space));
            Assert.AreEqual(0f, first.Get(0));
        }

        [Test]
        public void Typing_HidesKeyboardFromScripts()
        {
            BasisInputRequest request = Request();
            int space = request.AddKey(Key.Space), path = request.AddControl("<Keyboard>/a");
            Poll();
            request.Read(null);
            Press(Key.Space, Key.A);
            Poll(typing: true);
            Assert.AreEqual(0f, request.Get(space));
            Assert.AreEqual(0f, request.Get(path), "A control path must not get around the gate");
            Assert.AreEqual(0, request.TakeDown());
            Assert.IsFalse(request.Changed);
            Poll(focused: false);
            Assert.AreEqual(0f, request.Get(space));
            Poll(menu: true);
            Assert.AreEqual(0f, request.Get(space));
        }

        [Test]
        public void KeyHeldThroughBlock_StaysDownUntilReleased()
        {
            BasisInputRequest request = Request();
            int space = request.AddKey(Key.Space);
            Poll();
            Press(Key.Space);
            Poll(typing: true);
            Poll();
            Assert.AreEqual(0f, request.Get(space), "Enter closing a chat field must not fire the world");
            Assert.AreEqual(0, request.TakeDown());
            Press();
            Poll();
            Press(Key.Space);
            Poll();
            Assert.AreEqual(1f, request.Get(space));
            Assert.AreEqual(1 << space, request.TakeDown());
        }

        [Test]
        public void KeyHeldBeforeRequest_DoesNotFakeAPress()
        {
            Press(Key.Space);
            BasisInputRequest request = Request();
            int space = request.AddKey(Key.Space);
            Poll();
            Assert.AreEqual(0f, request.Get(space));
            Assert.AreEqual(0, request.TakeDown());
        }

        [Test]
        public void Blocking_ClearsAHeldKey()
        {
            BasisInputRequest request = Request();
            int space = request.AddKey(Key.Space);
            Poll();
            Press(Key.Space);
            Poll();
            request.TakeDown();
            Poll(menu: true);
            Assert.AreEqual(0f, request.Get(space));
            Assert.AreEqual(0, request.Held);
            Assert.AreEqual(1 << space, request.TakeUp());
        }

        [Test]
        public void Stick_ReadsBothAxes_AndIgnoresJitter()
        {
            BasisInputRequest request = Request();
            int x = request.AddGamepad(BasisInputGamepadControl.LeftStickX), y = request.AddGamepad(BasisInputGamepadControl.LeftStickY);
            Assert.IsTrue(request.IsBound(x));
            Poll();
            InputState.Change(gamepad, new GamepadState { leftStick = new Vector2(0.8f, 0f) });
            Poll();
            float settled = request.Get(x);
            Assert.Greater(settled, 0.5f);
            Assert.AreEqual(0f, request.Get(y));
            request.Read(null);
            Poll();
            Assert.IsFalse(request.Changed);
        }

        [Test]
        public void CurrentPadIsRead_WhenAnotherPadTakesOver()
        {
            BasisInputRequest request = Request();
            int south = request.AddGamepad(BasisInputGamepadControl.South);
            Poll();
            Gamepad other = InputSystem.AddDevice<Gamepad>();
            try
            {
                other.MakeCurrent();
                InputState.Change(other, new GamepadState { buttons = 1u << (int)GamepadButton.South });
                Poll();
                Poll();
                Assert.AreEqual(1f, request.Get(south));
            }
            finally
            {
                InputSystem.RemoveDevice(other);
            }
            gamepad.MakeCurrent();
            Poll();
            Assert.AreEqual(0f, request.Get(south));
        }

        [Test]
        public void Slots_AreCapped_AndBadInputIsRefused()
        {
            BasisInputRequest request = Request();
            for (int i = 0; i < BasisInputShim.MaxSlots; i++) Assert.AreEqual(i, request.AddKey((Key)((int)Key.A + i % 26 + (i >= 26 ? 26 : 0))));
            int channels = ChannelCount;
            Assert.AreEqual(-1, request.AddKey(Key.F1));
            Assert.AreEqual(channels, ChannelCount, "A refused slot must not leave a polled channel behind");
            BasisInputRequest second = Request();
            Assert.AreEqual(-1, second.AddKey(Key.None));
            Assert.AreEqual(-1, second.AddKey((Key)100000));
            Assert.AreEqual(-1, second.AddMouseButton(9));
            Assert.AreEqual(-1, second.AddControl(null));
            Assert.AreEqual(-1, second.AddControl(new string('a', 500)));
            Assert.AreEqual(-1, second.AddController((BasisControllerHand)9, BasisControllerButton.Grip));
            Assert.AreEqual(0f, second.Get(-1));
            Assert.IsFalse(second.IsBound(40));
        }

        [Test]
        public void UnknownControlPath_ReadsZero_ThenBindsWhenTheDeviceArrives()
        {
            BasisInputRequest request = Request();
            int slot = request.AddControl("<Joystick>/trigger");
            Assert.GreaterOrEqual(slot, 0);
            Assert.IsFalse(request.IsBound(slot));
            Poll();
            Assert.AreEqual(0f, request.Get(slot));
            Joystick joystick = InputSystem.AddDevice<Joystick>();
            try
            {
                Poll();
                Assert.IsTrue(request.IsBound(slot));
                using (StateEvent.From(joystick, out InputEventPtr pressed))
                {
                    joystick.trigger.WriteValueIntoEvent(1f, pressed);
                    InputState.Change(joystick, pressed);
                }
                Poll();
                Assert.AreEqual(1f, request.Get(slot));
                Assert.AreEqual(1 << slot, request.TakeDown());
            }
            finally
            {
                InputSystem.RemoveDevice(joystick);
            }
            Poll();
            Assert.IsFalse(request.IsBound(slot));
            Assert.AreEqual(0f, request.Get(slot));
        }

        [Test]
        public void ContentRequestCap_Applies()
        {
            CilboxProxy proxy = Proxy();
            for (int i = 0; i < BasisInputShim.MaxRequestsPerContent; i++)
            {
                BasisInputRequest request = BasisInputShim.Request(proxy);
                Assert.IsNotNull(request, i.ToString());
                requests.Add(request);
            }
            Assert.IsNull(BasisInputShim.Request(proxy));
        }

        [Test]
        public void ReleaseAfterOwnerDestroyed_LeavesNothing()
        {
            BasisInputRequest request = Request();
            request.AddKey(Key.Space);
            UnityEngine.Object.DestroyImmediate(hosts[hosts.Count - 1]);
            Requests.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Assert.IsTrue(request.IsReleased);
            Assert.AreEqual(baseChannels, ChannelCount);
            Assert.AreEqual(baseLeases, LeaseCount);
        }
    }
}
