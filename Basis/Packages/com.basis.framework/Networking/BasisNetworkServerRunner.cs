using Basis.Network;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static BasisPermissions.PermissionManager;

public class BasisNetworkServerRunner
{
    public Task serverTask;
    private readonly object lifecycleGate = new object();
    private CancellationTokenSource cancellationTokenSource;
    private BasisLanServerAnnouncer lanServerAnnouncer;

    [SerializeField]
    public Configuration Configuration;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetLanAdvertising()
    {
        BasisNetworkConnection.BasisNetworkServerRunner?.SetLanAdvertising(false);
    }

    public void Initialize(Configuration configuration, string LogPath, string UUIDTomarkAsAdmin)
    {
        Configuration = configuration;
        BasisServerSideLogging.Initialize(Configuration, LogPath);
        cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        serverTask = Task.Run(() =>
        {
            try
            {
                lock (lifecycleGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NetworkServer.StartServer(Configuration);
                    ApplyLanAdvertisingLocked(BasisNetworkManagement.HostShowToLan);
                }

                cancellationToken.ThrowIfCancellationRequested();
                PermissionIntegration.Manager.AddUserNode(UUIDTomarkAsAdmin, "*");
                PermissionIntegration.Manager.AddUserToGroup(UUIDTomarkAsAdmin, "admin");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SetLanAdvertising(false);
                BNL.LogError($"Server encountered an error: {ex.Message} {ex.StackTrace}");
            }
        }, cancellationToken);
    }

    public void SetLanAdvertising(bool enabled)
    {
        lock (lifecycleGate)
        {
            ApplyLanAdvertisingLocked(enabled);
        }
    }

    private void ApplyLanAdvertisingLocked(bool enabled)
    {
        BasisLanServerAnnouncer replacement = null;
        if (enabled && Configuration != null)
        {
            replacement = new BasisLanServerAnnouncer(
                Configuration.SetPort,
                Configuration.NetworkStackId,
                Configuration.ServerName,
                Configuration.ServerMotd,
                Configuration.UseAuth && !string.IsNullOrEmpty(Configuration.Password));
        }

        BasisLanServerAnnouncer previous = lanServerAnnouncer;
        lanServerAnnouncer = replacement;
        previous?.Dispose();
    }

    public void Stop()
    {
        lock (lifecycleGate)
        {
            cancellationTokenSource?.Cancel();
            ApplyLanAdvertisingLocked(false);
            NetworkServer.StopServer();
        }
    }
}
