using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Simulation
{
    public class BasisSimulateXR : BasisBaseTypeManagement
    {
        public List<BasisInputXRSimulate> Inputs = new List<BasisInputXRSimulate>();
        public BasisInputXRSimulate CreatePhysicalTrackedDevice(string UniqueID, string UnUniqueID, BasisBoneTrackedRole Role = BasisBoneTrackedRole.LeftHand, bool hasrole = false, string subSystems = "BasisSimulateXR")
        {
            GameObject gameObject = new GameObject(UniqueID);
            gameObject.transform.parent = BasisLocalPlayer.Instance.transform;

            GameObject Moveable = new GameObject(UniqueID + " move transform");
            Moveable.transform.parent = BasisLocalPlayer.Instance.transform;

            BasisInputXRSimulate BasisInput = gameObject.AddComponent<BasisInputXRSimulate>();
            BasisInput.FollowMovement = Moveable.transform;
            BasisInput.InitalizeTracking(UniqueID, UnUniqueID, subSystems, hasrole, Role);
            if (Inputs.Contains(BasisInput) == false)
            {
                Inputs.Add(BasisInput);
            }
            BasisDeviceManagement.Instance.TryAdd(BasisInput);
            return BasisInput;
        }
        public override void StopSDK()
        {
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public override void BeginLoadSDK()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public override void StartSDK()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
        }

        public override string Type()
        {
            return "SimulateXR";
        }
    }
}
