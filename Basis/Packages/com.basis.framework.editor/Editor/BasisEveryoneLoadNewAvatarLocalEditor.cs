using UnityEditor;
using UnityEngine;
using Basis.Editor.Localization;
using Basis.Scripts.Networking;

public class AvatarLoaderEditorWindow : EditorWindow
{
    private byte loadMode = 1;
    private string password = "";
    private string url = "";

    [MenuItem("Basis/Avatar/Force Load", false, 101)]
    public static void ShowWindow()
    {
        GetWindow<AvatarLoaderEditorWindow>(BasisEditorLocalization.Get("editor.avatarLoader.windowTitle"));
    }

    private void OnGUI()
    {
        GUILayout.Label(BasisEditorLocalization.Get("editor.avatarLoader.header"), EditorStyles.boldLabel);

        loadMode = (byte)EditorGUILayout.IntField(BasisEditorLocalization.Get("editor.avatarLoader.loadMode"), loadMode);
        password = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.avatarLoader.password"), password);
        url = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.avatarLoader.bundleUrl"), url);

        if (GUILayout.Button(BasisEditorLocalization.Get("editor.avatarLoader.forceLoad")))
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

        foreach (var players in BasisNetworkPlayers.RemotePlayers)
        {
            await players.Value.CreateAvatar(loadmode, loadableBundle);
        }

        Debug.Log("Avatar load initiated for all remote players.");
    }
}
