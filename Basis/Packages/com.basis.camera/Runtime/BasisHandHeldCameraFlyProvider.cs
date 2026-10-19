using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI.HandHeldCamera
{
    [AutoStaticsCleanup]
    public partial class BasisHandHeldCameraFlyProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        private static readonly Color FlyingColor = new Color(0.4f, 0.78f, 1f, 1f);

        private static BasisHandHeldCameraFlyProvider _instance;

        private Color _idleIconColor = Color.white;
        private Color _idleTitleColor = Color.white;

        public override string Title => BasisLocalization.Get(IsFlying ? "menu.provider.cameraFly.stop" : "menu.provider.cameraFly");
        public override string IconAddress => AddressableAssets.Sprites.Move;

        // Its own order rather than sharing one: a tie is broken on the title, and this title
        // changes as the camera takes off and lands.
        public override int Order => 11;

        public override bool Hidden => ResolveCamera() == null;

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            _instance = new BasisHandHeldCameraFlyProvider();
            BasisMenuBase<BasisMainMenu>.AddProvider(_instance);

            // Detach first: statics survive a domain reload, so with reload disabled in the editor
            // this runs again each play session and would stack up duplicate handlers.
            BasisHandHeldCameraRegistry.OnChanged -= RefreshMainMenu;
            BasisHandHeldCameraRegistry.OnChanged += RefreshMainMenu;
            BasisHandHeldCameraInteractable.OnFlyMenuVisibilityChanged -= RefreshMainMenu;
            BasisHandHeldCameraInteractable.OnFlyMenuVisibilityChanged += RefreshMainMenu;
            BasisHandHeldCameraInteractable.OnFlyStateChanged -= RefreshButton;
            BasisHandHeldCameraInteractable.OnFlyStateChanged += RefreshButton;
        }

        public override void RunAction()
        {
            BasisHandHeldCamera camera = ResolveCamera();
            if (camera == null) return;

            bool flying = !camera.IsFlyModeEnabled;
            camera.SetFlyModeEnabled(flying);

            // Taking off gets out of the way: the menu is over the shot, and the cursor is the
            // menu's while it is open. Landing does not — the menu is where you were.
            if (flying) BasisMainMenu.Close();
        }

        public override void OnButtonCreated(PanelButton button)
        {
            if (button.Descriptor.IconImage != null) _idleIconColor = button.Descriptor.IconImage.color;
            if (button.Descriptor.TitleLabel != null) _idleTitleColor = button.Descriptor.TitleLabel.color;

            ApplyVisuals(button);
        }

        private static BasisHandHeldCamera ResolveCamera()
        {
            IReadOnlyList<BasisHandHeldCamera> cameras = BasisHandHeldCameraRegistry.Cameras;

            BasisHandHeldCamera first = null;
            for (int Index = 0; Index < cameras.Count; Index++)
            {
                BasisHandHeldCamera camera = cameras[Index];
                if (camera == null || !camera.showFlyOnMainMenu) continue;
                // One already in the air wins, so the switch can always land what is flying.
                if (camera.IsFlyModeEnabled) return camera;
                if (first == null) first = camera;
            }

            BasisHandHeldCamera selected = BasisHandHeldCameraPanelProvider.SelectedCamera;
            if (selected != null && selected.showFlyOnMainMenu) return selected;

            return first;
        }

        private static bool IsFlying
        {
            get
            {
                BasisHandHeldCamera camera = ResolveCamera();
                return camera != null && camera.IsFlyModeEnabled;
            }
        }

        private static void RefreshMainMenu()
        {
            if (BasisMenuBase<BasisMainMenu>.Instance != null)
            {
                BasisMenuBase<BasisMainMenu>.Instance.BindProvidersToButtons();
            }
        }

        // Flight is also reachable from middle click, the VR thumbstick and the settings panel, so
        // the switch follows the camera rather than assuming it is the only writer.
        private static void RefreshButton()
        {
            if (_instance == null) return;

            PanelButton button = _instance.BoundButton;
            if (button == null || button.IsReleased) return;

            _instance.ApplyVisuals(button);
        }

        private void ApplyVisuals(PanelButton button)
        {
            bool flying = IsFlying;

            button.Descriptor.SetTitle(BasisLocalization.Get(flying ? "menu.provider.cameraFly.stop" : "menu.provider.cameraFly"));

            if (button.Descriptor.IconImage != null)
            {
                button.Descriptor.IconImage.color = flying ? FlyingColor : _idleIconColor;
            }

            if (button.Descriptor.TitleLabel != null)
            {
                button.Descriptor.TitleLabel.color = flying ? FlyingColor : _idleTitleColor;
            }
        }
    }
}
