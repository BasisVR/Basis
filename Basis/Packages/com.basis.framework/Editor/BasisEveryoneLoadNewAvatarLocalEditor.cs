using UnityEditor;
using UnityEngine;
using Basis.Scripts.Networking;

public class AvatarLoaderEditorWindow : EditorWindow
{
    private byte loadMode = 1;
    private string password = "";
    private string url = "";

    [MenuItem("Basis/Editor/Load Forced Avatar Window")]
    public static void ShowWindow()
    {
        GetWindow<AvatarLoaderEditorWindow>("Forced Avatar Loader");
    }

    private void OnGUI()
    {
        GUILayout.Label("Load Settings", EditorStyles.boldLabel);

        loadMode = (byte)EditorGUILayout.IntField("Load Mode (byte):", loadMode);
        password = EditorGUILayout.TextField("Unlock Password:", password);
        url = EditorGUILayout.TextField("Bundle URL:", url);

        if (GUILayout.Button("Force Load Avatars"))
        {
            ForceLoadAvatars(loadMode, password, url);
        }
    }

    private async void ForceLoadAvatars(byte loadmode, string password, string url)
    {
        BasisLoadableBundle loadableBundle = new BasisLoadableBundle
        {
            UnlockPassword = password,
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = url },
            BasisBundleConnector = new BasisBundleConnector(),
            BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
        };

        foreach (var players in BasisNetworkManagement.RemotePlayers)
        {
            await players.Value.RemotePlayer.CreateAvatar(loadmode, loadableBundle);
        }

        Debug.Log("Avatar load initiated for all remote players.");
    }
}
