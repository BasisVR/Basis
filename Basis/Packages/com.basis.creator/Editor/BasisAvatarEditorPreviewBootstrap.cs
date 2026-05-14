using UnityEditor;

namespace Basis.Scripts.BasisSdk.Players.Editor
{
    internal static class BasisAvatarEditorPreviewBootstrap
    {
        [InitializeOnLoadMethod]
        private static void Register()
        {
            BasisAvatarSDKInspector.PreviewFoldoutFactory = BasisEditorPreviewParametersFoldout.Create;
        }
    }
}
