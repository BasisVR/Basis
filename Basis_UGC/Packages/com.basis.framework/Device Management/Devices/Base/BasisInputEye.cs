using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices
{
    public abstract class BasisInputEye : MonoBehaviour
    {
        public Vector3 LeftPosition;
        public Vector3 RightPosition;
        public abstract void Initalize();
        public abstract void Simulate();
        public abstract void Shutdown();
    }
}
