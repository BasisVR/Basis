using UnityEngine;
public static class BasisStaticLogInitializer
{
    [RuntimeInitializeOnLoadMethod]
    private static void OnRuntimeMethodLoad()
    {
        Application.logMessageReceivedThreaded += BasisLogManager.HandleLog;
    }
}