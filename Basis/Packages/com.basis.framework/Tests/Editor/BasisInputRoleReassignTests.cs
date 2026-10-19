using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
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
    public class BasisInputRoleReassignTests
    {
        private static readonly PropertyInfo LocalPlayerInstance = typeof(BasisLocalPlayer).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        private readonly List<GameObject> spawned = new List<GameObject>();
        private BasisLocalPlayer savedPlayer;
        private BasisDeviceManagement savedManagement;
        private BasisLocalBoneControl left, right;

        [SetUp]
        public void SetUp()
        {
            Assert.That(LocalPlayerInstance, Is.Not.Null, "BasisLocalPlayer.Instance was renamed; this test needs updating.");
            savedPlayer = BasisLocalPlayer.Instance;
            savedManagement = BasisDeviceManagement.Instance;
            left = new BasisLocalBoneControl();
            right = new BasisLocalBoneControl();
            BasisLocalBoneDriver driver = new BasisLocalBoneDriver
            {
                Controls = new[] { left, right },
                trackedRoles = new[] { BasisBoneTrackedRole.LeftHand, BasisBoneTrackedRole.RightHand },
                ControlsLength = 2,
            };
            BasisLocalPlayer player = (BasisLocalPlayer)FormatterServices.GetUninitializedObject(typeof(BasisLocalPlayer));
            player.LocalBoneDriver = driver;
            LocalPlayerInstance.SetValue(null, player);
            BasisDeviceManagement management = (BasisDeviceManagement)FormatterServices.GetUninitializedObject(typeof(BasisDeviceManagement));
            management.AllInputDevices = new BasisObservableList<BasisInput>();
            BasisDeviceManagement.Instance = management;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in spawned)
            {
                Object.DestroyImmediate(gameObject);
            }
            spawned.Clear();
            BasisDeviceManagement.Instance = savedManagement;
            LocalPlayerInstance.SetValue(null, savedPlayer);
        }

        private BasisInputXRSimulate Spawn()
        {
            GameObject followObject = new GameObject("Simulated device move");
            GameObject deviceObject = new GameObject("Simulated device");
            spawned.Add(followObject);
            spawned.Add(deviceObject);
            BasisInputXRSimulate device = deviceObject.AddComponent<BasisInputXRSimulate>();
            device.FollowMovement = followObject.transform;
            device.UniqueDeviceIdentifier = "Test:Simulated";
            device.SubSystemIdentifier = "Test";
            BasisDeviceManagement.Instance.AllInputDevices.Add(device);
            return device;
        }

        [Test]
        public void ReassigningARole_ReleasesThePreviousBoneControl()
        {
            BasisInputXRSimulate device = Spawn();

            device.AssignRoleAndTracker(BasisBoneTrackedRole.RightHand);
            Assert.That(right.HasTracked, Is.EqualTo(BasisHasTracked.HasTracker));
            Assert.That(right.DevicesWithRoles, Is.EqualTo(new[] { device.UniqueDeviceIdentifier }));

            device.AssignRoleAndTracker(BasisBoneTrackedRole.LeftHand);

            Assert.That(right.DevicesWithRoles, Is.Empty);
            Assert.That(right.HasTracked, Is.EqualTo(BasisHasTracked.HasNoTracker));
            Assert.That(right.HasRigLayer, Is.EqualTo(BasisHasRigLayer.HasNoRigLayer));
            Assert.That(left.HasTracked, Is.EqualTo(BasisHasTracked.HasTracker));
            Assert.That(left.DevicesWithRoles, Is.EqualTo(new[] { device.UniqueDeviceIdentifier }));
            Assert.That(device.TryGetRole(out BasisBoneTrackedRole role), Is.True);
            Assert.That(role, Is.EqualTo(BasisBoneTrackedRole.LeftHand));
        }

        [Test]
        public void ReassigningTheSameRole_KeepsASingleMembership()
        {
            BasisInputXRSimulate device = Spawn();

            device.AssignRoleAndTracker(BasisBoneTrackedRole.RightHand);
            device.AssignRoleAndTracker(BasisBoneTrackedRole.RightHand);

            Assert.That(right.DevicesWithRoles.Count, Is.EqualTo(1));
            Assert.That(right.HasTracked, Is.EqualTo(BasisHasTracked.HasTracker));
        }
    }
}
