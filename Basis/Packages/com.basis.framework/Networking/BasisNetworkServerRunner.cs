using Basis.Network;
using Basis.Network.Core;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static BasisPermissions.PermissionManager;

public class BasisNetworkServerRunner
{
    public Task serverTask;
    CancellationTokenSource cancellationTokenSource;
    [SerializeField]
    public Configuration Configuration;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public void Initalize(Configuration configuration, string LogPath,string UUIDTomarkAsAdmin)
    {
        Configuration = configuration;
        BasisServerSideLogging.Initialize(Configuration, LogPath);

        if (configuration.TransportType == NetworkTransportType.Steam)
        {
            StartServer(UUIDTomarkAsAdmin);
            return;
        }

        cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;
        serverTask = Task.Run(() =>
        {
            StartServer(UUIDTomarkAsAdmin);
        }, cancellationToken);
    }

    private void StartServer(string UUIDTomarkAsAdmin)
    {
        try
        {
            NetworkServer.StartServer(Configuration);

            PermissionIntegration.Manager.AddUserNode(UUIDTomarkAsAdmin,"*");
            PermissionIntegration.Manager.AddUserToGroup(UUIDTomarkAsAdmin, "admin");
        }
        catch (Exception ex)
        {
            BNL.LogError($"Server encountered an error: {ex.Message} {ex.StackTrace}");
        }
    }

    public void Stop()
    {
        cancellationTokenSource?.Cancel();
        NetworkServer.Server?.Stop();
    }
}
