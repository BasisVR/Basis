#if !BASIS_FRAMEWORK_EXISTS
using Basis.Scripts.Animator_Driver;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Players.Editor
{
    // Avatar preview for projects without com.basis.framework. Compiled out when framework
    // is installed (gated via versionDefine BASIS_FRAMEWORK_EXISTS) — framework's real
    // BasisLocalPlayer registers itself with BasisLocalPlayerService at play-mode init.
    internal sealed class BasisEditorPreviewLocalPlayer : IBasisLocalPlayer, IBasisLocalEyeDriver
    {
        private const string FallbackControllerPath = "Packages/com.basis.sdk/Animator/BasisLocomotion.controller";
        private const string PreviewRootName = "BasisEditorPreviewRoot";

        private static readonly BasisEditorPreviewLocalPlayer _instance = new BasisEditorPreviewLocalPlayer();

        public static BasisAvatar ActiveAvatar { get; private set; }
        public static BasisAnimatorVariableApply Applier { get; private set; }
        public static event System.Action StateChanged;

        private static GameObject _previewRoot;

        [InitializeOnLoadMethod]
        private static void Register()
        {
            BasisLocalPlayerService.Instance = _instance;
            BasisLocalEyeDriverService.Instance = _instance;
            BasisLocalPlayerService.PlayerReady = true;
            BasisLocalPlayerService.OnLocalPlayerInitalized?.Invoke();
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

            _previewRoot = new GameObject(PreviewRootName) { hideFlags = HideFlags.DontSave };
            inSceneItem.transform.SetParent(_previewRoot.transform, worldPositionStays: false);
            inSceneItem.transform.localPosition = Vector3.zero;
            inSceneItem.transform.localRotation = Quaternion.identity;

            BasisAvatar avatar = inSceneItem.GetComponent<BasisAvatar>();
            if (avatar == null)
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
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public static void DisposeActive()
        {
            if (_previewRoot != null) Object.DestroyImmediate(_previewRoot);
            _previewRoot = null;
            ActiveAvatar = null;
            Applier = null;
            StateChanged?.Invoke();
        }

        // IBasisLocalEyeDriver — Phase 1 keeps no-op storage. Phase 3 will land a real driver.
        public float Liveliness { get; set; } = 0.5f;
        public float Attentiveness { get; set; } = 0.5f;
        public void ApplyPersonality() { }
    }
}
#endif
