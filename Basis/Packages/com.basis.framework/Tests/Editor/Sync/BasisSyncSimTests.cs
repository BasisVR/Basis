using System.Collections.Generic;
using Basis.Scripts.Networking.Sync;
using Basis.Scripts.Networking.Sync.Testing;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// End-to-end coverage of the offline value-sync simulation: convergence and robustness through the
    /// real codec + receiver under network impairment, plus a fidelity pin of the harness's managed
    /// interpolation against the real Burst job.
    /// </summary>
    public class BasisSyncSimTests
    {
        // ── Harness interp must match the real Burst job (within float epsilon) ──

        [Test]
        public void Interp_Managed_MatchesRealBurstJob()
        {
            var schema = new BasisSyncSchema();
            schema.AddField(BasisSyncFieldType.Position);                  // lerp
            schema.AddField(BasisSyncFieldType.Angle, interpolate: true);  // wrapped lerp (mode 2)
            schema.AddField(BasisSyncFieldType.Float, interpolate: false); // snap (mode 0)
            schema.AddField(BasisSyncFieldType.Rotation);                  // nlerp
            schema.AddField(BasisSyncFieldType.Int);                       // discrete snap
            schema.Lock();

            var cur = BasisSyncTestSupport.Values(schema);
            var nxt = BasisSyncTestSupport.Values(schema);
            for (int i = 0; i < schema.ContCount; i++) { cur.Cont[i] = 10f + i; nxt.Cont[i] = 350f - i * 3f; }
            cur.Rot[0] = BasisSyncTestSupport.Quat(10, 20, 30);
            nxt.Rot[0] = BasisSyncTestSupport.Quat(80, -40, 15);
            cur.Disc[0] = 5; nxt.Disc[0] = 99;

            const float t = 0.37f;
            var managed = BasisSyncTestSupport.Values(schema);
            BasisSyncSim.Interpolate(schema, cur, nxt, t, managed);

            RunRealJob(schema, cur, nxt, t, out float[] contOut, out quaternion rotOut, out int discOut);

            for (int i = 0; i < schema.ContCount; i++)
                Assert.AreEqual(contOut[i], managed.Cont[i], 1e-4f, $"continuous slot {i} diverged from the real job");
            Assert.LessOrEqual(BasisSyncTestSupport.QuatAngle(managed.Rot[0], rotOut), 0.01f, "rotation diverged from the real job");
            Assert.AreEqual(discOut, managed.Disc[0], "discrete diverged from the real job");
        }

        static void RunRealJob(BasisSyncSchema schema, BasisSyncValues cur, BasisSyncValues nxt, float t, out float[] contOut, out quaternion rotOut, out int discOut)
        {
            int cc = Mathf.Max(1, schema.ContCount), rc = Mathf.Max(1, schema.RotCount), dc = Mathf.Max(1, schema.DiscCount);
            var active = new NativeArray<byte>(1, Allocator.Temp);
            var tArr = new NativeArray<float>(1, Allocator.Temp);
            var contBase = new NativeArray<int>(1, Allocator.Temp);
            var contCount = new NativeArray<int>(1, Allocator.Temp);
            var rotBase = new NativeArray<int>(1, Allocator.Temp);
            var rotCount = new NativeArray<int>(1, Allocator.Temp);
            var discBase = new NativeArray<int>(1, Allocator.Temp);
            var discCount = new NativeArray<int>(1, Allocator.Temp);
            var contCur = new NativeArray<float>(cc, Allocator.Temp);
            var contNext = new NativeArray<float>(cc, Allocator.Temp);
            var contMode = new NativeArray<byte>(cc, Allocator.Temp);
            var rotCur = new NativeArray<quaternion>(rc, Allocator.Temp);
            var rotNext = new NativeArray<quaternion>(rc, Allocator.Temp);
            var discNext = new NativeArray<int>(dc, Allocator.Temp);
            var contOutN = new NativeArray<float>(cc, Allocator.Temp);
            var rotOutN = new NativeArray<quaternion>(rc, Allocator.Temp);
            var discOutN = new NativeArray<int>(dc, Allocator.Temp);

            try
            {
                active[0] = 1; tArr[0] = t;
                contBase[0] = 0; contCount[0] = schema.ContCount;
                rotBase[0] = 0; rotCount[0] = schema.RotCount;
                discBase[0] = 0; discCount[0] = schema.DiscCount;

                for (int i = 0; i < schema.ContCount; i++) { contCur[i] = cur.Cont[i]; contNext[i] = nxt.Cont[i]; }
                for (int i = 0; i < schema.RotCount; i++) { rotCur[i] = cur.Rot[i]; rotNext[i] = nxt.Rot[i]; }
                for (int i = 0; i < schema.DiscCount; i++) discNext[i] = nxt.Disc[i];

                for (int fi = 0; fi < schema.FieldCount; fi++)
                {
                    BasisSyncField fld = schema.GetField(fi);
                    if (fld.Pool != BasisSyncPool.Continuous) continue;
                    byte mode = fld.Type == BasisSyncFieldType.Angle ? (fld.Interpolate ? (byte)2 : (byte)0) : (fld.Interpolate ? (byte)1 : (byte)0);
                    for (int c = 0; c < fld.ContComponents; c++) contMode[fld.Offset + c] = mode;
                }

                var job = new InterpolateSyncObjectsJob
                {
                    Active = active, T = tArr,
                    ContBase = contBase, ContCount = contCount,
                    RotBase = rotBase, RotCount = rotCount,
                    DiscBase = discBase, DiscCount = discCount,
                    ContCur = contCur, ContNext = contNext, ContMode = contMode,
                    RotCur = rotCur, RotNext = rotNext, DiscNext = discNext,
                    ContOut = contOutN, RotOut = rotOutN, DiscOut = discOutN,
                };
                job.Execute(0);

                contOut = new float[schema.ContCount];
                for (int i = 0; i < schema.ContCount; i++) contOut[i] = contOutN[i];
                rotOut = rotOutN[0];
                discOut = discOutN[0];
            }
            finally
            {
                active.Dispose(); tArr.Dispose(); contBase.Dispose(); contCount.Dispose();
                rotBase.Dispose(); rotCount.Dispose(); discBase.Dispose(); discCount.Dispose();
                contCur.Dispose(); contNext.Dispose(); contMode.Dispose();
                rotCur.Dispose(); rotNext.Dispose(); discNext.Dispose();
                contOutN.Dispose(); rotOutN.Dispose(); discOutN.Dispose();
            }
        }

        // ── Convergence and robustness through the simulator ──

        [Test]
        public void Sim_Converges_OnEveryLosslessProfile()
        {
            foreach (string profileName in new[] { "perfect", "loss-light", "loss-heavy", "duplicate", "reorder", "jitter-heavy", "bad-wifi" })
            {
                NetworkProfile profile = Profile(profileName);
                foreach (SyncMotion motion in new[] { SyncMotion.Sine, SyncMotion.Step, SyncMotion.RandomWalk })
                {
                    BasisSyncSimResult r = BasisSyncSim.Run(KitchenScenario(profile, motion, seed: 7));
                    Assert.IsNull(r.Exception, $"{profileName}/{motion} threw: {r.Exception}");
                    Assert.IsTrue(r.Pass, $"{profileName}/{motion} failed to converge: {r.FailReason}");
                }
            }
        }

        [Test]
        public void Sim_IsDeterministic_SameSeedSameResult()
        {
            NetworkProfile profile = Profile("bad-wifi");
            BasisSyncSimResult a = BasisSyncSim.Run(KitchenScenario(profile, SyncMotion.RandomWalk, seed: 1234));
            BasisSyncSimResult b = BasisSyncSim.Run(KitchenScenario(profile, SyncMotion.RandomWalk, seed: 1234));

            Assert.AreEqual(a.PacketsSent, b.PacketsSent, "packet count not deterministic");
            Assert.AreEqual(a.PacketsDelivered, b.PacketsDelivered, "delivery not deterministic");
            Assert.AreEqual(a.WireBytesSent, b.WireBytesSent, "byte count not deterministic");
            Assert.AreEqual(a.Pass, b.Pass, "pass result not deterministic");
            Assert.AreEqual(a.MaxContError, b.MaxContError, 1e-6f, "error metric not deterministic");
        }

        [Test]
        public void Sim_Quantize_ReducesWireBytes()
        {
            NetworkProfile profile = Profile("perfect");
            BasisSyncSimResult full = BasisSyncSim.Run(SingleFieldScenario(BasisSyncFieldType.Position, false, profile, SyncMotion.Sine, seed: 5));
            BasisSyncSimResult quant = BasisSyncSim.Run(SingleFieldScenario(BasisSyncFieldType.Position, true, profile, SyncMotion.Sine, seed: 5));

            Assert.AreEqual(full.PacketsSent, quant.PacketsSent, "same motion/seed should send the same packet count");
            Assert.Less(quant.WireBytesSent, full.WireBytesSent, "quantized position should use fewer wire bytes");
            Assert.IsTrue(quant.Pass, $"quantized still has to converge: {quant.FailReason}");
        }

        [Test]
        public void Sim_StaticMotion_OnlySendsKeyframes()
        {
            BasisSyncSimResult r = BasisSyncSim.Run(SingleFieldScenario(BasisSyncFieldType.Float, false, Profile("perfect"), SyncMotion.Static, seed: 9));
            Assert.Greater(r.PacketsSent, 0, "should send at least the initial keyframe");
            Assert.AreEqual(r.Keyframes, r.PacketsSent, "a static value must never emit a delta packet");
            Assert.IsTrue(r.Pass, r.FailReason);
        }

        [Test]
        public void Sim_InterpolateOff_StillConverges()
        {
            foreach (string profileName in new[] { "perfect", "loss-heavy", "reorder" })
            {
                BasisSyncSimResult r = BasisSyncSim.Run(SingleFieldScenario(BasisSyncFieldType.Float, false, Profile(profileName), SyncMotion.Step, seed: 3, interpolate: false));
                Assert.IsTrue(r.Pass, $"{profileName} interpolate-off failed: {r.FailReason}");
            }
        }

        [Test]
        public void Sim_TeleportThreshold_RecoversFromJumps()
        {
            foreach (string profileName in new[] { "perfect", "loss-heavy", "jitter-heavy" })
            {
                BasisSyncSimScenario s = SingleFieldScenario(BasisSyncFieldType.Position, false, Profile(profileName), SyncMotion.Teleport, seed: 12);
                s.TeleportThreshold = true;
                s.TeleportThresholdMeters = 3f;
                BasisSyncSimResult r = BasisSyncSim.Run(s);
                Assert.IsNull(r.Exception, $"{profileName} teleport threw: {r.Exception}");
                Assert.IsTrue(r.Pass, $"{profileName} teleport failed to converge: {r.FailReason}");
            }
        }

        // ── helpers ──

        static NetworkProfile Profile(string name) => BasisSyncSimMatrix.BuildProfiles().Find(p => p.Name == name);

        static BasisSyncSimScenario SingleFieldScenario(BasisSyncFieldType type, bool quantize, NetworkProfile profile, SyncMotion motion, int seed, bool interpolate = true)
        {
            return new BasisSyncSimScenario
            {
                Name = $"{type}/{motion}/{profile.Name}",
                FieldConfigName = type.ToString(),
                Fields = new List<SimFieldSpec> { new SimFieldSpec(type, interpolate, quantize) },
                Motion = motion,
                Profile = profile,
                Seed = seed,
                Dt = 1f / 72f,
                DurationSeconds = 4f,
                SettleSeconds = 1.6f,
            };
        }

        static BasisSyncSimScenario KitchenScenario(NetworkProfile profile, SyncMotion motion, int seed)
        {
            return new BasisSyncSimScenario
            {
                Name = $"kitchen/{motion}/{profile.Name}",
                FieldConfigName = "KitchenSink",
                Fields = new List<SimFieldSpec>
                {
                    new SimFieldSpec(BasisSyncFieldType.Position),
                    new SimFieldSpec(BasisSyncFieldType.Rotation),
                    new SimFieldSpec(BasisSyncFieldType.Scale),
                    new SimFieldSpec(BasisSyncFieldType.Float),
                    new SimFieldSpec(BasisSyncFieldType.Angle),
                    new SimFieldSpec(BasisSyncFieldType.Vector4, true, true),
                    new SimFieldSpec(BasisSyncFieldType.Color, true, true),
                    new SimFieldSpec(BasisSyncFieldType.Int),
                    new SimFieldSpec(BasisSyncFieldType.Bool),
                    new SimFieldSpec(BasisSyncFieldType.Byte),
                },
                Motion = motion,
                Profile = profile,
                Seed = seed,
                Dt = 1f / 72f,
                DurationSeconds = 4f,
                SettleSeconds = 1.6f,
            };
        }
    }
}
