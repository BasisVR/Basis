using System.Collections.Generic;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.UI;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Framework.Tests
{
    public class BasisInputPointerPassTests
    {
        private readonly List<GameObject> spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in spawned)
            {
                Object.DestroyImmediate(gameObject);
            }
            spawned.Clear();
        }

        private BasisInputXRSimulate SpawnPointingDevice()
        {
            GameObject deviceObject = new GameObject("Pointing device");
            GameObject pointerObject = new GameObject("Pointing device raycaster");
            spawned.Add(deviceObject);
            spawned.Add(pointerObject);

            BasisInputXRSimulate device = deviceObject.AddComponent<BasisInputXRSimulate>();
            device.RaycastCoord.position = Vector3.zero;
            device.RaycastCoord.rotation = Quaternion.identity;
            device.HasRayCastOverrideSupport = true;
            device.BasisPointRaycaster = pointerObject.AddComponent<BasisPointRaycaster>();
            device.BasisPointRaycaster.Initialize(device);
            device.BasisUIRaycast = new BasisUIRaycast { BasisPointRaycaster = device.BasisPointRaycaster };
            device.HasRaycaster = true;
            return device;
        }

        [Test]
        public void APassWithoutRaycastSupportKeepsTheUIHitTheRaycastPassFound()
        {
            BasisInputXRSimulate device = SpawnPointingDevice();
            device.UpdateInputEvents(HasPlayerControlSupport: false, hasPlayerRaycastSupport: true);
            device.BasisUIRaycast.HadRaycastUITarget = true;

            device.UpdateInputEvents(HasPlayerControlSupport: false, hasPlayerRaycastSupport: false);

            Assert.That(device.BasisUIRaycast.HadRaycastUITarget, Is.True);
        }

        [Test]
        public void ARaycastPassOnAnIgnoredPointerClearsTheUIHit()
        {
            BasisInputXRSimulate device = SpawnPointingDevice();
            device.UpdateInputEvents(HasPlayerControlSupport: false, hasPlayerRaycastSupport: true);
            device.BasisUIRaycast.HadRaycastUITarget = true;
            device.IgnoredParts = BasisDeviceIgnore.Pointer;

            device.UpdateInputEvents(HasPlayerControlSupport: false, hasPlayerRaycastSupport: true);

            Assert.That(device.BasisUIRaycast.HadRaycastUITarget, Is.False);
        }
    }
}
