#if !BASIS_FRAMEWORK_EXISTS
using Basis.Scripts.Animator_Driver;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Players.Editor
{
    // SDK-side avatar preview. Backs off when framework's BasisLocalPlayer has
    // already registered with BasisLocalPlayerData (play mode).
    internal sealed class BasisEditorPreviewLocalPlayer : IBasisLocalPlayer
    {
        private const string FallbackControllerPath = "Packages/com.basis.sdk/Animator/BasisLocomotion.controller";
        private const string PreviewRootName = "BasisEditorPreviewRoot";

        private static readonly BasisEditorPreviewLocalPlayer _instance = new BasisEditorPreviewLocalPlayer();

        public static BasisAvatar ActiveAvatar { get; private set; }
        public static BasisAnimatorVariableApply Applier { get; private set; }
        public static BasisAvatarSimPlayer ActiveSimPlayer { get; private set; }
        public static BasisAvatarSimMic ActiveSimMic { get; private set; }
        public static event System.Action StateChanged;

        private static GameObject _previewRoot;

        [InitializeOnLoadMethod]
        private static void Register()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            if (BasisLocalPlayerData.Instance != null) return;
            BasisLocalPlayerData.Instance = _instance;
            BasisLocalPlayerData.RaiseLocalPlayerInitialized();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // The preview is created during play mode (via the inspector's Test-in-Editor flow,
            // which enters play mode and resolves the pending avatar from SessionState). Unity's
            // play-mode revert destroys the preview root + clone automatically — we just need
            // to drop our static refs so they don't dangle into the next session.
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                if (ActiveSimMic != null) ActiveSimMic.StopCapture();
                _previewRoot = null;
                ActiveAvatar = null;
                Applier = null;
                ActiveSimPlayer = null;
                ActiveSimMic = null;
                StateChanged?.Invoke();
            }
        }

        public Task CreateAvatarFromMode(BasisLoadMode LoadMode, BasisLoadableBundle BasisLoadableBundle)
        {
            if (LoadMode != BasisLoadMode.ByGameobjectReference)
            {
                Debug.LogWarning("[Basis SDK Preview] Only ByGameobjectReference is supported.");
                return Task.CompletedTask;
            }

            GameObject inSceneItem = BasisLoadableBundle?.LoadableGameobject?.InSceneItem;
            if (inSceneItem == null)
            {
                Debug.LogWarning("[Basis SDK Preview] Bundle has no InSceneItem.");
                return Task.CompletedTask;
            }

            DisposeActive();

            _previewRoot = new GameObject(PreviewRootName);
            inSceneItem.transform.SetParent(_previewRoot.transform, worldPositionStays: false);
            inSceneItem.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

            if (!inSceneItem.TryGetComponent(out BasisAvatar avatar))
            {
                Debug.LogError("[Basis SDK Preview] InSceneItem has no BasisAvatar component.");
                DisposeActive();
                return Task.CompletedTask;
            }

            if (avatar.Animator != null)
            {
                if (avatar.Animator.runtimeAnimatorController == null)
                {
                    var fallback = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(FallbackControllerPath);
                    if (fallback != null) avatar.Animator.runtimeAnimatorController = fallback;
                }
                avatar.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                Applier = new BasisAnimatorVariableApply();
                Applier.LoadCachedAnimatorHashes(avatar.Animator);
            }

            ActiveAvatar = avatar;
            avatar.NotifyAvatarReady(true);

            // SimPlayer + SimMic provide play-mode ticking for blink and visemes.
            // The MonoBehaviours only tick in play mode; in edit mode they're inert.
            ActiveSimPlayer = _previewRoot.AddComponent<BasisAvatarSimPlayer>();
            ActiveSimPlayer.Bootstrap(avatar);

            ActiveSimMic = _previewRoot.AddComponent<BasisAvatarSimMic>();
            ActiveSimMic.Driver = ActiveSimPlayer.Viseme;

            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public static void DisposeActive()
        {
            if (ActiveSimMic != null) ActiveSimMic.StopCapture();
            if (_previewRoot != null) Object.DestroyImmediate(_previewRoot);
            _previewRoot = null;
            ActiveAvatar = null;
            Applier = null;
            ActiveSimPlayer = null;
            ActiveSimMic = null;
            StateChanged?.Invoke();
        }

    }
}
#endif
