using System;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.OpenVR.Structs
{
[System.Serializable]
public struct OpenVRDevice
{
    public ETrackedDeviceClass deviceClass;
    public uint deviceIndex;
    public string deviceName;
}
}