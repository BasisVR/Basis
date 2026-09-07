using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using NUnit.Framework;
using Basis.Cinematics;
using UnityEngine;
using CameraPinSpace = BasisHandHeldCameraInteractable.CameraPinSpace;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Pins the camera's mode state machine and, more importantly, the transitions between modes.
    /// Auto-follow and fly both drive <see cref="CameraPinSpace.WorldSpace"/>, so turning one off
    /// can silently strand or clobber the other — that coupling is what these assert.
    ///
    /// Awake never runs here: outside play mode Unity does not invoke it for a plain
    /// MonoBehaviour, so AddComponent yields a camera with field initializers applied and no
    /// scene dependencies. Every method under test null-guards its scene state for that reason.
    /// </summary>
    public class BasisHandHeldCameraModeTests
    {
        private GameObject _go;
        private BasisHandHeldCamera _camera;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("HandHeldCameraUnderTest");
            _camera = _go.AddComponent<BasisHandHeldCamera>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [Test]
        public void Defaults_StartHandHeldAndNotFollowing()
        {
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.HandHeld));
            Assert.That(_camera.Modifiers.DrivesPosition, Is.False);
            Assert.That(_camera.Modifiers.DrivesPosition, Is.False);
        }

        [Test]
        public void FollowPlayspace_DefaultsOn()
        {
            // Maintainer asked for centre-of-mass following to be the out-of-the-box behaviour.
            Assert.That(_camera.subjectSettings.anchorToBody, Is.True);
        }

        [Test]
        public void EnableAutoFollow_TakesWorldSpace()
        {
            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

            Assert.That(_camera.Modifiers.DrivesPosition, Is.True);
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace),
                "Auto-follow drives the camera through the fly pin, so it must claim WorldSpace.");
        }

        [Test]
        public void DisableAutoFollow_ReturnsToHandHeld()
        {
            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);
            _camera.SetPositionModifier(BasisCameraPositionModifier.FreeFly);

            Assert.That(_camera.Modifiers.DrivesPosition, Is.False);
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.HandHeld),
                "Turning follow off must hand the camera back to the player's hand.");
        }

        [Test]
        public void AutoFollowRoundTrip_RestoresStartingState()
        {
            CameraPinSpace before = _camera.PinSpace;

            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);
            _camera.SetPositionModifier(BasisCameraPositionModifier.FreeFly);

            Assert.That(_camera.PinSpace, Is.EqualTo(before));
            Assert.That(_camera.Modifiers.DrivesPosition, Is.False);
        }

        [Test]
        public void DisableAutoFollow_DoesNotClobberAnUnrelatedPinSpace()
        {
            // Follow only owns WorldSpace. If the camera has since been pinned to the playspace,
            // switching follow off must leave that alone rather than yanking it back to the hand.
            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);
            _camera.PinSpace = CameraPinSpace.PlaySpace;

            _camera.SetPositionModifier(BasisCameraPositionModifier.FreeFly);

            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.PlaySpace));
            Assert.That(_camera.Modifiers.DrivesPosition, Is.False);
        }

        [Test]
        public void EnableAutoFollow_IsIdempotent()
        {
            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);
            _camera.PinSpace = CameraPinSpace.WorldSpace;

            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace));
            Assert.That(_camera.Modifiers.DrivesPosition, Is.True);
        }

        [Test]
        public void DisableAutoFollow_WhenAlreadyOff_LeavesPinSpaceUntouched()
        {
            _camera.PinSpace = CameraPinSpace.PlaySpace;

            _camera.SetPositionModifier(BasisCameraPositionModifier.FreeFly);

            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.PlaySpace),
                "A no-op disable must not run the WorldSpace hand-back path.");
        }

        [Test]
        public void EnableAutoFollow_FromEveryStartingPinSpace_DetachesWithoutMovingAnAnchor()
        {
            // Fitting a modifier has to take the camera out of the hand, because something else is
            // now steering it. It must not pick the anchor: a camera the user bolted to a vehicle
            // stays bolted to it, and only a camera that was in their hand needs somewhere to go.
            foreach (CameraPinSpace start in System.Enum.GetValues(typeof(CameraPinSpace)))
            {
                _camera.SetPositionModifier(BasisCameraPositionModifier.FreeFly);
                _camera.PinSpace = start;

                _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

                CameraPinSpace expected = start == CameraPinSpace.HandHeld ? CameraPinSpace.WorldSpace : start;
                Assert.That(_camera.PinSpace, Is.EqualTo(expected),
                    $"Enabling follow from {start} must leave the camera detached on {expected}.");
                Assert.That(_camera.PinSpace, Is.Not.EqualTo(CameraPinSpace.HandHeld));
                Assert.That(_camera.Modifiers.DrivesPosition, Is.True);
            }
        }

        [Test]
        public void RepeatedSwapping_ConvergesRatherThanDrifting()
        {
            // Guards against a transition that only works the first time — the state machine has
            // to survive a user flipping the toggle repeatedly.
            for (int Index = 0; Index < 10; Index++)
            {
                _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);
                Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace));

                _camera.SetPositionModifier(BasisCameraPositionModifier.FreeFly);
                Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.HandHeld));
            }

            Assert.That(_camera.Modifiers.DrivesPosition, Is.False);
        }

        [Test]
        public void PlayspaceToggle_IsIndependentOfFollowState()
        {
            // The anchor choice must survive follow being switched on and off, or the user's
            // preference silently resets every time they stop following.
            _camera.subjectSettings.anchorToBody = false;

            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);
            Assert.That(_camera.subjectSettings.anchorToBody, Is.False);

            _camera.SetPositionModifier(BasisCameraPositionModifier.FreeFly);
            Assert.That(_camera.subjectSettings.anchorToBody, Is.False);
        }

        // ---- Fly mode ---------------------------------------------------------------------
        // Edit mode reports no boot mode, so IsUserInDesktop() is false and these run the VR path
        // — the one that has to keep working after the prop has been let go.

        [Test]
        public void FlyMode_DefaultsOff()
        {
            Assert.That(_camera.IsFlyModeEnabled, Is.False);
            Assert.That(_camera.IsFlying, Is.False);
        }

        [Test]
        public void EnableFly_TakesWorldSpace()
        {
            _camera.SetFlyModeEnabled(true);

            Assert.That(_camera.IsFlyModeEnabled, Is.True);
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace),
                "Flying means the camera is out in the world, not on the end of your arm.");
        }

        [Test]
        public void DisableFly_ReturnsToHandHeld()
        {
            _camera.SetFlyModeEnabled(true);
            _camera.SetFlyModeEnabled(false);

            Assert.That(_camera.IsFlyModeEnabled, Is.False);
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.HandHeld));
        }

        [Test]
        public void EnableFly_LeavesAFittedModifierAlone()
        {
            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

            _camera.SetFlyModeEnabled(true);

            Assert.That(_camera.IsFlyModeEnabled, Is.True);
            Assert.That(_camera.Modifiers.DrivesPosition, Is.True,
                "Flight is an override on top of the stack, not a state that unfits it.");
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace));
        }

        [Test]
        public void FittingAModifier_LeavesFlightArmed()
        {
            _camera.SetFlyModeEnabled(true);

            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

            Assert.That(_camera.IsFlyModeEnabled, Is.True,
                "The sticks keep steering over the modifier until flight is switched off.");
            Assert.That(_camera.Modifiers.DrivesPosition, Is.True);
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace));
        }

        [Test]
        public void DisableFly_WithAModifierFitted_StaysInTheWorld()
        {
            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);
            _camera.SetFlyModeEnabled(true);

            _camera.SetFlyModeEnabled(false);

            Assert.That(_camera.IsFlyModeEnabled, Is.False);
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace),
                "The modifier still has the camera; only the sticks were handed back.");
        }

        [Test]
        public void EnableFly_IsIdempotent()
        {
            _camera.SetFlyModeEnabled(true);
            _camera.SetFlyModeEnabled(true);

            Assert.That(_camera.IsFlyModeEnabled, Is.True);
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace));
        }

        [Test]
        public void DisableFly_WhenAlreadyOff_LeavesPinSpaceUntouched()
        {
            _camera.PinSpace = CameraPinSpace.PlaySpace;

            _camera.SetFlyModeEnabled(false);

            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.PlaySpace),
                "A no-op disable must not run the hand-back path.");
        }

        [Test]
        public void RepeatedFlyToggling_ConvergesRatherThanDrifting()
        {
            // Every entry takes the player's look/move/crouch locks and every exit gives them back,
            // so a transition that only balances the first time leaves the player unable to move.
            for (int Index = 0; Index < 10; Index++)
            {
                _camera.SetFlyModeEnabled(true);
                Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace));

                _camera.SetFlyModeEnabled(false);
                Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.HandHeld));
            }

            Assert.That(_camera.IsFlyModeEnabled, Is.False);
        }

        [Test]
        public void MiddleClick_IsReservedForFlyRatherThanPickupDragRotate()
        {
            // BasisPickupInteractable drag-rotates the held prop on the same Secondary2DAxisClick
            // that toggles fly here, and both poll every frame. Without the reservation the camera
            // spins under the mouse on the way into flight.
            GameObject probeGo = new GameObject("MiddleClickProbe");
            try
            {
                MiddleClickProbe probe = probeGo.AddComponent<MiddleClickProbe>();
                Assert.That(probe.MiddleClickReserved, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(probeGo);
            }
        }

        /// <summary>Surfaces the protected reservation flag so the test above can read it.</summary>
        private sealed class MiddleClickProbe : BasisHandHeldCamera
        {
            public bool MiddleClickReserved => DesktopMiddleClickReserved;
        }

        private static readonly string[] PersistedFlySettings =
        {
            "flySpeed", "flyClimbSpeed", "flyFastMultiplier", "flyTurnSpeed", "flyMouseSensitivity",
            "flyMomentum", "flyMovementFollowsPitch", "showFlyOnMainMenu",
            "vrLeftHandFlyEnabled", "vrRightHandFlyRotateEnabled",
            "vrHandFlyMoveDeadzone", "vrHandFlyMoveReach", "vrHandFlyMoveSensitivity",
            "vrHandFlyTurnDeadzone", "vrHandFlyTurnReach", "vrHandFlyTurnSensitivity",
        };

        [Test]
        public void FlyMode_IsNotPersisted()
        {
            // Deliberate, and the same call auto-follow makes: a settings file that remembered
            // flight would have the camera fly out of your hand the moment it spawned.
            Assert.That(
                typeof(BasisHandHeldCameraUI.CameraSettings).GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
                Has.None.Matches<System.Reflection.FieldInfo>(f => f.Name.ToLowerInvariant().Contains("fly") && System.Array.IndexOf(PersistedFlySettings, f.Name) < 0),
                "Fly mode is per-session state, not a saved setting; the speeds it flies and turns at, whether it glides to a stop, and whether the hotbar carries a switch for it, are.");
        }

        [Test]
        public void CountdownRemaining_StartsIdle()
        {
            Assert.That(_camera.CountdownRemaining, Is.Zero);
            Assert.That(_camera.IsCountingDown, Is.False);
        }

        [Test]
        public void FollowTarget_DefaultsToLocalPlayer()
        {
            Assert.That(_camera.followTargetBound, Is.False);
            Assert.That(_camera.IsFollowingRemotePlayer, Is.False);
            Assert.That(_camera.TryGetFollowTargetPlayer(out _), Is.False);
        }

        [Test]
        public void FollowTargetZero_IsARemote_NotTheLocalPlayer()
        {
            // LiteNetLib allocates peer ids from zero up, so the first player to join an instance
            // is net id 0. While 0 doubled as the "follow me" sentinel they were the one person in
            // the room the camera would not follow: the selection resolved straight back to local.
            _camera.SetFollowTargetPlayer(0);

            Assert.That(_camera.IsFollowingRemotePlayer, Is.True);
            Assert.That(_camera.TryGetFollowTargetPlayer(out ushort bound), Is.True);
            Assert.That(bound, Is.Zero);
        }

        [Test]
        public void ClearFollowTargetPlayer_UnbindsEvenFromNetIdZero()
        {
            _camera.SetFollowTargetPlayer(0);

            _camera.ClearFollowTargetPlayer();

            Assert.That(_camera.IsFollowingRemotePlayer, Is.False);
            Assert.That(_camera.TryGetFollowTargetPlayer(out _), Is.False);
        }

        [Test]
        public void FollowingTheFirstPlayerToJoin_ResolvesToThemNotTheLocalPlayer()
        {
            const ushort netId = 0;
            GameObject root = new GameObject("FirstJoinerRootUnderTest");
            GameObject head = new GameObject("FirstJoinerHeadUnderTest");
            root.transform.position = new Vector3(-6f, 0f, 3f);
            head.transform.position = new Vector3(-6f, 1.6f, 3f);

            BasisNetworkPlayers.RemotePlayers[netId] = new BasisRemotePlayer
            {
                AvatarAnimatorTransform = root.transform,
                MouthTransform = head.transform,
            };

            try
            {
                _camera.SetFollowTargetPlayer(netId);
                _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

                Assert.That(_camera.TryGetFollowFocusPoint(out Vector3 point), Is.True);
                Assert.That(_camera.TryGetFollowTargetPlayer(out ushort bound), Is.True,
                    "A connected remote must survive the resolve, net id 0 included.");
                Assert.That(bound, Is.EqualTo(netId));
                Assert.That(point.x, Is.EqualTo(-6f).Within(0.001f));
                Assert.That(point.y, Is.EqualTo(1.6f).Within(0.001f));
                Assert.That(point.z, Is.EqualTo(3f).Within(0.001f),
                    "Net id 0 is a real remote; the aim point has to be theirs, not the local player's.");
            }
            finally
            {
                BasisNetworkPlayers.RemotePlayers.TryRemove(netId, out _);
                Object.DestroyImmediate(head);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SetFollowTargetPlayer_MarksRemote()
        {
            _camera.SetFollowTargetPlayer(42);

            Assert.That(_camera.IsFollowingRemotePlayer, Is.True);
            Assert.That(_camera.TryGetFollowTargetPlayer(out ushort bound), Is.True);
            Assert.That(bound, Is.EqualTo(42));
        }

        [Test]
        public void FollowingAMissingRemote_FallsBackToLocalOnResolve()
        {
            // No such player is connected in a unit test, so resolving must drop the dangling id
            // back to the local player rather than leaving the camera pointed at nobody.
            _camera.SetFollowTargetPlayer(9999);
            _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

            _camera.TryGetFollowFocusPoint(out _);

            Assert.That(_camera.TryGetFollowTargetPlayer(out _), Is.False,
                "An unresolvable remote target must unbind back to the local player on the next resolve.");
        }

        [Test]
        public void FollowingAConnectedRemote_ResolvesToThatRemote()
        {
            const ushort netId = 7;
            GameObject root = new GameObject("RemoteRootUnderTest");
            GameObject head = new GameObject("RemoteHeadUnderTest");
            root.transform.position = new Vector3(12f, 0f, -4f);
            head.transform.position = new Vector3(12f, 1.7f, -4f);

            BasisNetworkPlayers.RemotePlayers[netId] = new BasisRemotePlayer
            {
                AvatarAnimatorTransform = root.transform,
                MouthTransform = head.transform,
            };

            try
            {
                _camera.SetFollowTargetPlayer(netId);
                _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

                Assert.That(_camera.TryGetFollowFocusPoint(out Vector3 point), Is.True);
                Assert.That(_camera.TryGetFollowTargetPlayer(out ushort bound), Is.True,
                    "A connected remote must survive the resolve. PlayerSelf is only ever assigned " +
                    "for the local player, so gating the remote path on it reset the target every frame.");
                Assert.That(bound, Is.EqualTo(netId));
                Assert.That(point.x, Is.EqualTo(12f).Within(0.001f));
                Assert.That(point.y, Is.EqualTo(1.7f).Within(0.001f), "Aim point is the remote's head.");
                Assert.That(point.z, Is.EqualTo(-4f).Within(0.001f));
            }
            finally
            {
                BasisNetworkPlayers.RemotePlayers.TryRemove(netId, out _);
                Object.DestroyImmediate(head);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void FollowingARemoteWithNoAvatarYet_KeepsTheTarget()
        {
            const ushort netId = 8;
            BasisNetworkPlayers.RemotePlayers[netId] = new BasisRemotePlayer();

            try
            {
                _camera.SetFollowTargetPlayer(netId);
                _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

                _camera.TryGetFollowFocusPoint(out _);

                Assert.That(_camera.TryGetFollowTargetPlayer(out ushort bound), Is.True,
                    "A remote between avatars has nothing to read yet but is still connected, so " +
                    "the selection must survive the gap rather than snapping back to the local player.");
                Assert.That(bound, Is.EqualTo(netId));
            }
            finally
            {
                BasisNetworkPlayers.RemotePlayers.TryRemove(netId, out _);
            }
        }

        [Test]
        public void AutoFocusFollowSubject_DefaultsOff()
        {
            Assert.That(_camera.autoFocusFollowSubject, Is.False);
        }

        [Test]
        public void FollowingARemoteWithAnUndrivenMouth_HoldsTheTargetInsteadOfFilmingTheOrigin()
        {
            // The mouth marker is built at the world origin when a player joins and only starts
            // moving once the bone job system has them registered against a loaded avatar. Reading
            // it before that flew the camera to 0,0,0 and parked it there, which is a follow that
            // "does nothing" from the user's seat. Nothing is registered in an edit-mode test, so
            // a mouth with no avatar root is exactly that state.
            const ushort netId = 11;
            GameObject mouth = new GameObject("RemoteMouthAtOriginUnderTest");

            BasisRemotePlayer remote = new BasisRemotePlayer { MouthTransform = mouth.transform };
            BasisNetworkPlayers.RemotePlayers[netId] = remote;

            try
            {
                _camera.SetFollowTargetPlayer(netId);
                _camera.SetPositionModifier(BasisCameraPositionModifier.FollowSubject);

                Assert.That(_camera.TryResolveRemoteSubjectForTest(netId, remote, out _, out _, out _),
                    Is.False, "An undriven mouth marker is not a position; the frame must be skipped.");

                _camera.TryGetFollowFocusPoint(out _);
                Assert.That(_camera.TryGetFollowTargetPlayer(out ushort bound), Is.True,
                    "They are still connected, so the selection has to survive the gap.");
                Assert.That(bound, Is.EqualTo(netId));
            }
            finally
            {
                BasisNetworkPlayers.RemotePlayers.TryRemove(netId, out _);
                Object.DestroyImmediate(mouth);
            }
        }

        [Test]
        public void RemoteSubjectYaw_ComesFromTheBodyNotTheHead()
        {
            // The mouth marker's rotation is head facing. Framing off it swings the camera around
            // the subject on every glance, so the avatar root has to win whenever it exists.
            const ushort netId = 12;
            GameObject root = new GameObject("RemoteRootYawUnderTest");
            GameObject mouth = new GameObject("RemoteMouthYawUnderTest");
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 90f, 0f));
            mouth.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, 0f), Quaternion.Euler(0f, -140f, 0f));

            BasisRemotePlayer remote = new BasisRemotePlayer
            {
                AvatarAnimatorTransform = root.transform,
                MouthTransform = mouth.transform,
            };

            try
            {
                Assert.That(_camera.TryResolveRemoteSubjectForTest(netId, remote, out Quaternion yaw, out _, out _),
                    Is.True);
                Assert.That(Quaternion.Angle(yaw, Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(0.5f),
                    "Body yaw, not the head's.");
            }
            finally
            {
                Object.DestroyImmediate(mouth);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RemoteSubjectYaw_ReadsTheirHipsThroughTheRigsRestFrame()
        {
            // The root pose the bone jobs write is backed out of the hips' PARENT, so it never turns
            // when somebody turns around in their own playspace; only their hips do. The rig's hips
            // rest frame is what turns that hips rotation back into which way the avatar faces.
            const ushort netId = 14;
            GameObject root = new GameObject("RemoteRootUnderTest");
            GameObject hips = new GameObject("RemoteHipsUnderTest");
            GameObject mouth = new GameObject("RemoteMouthUnderTest");
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 90f, 0f));
            hips.transform.SetParent(root.transform, false);
            hips.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            mouth.transform.position = new Vector3(0f, 1.6f, 0f);

            BasisRemotePlayer remote = new BasisRemotePlayer
            {
                AvatarAnimatorTransform = root.transform,
                MouthTransform = mouth.transform,
            };
            remote.RemoteAvatarDriver.References.Hips = hips.transform;
            remote.RemoteAvatarDriver.HipsToCharacterBasis = Quaternion.Euler(0f, 180f, 0f);
            remote.RemoteAvatarDriver.DerivedRootToCharacterBasis = Quaternion.Euler(0f, 180f, 0f);

            try
            {
                Assert.That(_camera.TryResolveRemoteSubjectForTest(netId, remote, out Quaternion yaw, out _, out _),
                    Is.True);
                Assert.That(Quaternion.Angle(yaw, Quaternion.Euler(0f, 225f, 0f)), Is.LessThan(0.5f),
                    "The shot is framed against the way the body faces, not the way its root does.");
            }
            finally
            {
                Object.DestroyImmediate(mouth);
                Object.DestroyImmediate(hips);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RemoteSubjectYaw_FallsBackToTheRootAndItsRigConstantWithoutHips()
        {
            // Before the hips are registered the root is all there is. It carries whatever the
            // exporter baked above the skeleton — a model authored facing -Z puts a straight 180
            // degrees in it — and that constant is divided back out.
            const ushort netId = 15;
            GameObject root = new GameObject("RemoteRootFlippedRigUnderTest");
            GameObject mouth = new GameObject("RemoteMouthFlippedRigUnderTest");
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 90f, 0f));
            mouth.transform.position = new Vector3(0f, 1.6f, 0f);

            BasisRemotePlayer remote = new BasisRemotePlayer
            {
                AvatarAnimatorTransform = root.transform,
                MouthTransform = mouth.transform,
            };
            remote.RemoteAvatarDriver.DerivedRootToCharacterBasis = Quaternion.Euler(0f, 180f, 0f);

            try
            {
                Assert.That(_camera.TryResolveRemoteSubjectForTest(netId, remote, out Quaternion yaw, out _, out _),
                    Is.True);
                Assert.That(Quaternion.Angle(yaw, Quaternion.Euler(0f, 270f, 0f)), Is.LessThan(0.5f),
                    "The shot is framed against the way the avatar faces, not the way its root does.");
            }
            finally
            {
                Object.DestroyImmediate(mouth);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RemoteSubjectScale_TracksTheirAvatarHeight()
        {
            // Remote scale used to be hardcoded to 1, so the authored offsets — and the teleport
            // threshold that decides snap versus ease — were sized for a default avatar no matter
            // who was being filmed. Root-to-head is the same measurement the local path takes.
            const ushort netId = 13;
            GameObject root = new GameObject("RemoteRootScaleUnderTest");
            GameObject mouth = new GameObject("RemoteMouthScaleUnderTest");
            root.transform.position = Vector3.zero;
            mouth.transform.position = new Vector3(0f, 3f, 0f);

            BasisRemotePlayer remote = new BasisRemotePlayer
            {
                AvatarAnimatorTransform = root.transform,
                MouthTransform = mouth.transform,
            };

            try
            {
                Assert.That(_camera.TryResolveRemoteSubjectForTest(netId, remote, out _, out float scale, out _),
                    Is.True);
                Assert.That(scale, Is.EqualTo(2f).Within(0.01f),
                    "A head twice the nominal 1.5m up frames at twice the distance.");
            }
            finally
            {
                Object.DestroyImmediate(mouth);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void FlattenToYaw_HoldsItsHeadingAcrossVertical()
        {
            // eulerAngles.y — and a plain flattened forward axis — both jump 180 degrees the
            // instant the source tips past vertical, which a head does whenever someone looks
            // near-straight up or down. That threw the camera to the far side of its subject.
            Quaternion justBefore = BasisHandHeldCameraInteractable.FlattenToYawForTest(Quaternion.Euler(89f, 0f, 0f));
            Quaternion justAfter = BasisHandHeldCameraInteractable.FlattenToYawForTest(Quaternion.Euler(91f, 0f, 0f));

            Assert.That(Quaternion.Angle(justBefore, justAfter), Is.LessThan(5f),
                "Two degrees of pitch either side of vertical must not swing the heading.");
            Assert.That(Quaternion.Angle(justBefore, Quaternion.identity), Is.LessThan(5f),
                "Pitching a forward-facing subject down does not change which way they face.");
        }

        [Test]
        public void FlattenToYaw_MatchesEulerWhereThatIsWellDefined()
        {
            // The pole handling must not cost anything in the ordinary upright case.
            foreach (float heading in new[] { 0f, 37f, 90f, 179f, 250f })
            {
                Quaternion flattened = BasisHandHeldCameraInteractable.FlattenToYawForTest(
                    Quaternion.Euler(20f, heading, 15f));

                Assert.That(Quaternion.Angle(flattened, Quaternion.Euler(0f, heading, 0f)), Is.LessThan(0.5f),
                    $"Upright heading {heading} must survive flattening unchanged.");
            }
        }
    }
}
