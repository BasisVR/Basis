using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisStatsShimTests
    {
        static readonly Type Stats = typeof(BasisStatsShim).Assembly.GetType("Cilbox.CilboxStats", true);
        readonly List<GameObject> hosts = new List<GameObject>();
        int ticks;

        void CountTick() => ticks++;

        static FieldInfo Field(string name)
        {
            FieldInfo field = Stats.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, $"CilboxStats no longer has a {name} field; this test cannot drive it.");
            return field;
        }

        static void Set(string name, object value) => Field(name).SetValue(null, value);

        static void ResetStats()
        {
            Stats.GetMethod("Release", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Set("tickFrame", int.MinValue);
            Set("timingFrame", int.MinValue);
            Set("clockRead", double.NegativeInfinity);
            Set("timingRead", double.NegativeInfinity);
            Set("timing", default(BasisFrameBottleneckReading));
        }

        static void DrainClock()
        {
            for (int index = 0; index < 64; index++) BasisFrameClock.RemoveRequest();
        }

        [SetUp]
        public void SetUp()
        {
            ResetStats();
            DrainClock();
            ticks = 0;
            BasisFrameClock.OnTick += CountTick;
        }

        [TearDown]
        public void TearDown()
        {
            BasisFrameClock.OnTick -= CountTick;
            ResetStats();
            DrainClock();
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        CilboxBasisCommon Box(Type boxType)
        {
            GameObject host = new GameObject(boxType.Name + " stats shim test");
            hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }

        [Test]
        public void EveryBox_ReadsEveryStat()
        {
            PropertyInfo[] stats = typeof(BasisStatsShim).GetProperties(BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotEmpty(stats);
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                CilboxBasisCommon box = Box(boxType);
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisStatsShim)), boxType.Name);
                foreach (PropertyInfo stat in stats)
                {
                    string getter = stat.GetGetMethod().Name;
                    Assert.IsTrue(box.CheckMethodAllowed(out MethodInfo _, typeof(BasisStatsShim), getter, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), getter + "()"), boxType.Name + " " + getter);
                }
                Assert.IsFalse(box.CheckTypeAllowed(Stats), boxType.Name + " can reach the clock lease behind the shim.");
            }
        }

        [Test]
        public void TheShim_HasNoNonPublicMembers()
        {
            Assert.IsEmpty(typeof(BasisStatsShim).GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly),
                "Cilbox resolves non-public members of a whitelisted type, so helpers belong on CilboxStats.");
        }

        [Test]
        public void ReadingTheFrameRate_HoldsTheClock()
        {
            BasisFrameClock.Tick(1f / 60f);
            Assert.That(ticks, Is.Zero);

            _ = BasisStatsShim.FramesPerSecond;
            BasisFrameClock.Tick(1f / 90f);

            Assert.That(ticks, Is.EqualTo(1));
            Assert.That(BasisStatsShim.FramesPerSecond, Is.EqualTo(90f).Within(0.01f));
            Assert.That(BasisStatsShim.FrameTimeMs, Is.EqualTo(1000f / 90f).Within(0.01f));
            Assert.That(BasisStatsShim.FramesPerSecond, Is.EqualTo(BasisFrameClock.SmoothedFramesPerSecond), "the shim must report the same number as the menu.");
        }

        [Test]
        public void TheFirstRead_UsesTheLastFrameNotAStaleAverage()
        {
            BasisFrameClock.AddRequest();
            BasisFrameClock.Tick(1f / 7f);
            BasisFrameClock.RemoveRequest();
            float delta = Time.unscaledDeltaTime;
            float expected = delta > 0f ? 1f / delta : 0f;
            Assume.That(expected, Is.Not.EqualTo(BasisFrameClock.SmoothedFramesPerSecond).Within(0.01f));

            Assert.That(BasisStatsShim.FramesPerSecond, Is.EqualTo(expected));
        }

        [Test]
        public void AnIdleLease_HandsTheClockBack()
        {
            _ = BasisStatsShim.FramesPerSecond;
            BasisFrameClock.Tick(1f / 60f);
            Assert.That(ticks, Is.EqualTo(1));

            Set("clockRead", double.NegativeInfinity);
            BasisFrameClock.Tick(1f / 60f);
            BasisFrameClock.Tick(1f / 60f);
            Assert.That(ticks, Is.EqualTo(2), "a script that stopped reading must not keep the clock running.");

            _ = BasisStatsShim.FramesPerSecond;
            BasisFrameClock.Tick(1f / 60f);
            Assert.That(ticks, Is.EqualTo(3), "reading again must take the clock back.");
        }

        [Test]
        public void FrameRateAlone_DoesNotCaptureFrameTimings()
        {
            _ = BasisStatsShim.FramesPerSecond;
            BasisFrameClock.Tick(1f / 60f);
            Assert.That(Field("timingFrame").GetValue(null), Is.EqualTo(int.MinValue));

            _ = BasisStatsShim.Bottleneck;
            Assert.That(Field("timingFrame").GetValue(null), Is.EqualTo(Time.frameCount));
        }

        [Test]
        public void EveryBottleneckKind_HasAnId()
        {
            var ids = new Dictionary<BasisFrameBottleneckKind, int>
            {
                { BasisFrameBottleneckKind.Measuring, BasisStatsShim.BottleneckMeasuring },
                { BasisFrameBottleneckKind.Cpu, BasisStatsShim.BottleneckCpu },
                { BasisFrameBottleneckKind.Gpu, BasisStatsShim.BottleneckGpu },
                { BasisFrameBottleneckKind.Balanced, BasisStatsShim.BottleneckBalanced },
                { BasisFrameBottleneckKind.FrameCap, BasisStatsShim.BottleneckFrameCap },
                { BasisFrameBottleneckKind.NoGpuTimer, BasisStatsShim.BottleneckUnknown },
            };
            foreach (BasisFrameBottleneckKind kind in Enum.GetValues(typeof(BasisFrameBottleneckKind)))
            {
                Assert.IsTrue(ids.ContainsKey(kind), $"{kind} has no BasisStatsShim id.");
                Set("timing", new BasisFrameBottleneckReading { Kind = kind, TargetMs = 1000.0 / 72.0, CpuBusyMs = 4.0, GpuMs = 9.0 });
                Set("timingFrame", Time.frameCount);
                Assert.That(BasisStatsShim.Bottleneck, Is.EqualTo(ids[kind]), kind.ToString());
                Assert.That(BasisStatsShim.TargetFramesPerSecond, Is.EqualTo(72f).Within(0.01f));
                Assert.That(BasisStatsShim.CpuFrameTimeMs, Is.EqualTo(4f));
                Assert.That(BasisStatsShim.GpuFrameTimeMs, Is.EqualTo(9f));
            }
        }

        [Test]
        public void Offline_ReportsTheSentinels()
        {
            Assume.That(BasisNetworkConnection.LocalPlayerIsConnected, Is.False);
            Assert.IsFalse(BasisStatsShim.IsConnected);
            Assert.That(BasisStatsShim.RoundTripTimeMs, Is.EqualTo(-1));
            Assert.That(BasisStatsShim.SecondsSinceLastPacket, Is.EqualTo(-1f));
            Assert.That(BasisStatsShim.PlayerLimit, Is.Zero);
        }
    }
}
