using Basis.Scripts.Drivers;
using NUnit.Framework;

namespace Basis.Tests.Rendering
{
    /// <summary>
    /// ClassifyCpuMarker buckets ProfilerRecorder samples by name prefix, and SampleCpu sums every
    /// bucketed row unconditionally. ProfilerRecorder time is INCLUSIVE of everything nested inside
    /// a marker's Begin/End span, so a wrapper marker that spans other already-bucketed markers must
    /// be excluded rather than classified — see project_basis_perfbar_segment_doublecount. These
    /// tests pin the exclusion list and the surrounding prefix rules so a future edit to either one
    /// cannot silently reintroduce the double-count or regress a real leaf marker's bucket.
    /// </summary>
    public class BasisPerformanceCpuClassifyTests
    {
        [TestCase("BasisDriver.Update")]
        [TestCase("BasisDriver.FixedUpdate")]
        [TestCase("BasisDriver.LateUpdate")]
        [TestCase("BasisDriver.OnBeforeRender")]
        [TestCase("BasisDriver.LocalPlayer")]
        [TestCase("BasisDriver.LocalPlayer.Simulate")]
        [TestCase("BasisDriver.LocalPlayer.FinishSimulate")]
        [TestCase("BasisDriver.LocalPlayer.Movement")]
        [TestCase("BasisDriver.LocalPlayer.IKDestinations")]
        [TestCase("BasisDriver.LocalPlayer.LocoPoseSchedule")]
        [TestCase("BasisDriver.LocalPlayer.PlayspaceMover")]
        [TestCase("BasisDriver.DeviceManagement.Simulate")]
        [TestCase("BasisDriver.DeviceManagement.BaseTypes")]
        [TestCase("BasisDriver.Avatar.Install")]
        [TestCase("BasisDriver.Avatar.Calibrate")]
        [TestCase("BasisDriver.Avatar.Calibrate.BoneJobRegister")]
        [TestCase("BasisDriver.Network.AfterAvatarChanges")]
        [TestCase("BasisEerie.Spine")]
        public void InclusiveContainerMarker_IsExcluded(string name)
        {
            Assert.That(BasisPerformanceBarData.ClassifyCpuMarker(name), Is.Null,
                $"'{name}' wraps other already-bucketed markers — counting it too would double the same frame time.");
        }

        [TestCase("BasisDriver.LocalPlayer.VisemeSimulate", BasisPerformanceCpuSegment.Movement)]
        [TestCase("BasisDriver.LocalPlayer.IKDest.FootSchedule", BasisPerformanceCpuSegment.Movement)]
        [TestCase("BasisDriver.LocalPlayer.Move.Physics", BasisPerformanceCpuSegment.Movement)]
        [TestCase("BasisDriver.LocoPose.Gate", BasisPerformanceCpuSegment.Movement)]
        [TestCase("BasisDriver.Network.TransmitSchedule", BasisPerformanceCpuSegment.Networking)]
        [TestCase("BasisDriver.Sync.ScheduleRemote", BasisPerformanceCpuSegment.Networking)]
        [TestCase("BasisDriver.Jiggle.DispatchSimulate", BasisPerformanceCpuSegment.Jiggle)]
        [TestCase("BasisDriver.HVRComms.VariableNetworking", BasisPerformanceCpuSegment.Voice)]
        [TestCase("BasisDriver.Avatar.Install.Harvest", BasisPerformanceCpuSegment.AvatarLoad)]
        [TestCase("BasisEerie.Shoulders", BasisPerformanceCpuSegment.Ik)]
        [TestCase("BasisEerie.Spine.Lordosis", BasisPerformanceCpuSegment.Ik)]
        public void RealLeafMarker_ClassifiesToItsSegment(string name, BasisPerformanceCpuSegment expected)
        {
            Assert.That(BasisPerformanceBarData.ClassifyCpuMarker(name), Is.EqualTo(expected),
                "a genuine leaf marker must still classify correctly — the exclusion list must not over-match.");
        }

        [TestCase("BasisDriver.MainThreadActions")]
        [TestCase("BasisDriver.BTween.Simulate")]
        [TestCase("BasisDriver.Gizmo.Render")]
        public void GenericDriverMarker_FallsBackToEventDriver(string name)
        {
            Assert.That(BasisPerformanceBarData.ClassifyCpuMarker(name), Is.EqualTo(BasisPerformanceCpuSegment.EventDriver));
        }

        [TestCase("BasisNamePlate.Finish")]
        [TestCase("BasisConstraints.ScheduleSample")]
        [TestCase("BasisVisibility.Dispatch")]
        [TestCase("Basis.ImagePickup.AnimatedImage.Schedule")]
        [TestCase("Some.Unrelated.Marker")]
        public void NonDriverMarker_IsUnclassified(string name)
        {
            Assert.That(BasisPerformanceBarData.ClassifyCpuMarker(name), Is.Null,
                "not BasisDriver./BasisEerie.-prefixed, so it correctly falls to the Other residual rather than a bucket.");
        }
    }
}
