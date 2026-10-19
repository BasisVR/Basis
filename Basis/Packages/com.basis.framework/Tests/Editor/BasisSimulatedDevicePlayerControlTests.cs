using Unity.Scripting.LifecycleManagement;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Framework.Tests
{
    [NoAutoStaticsCleanup]
    public class BasisSimulatedDevicePlayerControlTests
    {
        private static readonly FieldInfo TrackedRoleField = typeof(BasisInput).GetField("trackedRole", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly PropertyInfo LocalPlayerInstance = typeof(BasisLocalPlayer).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        private static readonly FieldInfo DispatchFrameField = typeof(BasisActionDriver).GetField("s_RoleDispatchFrame", BindingFlags.NonPublic | BindingFlags.Static);

        private readonly List<GameObject> spawned = new List<GameObject>();
        private readonly List<(BasisActionDriver.ActionId action, BasisBoneTrackedRole role)> bound = new List<(BasisActionDriver.ActionId, BasisBoneTrackedRole)>();
        private BasisLocalPlayer savedPlayer;
        private BasisDeviceManagement savedManagement;
        private BasisLocalCharacterDriver driver;

        [SetUp]
        public void SetUp()
        {
            Assert.That(TrackedRoleField, Is.Not.Null, "BasisInput.trackedRole was renamed; this test needs updating.");
            Assert.That(LocalPlayerInstance, Is.Not.Null, "BasisLocalPlayer.Instance was renamed; this test needs updating.");
            Assert.That(DispatchFrameField, Is.Not.Null, "BasisActionDriver.s_RoleDispatchFrame was renamed; this test needs updating.");

            savedPlayer = BasisLocalPlayer.Instance;
            savedManagement = BasisDeviceManagement.Instance;

            BasisLocalPlayer player = (BasisLocalPlayer)FormatterServices.GetUninitializedObject(typeof(BasisLocalPlayer));
            driver = new BasisLocalCharacterDriver();
            player.LocalCharacterDriver = driver;
            LocalPlayerInstance.SetValue(null, player);

            BasisDeviceManagement management = (BasisDeviceManagement)FormatterServices.GetUninitializedObject(typeof(BasisDeviceManagement));
            management.AllInputDevices = new BasisObservableList<BasisInput>();
            BasisDeviceManagement.Instance = management;

            ((IDictionary)DispatchFrameField.GetValue(null)).Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach ((BasisActionDriver.ActionId action, BasisBoneTrackedRole role) in bound)
            {
                BasisActionDriver.Unbind(action, role);
            }
            bound.Clear();

            foreach (GameObject gameObject in spawned)
            {
                Object.DestroyImmediate(gameObject);
            }
            spawned.Clear();

            ((IDictionary)DispatchFrameField.GetValue(null)).Clear();
            BasisDeviceManagement.Instance = savedManagement;
            LocalPlayerInstance.SetValue(null, savedPlayer);
        }

        private void Bind(BasisActionDriver.ActionId action, BasisBoneTrackedRole role)
        {
            IReadOnlyList<BasisActionDriver.ActionId> existing = BasisActionDriver.GetActionsForRole(role);
            for (int index = 0; index < existing.Count; index++)
            {
                if (existing[index] == action) return;
            }
            BasisActionDriver.Bind(action, role);
            bound.Add((action, role));
        }

        private BasisInputXRSimulate Spawn(BasisBoneTrackedRole role)
        {
            GameObject deviceObject = new GameObject($"Simulated {role}");
            GameObject followObject = new GameObject($"Simulated {role} move");
            spawned.Add(deviceObject);
            spawned.Add(followObject);

            BasisInputXRSimulate device = deviceObject.AddComponent<BasisInputXRSimulate>();
            device.FollowMovement = followObject.transform;
            device.Control = new BasisLocalBoneControl();
            device.hasRoleAssigned = true;
            TrackedRoleField.SetValue(device, role);
            BasisDeviceManagement.Instance.AllInputDevices.Add(device);
            return device;
        }

        [Test]
        public void SimulatedRightHandLeavesTheKeyboardJumpAlone()
        {
            Bind(BasisActionDriver.ActionId.JumpOnPrimaryButton, BasisBoneTrackedRole.RightHand);
            BasisInputXRSimulate device = Spawn(BasisBoneTrackedRole.RightHand);
            driver.IsJumpHeld = true;

            device.LateDoPollData();

            Assert.That(driver.IsJumpHeld, Is.True);
        }

        [Test]
        public void SimulatedLeftHandLeavesTheKeyboardMovementAlone()
        {
            Bind(BasisActionDriver.ActionId.SetMovementSpeedMultiplierFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand);
            Bind(BasisActionDriver.ActionId.SetMovementVectorFromPrimary2DAxis, BasisBoneTrackedRole.LeftHand);
            Bind(BasisActionDriver.ActionId.TickMovementSpeed, BasisBoneTrackedRole.LeftHand);
            BasisInputXRSimulate device = Spawn(BasisBoneTrackedRole.LeftHand);
            driver.SetMovementVector(Vector2.up);
            driver.SetMovementSpeedMultiplier(1f);

            device.LateDoPollData();

            Assert.That(driver.MovementVector, Is.EqualTo(Vector2.up));
            Assert.That(driver.MovementSpeedScale, Is.EqualTo(1f));
        }

        [Test]
        public void TheSameDeviceClearsJumpWhenAskedToDrivePlayerControl()
        {
            Bind(BasisActionDriver.ActionId.JumpOnPrimaryButton, BasisBoneTrackedRole.RightHand);
            BasisInputXRSimulate device = Spawn(BasisBoneTrackedRole.RightHand);
            driver.IsJumpHeld = true;

            device.UpdateInputEvents(HasPlayerControlSupport: true, hasPlayerRaycastSupport: false);

            Assert.That(driver.IsJumpHeld, Is.False);
        }
    }
}
