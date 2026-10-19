using UnityEngine;
public static class BasisStaticLogInitializer
{
    [RuntimeInitializeOnLoadMethod]
    private static void OnRuntimeMethodLoad()
    {
        BasisLogManager.Start();
        Application.logMessageReceivedThreaded -= BasisLogManager.HandleLog;
        Application.logMessageReceivedThreaded += BasisLogManager.HandleLog;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
    }
    private static void OnQuitting()
    {
        Application.quitting -= OnQuitting;
        Application.logMessageReceivedThreaded -= BasisLogManager.HandleLog;
        BasisLogManager.Stop();
    }
}