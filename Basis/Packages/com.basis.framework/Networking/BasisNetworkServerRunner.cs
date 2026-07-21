using Basis.Network;
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

    [SerializeField]
    public Configuration Configuration;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetLanAdvertising()
    {
        NetworkServer.SetLanAdvertising(false);
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
                }

                cancellationToken.ThrowIfCancellationRequested();
                NetworkServer.SetLanAdvertising(BasisNetworkManagement.HostShowToLan);
                PermissionIntegration.Manager.AddUserNode(UUIDTomarkAsAdmin, "*");
                PermissionIntegration.Manager.AddUserToGroup(UUIDTomarkAsAdmin, "admin");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                NetworkServer.SetLanAdvertising(false);
                BNL.LogError($"Server encountered an error: {ex.Message} {ex.StackTrace}");
            }
        }, cancellationToken);
    }

    public void SetLanAdvertising(bool enabled)
    {
        NetworkServer.SetLanAdvertising(enabled);
    }

    public void Stop()
    {
        lock (lifecycleGate)
        {
            cancellationTokenSource?.Cancel();
        }

        // mDNS goodbye and socket shutdown may block briefly; do not hold the runner
        // lifecycle lock while the server-owned announcer is disposed.
        NetworkServer.StopServer();
    }
}
