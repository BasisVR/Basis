using System.Collections.Generic;
using Basis.Cinematics;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Shared rig for the modifier tests. Stacks are configured so the whole solve stays in managed
    /// code — the rotation slot holds rather than aims, and no shake is fitted — which keeps these
    /// runnable outside the Unity runtime and isolates the position solve from the rotation solve.
    /// </summary>
    public static class StackFixture
    {
        public static BasisCameraSubject Subject(Vector3 anchor = default, float yawDegrees = 0f, float scale = 1f)
            => new BasisCameraSubject
            {
                Valid = true,
                AnchorPos = anchor,
                LookPoint = anchor + Vector3.up * 0.2f,
                GroundPos = anchor - Vector3.up * 1.6f,
                Yaw = BasisCameraDamping.Yaw(yawDegrees),
                Scale = scale,
                Radius = 0.45f,
            };

        public static BasisCameraSolveContext Context(BasisCameraSubject subject, float deltaTime = 1f / 60f)
            => new BasisCameraSolveContext
            {
                Subject = subject,
                Fov = 40f,
                Aspect = 16f / 9f,
                DeltaTime = deltaTime,
                Time = 0f,
                OperatorPosition = Vector3.zero,
                OperatorRotation = Quaternion.identity,
            };

        public static BasisCameraSolveContext Context() => Context(Subject());

        /// <summary>
        /// A stack fitted with one position modifier and nothing that would reach a native Unity
        /// call, so the assertion sees the position solve on its own.
        /// </summary>
        public static BasisCameraModifierStack PositionOnly(BasisCameraPositionModifier modifier)
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack
            {
                positionModifier = modifier,
                rotationModifier = BasisCameraRotationModifier.Hold,
            };
            stack.follow.damping = Vector3.zero;
            stack.framing.damping = Vector3.zero;
            stack.dolly.damping = 0f;
            stack.orbit.headingDamping = 0f;
            stack.orbit.verticalDamping = 0f;
            return stack;
        }

        /// <summary>Writes the placement offset onto whichever block the fitted modifier reads.</summary>
        public static void Offset(BasisCameraModifierStack stack, Vector3 offset)
        {
            if (stack.positionModifier == BasisCameraPositionModifier.FrameSubject)
            {
                stack.framing.directionOffset = offset;
            }
            else
            {
                stack.follow.positionOffset = offset;
            }
        }

        public static void Binding(BasisCameraModifierStack stack, BasisCameraBindingMode mode)
        {
            if (stack.positionModifier == BasisCameraPositionModifier.FrameSubject)
            {
                stack.framing.bindingMode = mode;
            }
            else
            {
                stack.follow.bindingMode = mode;
            }
        }

        public static void Damping(BasisCameraModifierStack stack, Vector3 damping)
        {
            if (stack.positionModifier == BasisCameraPositionModifier.FrameSubject)
            {
                stack.framing.damping = damping;
            }
            else
            {
                stack.follow.damping = damping;
            }
        }

        /// <summary>Runs the stack to rest so the assertion sees its settled pose, not its first step.</summary>
        public static BasisCameraPose Settle(BasisCameraModifierStack stack, BasisCameraModifierState state,
            BasisCameraSolveContext context, int frames = 240)
        {
            BasisCameraPose pose = default;
            for (int Frame = 0; Frame < frames; Frame++)
            {
                pose = BasisCameraModifierSolver.Solve(stack, state, context);
            }
            return pose;
        }

        public static BasisCameraModifierState State() => new BasisCameraModifierState();
    }

    public class BasisCameraFollowModifierTests
    {
        [Test]
        public void TheCameraSettlesOnItsAuthoredOffset()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0.5f, 0f, 1.4f));
            stack.follow.lateralTracking = 0f;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context());

            Assert.That(pose.Position, Is.EqualTo(new Vector3(0.5f, 0f, 1.4f)).Using(Vec(1e-3f)));
        }

        [Test]
        public void SubjectYawBinding_CarriesTheOffsetRoundAsTheSubjectTurns()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            StackFixture.Binding(stack, BasisCameraBindingMode.SubjectYaw);
            stack.follow.lateralTracking = 0f;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(),
                StackFixture.Context(StackFixture.Subject(yawDegrees: 90f)));

            Assert.That(pose.Position, Is.EqualTo(new Vector3(2f, 0f, 0f)).Using(Vec(1e-3f)),
                "A subject facing +X should have the camera out along +X.");
        }

        [Test]
        public void WorldSpaceBinding_IgnoresWhichWayTheSubjectFaces()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            StackFixture.Binding(stack, BasisCameraBindingMode.WorldSpace);
            stack.follow.lateralTracking = 0f;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(),
                StackFixture.Context(StackFixture.Subject(yawDegrees: 90f)));

            Assert.That(pose.Position, Is.EqualTo(new Vector3(0f, 0f, 2f)).Using(Vec(1e-3f)));
        }

        [Test]
        public void SimpleFollowBinding_HoldsItsBearingRatherThanCirclingTheSubject()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0.5f, 0f, 1.4f));
            StackFixture.Binding(stack, BasisCameraBindingMode.SimpleFollow);
            stack.follow.lateralTracking = 0f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(0f, 0f, 1.4f), Quaternion.identity, 40f);

            BasisCameraPose pose = StackFixture.Settle(stack, state, StackFixture.Context());

            Assert.That(Vector3.Angle(Vector3.forward, pose.Position), Is.LessThan(1f),
                "A side offset read in a frame taken off the camera is a standing sideways push, and walks the shot round the subject.");
        }

        [Test]
        public void SimpleFollowBinding_KeepsWhicheverSideTheCameraWasAlreadyOn()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0.5f, 0f, 1.4f));
            StackFixture.Binding(stack, BasisCameraBindingMode.SimpleFollow);
            stack.follow.lateralTracking = 0f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(-2f, 0f, 0f), Quaternion.identity, 40f);

            BasisCameraPose pose = StackFixture.Settle(stack, state, StackFixture.Context());

            float distance = new Vector2(0.5f, 1.4f).magnitude;
            Assert.That(pose.Position, Is.EqualTo(new Vector3(-distance, 0f, 0f)).Using(Vec(1e-3f)),
                "It should have eased along its own line to the authored distance, not round to the subject's front.");
        }

        [Test]
        public void SimpleFollowBinding_DoesNotSwingRoundAsTheSubjectTurns()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            StackFixture.Binding(stack, BasisCameraBindingMode.SimpleFollow);
            stack.follow.lateralTracking = 0f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(0f, 0f, 2f), Quaternion.identity, 40f);

            BasisCameraPose pose = StackFixture.Settle(stack, state,
                StackFixture.Context(StackFixture.Subject(yawDegrees: 180f)));

            Assert.That(pose.Position, Is.EqualTo(new Vector3(0f, 0f, 2f)).Using(Vec(1e-3f)),
                "Turning on the spot is what this binding exists to ignore.");
        }

        [Test]
        public void TheOffsetScalesWithTheAvatar()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            stack.follow.lateralTracking = 0f;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(),
                StackFixture.Context(StackFixture.Subject(scale: 2f)));

            Assert.That(pose.Position.z, Is.EqualTo(4f).Within(1e-3f));
        }

        [Test]
        public void TheCameraTracksTheSubjectWhenTheyMove()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            stack.follow.lateralTracking = 0f;

            BasisCameraModifierState state = StackFixture.State();
            StackFixture.Settle(stack, state, StackFixture.Context());
            BasisCameraPose pose = StackFixture.Settle(stack, state,
                StackFixture.Context(StackFixture.Subject(new Vector3(10f, 0f, 0f))));

            Assert.That(pose.Position, Is.EqualTo(new Vector3(10f, 0f, 2f)).Using(Vec(1e-3f)));
        }

        [Test]
        public void DampingMakesTheCameraLagRatherThanTeleport()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            StackFixture.Damping(stack, new Vector3(0.5f, 0.5f, 0.5f));
            stack.follow.lateralTracking = 0f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraPose first = BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context());

            Assert.That(first.Position.z, Is.GreaterThan(0f), "It should have started moving.");
            Assert.That(first.Position.z, Is.LessThan(2f), "One damped frame must not arrive.");
        }

        [Test]
        public void AJumpFurtherThanTheSnapDistanceCuts()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            StackFixture.Damping(stack, new Vector3(2f, 2f, 2f));
            stack.follow.lateralTracking = 0f;
            stack.follow.teleportDistance = 10f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state,
                StackFixture.Context(StackFixture.Subject(new Vector3(500f, 0f, 0f))));

            Assert.That(pose.Position, Is.EqualTo(new Vector3(500f, 0f, 2f)).Using(Vec(1e-3f)),
                "Past the snap distance the camera jumps instead of sweeping the map.");
        }

        [Test]
        public void ALockedOffCameraIgnoresTheSubjectEntirely()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.LockedOff);

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(3f, 2f, 1f), Quaternion.identity, 40f);

            BasisCameraPose pose = StackFixture.Settle(stack, state,
                StackFixture.Context(StackFixture.Subject(new Vector3(50f, 0f, 50f))));

            Assert.That(pose.Position, Is.EqualTo(new Vector3(3f, 2f, 1f)).Using(Vec(1e-4f)));
        }

        [Test]
        public void LateralTrackingClosesTheSideGapWhileTheSubjectStrafes()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(2f, 0f, 2f));
            stack.follow.lateralTracking = 1f;

            BasisCameraModifierState state = StackFixture.State();
            BasisCameraSubject subject = StackFixture.Subject();

            // Walk the subject sideways for long enough that the filtered speed saturates.
            float sideOffset = 0f;
            for (int Frame = 0; Frame < 240; Frame++)
            {
                sideOffset += 4f / 60f;
                subject.AnchorPos = new Vector3(sideOffset, 0f, 0f);
                BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context(subject));
            }

            Assert.That(state.SmoothedLateralSpeed, Is.GreaterThan(1f),
                "The strafe should have registered as lateral speed.");
        }

        [Test]
        public void StandingStillLeavesTheAuthoredSideOffsetAlone()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(2f, 0f, 2f));
            stack.follow.lateralTracking = 1f;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context());

            Assert.That(pose.Position.x, Is.EqualTo(2f).Within(1e-3f));
        }

        [Test]
        public void AnInvalidSubjectHoldsTheCameraWhereItWas()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(1f, 2f, 3f), Quaternion.identity, 40f);

            BasisCameraSolveContext context = StackFixture.Context();
            context.Subject = default;

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(pose.Position, Is.EqualTo(new Vector3(1f, 2f, 3f)).Using(Vec(1e-4f)));
        }

        internal static IEqualityComparer<Vector3> Vec(float tolerance) => new VectorComparer(tolerance);

        private sealed class VectorComparer : IEqualityComparer<Vector3>
        {
            private readonly float _tolerance;
            public VectorComparer(float tolerance) => _tolerance = tolerance;
            public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= _tolerance;
            public int GetHashCode(Vector3 v) => v.GetHashCode();
        }
    }

    public class BasisCameraFramingModifierTests
    {
        [Test]
        public void TheCameraSitsAtTheDistanceThatHoldsTheSubjectAtTheRequestedSize()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FrameSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 3f));
            stack.framing.screenFraction = 0.35f;
            stack.framing.minDistance = 0.1f;
            stack.framing.maxDistance = 50f;

            BasisCameraSolveContext context = StackFixture.Context();
            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), context);

            float expected = BasisCameraFraming.DistanceToFit(0.45f, context.Fov, context.Aspect, 0.35f);
            Assert.That(pose.Position.magnitude, Is.EqualTo(expected).Within(1e-3f));
        }

        [Test]
        public void ABiggerSubjectPushesTheCameraBack()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FrameSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 3f));
            stack.framing.minDistance = 0.1f;
            stack.framing.maxDistance = 50f;

            BasisCameraSubject small = StackFixture.Subject();
            BasisCameraSubject big = StackFixture.Subject();
            big.Radius = 1.8f;

            float near = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context(small)).Position.magnitude;
            float far = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context(big)).Position.magnitude;

            Assert.That(far, Is.GreaterThan(near));
        }

        [Test]
        public void TheDistanceIsClampedBetweenTheAuthoredLimits()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FrameSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 3f));
            stack.framing.screenFraction = 0.01f;
            stack.framing.minDistance = 0.5f;
            stack.framing.maxDistance = 4f;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context());

            Assert.That(pose.Position.magnitude, Is.EqualTo(4f).Within(1e-3f));
        }

        [Test]
        public void ZoomFramingHoldsTheCameraStillAndChangesTheLensInstead()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FrameSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 3f));
            stack.framing.usesZoom = true;
            stack.framing.damping = Vector3.zero;

            BasisCameraSolveContext context = StackFixture.Context();
            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), context);

            Assert.That(pose.Position, Is.EqualTo(new Vector3(0f, 0f, 3f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-3f)),
                "Zoom framing must not dolly.");
            Assert.That(pose.Fov, Is.Not.EqualTo(context.Fov).Within(1e-3f), "The lens should have been driven.");
            Assert.That(stack.DrivesLens, Is.True);
        }

        [Test]
        public void SimpleFollowBinding_FramesFromTheBearingTheCameraIsAlreadyOn()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FrameSubject);
            StackFixture.Offset(stack, new Vector3(1.2f, 0f, 3f));
            StackFixture.Binding(stack, BasisCameraBindingMode.SimpleFollow);
            stack.framing.minDistance = 0.1f;
            stack.framing.maxDistance = 50f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(-3f, 0f, 0f), Quaternion.identity, 40f);

            BasisCameraPose pose = StackFixture.Settle(stack, state, StackFixture.Context());

            Assert.That(pose.Position.z, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(pose.Position.x, Is.LessThan(0f),
                "The direction offset's own bearing must not drag the framing round the subject.");
        }

        [Test]
        public void ZeroOffsetDoesNotDivideByZero()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FrameSubject);
            StackFixture.Offset(stack, Vector3.zero);

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context());

            Assert.That(float.IsNaN(pose.Position.x), Is.False);
            Assert.That(pose.Position, Is.EqualTo(Vector3.zero)
                .Using(BasisCameraFollowModifierTests.Vec(1e-4f)));
        }
    }

    public class BasisCameraOrbitModifierTests
    {
        [Test]
        public void TheCameraSitsOnTheRingTheVerticalAxisSelects()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.Orbit);
            stack.orbit.verticalAxis = 1f;
            stack.orbit.followSubjectHeading = false;
            stack.orbit.heading = 0f;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context());

            Assert.That(pose.Position.y, Is.EqualTo(stack.orbit.top.height).Within(1e-2f));
        }

        [Test]
        public void SweepingUpRaisesTheCamera()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.Orbit);
            stack.orbit.followSubjectHeading = false;

            stack.orbit.verticalAxis = 0f;
            float low = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context()).Position.y;

            stack.orbit.verticalAxis = 1f;
            float high = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context()).Position.y;

            Assert.That(high, Is.GreaterThan(low));
        }

        [Test]
        public void HeadingWalksTheCameraRoundTheSubject()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.Orbit);
            stack.orbit.followSubjectHeading = false;

            stack.orbit.heading = 0f;
            Vector3 front = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context()).Position;

            stack.orbit.heading = 180f;
            Vector3 back = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context()).Position;

            Assert.That(Vector3.Distance(front, back), Is.GreaterThan(1f),
                "Half a turn should put the camera on the other side.");
        }

        [Test]
        public void TheVerticalSweepIsDampedWhenAskedTo()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.Orbit);
            stack.orbit.followSubjectHeading = false;
            stack.orbit.verticalDamping = 1f;
            stack.orbit.verticalAxis = 1f;

            BasisCameraModifierState state = StackFixture.State();
            BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context());

            Assert.That(state.VerticalAxis, Is.GreaterThan(0f), "It should have started sweeping.");
            Assert.That(state.VerticalAxis, Is.LessThan(1f), "One damped frame must not arrive.");
        }
    }

    public class BasisCameraDollyModifierTests
    {
        private static IReadOnlyList<Vector3> Track() => new List<Vector3>
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(5f, 0f, 0f),
            new Vector3(10f, 0f, 0f),
        };

        private static BasisCameraSolveContext TrackContext(BasisCameraSubject subject, bool looped = false)
        {
            BasisCameraSolveContext context = StackFixture.Context(subject);
            context.DollyPoints = Track();
            context.DollyLooped = looped;
            return context;
        }

        [Test]
        public void TheCameraRidesToTheTrackPositionItWasGiven()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.DollyTrack);
            stack.dolly.mode = BasisCameraDollyMode.Manual;
            stack.dolly.position = 1f;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), TrackContext(StackFixture.Subject()));

            Assert.That(pose.Position, Is.EqualTo(new Vector3(5f, 0f, 0f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-2f)));
        }

        [Test]
        public void AutoTrackSlidesToWhicheverPartOfTheTrackIsNearestTheSubject()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.DollyTrack);
            stack.dolly.mode = BasisCameraDollyMode.FollowSubject;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(),
                TrackContext(StackFixture.Subject(new Vector3(9.5f, 0f, 0f))));

            Assert.That(pose.Position.x, Is.GreaterThan(8f));
        }

        [Test]
        public void ATrackWithNoPointsHoldsTheCameraInsteadOfCollapsing()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.DollyTrack);

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(1f, 2f, 3f), Quaternion.identity, 40f);

            BasisCameraSolveContext context = StackFixture.Context();
            context.DollyPoints = new List<Vector3>();

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(pose.Position, Is.EqualTo(new Vector3(1f, 2f, 3f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-4f)));
        }

        [Test]
        public void ANullTrackIsHandledLikeAnEmptyOne()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.DollyTrack);

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(4f, 5f, 6f), Quaternion.identity, 40f);

            Assert.That(() => BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context()), Throws.Nothing);
            Assert.That(state.Position, Is.EqualTo(new Vector3(4f, 5f, 6f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-4f)));
        }

        [Test]
        public void TrackSpeedCarriesTheCameraAlongOverTime()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.DollyTrack);
            stack.dolly.mode = BasisCameraDollyMode.Play;
            stack.dolly.playing = true;
            stack.dolly.speed = 2f;

            BasisCameraModifierState state = StackFixture.State();
            BasisCameraSolveContext context = TrackContext(StackFixture.Subject());

            for (int Frame = 0; Frame < 60; Frame++)
            {
                BasisCameraModifierSolver.Solve(stack, state, context);
            }

            Assert.That(state.DollyPosition, Is.GreaterThan(0f));
        }

        [Test]
        public void APausedMoveHoldsThePlayheadExactlyWhereItGotTo()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.DollyTrack);
            stack.dolly.mode = BasisCameraDollyMode.Play;
            stack.dolly.playing = true;
            stack.dolly.speed = 2f;

            BasisCameraModifierState state = StackFixture.State();
            BasisCameraSolveContext context = TrackContext(StackFixture.Subject());

            for (int Frame = 0; Frame < 30; Frame++)
            {
                BasisCameraModifierSolver.Solve(stack, state, context);
            }

            float paused = state.DollyPosition;
            Assert.That(paused, Is.GreaterThan(0f), "It should have started moving.");

            stack.dolly.playing = false;
            for (int Frame = 0; Frame < 60; Frame++)
            {
                BasisCameraModifierSolver.Solve(stack, state, context);
            }

            Assert.That(state.DollyPosition, Is.EqualTo(paused).Within(1e-3f),
                "Pausing must hold the playhead, not drift or snap back.");
        }

        [Test]
        public void AnOpenTrackReportsTheMoveOverWhenItRunsOut()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.DollyTrack);
            stack.dolly.mode = BasisCameraDollyMode.Play;
            stack.dolly.playing = true;
            stack.dolly.speed = 20f;

            BasisCameraModifierState state = StackFixture.State();
            BasisCameraSolveContext context = TrackContext(StackFixture.Subject());

            StackFixture.Settle(stack, state, context);

            Assert.That(state.DollyCompleted, Is.True,
                "A move that reaches the end of an open track has to say so, or the play button lies.");
            Assert.That(state.DollyPosition,
                Is.LessThanOrEqualTo(BasisCameraSpline.MaxPosition(3, false) + 1e-3f));
        }

        [Test]
        public void ALoopedTrackNeverReportsTheMoveOver()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.DollyTrack);
            stack.dolly.mode = BasisCameraDollyMode.Play;
            stack.dolly.playing = true;
            stack.dolly.speed = 20f;

            BasisCameraModifierState state = StackFixture.State();
            StackFixture.Settle(stack, state, TrackContext(StackFixture.Subject(), looped: true));

            Assert.That(state.DollyCompleted, Is.False, "A loop has no end to reach.");
        }

        [Test]
        public void ManualModeIgnoresTheSpeedEntirely()
        {
            // The old behaviour silently ignored the position slider whenever speed was non-zero.
            // The mode now says which one is in charge, so both keep meaning what they say.
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.DollyTrack);
            stack.dolly.mode = BasisCameraDollyMode.Manual;
            stack.dolly.position = 1f;
            stack.dolly.speed = 5f;
            stack.dolly.playing = true;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), TrackContext(StackFixture.Subject()));

            Assert.That(pose.Position, Is.EqualTo(new Vector3(5f, 0f, 0f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-2f)),
                "Manual mode must sit where the playhead says, whatever the speed is set to.");
        }

        [Test]
        public void TheDollyPositionIsClampedToTheTrackOnAnOpenPath()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.DollyTrack);
            stack.dolly.mode = BasisCameraDollyMode.Manual;
            stack.dolly.position = 99f;

            BasisCameraModifierState state = StackFixture.State();
            StackFixture.Settle(stack, state, TrackContext(StackFixture.Subject()));

            Assert.That(state.DollyPosition, Is.LessThanOrEqualTo(BasisCameraSpline.MaxPosition(3, false) + 1e-3f));
        }
    }

    /// <summary>
    /// The aim that films nobody. Every other rotation modifier resolves a subject; this one reads
    /// the path, so the cases worth pinning are the ones where there is no subject to fall back on
    /// and the ones where the path itself cannot answer.
    /// </summary>
    public class BasisCameraTrackAimTests
    {
        /// <summary>Straight out along +X, so the whole path has one heading to check against.</summary>
        private static IReadOnlyList<Vector3> StraightTrack() => new List<Vector3>
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(5f, 0f, 0f),
            new Vector3(10f, 0f, 0f),
        };

        /// <summary>Out along +X then away down +Z, so the aim has to turn with the corner.</summary>
        private static IReadOnlyList<Vector3> CorneringTrack() => new List<Vector3>
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(10f, 0f, 0f),
            new Vector3(10f, 0f, 10f),
        };

        private static BasisCameraModifierStack TrackAimStack(
            BasisCameraPositionModifier position = BasisCameraPositionModifier.DollyTrack)
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(position);
            stack.rotationModifier = BasisCameraRotationModifier.AimAlongTrack;
            stack.trackAim.damping = Vector3.zero;
            return stack;
        }

        private static BasisCameraSolveContext TrackContext(IReadOnlyList<Vector3> points,
            BasisCameraSubject subject = default, bool looped = false)
        {
            BasisCameraSolveContext context = StackFixture.Context(subject);
            context.DollyPoints = points;
            context.DollyLooped = looped;
            return context;
        }

        private static float Yaw(Quaternion rotation)
            => BasisCameraDamping.NormalizeAngle(rotation.eulerAngles.y);

        [Test]
        public void TheShotLooksTheWayTheMoveIsTravelling()
        {
            BasisCameraModifierStack stack = TrackAimStack();
            stack.dolly.mode = BasisCameraDollyMode.Manual;
            stack.dolly.position = 1f;

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), TrackContext(StraightTrack()));

            Assert.That(Yaw(pose.Rotation), Is.EqualTo(90f).Within(0.5f),
                "A track running out along +X has to put the shot on a heading of 90 degrees.");
        }

        [Test]
        public void ItAimsWithNobodyBeingFilmedAtAll()
        {
            // The whole point of the modifier: a track is authored in the world, so a shot down one
            // is the case that has no subject to resolve. Every other aim returns early here.
            BasisCameraModifierStack stack = TrackAimStack();
            stack.subject.modifier = BasisCameraSubjectModifier.None;
            stack.dolly.mode = BasisCameraDollyMode.Manual;
            stack.dolly.position = 1f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraPose pose = StackFixture.Settle(stack, state,
                TrackContext(StraightTrack(), new BasisCameraSubject { Valid = false }));

            Assert.That(Yaw(pose.Rotation), Is.EqualTo(90f).Within(0.5f));
        }

        [Test]
        public void TheAimTurnsWithTheCornerItIsRiding()
        {
            BasisCameraModifierStack stack = TrackAimStack();
            stack.dolly.mode = BasisCameraDollyMode.Manual;

            stack.dolly.position = 0.5f;
            float early = Yaw(StackFixture.Settle(stack, StackFixture.State(),
                TrackContext(CorneringTrack())).Rotation);

            stack.dolly.position = 1.5f;
            float late = Yaw(StackFixture.Settle(stack, StackFixture.State(),
                TrackContext(CorneringTrack())).Rotation);

            Assert.That(early, Is.EqualTo(90f).Within(15f), "The first leg runs along +X.");
            Assert.That(late, Is.EqualTo(0f).Within(15f), "The second runs along +Z.");
        }

        [Test]
        public void TheOffsetCanTurnTheShotBackUpTheTrack()
        {
            // The pull-back shot, and the reason there is no separate reverse toggle: a yaw offset
            // of 180 is the whole of it.
            BasisCameraModifierStack stack = TrackAimStack();
            stack.dolly.mode = BasisCameraDollyMode.Manual;
            stack.dolly.position = 1f;
            stack.trackAim.rotationOffset = new Vector3(0f, 180f, 0f);

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), TrackContext(StraightTrack()));

            Assert.That(Yaw(pose.Rotation), Is.EqualTo(-90f).Within(0.5f));
        }

        [Test]
        public void AHandFlownCameraTakesTheHeadingOfTheNearestPartOfTheTrack()
        {
            // Nothing is riding the track, so there is no playhead to read. The nearest point on
            // the path stands in, which is what lets a flown camera be locked to a laid-out line.
            BasisCameraModifierStack stack = TrackAimStack(BasisCameraPositionModifier.FreeFly);

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(10f, 0f, 4f), Quaternion.identity, 40f);

            BasisCameraPose pose = StackFixture.Settle(stack, state, TrackContext(CorneringTrack()));

            Assert.That(Yaw(pose.Rotation), Is.EqualTo(0f).Within(15f),
                "Standing beside the second leg, the heading to take is that leg's.");
        }

        [Test]
        public void ATrackTooShortToHaveADirectionHoldsTheAimInstead()
        {
            // One point is a place, not a path. The tangent's own fallback is world forward, and
            // snapping the shot there would be worse than leaving it where the operator put it.
            BasisCameraModifierStack stack = TrackAimStack(BasisCameraPositionModifier.LockedOff);

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, BasisCameraDamping.Yaw(45f), 40f);

            BasisCameraPose pose = StackFixture.Settle(stack, state,
                TrackContext(new List<Vector3> { new Vector3(3f, 0f, 3f) }));

            Assert.That(Yaw(pose.Rotation), Is.EqualTo(45f).Within(0.5f));
        }

        [Test]
        public void NoTrackAtAllIsHandledLikeAShortOne()
        {
            BasisCameraModifierStack stack = TrackAimStack(BasisCameraPositionModifier.LockedOff);

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, BasisCameraDamping.Yaw(45f), 40f);

            Assert.That(() => StackFixture.Settle(stack, state, StackFixture.Context()), Throws.Nothing);
            Assert.That(Yaw(state.Rotation), Is.EqualTo(45f).Within(0.5f));
        }

        [Test]
        public void AVerticalClimbDoesNotCollapseTheAim()
        {
            // LookRotation answers a forward and an up on the same line with identity, which would
            // cut the shot round to world forward at the steepest part of a crane move.
            BasisCameraModifierStack stack = TrackAimStack();
            stack.dolly.mode = BasisCameraDollyMode.Manual;
            stack.dolly.position = 1f;

            var climb = new List<Vector3>
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, 5f, 0f),
                new Vector3(0f, 10f, 0f),
            };

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            StackFixture.Settle(stack, state, TrackContext(climb));

            Assert.That((state.Rotation * Vector3.forward).y, Is.GreaterThan(0.99f),
                "The shot has to keep looking up the climb rather than snapping level.");
        }
    }

    public class BasisCameraOcclusionEffectTests
    {
        private static BasisCameraSolveContext WithProbe(BasisCameraSolveContext context, float freeDistance)
        {
            context.OcclusionProbe = (Vector3 target, Vector3 desired, out float free) =>
            {
                free = freeDistance;
                return true;
            };
            return context;
        }

        private static BasisCameraModifierStack OccludedStack()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 4f));
            stack.follow.lateralTracking = 0f;
            stack.occlusion.padding = 0.25f;
            stack.occlusion.minDistance = 0.4f;
            stack.occlusion.returnDamping = 0f;
            stack.AddEffect(BasisCameraEffectModifier.AvoidOcclusion);
            return stack;
        }

        [Test]
        public void AWallPullsTheCameraInToSitInFrontOfIt()
        {
            BasisCameraModifierStack stack = OccludedStack();
            BasisCameraSolveContext context = WithProbe(StackFixture.Context(), 2f);

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), context);

            float distance = Vector3.Distance(pose.Position, context.Subject.LookPoint);
            Assert.That(distance, Is.EqualTo(2f - 0.25f).Within(1e-2f));
        }

        [Test]
        public void OcclusionIsIgnoredEntirelyWhenTheStackDoesNotAskForIt()
        {
            BasisCameraModifierStack stack = OccludedStack();
            stack.RemoveEffect(BasisCameraEffectModifier.AvoidOcclusion);

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(),
                WithProbe(StackFixture.Context(), 0.5f));

            Assert.That(pose.Position.z, Is.EqualTo(4f).Within(1e-2f));
        }

        [Test]
        public void TheCameraNeverPushesCloserThanTheMinimum()
        {
            BasisCameraModifierStack stack = OccludedStack();
            BasisCameraSolveContext context = WithProbe(StackFixture.Context(), 0.05f);

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), context);

            float distance = Vector3.Distance(pose.Position, context.Subject.LookPoint);
            Assert.That(distance, Is.GreaterThanOrEqualTo(0.4f - 1e-3f));
        }

        [Test]
        public void OcclusionNeverPushesTheCameraFurtherOutThanAuthored()
        {
            BasisCameraModifierStack stack = OccludedStack();
            BasisCameraSolveContext context = WithProbe(StackFixture.Context(), 100f);

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), context);

            float distance = Vector3.Distance(pose.Position, context.Subject.LookPoint);
            Assert.That(distance, Is.LessThanOrEqualTo(Vector3.Distance(new Vector3(0f, 0f, 4f),
                context.Subject.LookPoint) + 1e-3f));
        }

        [Test]
        public void ThePullInIsImmediateButTheReturnIsEased()
        {
            BasisCameraModifierStack stack = OccludedStack();
            stack.occlusion.returnDamping = 1f;

            BasisCameraModifierState state = StackFixture.State();
            StackFixture.Settle(stack, state, WithProbe(StackFixture.Context(), 1f));
            float pulledIn = state.OcclusionDistance;

            BasisCameraSolveContext clear = StackFixture.Context();
            clear.OcclusionProbe = (Vector3 target, Vector3 desired, out float free) =>
            {
                free = 0f;
                return false;
            };
            BasisCameraModifierSolver.Solve(stack, state, clear);

            Assert.That(state.OcclusionDistance, Is.GreaterThan(pulledIn), "It should be easing back out.");
            Assert.That(state.OcclusionDistance, Is.LessThan(4f), "One frame must not complete the return.");
        }

        [Test]
        public void AMissingProbeIsTreatedAsAClearShot()
        {
            BasisCameraModifierStack stack = OccludedStack();

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context());

            Assert.That(pose.Position.z, Is.EqualTo(4f).Within(1e-2f));
        }
    }

    public class BasisCameraSteadySubjectEffectTests
    {
        private static BasisCameraModifierStack SteadyStack()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            StackFixture.Binding(stack, BasisCameraBindingMode.WorldSpace);
            stack.follow.lateralTracking = 0f;
            stack.steady.smoothing = 0.25f;
            stack.steady.verticalDeadZone = 0f;
            stack.AddEffect(BasisCameraEffectModifier.SteadySubject);
            return stack;
        }

        [Test]
        public void ASubjectThatJitters_LeavesTheCameraSteadierThanTheyAre()
        {
            BasisCameraModifierStack steady = SteadyStack();
            BasisCameraModifierStack raw = SteadyStack();
            raw.RemoveEffect(BasisCameraEffectModifier.SteadySubject);

            BasisCameraModifierState steadyState = StackFixture.State();
            BasisCameraModifierState rawState = StackFixture.State();

            float steadyTravel = 0f;
            float rawTravel = 0f;
            Vector3 lastSteady = Vector3.zero;
            Vector3 lastRaw = Vector3.zero;

            for (int Frame = 0; Frame < 120; Frame++)
            {
                // A subject that ends every other frame back where they started: all wobble, no travel.
                float wobble = (Frame % 2 == 0 ? 0.05f : -0.05f);
                BasisCameraSolveContext context = StackFixture.Context(
                    StackFixture.Subject(new Vector3(wobble, 0f, 0f)));

                BasisCameraPose steadyPose = BasisCameraModifierSolver.Solve(steady, steadyState, context);
                BasisCameraPose rawPose = BasisCameraModifierSolver.Solve(raw, rawState, context);

                if (Frame > 10)
                {
                    steadyTravel += Vector3.Distance(steadyPose.Position, lastSteady);
                    rawTravel += Vector3.Distance(rawPose.Position, lastRaw);
                }
                lastSteady = steadyPose.Position;
                lastRaw = rawPose.Position;
            }

            Assert.That(steadyTravel, Is.LessThan(rawTravel * 0.5f),
                "The filtered anchor should leave the camera moving far less than the raw one does.");
        }

        [Test]
        public void TheAimPointMovesWithTheAnchor_SoTheShotStaysOnTheSubject()
        {
            BasisCameraModifierStack stack = SteadyStack();
            stack.rotationModifier = BasisCameraRotationModifier.Hold;

            BasisCameraModifierState state = StackFixture.State();
            StackFixture.Settle(stack, state, StackFixture.Context());

            // Once settled on a still subject the correction is spent, so the camera has to be
            // exactly where it would have been with nothing fitted.
            BasisCameraPose pose = StackFixture.Settle(stack, state, StackFixture.Context());
            Assert.That(pose.Position, Is.EqualTo(new Vector3(0f, 0f, 2f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-3f)));
        }

        [Test]
        public void TheDeadZoneAbsorbsAJumpThatLandsWhereItStarted()
        {
            BasisCameraModifierStack stack = SteadyStack();
            stack.steady.smoothing = 0f;
            stack.steady.verticalDeadZone = 0.5f;

            BasisCameraModifierState state = StackFixture.State();
            StackFixture.Settle(stack, state, StackFixture.Context());
            float restingHeight = state.Position.y;

            BasisCameraPose mid = BasisCameraModifierSolver.Solve(stack, state,
                StackFixture.Context(StackFixture.Subject(new Vector3(0f, 0.3f, 0f))));

            Assert.That(mid.Position.y, Is.EqualTo(restingHeight).Within(1e-3f),
                "A hop smaller than the dead zone must not move the shot at all.");
        }

        [Test]
        public void MovementBeyondTheDeadZoneIsStillFollowed()
        {
            BasisCameraModifierStack stack = SteadyStack();
            stack.steady.smoothing = 0f;
            stack.steady.verticalDeadZone = 0.5f;

            BasisCameraModifierState state = StackFixture.State();
            StackFixture.Settle(stack, state, StackFixture.Context());

            BasisCameraPose pose = StackFixture.Settle(stack, state,
                StackFixture.Context(StackFixture.Subject(new Vector3(0f, 4f, 0f))));

            Assert.That(pose.Position.y, Is.EqualTo(4f - 0.5f).Within(1e-2f),
                "Climbing a storey should carry the shot up, less the dead zone it spent getting out.");
        }

        [Test]
        public void TheDeadZoneScalesWithTheAvatar()
        {
            BasisCameraModifierStack stack = SteadyStack();
            stack.steady.smoothing = 0f;
            stack.steady.verticalDeadZone = 0.5f;

            BasisCameraModifierState state = StackFixture.State();
            StackFixture.Settle(stack, state, StackFixture.Context(StackFixture.Subject(scale: 2f)));

            BasisCameraPose pose = StackFixture.Settle(stack, state,
                StackFixture.Context(StackFixture.Subject(new Vector3(0f, 4f, 0f), scale: 2f)));

            Assert.That(pose.Position.y, Is.EqualTo(4f - 1f).Within(1e-2f));
        }

        [Test]
        public void TheFilterSurvivesTheModifiersThatResetSubjectHistoryEveryFrame()
        {
            // Orbit calls ResetSubjectHistory on every solve. If the settled anchor were cleared
            // there too, the filter would re-seed each frame and the effect would do nothing.
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.Orbit);
            stack.steady.smoothing = 0.5f;
            stack.AddEffect(BasisCameraEffectModifier.SteadySubject);

            BasisCameraModifierState state = StackFixture.State();
            BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context());
            BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context());

            Assert.That(state.HasSteadyAnchor, Is.True);
        }
    }

    public class BasisCameraCollisionEffectTests
    {
        private static BasisCameraModifierStack CollisionStack()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FreeFly);
            stack.collision.radius = 0.2f;
            stack.collision.padding = 0.1f;
            stack.AddEffect(BasisCameraEffectModifier.AvoidCollision);
            return stack;
        }

        private static BasisCameraSolveContext WithSweep(BasisCameraSolveContext context, float freeDistance, bool hit = true)
        {
            context.SweepProbe = (Vector3 origin, Vector3 direction, float distance, float radius, out float free) =>
            {
                free = freeDistance;
                return hit;
            };
            return context;
        }

        [Test]
        public void AWallInThePathStopsTheCameraShortOfIt()
        {
            BasisCameraModifierStack stack = CollisionStack();
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraSolveContext context = WithSweep(StackFixture.Context(), 1f);
            context.OperatorMove = new Vector3(0f, 0f, 5f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(pose.Position.z, Is.EqualTo(1f - 0.1f).Within(1e-3f),
                "It should stop at the hit, less the clearance.");
        }

        [Test]
        public void AClearPathIsTravelledInFull()
        {
            BasisCameraModifierStack stack = CollisionStack();
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraSolveContext context = WithSweep(StackFixture.Context(), 0f, hit: false);
            context.OperatorMove = new Vector3(0f, 0f, 5f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(pose.Position.z, Is.EqualTo(5f).Within(1e-3f));
        }

        [Test]
        public void StartingInsideGeometryDoesNotPinTheCameraThere()
        {
            // A sweep that begins overlapping reports nothing free. Honouring that would leave the
            // camera stuck wherever it was standing when the geometry arrived around it.
            BasisCameraModifierStack stack = CollisionStack();
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraSolveContext context = WithSweep(StackFixture.Context(), 0f);
            context.OperatorMove = new Vector3(0f, 0f, 5f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(pose.Position.z, Is.EqualTo(5f).Within(1e-3f));
        }

        [Test]
        public void ItNeverOvershootsThePathItWasAskedToTravel()
        {
            BasisCameraModifierStack stack = CollisionStack();
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraSolveContext context = WithSweep(StackFixture.Context(), 100f);
            context.OperatorMove = new Vector3(0f, 0f, 2f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(pose.Position.z, Is.EqualTo(2f).Within(1e-3f));
        }

        [Test]
        public void AMissingProbeIsTreatedAsAnEmptyRoom()
        {
            BasisCameraModifierStack stack = CollisionStack();
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraSolveContext context = StackFixture.Context();
            context.OperatorMove = new Vector3(0f, 0f, 5f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(pose.Position.z, Is.EqualTo(5f).Within(1e-3f));
        }
    }

    public class BasisCameraDollyZoomEffectTests
    {
        private static BasisCameraModifierStack DollyZoomStack()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FreeFly);
            stack.dollyZoom.minFov = 5f;
            stack.dollyZoom.maxFov = 120f;
            stack.AddEffect(BasisCameraEffectModifier.DollyZoom);
            return stack;
        }

        private static float ApparentSize(BasisCameraPose pose, Vector3 lookPoint)
        {
            float distance = Vector3.Distance(pose.Position, lookPoint);
            return distance * Mathf.Tan(pose.Fov * 0.5f * Mathf.Deg2Rad);
        }

        [Test]
        public void PullingBackWidensTheLensByExactlyWhatTheDistanceTook()
        {
            BasisCameraModifierStack stack = DollyZoomStack();
            BasisCameraModifierState state = StackFixture.State();

            BasisCameraSolveContext near = StackFixture.Context();
            near.OperatorPosition = new Vector3(0f, 0f, 3f);
            BasisCameraPose first = BasisCameraModifierSolver.Solve(stack, state, near);

            BasisCameraSolveContext far = StackFixture.Context();
            far.OperatorMove = new Vector3(0f, 0f, 6f);
            BasisCameraPose second = BasisCameraModifierSolver.Solve(stack, state, far);

            Assert.That(second.Fov, Is.LessThan(first.Fov),
                "Backing away has to narrow the lens to hold the subject the same size.");
            Assert.That(ApparentSize(second, far.Subject.LookPoint),
                Is.EqualTo(ApparentSize(first, near.Subject.LookPoint)).Within(1e-3f));
        }

        [Test]
        public void TheLensStaysInsideItsAuthoredLimits()
        {
            BasisCameraModifierStack stack = DollyZoomStack();
            stack.dollyZoom.minFov = 30f;
            stack.dollyZoom.maxFov = 50f;

            BasisCameraModifierState state = StackFixture.State();
            BasisCameraSolveContext near = StackFixture.Context();
            near.OperatorPosition = new Vector3(0f, 0f, 3f);
            BasisCameraModifierSolver.Solve(stack, state, near);

            BasisCameraSolveContext miles = StackFixture.Context();
            miles.OperatorMove = new Vector3(0f, 0f, 397f);
            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, miles);

            Assert.That(pose.Fov, Is.EqualTo(30f).Within(1e-3f));
        }

        [Test]
        public void TheReferenceIsTakenFromTheShotAsItStood()
        {
            BasisCameraModifierStack stack = DollyZoomStack();
            BasisCameraModifierState state = StackFixture.State();

            BasisCameraSolveContext context = StackFixture.Context();
            context.OperatorPosition = new Vector3(0f, 0f, 3f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(pose.Fov, Is.EqualTo(context.Fov).Within(1e-3f),
                "Fitting it must not move the lens on the frame it was fitted.");
        }

        [Test]
        public void TheStackTakesTheLensChannelWhileItIsFitted()
        {
            BasisCameraModifierStack stack = DollyZoomStack();
            Assert.That(stack.DrivesLens, Is.True);

            stack.RemoveEffect(BasisCameraEffectModifier.DollyZoom);
            Assert.That(stack.DrivesLens, Is.False);
        }

        [Test]
        public void ReseedingRetakesTheReference()
        {
            BasisCameraModifierStack stack = DollyZoomStack();
            BasisCameraModifierState state = StackFixture.State();

            BasisCameraSolveContext near = StackFixture.Context();
            near.OperatorPosition = new Vector3(0f, 0f, 3f);
            BasisCameraModifierSolver.Solve(stack, state, near);

            state.Reseed();

            BasisCameraSolveContext far = StackFixture.Context();
            far.OperatorPosition = new Vector3(0f, 0f, 9f);
            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, far);

            Assert.That(pose.Fov, Is.EqualTo(far.Fov).Within(1e-3f));
        }
    }

    public class BasisCameraRigWeightEffectTests
    {
        private static BasisCameraModifierStack RigWeightStack()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.LockedOff);
            stack.rigWeight.responsiveness = 3f;
            stack.rigWeight.bounce = 1f;
            stack.AddEffect(BasisCameraEffectModifier.RigWeight);
            return stack;
        }

        private static BasisCameraSolveContext Steer(float yawDegrees)
        {
            BasisCameraSolveContext context = StackFixture.Context();
            context.OperatorYaw = yawDegrees;
            return context;
        }

        private static float Yaw(Quaternion rotation)
            => BasisCameraDamping.NormalizeAngle(rotation.eulerAngles.y);

        [Test]
        public void AFastPanCarriesPastTheMarkBeforeSettling()
        {
            BasisCameraModifierStack stack = RigWeightStack();
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            float furthest = 0f;
            for (int Frame = 0; Frame < 120; Frame++)
            {
                BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, Steer(Frame == 0 ? 30f : 0f));
                furthest = Mathf.Max(furthest, Yaw(pose.Rotation));
            }

            Assert.That(furthest, Is.GreaterThan(30f),
                "At full bounce the rig has to overshoot, which is the thing damping cannot do.");
        }

        [Test]
        public void ItSettlesOnTheAimItWasGiven()
        {
            BasisCameraModifierStack stack = RigWeightStack();
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraPose pose = default;
            for (int Frame = 0; Frame < 600; Frame++)
            {
                pose = BasisCameraModifierSolver.Solve(stack, state, Steer(Frame == 0 ? 30f : 0f));
            }

            Assert.That(Yaw(pose.Rotation), Is.EqualTo(30f).Within(0.5f));
        }

        [Test]
        public void NoBounceArrivesWithoutCrossingTheMark()
        {
            BasisCameraModifierStack stack = RigWeightStack();
            stack.rigWeight.bounce = 0f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            for (int Frame = 0; Frame < 600; Frame++)
            {
                BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, Steer(Frame == 0 ? 30f : 0f));
                Assert.That(Yaw(pose.Rotation), Is.LessThanOrEqualTo(30f + 1e-2f));
            }
        }

        [Test]
        public void ItStaysStableAtALongFrame()
        {
            // A stiff spring stepped once at a long frame gains energy rather than losing it. The
            // substepping is what keeps a hitching frame from spiralling the camera.
            BasisCameraModifierStack stack = RigWeightStack();
            stack.rigWeight.responsiveness = 12f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraPose pose = default;
            for (int Frame = 0; Frame < 400; Frame++)
            {
                BasisCameraSolveContext context = Steer(Frame == 0 ? 30f : 0f);
                context.DeltaTime = 0.25f;
                pose = BasisCameraModifierSolver.Solve(stack, state, context);
            }

            Assert.That(Yaw(pose.Rotation), Is.EqualTo(30f).Within(1f));
        }

        [Test]
        public void ACutIsSnappedRatherThanSwungThrough()
        {
            BasisCameraModifierStack stack = RigWeightStack();
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);
            BasisCameraModifierSolver.Solve(stack, state, Steer(0f));

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, Steer(170f));

            Assert.That(Mathf.Abs(Yaw(pose.Rotation)), Is.EqualTo(170f).Within(1e-2f),
                "Past the snap angle the aim has cut, and springing across it would sweep the shot.");
        }

        [Test]
        public void TheOperatorsOwnAimIsLeftUnlagged()
        {
            // The same trap shake carries: feeding the published pose back would let the lag
            // accumulate, and the aim would walk away from where the sticks left it.
            BasisCameraModifierStack stack = RigWeightStack();
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            for (int Frame = 0; Frame < 60; Frame++)
            {
                BasisCameraModifierSolver.Solve(stack, state, Steer(Frame == 0 ? 30f : 0f));
            }

            Assert.That(Yaw(state.Rotation), Is.EqualTo(30f).Within(1e-2f),
                "state.Rotation is what the operator continues from, so it must be the un-lagged aim.");
        }
    }

    public class BasisCameraOperatorChannelTests
    {
        [Test]
        public void AFreshStateSeedsFromTheOperatorPose()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack
            {
                positionModifier = BasisCameraPositionModifier.FreeFly,
                rotationModifier = BasisCameraRotationModifier.Hold,
            };

            BasisCameraSolveContext context = StackFixture.Context();
            context.OperatorPosition = new Vector3(7f, 8f, 9f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, StackFixture.State(), context);

            Assert.That(pose.Position, Is.EqualTo(new Vector3(7f, 8f, 9f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-4f)));
        }

        [Test]
        public void HoldAbsorbsTheOperatorsSteeringIntoItsOwnRotation()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack
            {
                positionModifier = BasisCameraPositionModifier.LockedOff,
                rotationModifier = BasisCameraRotationModifier.Hold,
            };
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraSolveContext steering = StackFixture.Context();
            steering.OperatorYaw = 90f;
            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, steering);
            Assert.That(Quaternion.Angle(pose.Rotation, BasisCameraDamping.Yaw(90f)), Is.LessThan(0.01f));

            BasisCameraPose released = BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context());
            Assert.That(Quaternion.Angle(released.Rotation, BasisCameraDamping.Yaw(90f)), Is.LessThan(0.01f),
                "Hold keeps whatever the operator left it at.");
            Assert.That(Quaternion.Angle(state.Rotation, BasisCameraDamping.Yaw(90f)), Is.LessThan(0.01f));
        }

        [Test]
        public void AStackCanTakeOneChannelAndLeaveTheOther()
        {
            // The combination the two slots exist to make possible: fly the camera by hand while
            // something else keeps it pointed. The old design had no way to express this.
            BasisCameraModifierStack stack = new BasisCameraModifierStack
            {
                positionModifier = BasisCameraPositionModifier.FreeFly,
                rotationModifier = BasisCameraRotationModifier.MatchSubject,
            };
            stack.matchSubject.damping = Vector3.zero;

            Assert.That(stack.DrivesPosition, Is.False);
            Assert.That(stack.DrivesRotation, Is.True);
            Assert.That(stack.DrivesAnything, Is.True);

            BasisCameraSolveContext context = StackFixture.Context(StackFixture.Subject(yawDegrees: 90f));
            context.OperatorPosition = new Vector3(1f, 2f, 3f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, StackFixture.State(), context);

            Assert.That(pose.Position, Is.EqualTo(new Vector3(1f, 2f, 3f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-4f)));
        }

        [Test]
        public void HoldKeepsWhateverRotationItAlreadyHad()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.LockedOff);

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, BasisCameraDamping.Yaw(45f), 40f);

            BasisCameraSolveContext context = StackFixture.Context();
            context.OperatorRotation = BasisCameraDamping.Yaw(-120f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(Quaternion.Angle(pose.Rotation, BasisCameraDamping.Yaw(45f)), Is.LessThan(0.01f));
        }

        [Test]
        public void ANullStackFallsBackToTheOperatorPose()
        {
            BasisCameraSolveContext context = StackFixture.Context();
            context.OperatorPosition = new Vector3(2f, 3f, 4f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(null, null, context);

            Assert.That(pose.Position, Is.EqualTo(new Vector3(2f, 3f, 4f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-4f)));
        }
    }

    public class BasisCameraLensEffectTests
    {
        [Test]
        public void TheLensOverrideDrivesTheFieldOfView()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.LockedOff);
            stack.lens.fov = 90f;
            stack.lens.damping = 0f;
            stack.AddEffect(BasisCameraEffectModifier.LensOverride);

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context());

            Assert.That(stack.DrivesLens, Is.True);
            Assert.That(pose.Fov, Is.EqualTo(90f).Within(1e-3f));
        }

        [Test]
        public void WithoutTheOverrideTheOperatorsLensIsLeftAlone()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.LockedOff);
            stack.lens.fov = 90f;

            BasisCameraSolveContext context = StackFixture.Context();
            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), context);

            Assert.That(stack.DrivesLens, Is.False);
            Assert.That(pose.Fov, Is.EqualTo(context.Fov).Within(1e-3f));
        }
    }

    public class BasisCameraShakeEffectTests
    {
        [Test]
        public void ShakeIsAppliedToTheOutputWithoutAccumulatingIntoTheSolveState()
        {
            // The wander has to be an offset on the finished pose, never folded back into the pose
            // the next frame continues from. Accumulated, it random-walks the camera away from
            // wherever it was put — which is exactly what a hand-flown camera with shake fitted is.
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.LockedOff);
            stack.shake = BasisCameraNoiseSettings.ForProfile(BasisCameraNoiseProfile.Shaky);
            stack.AddEffect(BasisCameraEffectModifier.Shake);

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(5f, 1f, 5f), Quaternion.identity, 40f);

            BasisCameraSolveContext context = StackFixture.Context();
            for (int Frame = 0; Frame < 600; Frame++)
            {
                context.Time = Frame / 60f;
                BasisCameraModifierSolver.Solve(stack, state, context);
            }

            Assert.That(state.Position, Is.EqualTo(new Vector3(5f, 1f, 5f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-4f)),
                "Ten seconds of shake moved the pose it is meant to be an offset from.");
        }

        [Test]
        public void ShakeMovesTheOutputItLeavesTheStateAlone()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.LockedOff);
            stack.shake = BasisCameraNoiseSettings.ForProfile(BasisCameraNoiseProfile.Shaky);
            stack.AddEffect(BasisCameraEffectModifier.Shake);

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(5f, 1f, 5f), Quaternion.identity, 40f);

            BasisCameraSolveContext context = StackFixture.Context();
            context.Time = 3.7f;

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(Vector3.Distance(pose.Position, state.Position), Is.GreaterThan(0f),
                "A shaky profile should have moved the output.");
        }
    }

    public class BasisCameraLookAheadEffectTests
    {
        [Test]
        public void LookAheadLeadsAMovingSubject()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            stack.follow.lateralTracking = 0f;
            stack.lookAhead.time = 0.5f;
            stack.lookAhead.limit = 10f;
            stack.AddEffect(BasisCameraEffectModifier.LookAhead);

            BasisCameraSubject subject = StackFixture.Subject();
            subject.Velocity = new Vector3(4f, 0f, 0f);

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context(subject));

            Assert.That(pose.Position.x, Is.GreaterThan(1f), "The camera should be led ahead of the subject.");
        }

        [Test]
        public void WithoutTheEffectTheSubjectsVelocityIsIgnored()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            stack.follow.lateralTracking = 0f;

            BasisCameraSubject subject = StackFixture.Subject();
            subject.Velocity = new Vector3(4f, 0f, 0f);

            BasisCameraPose pose = StackFixture.Settle(stack, StackFixture.State(), StackFixture.Context(subject));

            Assert.That(pose.Position.x, Is.EqualTo(0f).Within(1e-3f));
        }
    }

    public class BasisCameraModifierStateTests
    {
        [Test]
        public void Seed_ContinuesFromTheHandOffPoseRatherThanCuttingToTheSolve()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            StackFixture.Damping(stack, new Vector3(1f, 1f, 1f));
            stack.follow.lateralTracking = 0f;
            stack.follow.teleportDistance = 100f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(0f, 0f, 20f), Quaternion.identity, 40f);

            BasisCameraPose first = BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context());

            Assert.That(first.Position.z, Is.LessThan(20f), "It should have started easing in.");
            Assert.That(first.Position.z, Is.GreaterThan(2f), "It must not have arrived in one frame.");
        }

        [Test]
        public void Reseed_RederivesFromTheSubjectRatherThanEasingAcrossTheMap()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            StackFixture.Damping(stack, new Vector3(1f, 1f, 1f));
            stack.follow.lateralTracking = 0f;

            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(0f, 0f, 500f), Quaternion.identity, 40f);
            state.Reseed();

            BasisCameraSolveContext context = StackFixture.Context();
            context.OperatorPosition = Vector3.zero;

            BasisCameraPose first = BasisCameraModifierSolver.Solve(stack, state, context);

            Assert.That(first.Position.z, Is.LessThan(10f),
                "A reseed drops the old pose, so the camera must not sweep in from 500 metres away.");
        }

        [Test]
        public void Seed_ClearsTheStrafeHistory()
        {
            BasisCameraModifierState state = StackFixture.State();
            state.LastAnchor = new Vector3(100f, 0f, 0f);
            state.HasLastAnchor = true;
            state.SmoothedLateralSpeed = 42f;

            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            Assert.That(state.HasLastAnchor, Is.False);
            Assert.That(state.SmoothedLateralSpeed, Is.EqualTo(0f));
        }

        [Test]
        public void ResetSubjectHistory_DropsTheStrafeHistoryWithoutMovingTheCamera()
        {
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(1f, 2f, 3f), Quaternion.identity, 40f);
            state.SmoothedLateralSpeed = 9f;
            state.HasLastAnchor = true;

            state.ResetSubjectHistory();

            Assert.That(state.Position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(state.HasLastAnchor, Is.False);
            Assert.That(state.SmoothedLateralSpeed, Is.EqualTo(0f));
        }
    }

    public class BasisCameraOperatorOverrideTests
    {
        private static BasisCameraSolveContext Pushed(Vector3 move)
        {
            BasisCameraSolveContext context = StackFixture.Context();
            context.OperatorMove = move;
            return context;
        }

        private static BasisCameraSolveContext Steered(float yaw, float pitch)
        {
            BasisCameraSolveContext context = StackFixture.Context();
            context.OperatorYaw = yaw;
            context.OperatorPitch = pitch;
            return context;
        }

        private static float Yaw(Quaternion rotation)
            => BasisCameraDamping.NormalizeAngle(rotation.eulerAngles.y);

        [Test]
        public void FreeFlyKeepsEveryPushTheOperatorMakes()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FreeFly);
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            for (int Frame = 0; Frame < 10; Frame++)
            {
                BasisCameraModifierSolver.Solve(stack, state, Pushed(new Vector3(0.1f, 0f, 0f)));
            }
            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context());

            Assert.That(pose.Position.x, Is.EqualTo(1f).Within(1e-4f), "Nothing steers a free-flying camera back.");
        }

        [Test]
        public void AFollowCameraGivesWayWhilePushedAndEasesBackWhenReleased()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(0f, 0f, 2f), Quaternion.identity, 40f);

            BasisCameraPose pushed = default;
            for (int Frame = 0; Frame < 30; Frame++)
            {
                pushed = BasisCameraModifierSolver.Solve(stack, state, Pushed(new Vector3(0f, 0f, 0.1f)));
            }
            Assert.That(pushed.Position.z, Is.EqualTo(5f).Within(1e-3f),
                "While the stick is held the operator moves the camera off its mark, whatever the damping.");

            BasisCameraPose released = default;
            for (int Frame = 0; Frame < 600; Frame++)
            {
                released = BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context());
            }
            Assert.That(released.Position.z, Is.EqualTo(2f).Within(1e-2f), "Let go and the modifier takes the camera back.");
        }

        [Test]
        public void AnOrbitIsPushedOffItsRingOnlyWhileTheStickIsHeld()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.Orbit);
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);
            BasisCameraPose onRing = BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context());

            BasisCameraPose pushed = default;
            for (int Frame = 0; Frame < 20; Frame++)
            {
                pushed = BasisCameraModifierSolver.Solve(stack, state, Pushed(new Vector3(0f, 0.1f, 0f)));
            }
            Assert.That(pushed.Position.y - onRing.Position.y, Is.EqualTo(2f).Within(1e-3f),
                "A modifier with no memory of its own still gives way to the operator.");

            BasisCameraPose released = default;
            for (int Frame = 0; Frame < 600; Frame++)
            {
                released = BasisCameraModifierSolver.Solve(stack, state, StackFixture.Context());
            }
            Assert.That(Vector3.Distance(released.Position, onRing.Position), Is.LessThan(1e-2f));
        }

        [Test]
        public void LookAtSubjectIsSteeredWhileInputIsGivenAndReaimsWhenItStops()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.LockedOff);
            stack.rotationModifier = BasisCameraRotationModifier.LookAtSubject;
            stack.lookAt.damping = Vector3.zero;
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);
            BasisCameraSolveContext context = StackFixture.Context(StackFixture.Subject(new Vector3(0f, 0f, 10f)));

            BasisCameraSolveContext steering = context;
            steering.OperatorYaw = 30f;
            BasisCameraPose steered = BasisCameraModifierSolver.Solve(stack, state, steering);
            Assert.That(Yaw(steered.Rotation), Is.EqualTo(30f).Within(0.5f));
            Assert.That(Yaw(state.Rotation), Is.EqualTo(0f).Within(0.5f),
                "The modifier's own aim is untouched; the steering sits on top of it.");

            BasisCameraPose released = default;
            for (int Frame = 0; Frame < 600; Frame++)
            {
                released = BasisCameraModifierSolver.Solve(stack, state, context);
            }
            Assert.That(Yaw(released.Rotation), Is.EqualTo(0f).Within(0.5f));
        }

        [Test]
        public void SteeringNeverPitchesPastVertical()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.LockedOff);
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);

            BasisCameraPose pose = BasisCameraModifierSolver.Solve(stack, state, Steered(0f, 200f));

            Assert.That(BasisCameraModifierSolver.PitchDegrees(pose.Rotation),
                Is.EqualTo(BasisCameraModifierSolver.OperatorMaxPitch).Within(1e-3f));
        }

        [Test]
        public void SteeringLevelsARolledCamera()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.LockedOff);
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, BasisCameraDamping.Roll(20f), 40f);

            BasisCameraPose pose = default;
            for (int Frame = 0; Frame < 600; Frame++)
            {
                pose = BasisCameraModifierSolver.Solve(stack, state, Steered(0.01f, 0f));
            }

            Vector3 right = pose.Rotation * Vector3.right;
            Assert.That(Mathf.Abs(right.y), Is.LessThan(1e-3f), "Held roll drains out while the operator steers.");
        }

        [Test]
        public void AWallClipsThePushRatherThanRememberingItBeyondTheWall()
        {
            BasisCameraModifierStack stack = StackFixture.PositionOnly(BasisCameraPositionModifier.FollowSubject);
            StackFixture.Offset(stack, new Vector3(0f, 0f, 2f));
            stack.AddEffect(BasisCameraEffectModifier.AvoidCollision);
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(new Vector3(0f, 0f, 2f), Quaternion.identity, 40f);

            for (int Frame = 0; Frame < 10; Frame++)
            {
                BasisCameraSolveContext context = Pushed(new Vector3(0f, 0f, 1f));
                context.SweepProbe = (Vector3 origin, Vector3 direction, float distance, float radius, out float free) =>
                {
                    free = Mathf.Max(0f, 2.5f - origin.z);
                    return true;
                };
                BasisCameraModifierSolver.Solve(stack, state, context);
            }

            Assert.That(state.OperatorOffset.z, Is.LessThanOrEqualTo(0.5f),
                "What the wall took off the push comes off the offset too, or letting go would leave the camera stuck at the wall while a phantom offset drained.");
        }

        [Test]
        public void ThePushRidesAnAnchorThatTurns()
        {
            BasisCameraModifierState state = StackFixture.State();
            state.Seed(Vector3.zero, Quaternion.identity, 40f);
            state.OperatorOffset = new Vector3(1f, 0f, 0f);

            state.Transport(Vector3.zero, Quaternion.identity, Vector3.zero, BasisCameraDamping.Yaw(90f));

            Assert.That(state.OperatorOffset, Is.EqualTo(new Vector3(0f, 0f, -1f))
                .Using(BasisCameraFollowModifierTests.Vec(1e-4f)));
        }
    }
}
