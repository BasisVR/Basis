using NUnit.Framework;

namespace Basis.Tests.IK
{
    /// <summary>
    /// Pins the coarseness of the network-gizmo label change-keys (BasisNetworkGizmoLabelCore).
    /// The interp fraction cycles 0→1 every network keyframe, so if these keys ever become
    /// fine-grained again, every debug label re-tessellates its TMP mesh every frame and the
    /// gizmo system falls back over at high player counts. Each test states the bucket
    /// contract the consumers rely on: values inside one bucket → same key (no rebuild),
    /// crossing a bucket → new key (one rebuild).
    /// </summary>
    public class BasisGizmoLabelKeyTests
    {
        private static int PlayerKey(float t = 0.3f, float rate = 1f, int staged = 2, float bps = 1000f, float pps = 20f, bool state = true, bool bw = true, int id = 7)
        {
            return BasisNetworkGizmoLabelCore.PlayerLabelKey(id, t, rate, staged, bps, pps, state, bw);
        }

        // ── Interp-t: 10% buckets ───────────────────────────────────────────

        [Test]
        public void PlayerKey_InterpWithinBucket_SameKey()
        {
            Assert.AreEqual(PlayerKey(t: 0.31f), PlayerKey(t: 0.34f));
            Assert.AreEqual(PlayerKey(t: 0.06f), PlayerKey(t: 0.14f));
        }

        [Test]
        public void PlayerKey_InterpCrossingBucket_ChangesKey()
        {
            Assert.AreNotEqual(PlayerKey(t: 0.34f), PlayerKey(t: 0.36f));
        }

        [Test]
        public void PlayerKey_InterpClamped_OutOfRangeMatchesEdge()
        {
            Assert.AreEqual(PlayerKey(t: 1f), PlayerKey(t: 1.7f));
            Assert.AreEqual(PlayerKey(t: 0f), PlayerKey(t: -0.5f));
        }

        // ── Playback rate: 5% buckets ───────────────────────────────────────

        [Test]
        public void PlayerKey_RateWithinBucket_SameKey()
        {
            Assert.AreEqual(PlayerKey(rate: 1.00f), PlayerKey(rate: 1.02f));
        }

        [Test]
        public void PlayerKey_RateCrossingBucket_ChangesKey()
        {
            Assert.AreNotEqual(PlayerKey(rate: 1.00f), PlayerKey(rate: 1.08f));
        }

        // ── Bandwidth: 128 B/s buckets ──────────────────────────────────────

        [Test]
        public void PlayerKey_BandwidthWithinBucket_SameKey()
        {
            Assert.AreEqual(PlayerKey(bps: 1000f), PlayerKey(bps: 1020f));
        }

        [Test]
        public void PlayerKey_BandwidthCrossingBucket_ChangesKey()
        {
            Assert.AreNotEqual(PlayerKey(bps: 1000f), PlayerKey(bps: 1200f));
        }

        // ── Non-bucketed inputs must always change the key ──────────────────

        [Test]
        public void PlayerKey_StagedCountChange_ChangesKey()
        {
            Assert.AreNotEqual(PlayerKey(staged: 2), PlayerKey(staged: 3));
        }

        [Test]
        public void PlayerKey_DifferentPlayers_DifferentKeys()
        {
            Assert.AreNotEqual(PlayerKey(id: 7), PlayerKey(id: 8));
        }

        [Test]
        public void PlayerKey_ModeFlags_ChangeKey()
        {
            Assert.AreNotEqual(PlayerKey(state: true, bw: true), PlayerKey(state: true, bw: false));
            Assert.AreNotEqual(PlayerKey(state: true, bw: true), PlayerKey(state: false, bw: true));
        }

        [Test]
        public void PlayerKey_DisabledSections_IgnoreTheirInputs()
        {
            // With state off, interp/rate/staged must not affect the key (no pointless rebuilds).
            Assert.AreEqual(
                PlayerKey(state: false, t: 0.1f, rate: 0.9f, staged: 1),
                PlayerKey(state: false, t: 0.9f, rate: 1.3f, staged: 9));
            // With bandwidth off, byte/packet rates must not affect the key.
            Assert.AreEqual(
                PlayerKey(bw: false, bps: 100f, pps: 5f),
                PlayerKey(bw: false, bps: 90000f, pps: 50f));
        }

        // ── Sync-object key ─────────────────────────────────────────────────

        private static int SyncKey(float t = 0.4f, int depth = 3, bool extrap = false, float bps = 500f, float pps = 10f, bool state = true, bool bw = true, int id = 42)
        {
            return BasisNetworkGizmoLabelCore.SyncLabelKey(id, t, depth, extrap, bps, pps, state, bw);
        }

        [Test]
        public void SyncKey_InterpWithinBucket_SameKey()
        {
            Assert.AreEqual(SyncKey(t: 0.41f), SyncKey(t: 0.44f));
        }

        [Test]
        public void SyncKey_InterpCrossingBucket_ChangesKey()
        {
            Assert.AreNotEqual(SyncKey(t: 0.44f), SyncKey(t: 0.46f));
        }

        [Test]
        public void SyncKey_OvershootClampsAtTenX()
        {
            // The sync job lerps unclamped (overshoot is a real state), but the key clamps
            // at 9.99 so a runaway extrapolation doesn't churn the label forever.
            Assert.AreEqual(SyncKey(t: 9.99f), SyncKey(t: 25f));
        }

        [Test]
        public void SyncKey_ExtrapolationFlip_ChangesKey()
        {
            Assert.AreNotEqual(SyncKey(extrap: false), SyncKey(extrap: true));
        }

        [Test]
        public void SyncKey_BufferDepthChange_ChangesKey()
        {
            Assert.AreNotEqual(SyncKey(depth: 3), SyncKey(depth: 4));
        }

        [Test]
        public void SyncKey_IsDeterministic()
        {
            Assert.AreEqual(SyncKey(), SyncKey());
            Assert.AreEqual(PlayerKey(), PlayerKey());
        }

        // ── Voice (additional-info) section ─────────────────────────────────

        private static int VoiceKey(float vbps, float vpps = 20f, bool voice = true)
        {
            return BasisNetworkGizmoLabelCore.PlayerLabelKey(7, 0.3f, 1f, 2, 1000f, 20f, true, true, vbps, vpps, voice);
        }

        [Test]
        public void PlayerKey_VoiceWithinBucket_SameKey()
        {
            Assert.AreEqual(VoiceKey(2000f), VoiceKey(2020f));
        }

        [Test]
        public void PlayerKey_VoiceCrossingBucket_ChangesKey()
        {
            Assert.AreNotEqual(VoiceKey(2000f), VoiceKey(2200f));
        }

        [Test]
        public void PlayerKey_VoiceDisabled_IgnoresVoiceInputs()
        {
            Assert.AreEqual(VoiceKey(100f, 5f, voice: false), VoiceKey(90000f, 50f, voice: false));
        }

        [Test]
        public void PlayerKey_VoiceFlag_ChangesKey()
        {
            Assert.AreNotEqual(VoiceKey(2000f, voice: true), VoiceKey(2000f, voice: false));
        }

        // ── Channel-totals overview key ─────────────────────────────────────

        private static int Overview(int avatars = 10, float aBps = 20000f, float aPps = 200f, float vBps = 8000f, float vPps = 150f, int scene = 4, float sBps = 900f, float sPps = 30f)
        {
            return BasisNetworkGizmoLabelCore.OverviewKey(avatars, aBps, aPps, vBps, vPps, scene, sBps, sPps);
        }

        [Test]
        public void OverviewKey_WithinBuckets_SameKey()
        {
            // Values chosen inside one 128 B/s bucket — 8000 exactly straddles a rounding
            // boundary (62.5) and is deliberately avoided.
            Assert.AreEqual(Overview(aBps: 20000f, vBps: 8100f, sBps: 900f), Overview(aBps: 20030f, vBps: 8120f, sBps: 930f));
        }

        [Test]
        public void OverviewKey_EachChannelCrossingABucket_ChangesKey()
        {
            int baseline = Overview();
            Assert.AreNotEqual(baseline, Overview(aBps: 21000f));
            Assert.AreNotEqual(baseline, Overview(vBps: 9000f));
            Assert.AreNotEqual(baseline, Overview(sBps: 2000f));
        }

        [Test]
        public void OverviewKey_CountChanges_ChangeKey()
        {
            Assert.AreNotEqual(Overview(avatars: 10), Overview(avatars: 11));
            Assert.AreNotEqual(Overview(scene: 4), Overview(scene: 5));
        }

        [Test]
        public void OverviewKey_IsDeterministic()
        {
            Assert.AreEqual(Overview(), Overview());
        }
    }
}
