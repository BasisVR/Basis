using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.GlobalIllumination
{
    public class BasisGlobalIlluminationEmitterTests
    {
        private readonly List<GameObject> spawned = new List<GameObject>();
        private Camera camera;
        private GameObject cameraHost;

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationEmitter.Registered.Clear();
            cameraHost = new GameObject("BasisGITestCamera");
            camera = cameraHost.AddComponent<Camera>();
            camera.transform.position = Vector3.zero;
        }

        [TearDown]
        public void TearDown()
        {
            for (int index = 0; index < spawned.Count; index++)
            {
                if (spawned[index] != null) { Object.DestroyImmediate(spawned[index]); }
            }
            spawned.Clear();
            if (cameraHost != null) { Object.DestroyImmediate(cameraHost); }
            BasisGlobalIlluminationEmitter.Registered.Clear();
        }

        private BasisGlobalIlluminationEmitter Spawn(Vector3 position, float intensity, float range)
        {
            GameObject host = new GameObject("BasisGIEmitter");
            host.transform.position = position;
            spawned.Add(host);
            BasisGlobalIlluminationEmitter emitter = host.AddComponent<BasisGlobalIlluminationEmitter>();
            emitter.Intensity = intensity;
            emitter.Range = range;
            emitter.Radius = 0.25f;
            return emitter;
        }

        [Test]
        public void EmittersRegisterOnEnableAndUnregisterOnDisable()
        {
            BasisGlobalIlluminationEmitter emitter = Spawn(Vector3.zero, 1f, 10f);
            Assert.Contains(emitter, BasisGlobalIlluminationEmitter.Registered);
            emitter.enabled = false;
            Assert.IsFalse(BasisGlobalIlluminationEmitter.Registered.Contains(emitter));
        }

        [Test]
        public void ZeroIntensityEmittersDoNotContribute()
        {
            BasisGlobalIlluminationEmitter emitter = Spawn(Vector3.zero, 0f, 10f);
            Assert.IsFalse(emitter.Contributes);
            Assert.AreEqual(0, BasisGlobalIlluminationPass.GatherEmitters(camera, 8));
        }

        [Test]
        public void GatherIsCappedByTheQualityBudget()
        {
            for (int index = 0; index < 10; index++) { Spawn(new Vector3(index, 0f, 0f), 1f, 20f); }
            Assert.AreEqual(4, BasisGlobalIlluminationPass.GatherEmitters(camera, 4));
        }

        [Test]
        public void GatherIsCappedByTheShaderArray()
        {
            for (int index = 0; index < BasisGlobalIlluminationPass.MaxEmitters + 8; index++)
            {
                Spawn(new Vector3(index * 0.1f, 0f, 0f), 1f, 100f);
            }
            Assert.AreEqual(BasisGlobalIlluminationPass.MaxEmitters, BasisGlobalIlluminationPass.GatherEmitters(camera, 1024));
        }

        [Test]
        public void NearBrightEmittersWinOverDistantOnes()
        {
            BasisGlobalIlluminationEmitter far = Spawn(new Vector3(0f, 0f, 50f), 1f, 100f);
            BasisGlobalIlluminationEmitter near = Spawn(new Vector3(0f, 0f, 2f), 1f, 100f);
            Assert.AreEqual(1, BasisGlobalIlluminationPass.GatherEmitters(camera, 1));
            Assert.IsTrue(BasisGlobalIlluminationEmitter.Registered.Contains(near));
            Assert.IsTrue(BasisGlobalIlluminationEmitter.Registered.Contains(far));
        }

        [Test]
        public void DestroyedEmittersArePrunedRatherThanThrowing()
        {
            BasisGlobalIlluminationEmitter emitter = Spawn(Vector3.zero, 1f, 10f);
            Object.DestroyImmediate(emitter.gameObject);
            spawned.Clear();
            Assert.DoesNotThrow(() => BasisGlobalIlluminationPass.GatherEmitters(camera, 8));
            Assert.AreEqual(0, BasisGlobalIlluminationEmitter.Registered.Count);
        }

        [Test]
        public void RadianceIsScaledByIntensity()
        {
            BasisGlobalIlluminationEmitter emitter = Spawn(Vector3.zero, 3f, 10f);
            emitter.Color = Color.white;
            Vector3 radiance = emitter.Radiance;
            Assert.AreEqual(3f, radiance.x, 1e-3f);
            Assert.AreEqual(3f, radiance.y, 1e-3f);
            Assert.AreEqual(3f, radiance.z, 1e-3f);
        }
    }
}
