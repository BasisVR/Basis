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
    CancellationTokenSource cancellationTokenSource;
    [SerializeField]
    public Configuration Configuration;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public void Initialize(Configuration configuration, string LogPath, string UUIDTomarkAsAdmin)
    {
        Configuration = configuration;
        BasisServerSideLogging.Initialize(Configuration, LogPath);
        cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;
        serverTask = Task.Run(() =>
        {
            try
            {
                lock (lifecycleGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NetworkServer.StartServer(Configuration);

                    if (BasisNetworkManagement.HostShowToLan)
                    {
                        BasisLanServerAdvertiser.Start(
                            Configuration.SetPort,
                            Configuration.NetworkStackId,
                            Configuration.ServerName,
                            Configuration.ServerMotd,
                            Configuration.UseAuth && !string.IsNullOrEmpty(Configuration.Password));
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                PermissionIntegration.Manager.AddUserNode(UUIDTomarkAsAdmin,"*");
                PermissionIntegration.Manager.AddUserToGroup(UUIDTomarkAsAdmin, "admin");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                BasisLanServerAdvertiser.Stop();
                BNL.LogError($"Server encountered an error: {ex.Message} {ex.StackTrace}");
                // Optionally, handle server restart or log critical errors
            }
        }, cancellationToken);
    }
    public void Stop()
    {
        lock (lifecycleGate)
        {
            cancellationTokenSource?.Cancel();
            BasisLanServerAdvertiser.Stop();
            NetworkServer.StopServer();
        }
    }
}
