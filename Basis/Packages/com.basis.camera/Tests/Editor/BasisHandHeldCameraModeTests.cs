using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using NUnit.Framework;
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
            Assert.That(_camera.IsAutoFollowing, Is.False);
            Assert.That(_camera.autoFollowEnabled, Is.False);
        }

        [Test]
        public void FollowPlayspace_DefaultsOn()
        {
            // Maintainer asked for centre-of-mass following to be the out-of-the-box behaviour.
            Assert.That(_camera.autoFollowPlayspace, Is.True);
        }

        [Test]
        public void EnableAutoFollow_TakesWorldSpace()
        {
            _camera.SetAutoFollowEnabled(true);

            Assert.That(_camera.IsAutoFollowing, Is.True);
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace),
                "Auto-follow drives the camera through the fly pin, so it must claim WorldSpace.");
        }

        [Test]
        public void DisableAutoFollow_ReturnsToHandHeld()
        {
            _camera.SetAutoFollowEnabled(true);
            _camera.SetAutoFollowEnabled(false);

            Assert.That(_camera.IsAutoFollowing, Is.False);
            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.HandHeld),
                "Turning follow off must hand the camera back to the player's hand.");
        }

        [Test]
        public void AutoFollowRoundTrip_RestoresStartingState()
        {
            CameraPinSpace before = _camera.PinSpace;

            _camera.SetAutoFollowEnabled(true);
            _camera.SetAutoFollowEnabled(false);

            Assert.That(_camera.PinSpace, Is.EqualTo(before));
            Assert.That(_camera.IsAutoFollowing, Is.False);
        }

        [Test]
        public void DisableAutoFollow_DoesNotClobberAnUnrelatedPinSpace()
        {
            // Follow only owns WorldSpace. If the camera has since been pinned to the playspace,
            // switching follow off must leave that alone rather than yanking it back to the hand.
            _camera.SetAutoFollowEnabled(true);
            _camera.PinSpace = CameraPinSpace.PlaySpace;

            _camera.SetAutoFollowEnabled(false);

            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.PlaySpace));
            Assert.That(_camera.IsAutoFollowing, Is.False);
        }

        [Test]
        public void EnableAutoFollow_IsIdempotent()
        {
            _camera.SetAutoFollowEnabled(true);
            _camera.PinSpace = CameraPinSpace.WorldSpace;

            _camera.SetAutoFollowEnabled(true);

            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace));
            Assert.That(_camera.IsAutoFollowing, Is.True);
        }

        [Test]
        public void DisableAutoFollow_WhenAlreadyOff_LeavesPinSpaceUntouched()
        {
            _camera.PinSpace = CameraPinSpace.PlaySpace;

            _camera.SetAutoFollowEnabled(false);

            Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.PlaySpace),
                "A no-op disable must not run the WorldSpace hand-back path.");
        }

        [Test]
        public void EnableAutoFollow_FromEveryStartingPinSpace_EndsInWorldSpace()
        {
            foreach (CameraPinSpace start in System.Enum.GetValues(typeof(CameraPinSpace)))
            {
                _camera.SetAutoFollowEnabled(false);
                _camera.PinSpace = start;

                _camera.SetAutoFollowEnabled(true);

                Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace),
                    $"Enabling follow from {start} must end in WorldSpace.");
                Assert.That(_camera.IsAutoFollowing, Is.True);
            }
        }

        [Test]
        public void RepeatedSwapping_ConvergesRatherThanDrifting()
        {
            // Guards against a transition that only works the first time — the state machine has
            // to survive a user flipping the toggle repeatedly.
            for (int Index = 0; Index < 10; Index++)
            {
                _camera.SetAutoFollowEnabled(true);
                Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.WorldSpace));

                _camera.SetAutoFollowEnabled(false);
                Assert.That(_camera.PinSpace, Is.EqualTo(CameraPinSpace.HandHeld));
            }

            Assert.That(_camera.IsAutoFollowing, Is.False);
        }

        [Test]
        public void PlayspaceToggle_IsIndependentOfFollowState()
        {
            // The anchor choice must survive follow being switched on and off, or the user's
            // preference silently resets every time they stop following.
            _camera.autoFollowPlayspace = false;

            _camera.SetAutoFollowEnabled(true);
            Assert.That(_camera.autoFollowPlayspace, Is.False);

            _camera.SetAutoFollowEnabled(false);
            Assert.That(_camera.autoFollowPlayspace, Is.False);
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
            Assert.That(_camera.followTargetPlayerId, Is.Zero);
            Assert.That(_camera.IsFollowingRemotePlayer, Is.False);
        }

        [Test]
        public void SetFollowTargetPlayer_MarksRemote()
        {
            _camera.SetFollowTargetPlayer(42);

            Assert.That(_camera.followTargetPlayerId, Is.EqualTo(42));
            Assert.That(_camera.IsFollowingRemotePlayer, Is.True);
        }

        [Test]
        public void FollowingAMissingRemote_FallsBackToLocalOnResolve()
        {
            // No such player is connected in a unit test, so resolving must drop the dangling id
            // back to the local player rather than leaving the camera pointed at nobody.
            _camera.SetFollowTargetPlayer(9999);
            _camera.SetAutoFollowEnabled(true);

            _camera.TryGetFollowFocusPoint(out _);

            Assert.That(_camera.followTargetPlayerId, Is.Zero,
                "An unresolvable remote target must reset to 0 (local) on the next resolve.");
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
                _camera.SetAutoFollowEnabled(true);

                Assert.That(_camera.TryGetFollowFocusPoint(out Vector3 point), Is.True);
                Assert.That(_camera.followTargetPlayerId, Is.EqualTo(netId),
                    "A connected remote must survive the resolve. PlayerSelf is only ever assigned " +
                    "for the local player, so gating the remote path on it reset the target every frame.");
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
                _camera.SetAutoFollowEnabled(true);

                _camera.TryGetFollowFocusPoint(out _);

                Assert.That(_camera.followTargetPlayerId, Is.EqualTo(netId),
                    "A remote between avatars has nothing to read yet but is still connected, so " +
                    "the selection must survive the gap rather than snapping back to the local player.");
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
    }
}
