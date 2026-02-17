using Basis.Scripts.Device_Management;
using System;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Base class for device management in the Basis SDK.  
/// Provides a lifecycle framework for starting, stopping, and validating devices.  
/// Inherit from this class to implement device-specific management logic.
/// </summary>
public abstract class BasisBaseTypeManagement : MonoBehaviour
{
    /// <summary>
    /// Indicates whether the device has been successfully booted.  
    /// This is updated during <see cref="AttemptStartSDK"/> and <see cref="AttemptStopSDK"/>.
    /// </summary>
    public bool IsDeviceBooted = false;

    /// <summary>
    /// Defines how the device is expected to operate within the system.  
    /// Devices may either boot on demand (<see cref="BasisDeviceMode.BootByCall"/>)  
    /// or exist permanently (<see cref="BasisDeviceMode.PermanentlyExists"/>).
    /// </summary>
    public BasisDeviceMode DeviceMode;

    /// <summary>
    /// Stops the SDK-specific behavior for this device.  
    /// Override this method to provide shutdown or cleanup logic.
    /// </summary>
    public virtual void StopSDK()
    {
    }

    /// <summary>
    /// Starts the SDK-specific behavior for this device.  
    /// Override this method to provide initialization logic.
    /// </summary>
    public virtual void StartSDK()
    {
    }
    /// <summary>
    /// this is the correct time to poll everything
    /// </summary>
    public virtual void Simulate()
    {

    }

    /// <summary>
    /// Determines if the device can be booted based on the provided request string.  
    /// Override to implement custom boot validation.
    /// </summary>
    /// <param name="BootRequest">A string identifying the requested device to boot.</param>
    /// <returns><c>true</c> if the request matches the device type; otherwise, <c>false</c>.</returns>
    public virtual bool IsDeviceBootable(string BootRequest)
    {
        return false;
    }

    /// <summary>
    /// Attempts to validate whether the device can be booted for the given request.  
    /// Includes safeguards against duplicate boot attempts or invalid requests.
    /// </summary>
    /// <param name="BootRequest">The device identifier string.</param>
    /// <param name="OnlyFinding">
    /// If <c>true</c>, performs only validation without altering boot state.  
    /// If <c>false</c>, prevents re-booting an already booted device.
    /// </param>
    /// <returns><c>true</c> if the device is bootable; otherwise, <c>false</c>.</returns>
    public bool AttemptIsDeviceBootable(string BootRequest, bool OnlyFinding)
    {
        if (string.IsNullOrEmpty(BootRequest))
        {
            BasisDebug.LogError("Empty or null boot request recieved", BasisDebug.LogTag.Device);
            return false;
        }

        if (IsDeviceBooted && OnlyFinding == false)
        {
            // If this device is already booted, ignore new boot attempts.
            return false;
        }

        return IsDeviceBootable(BootRequest);
    }

    /// <summary>
    /// Attempts to stop the device if its <see cref="DeviceMode"/> allows it.  
    /// Catches and logs errors to prevent shutdown failure from disrupting the system.
    /// </summary>
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

            // Ensure the booted flag is cleared even if shutdown failed.
            IsDeviceBooted = false;
        }
    }

    /// <summary>
    /// Attempts to start the device asynchronously.  
    /// Sets <see cref="IsDeviceBooted"/> before startup to ensure clean shutdown if initialization fails.  
    /// Logs errors and falls back to default device management mode on failure.
    /// </summary>
    public async Task AttemptStartSDK()
    {
        try
        {
            if (IsDeviceBooted == false)
            {
                IsDeviceBooted = true; // Mark immediately to allow shutdown handling
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

    /// <summary>
    /// Starts the device if it is configured to exist permanently.  
    /// Useful for devices that should always be available (e.g., core systems).
    /// </summary>
    public async Task StartIfPermanentlyExists()
    {
        if (DeviceMode == BasisDeviceMode.PermanentlyExists)
        {
            await AttemptStartSDK();
        }
    }

    /// <summary>
    /// Defines how a device is expected to behave in the system.
    /// </summary>
    public enum BasisDeviceMode
    {
        /// <summary>
        /// Device only boots when explicitly requested.
        /// </summary>
        BootByCall,

        /// <summary>
        /// Device is always present and should start automatically.
        /// </summary>
        PermanentlyExists
    }
}
