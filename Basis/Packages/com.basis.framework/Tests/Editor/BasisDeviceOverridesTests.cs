using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Framework.Tests
{
    [NoAutoStaticsCleanup]
    public class BasisDeviceOverridesTests
    {
        private static readonly PropertyInfo LocalPlayerInstance = typeof(BasisLocalPlayer).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        private readonly List<GameObject> spawned = new List<GameObject>();
        private BasisLocalPlayer savedPlayer;
        private BasisDeviceManagement savedManagement;
        private GameObject managementObject;
        private BasisLocalBoneControl left, right, hips;
        private string storagePath;

        [SetUp]
        public void SetUp()
        {
            Assert.That(LocalPlayerInstance, Is.Not.Null, "BasisLocalPlayer.Instance was renamed; this test needs updating.");
            savedPlayer = BasisLocalPlayer.Instance;
            savedManagement = BasisDeviceManagement.Instance;
            left = new BasisLocalBoneControl();
            right = new BasisLocalBoneControl();
            hips = new BasisLocalBoneControl();
            BasisLocalBoneDriver driver = new BasisLocalBoneDriver
            {
                Controls = new[] { left, right, hips },
                trackedRoles = new[] { BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.RightHand, BasisBoneTrackedRole.Hips },
                ControlsLength = 3,
            };
            BasisLocalPlayer player = (BasisLocalPlayer)FormatterServices.GetUninitializedObject(typeof(BasisLocalPlayer));
            player.LocalBoneDriver = driver;
            LocalPlayerInstance.SetValue(null, player);
            managementObject = new GameObject("Device management");
            managementObject.SetActive(false);
            BasisDeviceManagement management = managementObject.AddComponent<BasisDeviceManagement>();
            management.AllInputDevices = new BasisObservableList<BasisInput>();
            management.BasisDeviceNameMatcher = ScriptableObject.CreateInstance<BasisDeviceNameMatcher>();
            management.BasisDeviceNameMatcher.BasisDevice.Add(new DeviceSupportInformation
            {
                DeviceID = ControllerModel,
                matchableDeviceIds = new[] { ControllerModel },
                HasTrackedRole = true,
                TrackedRole = BasisBoneTrackedRole.RightHand,
                HasRayCastSupport = false,
            });
            BasisDeviceManagement.Instance = management;
            storagePath = Path.Combine(Path.GetTempPath(), "basis-device-overrides-" + Guid.NewGuid().ToString("N") + ".json");
            BasisDeviceOverrides.SetStoragePath(storagePath);
        }

        [TearDown]
        public void TearDown()
        {
            BasisDeviceOverrides.SetStoragePath(null);
            if (File.Exists(storagePath))
            {
                File.Delete(storagePath);
            }
            foreach (GameObject gameObject in spawned)
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
            spawned.Clear();
            BasisDeviceManagement.Instance = savedManagement;
            UnityEngine.Object.DestroyImmediate(managementObject);
            LocalPlayerInstance.SetValue(null, savedPlayer);
        }

        private const string ControllerModel = "test_controller_model";

        private BasisInputXRSimulate SpawnObject(string id, string serial)
        {
            GameObject followObject = new GameObject(id + " move");
            GameObject deviceObject = new GameObject(id);
            spawned.Add(followObject);
            spawned.Add(deviceObject);
            BasisInputXRSimulate device = deviceObject.AddComponent<BasisInputXRSimulate>();
            device.FollowMovement = followObject.transform;
            device.DeviceSerial = serial;
            return device;
        }

        private void Disconnect(BasisInputXRSimulate device)
        {
            device.StopTracking();
            BasisDeviceManagement.Instance.AllInputDevices.Remove(device);
            GameObject followObject = device.FollowMovement.gameObject;
            GameObject deviceObject = device.gameObject;
            spawned.Remove(followObject);
            spawned.Remove(deviceObject);
            UnityEngine.Object.DestroyImmediate(followObject);
            UnityEngine.Object.DestroyImmediate(deviceObject);
        }

        private BasisInputXRSimulate Spawn(string id, string subsystem, bool hasNaturalRole, BasisBoneTrackedRole naturalRole, string serial = "")
        {
            BasisInputXRSimulate device = SpawnObject(id, serial);
            device.UniqueDeviceIdentifier = id;
            device.SubSystemIdentifier = subsystem;
            device.HasNaturalRole = hasNaturalRole;
            device.NaturalRole = naturalRole;
            device.OverrideKey = BasisDeviceOverrides.KeyFor(device);
            device.IgnoredParts = BasisDeviceOverrides.GetIgnore(device.OverrideKey);
            BasisDeviceManagement.Instance.AllInputDevices.Add(device);
            device.ResolveRole();
            return device;
        }

        private static bool Holds(BasisInput device, BasisBoneTrackedRole role)
        {
            return device.TryGetRole(out BasisBoneTrackedRole held) && held == role;
        }

        [Test]
        public void KeyFor_UsesTheSerialWhenTheBackendReportsOne()
        {
            BasisInputXRSimulate withSerial = Spawn("7|model", "Test", false, BasisBoneTrackedRole.CenterEye, "LHR-42");
            BasisInputXRSimulate withoutSerial = Spawn("8|model", "Test", false, BasisBoneTrackedRole.CenterEye);

            Assert.That(withSerial.OverrideKey, Is.EqualTo("Test|LHR-42"));
            Assert.That(withoutSerial.OverrideKey, Is.EqualTo("Test|8|model"));
        }

        [Test]
        public void HandOverride_ReplacesTheNaturalRole_AndClearingRestoresIt()
        {
            BasisInputXRSimulate device = Spawn("ctrl", "Test", true, BasisBoneTrackedRole.RightHand);
            Assert.That(Holds(device, BasisBoneTrackedRole.RightHand), Is.True);

            BasisDeviceOverrides.SetHand(device.OverrideKey, BasisBoneTrackedRole.LeftHand);

            Assert.That(Holds(device, BasisBoneTrackedRole.LeftHand), Is.True);
            Assert.That(left.DevicesWithRoles, Is.EqualTo(new[] { "ctrl" }));
            Assert.That(left.HasTracked, Is.EqualTo(BasisHasTracked.HasTracker));
            Assert.That(right.DevicesWithRoles, Is.Empty);
            Assert.That(right.HasTracked, Is.EqualTo(BasisHasTracked.HasNoTracker));

            BasisDeviceOverrides.ClearHand(device.OverrideKey);

            Assert.That(Holds(device, BasisBoneTrackedRole.RightHand), Is.True);
            Assert.That(left.DevicesWithRoles, Is.Empty);
            Assert.That(right.DevicesWithRoles, Is.EqualTo(new[] { "ctrl" }));
        }

        [Test]
        public void AStoredOverride_AppliesWhenTheDeviceAppears()
        {
            BasisDeviceOverrides.SetHand("Test|LHR-1", BasisBoneTrackedRole.LeftHand);

            BasisInputXRSimulate device = Spawn("3|controller", "Test", true, BasisBoneTrackedRole.RightHand, "LHR-1");

            Assert.That(device.OverrideKey, Is.EqualTo("Test|LHR-1"));
            Assert.That(Holds(device, BasisBoneTrackedRole.LeftHand), Is.True);
            Assert.That(left.DevicesWithRoles, Is.EqualTo(new[] { "3|controller" }));
        }

        [Test]
        public void ANaturalClaimant_BacksOffFromAForcedHolder()
        {
            BasisInputXRSimulate forced = Spawn("a", "Test", true, BasisBoneTrackedRole.RightHand);
            BasisDeviceOverrides.SetHand(forced.OverrideKey, BasisBoneTrackedRole.LeftHand);

            BasisInputXRSimulate natural = Spawn("b", "Test", true, BasisBoneTrackedRole.LeftHand);

            Assert.That(Holds(forced, BasisBoneTrackedRole.LeftHand), Is.True);
            Assert.That(natural.TryGetRole(out _), Is.False);
            Assert.That(left.DevicesWithRoles, Is.EqualTo(new[] { "a" }));
        }

        [Test]
        public void AForcedClaimant_ReclaimsFromANaturalHolder()
        {
            BasisInputXRSimulate natural = Spawn("b", "Test", true, BasisBoneTrackedRole.LeftHand);
            BasisInputXRSimulate forced = Spawn("a", "Test", true, BasisBoneTrackedRole.RightHand);

            BasisDeviceOverrides.SetHand(forced.OverrideKey, BasisBoneTrackedRole.LeftHand);

            Assert.That(Holds(forced, BasisBoneTrackedRole.LeftHand), Is.True);
            Assert.That(natural.TryGetRole(out _), Is.False);
            Assert.That(left.DevicesWithRoles, Is.EqualTo(new[] { "a" }));
        }

        [Test]
        public void SwappingBothHands_LeavesEachDeviceOnTheOtherHand()
        {
            BasisInputXRSimulate physicalLeft = Spawn("l", "Test", true, BasisBoneTrackedRole.LeftHand);
            BasisInputXRSimulate physicalRight = Spawn("r", "Test", true, BasisBoneTrackedRole.RightHand);

            BasisDeviceOverrides.SetHand(physicalLeft.OverrideKey, BasisBoneTrackedRole.RightHand);
            BasisDeviceOverrides.SetHand(physicalRight.OverrideKey, BasisBoneTrackedRole.LeftHand);

            Assert.That(Holds(physicalLeft, BasisBoneTrackedRole.RightHand), Is.True);
            Assert.That(Holds(physicalRight, BasisBoneTrackedRole.LeftHand), Is.True);
            Assert.That(left.DevicesWithRoles, Is.EqualTo(new[] { "r" }));
            Assert.That(right.DevicesWithRoles, Is.EqualTo(new[] { "l" }));
        }

        [Test]
        public void IgnoringADevice_ReleasesItsRoleAndRefusesNewOnes()
        {
            BasisInputXRSimulate device = Spawn("ctrl", "Test", true, BasisBoneTrackedRole.LeftHand);

            BasisDeviceOverrides.SetIgnore(device.OverrideKey, BasisDeviceIgnore.Device);

            Assert.That(device.TryGetRole(out _), Is.False);
            Assert.That(device.HasControl, Is.False);
            Assert.That(left.DevicesWithRoles, Is.Empty);
            Assert.That(left.HasTracked, Is.EqualTo(BasisHasTracked.HasNoTracker));

            device.AssignRoleAndTracker(BasisBoneTrackedRole.LeftHand);
            Assert.That(device.TryGetRole(out _), Is.False);

            BasisDeviceOverrides.SetIgnore(device.OverrideKey, BasisDeviceIgnore.None);

            Assert.That(Holds(device, BasisBoneTrackedRole.LeftHand), Is.True);
            Assert.That(left.DevicesWithRoles, Is.EqualTo(new[] { "ctrl" }));
            Assert.That(left.HasTracked, Is.EqualTo(BasisHasTracked.HasTracker));
        }

        [Test]
        public void IgnoringThePose_KeepsTheRoleForInputButStopsDrivingTheBone()
        {
            BasisInputXRSimulate device = Spawn("ctrl", "Test", true, BasisBoneTrackedRole.LeftHand);

            BasisDeviceOverrides.SetIgnore(device.OverrideKey, BasisDeviceIgnore.Pose);

            Assert.That(Holds(device, BasisBoneTrackedRole.LeftHand), Is.True);
            Assert.That(device.IsPoseDriving, Is.False);
            Assert.That(left.DevicesWithRoles, Is.Empty);
            Assert.That(left.HasTracked, Is.EqualTo(BasisHasTracked.HasNoTracker));

            BasisDeviceOverrides.SetIgnore(device.OverrideKey, BasisDeviceIgnore.None);

            Assert.That(Holds(device, BasisBoneTrackedRole.LeftHand), Is.True);
            Assert.That(device.IsPoseDriving, Is.True);
            Assert.That(left.HasTracked, Is.EqualTo(BasisHasTracked.HasTracker));
        }

        [Test]
        public void IgnoringButtons_ClearsButtonsButKeepsSticks()
        {
            BasisInputXRSimulate device = Spawn("ctrl", "Test", true, BasisBoneTrackedRole.LeftHand);
            BasisDeviceOverrides.SetIgnore(device.OverrideKey, BasisDeviceIgnore.Buttons);
            device.CurrentInputState.Trigger = 1f;
            device.CurrentInputState.GripButton = true;
            device.CurrentInputState.PrimaryButtonGetState = true;
            device.CurrentInputState.Primary2DAxisRaw = new Vector2(0.5f, -0.5f);
            device.CurrentInputState.Primary2DAxisClick = true;

            device.ApplyIgnoreMask();

            Assert.That(device.CurrentInputState.Trigger, Is.EqualTo(0f));
            Assert.That(device.CurrentInputState.GripButton, Is.False);
            Assert.That(device.CurrentInputState.PrimaryButtonGetState, Is.False);
            Assert.That(device.CurrentInputState.Primary2DAxisRaw, Is.EqualTo(new Vector2(0.5f, -0.5f)));
            Assert.That(device.CurrentInputState.Primary2DAxisClick, Is.True);
        }

        [Test]
        public void IgnoringSticks_ClearsSticksButKeepsButtons()
        {
            BasisInputXRSimulate device = Spawn("ctrl", "Test", true, BasisBoneTrackedRole.LeftHand);
            BasisDeviceOverrides.SetIgnore(device.OverrideKey, BasisDeviceIgnore.Sticks);
            device.CurrentInputState.Trigger = 1f;
            device.CurrentInputState.GripButton = true;
            device.CurrentInputState.Primary2DAxisRaw = new Vector2(0.5f, -0.5f);
            device.CurrentInputState.Secondary2DAxisRaw = new Vector2(0.25f, 0.75f);
            device.CurrentInputState.Primary2DAxisClick = true;

            device.ApplyIgnoreMask();

            Assert.That(device.CurrentInputState.Trigger, Is.EqualTo(1f));
            Assert.That(device.CurrentInputState.GripButton, Is.True);
            Assert.That(device.CurrentInputState.Primary2DAxisRaw, Is.EqualTo(Vector2.zero));
            Assert.That(device.CurrentInputState.Secondary2DAxisRaw, Is.EqualTo(Vector2.zero));
            Assert.That(device.CurrentInputState.Primary2DAxisClick, Is.False);
        }

        [Test]
        public void IgnoringTheWholeDevice_ClearsEveryInput()
        {
            BasisInputXRSimulate device = Spawn("ctrl", "Test", true, BasisBoneTrackedRole.LeftHand);
            BasisDeviceOverrides.SetIgnore(device.OverrideKey, BasisDeviceIgnore.Device);
            device.CurrentInputState.Trigger = 1f;
            device.CurrentInputState.GripButton = true;
            device.CurrentInputState.Primary2DAxisRaw = Vector2.one;

            device.ApplyIgnoreMask();

            Assert.That(device.CurrentInputState.Trigger, Is.EqualTo(0f));
            Assert.That(device.CurrentInputState.GripButton, Is.False);
            Assert.That(device.CurrentInputState.Primary2DAxisRaw, Is.EqualTo(Vector2.zero));
            Assert.That(device.IgnoresPose && device.IgnoresButtons && device.IgnoresSticks && device.IgnoresFingers && device.IgnoresPointer, Is.True);
        }

        [Test]
        public void ACalibratedTracker_IsLeftAloneByOverrideChangesOnOtherDevices()
        {
            BasisInputXRSimulate tracker = Spawn("tracker", "Test", false, BasisBoneTrackedRole.CenterEye);
            tracker.AssignRoleAndTracker(BasisBoneTrackedRole.Hips);
            BasisInputXRSimulate controller = Spawn("ctrl", "Test", true, BasisBoneTrackedRole.RightHand);

            BasisDeviceOverrides.SetHand(controller.OverrideKey, BasisBoneTrackedRole.LeftHand);

            Assert.That(Holds(tracker, BasisBoneTrackedRole.Hips), Is.True);
            Assert.That(hips.DevicesWithRoles, Is.EqualTo(new[] { "tracker" }));
            Assert.That(Holds(controller, BasisBoneTrackedRole.LeftHand), Is.True);
        }

        [Test]
        public void AHandOverride_DoesNothingOnADeviceThatIsNotAHand()
        {
            BasisInputXRSimulate free = Spawn("free", "Test", false, BasisBoneTrackedRole.CenterEye);
            BasisInputXRSimulate calibrated = Spawn("tracker", "Test", false, BasisBoneTrackedRole.CenterEye);
            calibrated.AssignRoleAndTracker(BasisBoneTrackedRole.Hips);

            BasisDeviceOverrides.SetHand(free.OverrideKey, BasisBoneTrackedRole.LeftHand);
            BasisDeviceOverrides.SetHand(calibrated.OverrideKey, BasisBoneTrackedRole.LeftHand);

            Assert.That(free.TryGetRole(out _), Is.False);
            Assert.That(free.HasRoleOverride, Is.False);
            Assert.That(Holds(calibrated, BasisBoneTrackedRole.Hips), Is.True);
            Assert.That(left.DevicesWithRoles, Is.Empty);
        }

        [Test]
        public void IgnoringThePoseOfACalibratedTracker_PausesAndResumesItsBone()
        {
            BasisInputXRSimulate tracker = Spawn("tracker", "Test", false, BasisBoneTrackedRole.CenterEye);
            tracker.AssignRoleAndTracker(BasisBoneTrackedRole.Hips);
            Assert.That(hips.DevicesWithRoles, Is.EqualTo(new[] { "tracker" }));

            BasisDeviceOverrides.SetIgnore(tracker.OverrideKey, BasisDeviceIgnore.Pose);

            Assert.That(Holds(tracker, BasisBoneTrackedRole.Hips), Is.True);
            Assert.That(tracker.IsPoseDriving, Is.False);
            Assert.That(hips.HasTracked, Is.EqualTo(BasisHasTracked.HasNoTracker));

            BasisDeviceOverrides.SetIgnore(tracker.OverrideKey, BasisDeviceIgnore.None);

            Assert.That(Holds(tracker, BasisBoneTrackedRole.Hips), Is.True);
            Assert.That(tracker.IsPoseDriving, Is.True);
            Assert.That(hips.HasTracked, Is.EqualTo(BasisHasTracked.HasTracker));
            Assert.That(hips.DevicesWithRoles, Is.EqualTo(new[] { "tracker" }));
        }

        [Test]
        public void FindDevice_PrefersTheDeviceDrivingThePose()
        {
            BasisInputXRSimulate inputOnly = Spawn("buttons", "TestA", true, BasisBoneTrackedRole.LeftHand);
            BasisInputXRSimulate driving = Spawn("pose", "TestB", true, BasisBoneTrackedRole.LeftHand);

            BasisDeviceOverrides.SetIgnore(inputOnly.OverrideKey, BasisDeviceIgnore.Pose);

            Assert.That(BasisDeviceManagement.Instance.FindDevice(out BasisInput found, BasisBoneTrackedRole.LeftHand), Is.True);
            Assert.That(found, Is.SameAs(driving));
            Assert.That(left.DevicesWithRoles, Is.EqualTo(new[] { "pose" }));
        }

        [Test]
        public void Store_RoundTripsThroughDisk()
        {
            BasisDeviceOverrides.SetHand("Test|a", BasisBoneTrackedRole.RightHand);
            BasisDeviceOverrides.SetIgnore("Test|b", BasisDeviceIgnore.Sticks | BasisDeviceIgnore.Fingers);

            BasisDeviceOverrides.SetStoragePath(storagePath);

            Assert.That(BasisDeviceOverrides.TryGetHand("Test|a", out BasisBoneTrackedRole hand), Is.True);
            Assert.That(hand, Is.EqualTo(BasisBoneTrackedRole.RightHand));
            Assert.That(BasisDeviceOverrides.GetIgnore("Test|a"), Is.EqualTo(BasisDeviceIgnore.None));
            Assert.That(BasisDeviceOverrides.TryGetHand("Test|b", out _), Is.False);
            Assert.That(BasisDeviceOverrides.GetIgnore("Test|b"), Is.EqualTo(BasisDeviceIgnore.Sticks | BasisDeviceIgnore.Fingers));

            BasisDeviceOverrides.ClearAll();
            BasisDeviceOverrides.SetStoragePath(storagePath);

            Assert.That(BasisDeviceOverrides.TryGetHand("Test|a", out _), Is.False);
            Assert.That(BasisDeviceOverrides.GetIgnore("Test|b"), Is.EqualTo(BasisDeviceIgnore.None));
        }

        [Test]
        public void OverrideAndIgnore_SurviveReinitialisationReconnectAndReload()
        {
            BasisInputXRSimulate first = SpawnObject("1|" + ControllerModel, "LHR-9");
            first.InitializeTracking("1|" + ControllerModel, ControllerModel, "Test", true, BasisBoneTrackedRole.RightHand);
            BasisDeviceManagement.Instance.AllInputDevices.Add(first);
            Assert.That(Holds(first, BasisBoneTrackedRole.RightHand), Is.True);
            Assert.That(first.OverrideKey, Is.EqualTo("Test|LHR-9"));

            BasisDeviceOverrides.SetHand(first.OverrideKey, BasisBoneTrackedRole.LeftHand);
            BasisDeviceOverrides.SetIgnorePart(first.OverrideKey, BasisDeviceIgnore.Sticks, true);
            Assert.That(Holds(first, BasisBoneTrackedRole.LeftHand), Is.True);

            first.InitializeTracking("1|" + ControllerModel, ControllerModel, "Test", true, BasisBoneTrackedRole.RightHand);
            Assert.That(Holds(first, BasisBoneTrackedRole.LeftHand), Is.True, "a runtime re-initialisation keeps the override");
            Assert.That(first.IgnoresSticks, Is.True);

            Disconnect(first);
            Assert.That(left.DevicesWithRoles, Is.Empty);

            BasisInputXRSimulate second = SpawnObject("5|" + ControllerModel, "LHR-9");
            second.InitializeTracking("5|" + ControllerModel, ControllerModel, "Test", true, BasisBoneTrackedRole.RightHand);
            BasisDeviceManagement.Instance.AllInputDevices.Add(second);
            Assert.That(second.OverrideKey, Is.EqualTo("Test|LHR-9"), "the key follows the serial, not the device index");
            Assert.That(Holds(second, BasisBoneTrackedRole.LeftHand), Is.True, "a reconnected controller keeps the override");
            Assert.That(second.IgnoresSticks, Is.True, "a reconnected controller keeps the ignore");
            Assert.That(left.DevicesWithRoles, Is.EqualTo(new[] { "5|" + ControllerModel }));

            BasisDeviceOverrides.SetStoragePath(storagePath);
            Assert.That(BasisDeviceOverrides.TryGetHand("Test|LHR-9", out BasisBoneTrackedRole hand), Is.True, "the override was written to disk");
            Assert.That(hand, Is.EqualTo(BasisBoneTrackedRole.LeftHand));
            Assert.That(BasisDeviceOverrides.GetIgnore("Test|LHR-9"), Is.EqualTo(BasisDeviceIgnore.Sticks), "the ignore was written to disk");
        }

        [Test]
        public void AnIgnoredDevice_StaysIgnoredWhenRecreated()
        {
            BasisInputXRSimulate first = SpawnObject("1|" + ControllerModel, "LHR-9");
            first.InitializeTracking("1|" + ControllerModel, ControllerModel, "Test", true, BasisBoneTrackedRole.RightHand);
            BasisDeviceManagement.Instance.AllInputDevices.Add(first);
            BasisDeviceOverrides.SetIgnore(first.OverrideKey, BasisDeviceIgnore.Device);
            Assert.That(first.TryGetRole(out _), Is.False);

            Disconnect(first);

            BasisInputXRSimulate second = SpawnObject("2|" + ControllerModel, "LHR-9");
            second.InitializeTracking("2|" + ControllerModel, ControllerModel, "Test", true, BasisBoneTrackedRole.RightHand);
            BasisDeviceManagement.Instance.AllInputDevices.Add(second);

            Assert.That(second.IgnoresDevice, Is.True);
            Assert.That(second.TryGetRole(out _), Is.False);
            Assert.That(right.DevicesWithRoles, Is.Empty);
        }

        [Test]
        public void AnIgnoredDevice_LeavesItsCachedCalibrationForALaterUnignore()
        {
            BasisInputXRSimulate tracker = Spawn("tracker", "Test", false, BasisBoneTrackedRole.CenterEye);
            BasisDeviceOverrides.SetIgnore(tracker.OverrideKey, BasisDeviceIgnore.Device);
            BasisDeviceManagement.Instance.PreviouslyConnectedDevices.Add(new BasisStoredPreviousDevice
            {
                trackedRole = BasisBoneTrackedRole.Hips,
                hasRoleAssigned = true,
                SubSystemIdentifier = "Test",
                UniqueDeviceIdentifier = "tracker",
            });

            BasisDeviceManagement.Instance.TryRestoreCachedRole(tracker);

            Assert.That(BasisDeviceManagement.Instance.PreviouslyConnectedDevices.Count, Is.EqualTo(1));
            Assert.That(tracker.TryGetRole(out _), Is.False);
            Assert.That(hips.DevicesWithRoles, Is.Empty);
        }

        [Test]
        public void SetHand_RejectsRolesThatAreNotHands()
        {
            BasisDeviceOverrides.SetHand("Test|a", BasisBoneTrackedRole.Hips);

            Assert.That(BasisDeviceOverrides.TryGetHand("Test|a", out _), Is.False);
        }
    }
}
