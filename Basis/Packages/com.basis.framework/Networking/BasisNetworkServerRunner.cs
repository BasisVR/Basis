using Basis.Network;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

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
        cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;
        serverTask = Task.Run(() =>
        {
            try
            {
                NetworkServer.StartServer(Configuration);
                NetworkServer.authIdentity.AddNetPeerAsAdmin(UUIDTomarkAsAdmin);
            }
            catch (Exception ex)
            {
                BNL.LogError($"Server encountered an error: {ex.Message}");
                // Optionally, handle server restart or log critical errors
            }
        }, cancellationToken);
    }
    public void Stop()
    {
        cancellationTokenSource.Cancel();
    }
}
