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
    public void Initalize(Configuration Configuration, string LogPath)
    {
        //  BasisPrometheus.StartPrometheus(config.PromethusPort, config.PromethusUrl);
        // Initialize server-side logging
       //string FolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        BasisServerSideLogging.Initialize(Configuration, LogPath);
        // Create a cancellation token source
        cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;
        serverTask = Task.Run(() =>
        {
            try
            {
                Configuration Configuration = new Configuration();
                NetworkServer.StartServer(Configuration);
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
