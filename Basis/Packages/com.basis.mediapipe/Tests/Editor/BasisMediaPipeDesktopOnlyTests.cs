using Unity.Scripting.LifecycleManagement;
using System.Reflection;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using UnityEngine;

namespace Basis.MediaPipe.Tests
{
    [NoAutoStaticsCleanup]
    public class BasisMediaPipeDesktopOnlyTests
    {
        private const BindingFlags InstanceFlags = BindingFlags.NonPublic | BindingFlags.Instance, StaticFlags = BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly MethodInfo RemoveTracker = typeof(BasisMediaPipeManagement).GetMethod("RemoveTracker", InstanceFlags);
        private static readonly MethodInfo DestroyAllTrackers = typeof(BasisMediaPipeManagement).GetMethod("DestroyAllTrackers", InstanceFlags);
        private static readonly MethodInfo RegisterDeviceMatch = typeof(BasisMediaPipeManagement).GetMethod("RegisterDeviceMatch", StaticFlags);
        private static readonly FieldInfo DeviceMatchRegistered = typeof(BasisMediaPipeManagement).GetField("_deviceMatchRegistered", StaticFlags);
        private static readonly FieldInfo Backend = typeof(BasisMediaPipeManagement).GetField("_backend", InstanceFlags);
        private static readonly FieldInfo LeftHandIK = typeof(BasisMediaPipeManagement).GetField("_leftHandIK", InstanceFlags);
        private BasisLocalBoneControl savedLeft, savedRight, left, right;
        private BasisDeviceManagement savedManagement, management;
        private BasisMediaPipeManagement savedInstance, mediaPipe;
        private GameObject host, managementHost;
        private BasisDeviceNameMatcher matcher;

        [SetUp]
        public void SetUp()
        {
            Assert.That(RemoveTracker, Is.Not.Null, "BasisMediaPipeManagement.RemoveTracker was renamed; this test needs updating.");
            Assert.That(DestroyAllTrackers, Is.Not.Null, "BasisMediaPipeManagement.DestroyAllTrackers was renamed; this test needs updating.");
            Assert.That(RegisterDeviceMatch, Is.Not.Null, "BasisMediaPipeManagement.RegisterDeviceMatch was renamed; this test needs updating.");
            Assert.That(DeviceMatchRegistered, Is.Not.Null, "BasisMediaPipeManagement._deviceMatchRegistered was renamed; this test needs updating.");
            Assert.That(Backend, Is.Not.Null, "BasisMediaPipeManagement._backend was renamed; this test needs updating.");
            Assert.That(LeftHandIK, Is.Not.Null, "BasisMediaPipeManagement._leftHandIK was renamed; this test needs updating.");
            savedLeft = BasisLocalBoneDriver.LeftHandControl;
            savedRight = BasisLocalBoneDriver.RightHandControl;
            savedManagement = BasisDeviceManagement.Instance;
            savedInstance = BasisMediaPipeManagement.Instance;
            left = new BasisLocalBoneControl();
            right = new BasisLocalBoneControl();
            BasisLocalBoneDriver.LeftHandControl = left;
            BasisLocalBoneDriver.RightHandControl = right;
            managementHost = new GameObject("BasisDeviceManagement test");
            managementHost.SetActive(false);
            management = managementHost.AddComponent<BasisDeviceManagement>();
            matcher = ScriptableObject.CreateInstance<BasisDeviceNameMatcher>();
            management.BasisDeviceNameMatcher = matcher;
            management.CurrentMode = BasisConstants.Desktop;
            BasisDeviceManagement.Instance = management;
            host = new GameObject("BasisMediaPipeManagement test");
            mediaPipe = host.AddComponent<BasisMediaPipeManagement>();
            DeviceMatchRegistered.SetValue(null, false);
        }

        [TearDown]
        public void TearDown()
        {
            DeviceMatchRegistered.SetValue(null, false);
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(managementHost);
            Object.DestroyImmediate(matcher);
            BasisDeviceManagement.Instance = savedManagement;
            BasisMediaPipeManagement.Instance = savedInstance;
            BasisLocalBoneDriver.LeftHandControl = savedLeft;
            BasisLocalBoneDriver.RightHandControl = savedRight;
        }

        private static DeviceSupportInformation MediaPipeEntry(BasisDeviceNameMatcher matcher) => matcher.GetAssociatedDeviceMatchableNames(BasisMediaPipeManagement.SubSystem);

        [Test]
        public void ReleasingAHandTracker_RestoresFullArmWeight()
        {
            left.RigLayerWeight = 0f;
            right.RigLayerWeight = 0.3f;
            LeftHandIK.SetValue(mediaPipe, 0.6f);

            RemoveTracker.Invoke(mediaPipe, new object[] { BasisBoneTrackedRole.LeftHand });

            Assert.That(left.RigLayerWeight, Is.EqualTo(1f));
            Assert.That(right.RigLayerWeight, Is.EqualTo(0.3f));
            Assert.That((float)LeftHandIK.GetValue(mediaPipe), Is.EqualTo(0f));
        }

        [Test]
        public void ReleasingTheHeadTracker_LeavesTheArmWeightsAlone()
        {
            left.RigLayerWeight = 0f;

            RemoveTracker.Invoke(mediaPipe, new object[] { BasisBoneTrackedRole.Head });

            Assert.That(left.RigLayerWeight, Is.EqualTo(0f));
        }

        [Test]
        public void DestroyingAllTrackers_RestoresBothArmWeights()
        {
            left.RigLayerWeight = 0f;
            right.RigLayerWeight = 0.3f;

            DestroyAllTrackers.Invoke(mediaPipe, null);

            Assert.That(left.RigLayerWeight, Is.EqualTo(1f));
            Assert.That(right.RigLayerWeight, Is.EqualTo(1f));
        }

        [Test]
        public void SimulateOutsideDesktop_StopsTheSdkAndReleasesTheArms()
        {
            management.CurrentMode = BasisConstants.OpenXRLoader;
            mediaPipe.IsDeviceBooted = true;
            Backend.SetValue(mediaPipe, new BasisMediaPipeNullBackend());
            left.RigLayerWeight = 0f;

            mediaPipe.Simulate();

            Assert.That(Backend.GetValue(mediaPipe), Is.Null);
            Assert.That(mediaPipe.IsDeviceBooted, Is.False);
            Assert.That(left.RigLayerWeight, Is.EqualTo(1f));
        }

        [Test]
        public void SimulateInDesktop_KeepsTheSdk()
        {
            mediaPipe.IsDeviceBooted = true;
            Backend.SetValue(mediaPipe, new BasisMediaPipeNullBackend());

            mediaPipe.Simulate();

            Assert.That(Backend.GetValue(mediaPipe), Is.Not.Null);
            Assert.That(mediaPipe.IsDeviceBooted, Is.True);
        }

        [Test]
        public void RegisteringTheDeviceMatch_ClearsAForcedRoleOnTheAssetEntry()
        {
            DeviceSupportInformation entry = new DeviceSupportInformation
            {
                DeviceID = BasisMediaPipeManagement.SubSystem,
                matchableDeviceIds = new[] { BasisMediaPipeManagement.SubSystem },
                HasTrackedRole = true,
                TrackedRole = BasisBoneTrackedRole.RightHand,
                HasRayCastSupport = true,
            };
            matcher.BasisDevice.Add(entry);

            RegisterDeviceMatch.Invoke(null, null);

            Assert.That(entry.HasTrackedRole, Is.False);
            Assert.That(entry.HasRayCastSupport, Is.False);
            Assert.That(matcher.BasisDevice.Count, Is.EqualTo(1));
            Assert.That(MediaPipeEntry(matcher).HasTrackedRole, Is.False);
        }

        [Test]
        public void RegisteringTheDeviceMatch_AddsARolelessEntryWhenTheAssetHasNone()
        {
            RegisterDeviceMatch.Invoke(null, null);

            Assert.That(matcher.BasisDevice.Count, Is.EqualTo(1));
            DeviceSupportInformation entry = MediaPipeEntry(matcher);
            Assert.That(entry.HasTrackedRole, Is.False);
            Assert.That(entry.HasRayCastSupport, Is.False);
            Assert.That(matcher.BasisDevice.Count, Is.EqualTo(1));
        }
    }
}
