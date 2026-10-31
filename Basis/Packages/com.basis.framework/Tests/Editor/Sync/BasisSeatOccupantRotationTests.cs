using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisSeatOccupantRotationTests
    {
        private GameObject _go;
        private BasisSeat _seat;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject(nameof(BasisSeatOccupantRotationTests));
            _seat = _go.AddComponent<BasisSeat>();
            _seat.SetPoints(new Vector3(0f, 0f, -0.25f), new Vector3(0f, -0.5f, 0.25f), new Vector3(0f, 0f, 0.25f), 90.0);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        private static BasisSeatRotationLimits Limits(float range, float snap) => new BasisSeatRotationLimits(range, snap);

        [Test]
        public void AFreshSeat_HoldsItsOccupantFacingForward()
        {
            Assert.AreEqual(0f, _seat.OccupantRotationRangeDegrees,
                "a seat must default to no occupant rotation — anything else silently changes every "
                + "already-authored chair in every world.");

            foreach (float delta in new[] { 5f, -30f, 400f, -1000f })
            {
                Assert.IsFalse(_seat.TurnOccupant(delta),
                    $"turning a held seat by {delta} reported a change");
                Assert.AreEqual(0f, _seat.OccupantYawDegrees, 1e-5f,
                    $"a held seat let its occupant turn to {_seat.OccupantYawDegrees} degrees");
            }

            Assert.IsFalse(_seat.SetOccupantYaw(90f), "a held seat accepted an absolute yaw");
            Assert.AreEqual(0f, _seat.OccupantYawDegrees, 1e-5f);
        }

        [Test]
        public void AHeldSeat_ComposesTheSamePoseAsBeforeRotationExisted()
        {
            var legs = new BasisSeatFitLegs { UpperLegLength = 0.42f, LowerLegLength = 0.40f, FootThickness = 0.08f };
            _go.transform.SetPositionAndRotation(new Vector3(2f, 0.5f, -3f), Quaternion.Euler(0f, 37f, 0f));

            _seat.CalculateSeatPositionRotation(legs, out Quaternion rot, out Vector3 pos);

            BasisSeatFitResult fit = BasisSeatFit.Solve(_seat.GetFitFrame(), legs);
            BasisSeatFit.ComposeHipsWorld(_go.transform.localToWorldMatrix, _go.transform.rotation,
                _seat.SpineRotation, fit.Back, out Vector3 expectedPos, out Quaternion expectedRot);

            Assert.Less((pos - expectedPos).magnitude, 1e-5f,
                $"a held seat moved its occupant {(pos - expectedPos).magnitude * 1000f:F3} mm");
            Assert.Less(Quaternion.Angle(rot, expectedRot), 1e-3f,
                $"a held seat turned its occupant {Quaternion.Angle(rot, expectedRot):F4} degrees");
        }

        [Test]
        public void Range_BoundsTheTurnEitherWayFromTheSeatsForward()
        {
            foreach (float range in new[] { 45f, 90f, 180f })
            {
                _seat.OccupantRotationSnapDegrees = 0f;
                _seat.OccupantRotationRangeDegrees = range;
                float limit = range * 0.5f;

                _seat.SetOccupantYaw(1000f);
                Assert.AreEqual(limit, _seat.OccupantYawDegrees, 1e-3f,
                    $"a {range} degree seat let its occupant reach {_seat.OccupantYawDegrees} degrees, past "
                    + $"the {limit} it allows either way.");

                _seat.SetOccupantYaw(-1000f);
                Assert.AreEqual(-limit, _seat.OccupantYawDegrees, 1e-3f,
                    $"a {range} degree seat let its occupant reach {_seat.OccupantYawDegrees} degrees.");

                _seat.SetOccupantYaw(limit * 0.5f);
                Assert.AreEqual(limit * 0.5f, _seat.OccupantYawDegrees, 1e-3f,
                    "a yaw inside the range must pass through untouched");
            }
        }

        [Test]
        public void FullCircle_SpinsFreely_AndStaysWrapped()
        {
            _seat.OccupantRotationRangeDegrees = 360f;

            foreach (float request in new[] { 0f, 90f, 179f, 181f, 359f, 720f, -450f })
            {
                _seat.SetOccupantYaw(request);
                float yaw = _seat.OccupantYawDegrees;

                Assert.LessOrEqual(yaw, 180f + 1e-3f, $"yaw {yaw} escaped the wrapped range");
                Assert.Greater(yaw, -180f - 1e-3f, $"yaw {yaw} escaped the wrapped range");
                Assert.AreEqual(0f, Mathf.DeltaAngle(request, yaw), 1e-2f,
                    $"a free-spin seat changed the requested facing from {request} to {yaw} — it is only "
                    + "supposed to wrap it.");
            }
        }

        [Test]
        public void Snap_QuantisesEveryReachableFacing()
        {
            foreach (float snap in new[] { 25f, 30f, 45f, 90f })
            {
                _seat.OccupantRotationRangeDegrees = 360f;
                _seat.OccupantRotationSnapDegrees = snap;

                for (float request = -180f; request <= 180f; request += 7f)
                {
                    _seat.SetOccupantYaw(request);
                    float yaw = _seat.OccupantYawDegrees;
                    float offStep = Mathf.Abs(Mathf.DeltaAngle(yaw, Mathf.Round(yaw / snap) * snap));

                    Assert.Less(offStep, 1e-2f,
                        $"a {snap} degree snap seat settled on {yaw}, which is {offStep:F3} degrees off a "
                        + "step boundary.");
                    Assert.LessOrEqual(Mathf.Abs(Mathf.DeltaAngle(request, yaw)), snap * 0.5f + 1e-2f,
                        $"a {snap} degree snap moved a request of {request} all the way to {yaw} — snapping "
                        + "should never travel more than half a step.");
                }
            }
        }

        [Test]
        public void SnapAndRangeTogether_OnlyOfferStepsThatAreAlsoInsideTheRange()
        {
            _seat.OccupantRotationRangeDegrees = 90f;
            _seat.OccupantRotationSnapDegrees = 30f;

            var reached = new System.Collections.Generic.HashSet<float>();
            for (float request = -180f; request <= 180f; request += 3f)
            {
                _seat.SetOccupantYaw(request);
                reached.Add(Mathf.Round(_seat.OccupantYawDegrees * 100f) / 100f);
            }

            CollectionAssert.AreEquivalent(new[] { -30f, 0f, 30f }, reached,
                "a 90 degree range with a 30 degree step should offer exactly -30, 0 and 30. Got: "
                + string.Join(", ", reached));
        }

        [Test]
        public void AStepWiderThanTheRange_LeavesOnlyTheCentre()
        {
            _seat.OccupantRotationRangeDegrees = 40f;
            _seat.OccupantRotationSnapDegrees = 90f;

            foreach (float request in new[] { -180f, -20f, -5f, 5f, 20f, 180f })
            {
                _seat.SetOccupantYaw(request);
                Assert.AreEqual(0f, _seat.OccupantYawDegrees, 1e-3f,
                    $"a 40 degree range with a 90 degree step settled on {_seat.OccupantYawDegrees} for a "
                    + $"request of {request}; no step other than 0 fits inside that range.");
            }
        }

        [Test]
        public void SmoothTurnInput_AccumulatesUntilItCrossesASnapStep()
        {
            _seat.OccupantRotationRangeDegrees = 360f;
            _seat.OccupantRotationSnapDegrees = 45f;

            int changes = 0;
            for (int frame = 0; frame < 40; frame++)
            {
                if (_seat.TurnOccupant(3f))
                {
                    changes++;
                }
            }

            Assert.AreEqual(90f, _seat.OccupantYawDegrees, 1e-3f,
                $"forty 3 degree turns (120 raw) on a 45 degree snap seat should land on 90; landed on "
                + $"{_seat.OccupantYawDegrees}. If this is 0 the accumulation is being rounded away every "
                + "frame and smooth input can never move a snapped seat.");
            Assert.AreEqual(2, changes,
                $"the applied yaw should have moved exactly twice (at 45 and at 90), not {changes} times — "
                + "snapping is what keeps a spinning stool nearly free on the wire.");
        }

        [Test]
        public void ShrinkingTheRange_PullsTheOccupantBackInside()
        {
            _seat.OccupantRotationRangeDegrees = 360f;
            _seat.SetOccupantYaw(170f);
            Assert.AreEqual(170f, _seat.OccupantYawDegrees, 1e-3f, "sanity: the occupant should be turned right round");

            _seat.OccupantRotationRangeDegrees = 60f;
            Assert.AreEqual(30f, _seat.OccupantYawDegrees, 1e-3f,
                $"after the range shrank to 60 the occupant is still at {_seat.OccupantYawDegrees} degrees, "
                + "outside what the seat now allows.");
        }

        [Test]
        public void TurningSpinsAboutThePelvis_WithoutMovingIt()
        {
            var legs = new BasisSeatFitLegs { UpperLegLength = 0.42f, LowerLegLength = 0.40f, FootThickness = 0.08f };
            Quaternion seatRot = Quaternion.Euler(0f, 41f, 0f);
            Matrix4x4 seatToWorld = Matrix4x4.TRS(new Vector3(1f, 0.45f, 2f), seatRot, Vector3.one);
            BasisSeatFitResult fit = BasisSeatFit.Solve(_seat.GetFitFrame(), legs);

            BasisSeatFit.ComposeHipsWorld(seatToWorld, seatRot, _seat.SpineRotation, fit.Back, 0f,
                out Vector3 basePos, out Quaternion baseRot, out Quaternion basePivot);
            Assert.AreEqual(Quaternion.identity.x, basePivot.x, 1e-6f, "an unturned occupant needs no pivot rotation");

            foreach (float yaw in new[] { 15f, -45f, 90f, 180f })
            {
                BasisSeatFit.ComposeHipsWorld(seatToWorld, seatRot, _seat.SpineRotation, fit.Back, yaw,
                    out Vector3 pos, out Quaternion rot, out Quaternion pivot);

                Assert.Less((pos - basePos).magnitude, 1e-5f,
                    $"turning {yaw} degrees slid the pelvis {(pos - basePos).magnitude * 1000f:F2} mm. The "
                    + "pelvis is the pivot — a stool spins its occupant in place.");
                Assert.AreEqual(Mathf.Abs(Mathf.DeltaAngle(0f, yaw)), Quaternion.Angle(rot, baseRot), 1e-2f,
                    $"a {yaw} degree turn rotated the occupant by {Quaternion.Angle(rot, baseRot):F3} degrees.");

                Vector3 spineAxis = baseRot * Vector3.up;
                Assert.Less(Vector3.Angle(rot * Vector3.up, spineAxis), 1e-2f,
                    $"a {yaw} degree turn tipped the spine axis; the turn must be a pure twist about it.");

                Vector3 aheadOfPelvis = basePos + baseRot * (Vector3.forward * 0.4f);
                Vector3 turned = BasisSeatFit.RotateAboutPivot(aheadOfPelvis, basePos, pivot);
                Assert.Less((turned - (basePos + rot * (Vector3.forward * 0.4f))).magnitude, 1e-4f,
                    $"the pivot rotation does not carry seat-space points (the foot targets) with the body "
                    + $"at {yaw} degrees, so the feet would stay pointing down the seat while the body swivels.");
            }
        }

        [Test]
        public void TheRemotePinCarriesTheOccupantsFacing()
        {
            var legs = new BasisSeatFitLegs { UpperLegLength = 0.42f, LowerLegLength = 0.40f, FootThickness = 0.08f };
            _go.transform.SetPositionAndRotation(new Vector3(-4f, 0f, 1.5f), Quaternion.Euler(0f, 118f, 0f));
            _seat.OccupantRotationRangeDegrees = 360f;

            _seat.SetOccupantYaw(0f);
            _seat.CalculateSeatPositionRotation(legs, out Quaternion forwardRot, out Vector3 forwardPos);

            foreach (float yaw in new[] { 30f, -75f, 145f })
            {
                _seat.SetOccupantYaw(yaw);
                _seat.CalculateSeatPositionRotation(legs, out Quaternion rot, out Vector3 pos);

                Assert.Less((pos - forwardPos).magnitude, 1e-5f,
                    "the remote pin moved the pelvis when the occupant turned");
                Assert.AreEqual(Mathf.Abs(yaw), Quaternion.Angle(rot, forwardRot), 1e-2f,
                    $"the remote pin turned the occupant {Quaternion.Angle(rot, forwardRot):F3} degrees "
                    + $"instead of {Mathf.Abs(yaw)}, so everyone else sees them facing the wrong way.");
            }
        }

        [Test]
        public void TheSeatPacketCarriesTheYaw_AndStaysBackwardReadable()
        {
            var sync = _go.AddComponent<BasisSeatSync>();
            sync.Seat = _seat;
            _seat.OccupantRotationRangeDegrees = 360f;
            _seat.SetOccupantYaw(123.456f);

            byte[] packet = sync.CreateSeatPacket(true);
            Assert.AreEqual(BasisSeatSync.BodyPacketLength, packet.Length, "the seat packet should be occupied + generation + yaw + body tail");
            Assert.AreEqual(1, packet[0], "the claim flag must stay in byte 0 where older readers look for it");

            float decoded = BasisSeatSync.DequantizeYaw((short)(packet[5] | (packet[6] << 8)));
            Assert.AreEqual(_seat.OccupantYawDegrees, decoded, 0.01f,
                $"the yaw came back as {decoded} instead of {_seat.OccupantYawDegrees} after a round trip "
                + "through the packet.");

            for (float yaw = -180f; yaw <= 180f; yaw += 3.7f)
            {
                float round = BasisSeatSync.DequantizeYaw(BasisSeatSync.QuantizeYaw(yaw));
                Assert.AreEqual(yaw, round, 0.01f, $"quantising {yaw} lost {Mathf.Abs(yaw - round):F4} degrees");
            }

            Assert.AreEqual(0, BasisSeatSync.QuantizeYaw(float.NaN), "a NaN yaw must not reach the wire");
        }

        [Test]
        public void ARemoteAppliesTheOccupantsYawVerbatim_NeverReResolvingIt()
        {
            _seat.OccupantRotationRangeDegrees = 360f;
            _seat.OccupantRotationSnapDegrees = 25f;

            _seat.ApplyNetworkedOccupantYaw(15f);
            Assert.AreEqual(15f, _seat.OccupantYawDegrees, 1e-4f,
                $"a remote re-snapped a received yaw of 15 to {_seat.OccupantYawDegrees}. Only the occupant "
                + "resolves; everyone else applies, or clients disagree about which way they are facing.");

            _seat.OccupantRotationRangeDegrees = 10f;
            _seat.ApplyNetworkedOccupantYaw(140f);
            Assert.AreEqual(140f, _seat.OccupantYawDegrees, 1e-4f,
                $"a remote clamped a received yaw of 140 to {_seat.OccupantYawDegrees} using its own copy of "
                + "the limits. The occupant's client is the authority on what it resolved against.");

            _seat.ApplyNetworkedOccupantYaw(float.NaN);
            Assert.AreEqual(140f, _seat.OccupantYawDegrees, 1e-4f, "a NaN from the wire must be ignored, not applied");
        }

        [Test]
        public void TheYawFlushIsRateLimited_ButNeverDropsTheFinalFacing()
        {
            var sync = _go.AddComponent<BasisSeatSync>();
            sync.Seat = _seat;
            _seat.OccupantRotationRangeDegrees = 360f;

            Assert.IsFalse(sync.FlushOccupantYaw(0f),
                "nothing to flush before anyone has turned, and nobody is seated locally anyway");

            _seat.SetOccupantYaw(20f);
            Assert.IsFalse(sync.FlushOccupantYaw(0f),
                "a turn by someone who is not the recorded local occupant must not broadcast");
        }

        [Test]
        public void EmptyingTheSeat_ReturnsItToForward()
        {
            _seat.OccupantRotationRangeDegrees = 360f;
            _seat.SetOccupantYaw(120f);
            Assert.AreEqual(120f, _seat.OccupantYawDegrees, 1e-3f, "sanity: the occupant should be turned");

            _seat.ResetOccupantYaw();
            Assert.AreEqual(0f, _seat.OccupantYawDegrees, 1e-5f,
                "the next person to sit here would start out turned 120 degrees");
        }

        [Test]
        public void AnEmptySeat_ReportsNobody_AndIsAvailable()
        {
            Assert.IsFalse(_seat.HasOccupant, "a fresh seat should be empty");
            Assert.IsFalse(_seat.IsLocalPlayerSeated, "a fresh seat should not hold the local player");
            Assert.IsTrue(_seat.IsAvailable, "a fresh seat should be available");
            Assert.IsFalse(_seat.TryGetOccupant(out IBasisPlayer occupant), "an empty seat reported an occupant");
            Assert.IsNull(occupant, "an empty seat handed back a player reference");
        }

        [Test]
        public void OccupancyAndIdentity_TrackTheNetworkedRecord()
        {
            _seat.SetSeatOccupied(true);
            Assert.IsTrue(_seat.HasOccupant, "the seat should report occupied");
            Assert.IsFalse(_seat.IsAvailable, "an occupied seat is not available");
            Assert.IsFalse(_seat.TryGetOccupant(out _),
                "the seat named an occupant before one had resolved on this client");

            _seat.SetSeatOccupied(false);
            Assert.IsFalse(_seat.HasOccupant, "the seat should report empty again");
            Assert.IsFalse(_seat.TryGetOccupant(out _), "an emptied seat still named an occupant");
        }

        [Test]
        public void TheForcePaths_RefuseWhenThereIsNothingToDo()
        {
            Assert.IsFalse(_seat.EjectLocalPlayer(),
                "ejecting from a seat the local player is not in should report false, not act");

            _seat.SetSeatOccupied(true);
            Assert.IsFalse(_seat.TrySeatLocalPlayer(),
                "the local player was allowed into a seat somebody else already holds");
            Assert.IsFalse(_seat.IsLocalPlayerSeated, "a refused seating still marked the local player seated");

            _seat.SetSeatOccupied(false);
            Assert.IsFalse(_seat.EjectLocalPlayer(),
                "ejecting still reported true after the seat emptied without the local player in it");
        }

        [Test]
        public void DegenerateLimitsAndRequests_StayFinite()
        {
            var limitSets = new[]
            {
                Limits(0f, 0f), Limits(-10f, 30f), Limits(360f, 0f), Limits(720f, 7f),
                Limits(1f, 0.0001f), Limits(90f, 1000f), Limits(float.NaN, 45f),
            };
            var requests = new[] { 0f, 45f, -45f, 1e6f, -1e6f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };

            foreach (BasisSeatRotationLimits limits in limitSets)
            {
                foreach (float request in requests)
                {
                    float resolved = BasisSeatFit.ResolveOccupantYaw(request, limits);
                    Assert.IsFalse(float.IsNaN(resolved) || float.IsInfinity(resolved),
                        $"resolving {request} against range {limits.RangeDegrees}/snap {limits.SnapDegrees} "
                        + $"gave {resolved}");

                    float applied = BasisSeatFit.AddOccupantYaw(0f, request, limits, out float raw);
                    Assert.IsFalse(float.IsNaN(applied) || float.IsInfinity(applied),
                        $"adding {request} against range {limits.RangeDegrees} gave {applied}");
                    Assert.IsFalse(float.IsNaN(raw) || float.IsInfinity(raw),
                        $"adding {request} against range {limits.RangeDegrees} left raw {raw}");
                }
            }
        }
    }
}
