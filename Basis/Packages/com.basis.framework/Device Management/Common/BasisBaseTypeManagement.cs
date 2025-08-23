using Basis.Scripts.Device_Management;
using System;
using System.Threading.Tasks;
using UnityEngine;

public abstract class BasisBaseTypeManagement : MonoBehaviour
{
    public bool IsDeviceBooted = false;
    public BasisDeviceMode DeviceMode;
    public virtual void StopSDK()
    {

    }
    public virtual void StartSDK()
    {

    }
    public virtual bool IsDeviceBootable(string BootRequest)
    {
        return false;
    }
    public bool AttemptIsDeviceBootable(string BootRequest,bool OnlyFinding)
    {
        if (string.IsNullOrEmpty(BootRequest))
        {
            BasisDebug.LogError("Empty or null boot request recieved", BasisDebug.LogTag.Device);
            return false;
        }
        if (IsDeviceBooted && OnlyFinding == false)
        {
            return false;
            //if this is device is already booted we dont touch it.
        }
        return IsDeviceBootable(BootRequest);
    }
    public void AttemptStopSDK()
    {
        if (DeviceMode == BasisDeviceMode.BootByCall && IsDeviceBooted)
        {
            try
            {
                StopSDK();
            }
            catch (Exception E)
            {
                BasisDebug.LogError($"AttemptStopSDK Failed {E}!", BasisDebug.LogTag.Device);
            }
            IsDeviceBooted = false;//make sure tis stopped first
        }
    }
    public async Task AttemptStartSDK()
    {
        try
        {
            if (IsDeviceBooted == false)
            {
                IsDeviceBooted = true;//set straight away so shutdown will handle correctly
                StartSDK();
            }
            else
            {
                BasisDebug.LogError("Attempting to start a already started device", BasisDebug.LogTag.Device);
            }
        }
        catch (Exception E)
        {
            BasisDebug.LogError($"AttemptStartSDK Failed {E} booting Default!", BasisDebug.LogTag.Device);
           await BasisDeviceManagement.Instance.SwitchSetModeToDefault();
        }
    }
    public async Task StartIfPermanentlyExists()
    {
        if (DeviceMode == BasisDeviceMode.PermanentlyExists)
        {
          await  AttemptStartSDK();
        }
    }
    public enum BasisDeviceMode
    {
        BootByCall,//this device only boots up when we request it.
        PermanentlyExists//this device is perminant
    }
}
