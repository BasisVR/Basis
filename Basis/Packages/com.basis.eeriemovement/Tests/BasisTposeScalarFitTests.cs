using Basis.IK;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisTposeScalarFitTests
    {
        const float tolerance = 1e-5f;
        static BasisEerieMovement Baked()
        {
            return new BasisEerieMovement
            {
                tposeBakeScale = 1f,
                tposeArmFitScale = 1f,
                tposeTorsoFitScale = 1f,
                tposeClavicleLenLeft = 0.1f,
                tposeClavicleLenRight = 0.12f,
                tposeShoulderToHandLeft = 0.6f,
                tposeShoulderToHandRight = 0.62f,
                tposeShoulderToElbowLeft = 0.35f,
                tposeShoulderToElbowRight = 0.37f,
                tposeLengthNeckToHips = new Vector3(0f, 0.5f, 0f),
                tposeHeadToNeckLocal = new Vector3(0f, -0.1f, 0f),
                minHeadSpineHeight = 0.62f,
            };
        }
        [Test]
        public void ArmFit_ScalesTheArmBeyondTheClavicle()
        {
            var job = Baked();
            job.RescaleTposeFit(1.2f, 1f);
            Assert.AreEqual(0.1f, job.tposeClavicleLenLeft, tolerance);
            Assert.AreEqual(0.1f + 0.5f * 1.2f, job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(0.1f + 0.25f * 1.2f, job.tposeShoulderToElbowLeft, tolerance);
            Assert.AreEqual(0.12f + 0.5f * 1.2f, job.tposeShoulderToHandRight, tolerance);
            Assert.AreEqual(0.5f, job.tposeLengthNeckToHips.y, tolerance);
            Assert.AreEqual(0.62f, job.minHeadSpineHeight, tolerance);
        }
        [Test]
        public void TorsoFit_ScalesTheSpineScalars()
        {
            var job = Baked();
            job.RescaleTposeFit(1f, 0.9f);
            Assert.AreEqual(0.45f, job.tposeLengthNeckToHips.y, tolerance);
            Assert.AreEqual(-0.09f, job.tposeHeadToNeckLocal.y, tolerance);
            Assert.AreEqual(0.62f * 0.9f, job.minHeadSpineHeight, tolerance);
            Assert.AreEqual(0.6f, job.tposeShoulderToHandLeft, tolerance);
        }
        [Test]
        public void Refit_IsIdempotentAndReversible()
        {
            var job = Baked();
            job.RescaleTposeFit(1.2f, 0.9f);
            var once = job;
            job.RescaleTposeFit(1.2f, 0.9f);
            Assert.AreEqual(once.tposeShoulderToHandLeft, job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(once.minHeadSpineHeight, job.minHeadSpineHeight, tolerance);
            job.RescaleTposeFit(1f, 1f);
            var baked = Baked();
            Assert.AreEqual(baked.tposeShoulderToHandLeft, job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(baked.tposeShoulderToElbowRight, job.tposeShoulderToElbowRight, tolerance);
            Assert.AreEqual(baked.tposeLengthNeckToHips.y, job.tposeLengthNeckToHips.y, tolerance);
            Assert.AreEqual(baked.minHeadSpineHeight, job.minHeadSpineHeight, tolerance);
        }
        [Test]
        public void UniformRescale_ComposesWithTheFit()
        {
            var job = Baked();
            job.RescaleTposeFit(1.2f, 0.9f);
            job.RescaleTposeScalars(2f);
            Assert.AreEqual(2f * (0.1f + 0.5f * 1.2f), job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(0.2f, job.tposeClavicleLenLeft, tolerance);
            Assert.AreEqual(0.9f, job.tposeLengthNeckToHips.y, tolerance);
            job.RescaleTposeFit(1f, 1f);
            Assert.AreEqual(1.2f, job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(1f, job.tposeLengthNeckToHips.y, tolerance);
        }
        [Test]
        public void InvalidScales_AreIgnored()
        {
            var job = Baked();
            job.RescaleTposeFit(0f, float.NaN);
            Assert.AreEqual(0.6f, job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(1f, job.tposeArmFitScale, tolerance);
        }
    }
}
