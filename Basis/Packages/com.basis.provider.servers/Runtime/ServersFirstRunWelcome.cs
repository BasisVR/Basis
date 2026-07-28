using Basis.Scripts.Settings;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public static class ServersFirstRunWelcome
    {
        public const string SeenKey = "servers.welcome.seen";

        public static bool ShouldShow =>
            BasisSettingsSystem.LoadString(SeenKey, BasisSettingsSystem.FreshSettingsFile ? "false" : "true") == "false";

        public static void MarkSeen()
        {
            BasisSettingsSystem.SaveStringQuiet(SeenKey, "true");
        }

        public static void ResetSeen()
        {
            BasisSettingsSystem.SaveStringQuiet(SeenKey, "false");
        }

        public static async Task<bool> ShowAsync(BasisMenuPanel panel)
        {
            bool advanced = await ShowPageAsync(panel,
                BasisLocalization.Get("menu.servers.welcome.title"),
                BasisLocalization.Get("menu.servers.welcome.body"),
                AddressableAssets.Sprites.Information,
                BasisLocalization.Get("menu.servers.welcome.next"));
            if (!advanced) return false;
            if (panel == null || panel.IsReleased) return false;

            bool acknowledged = await ShowPageAsync(panel,
                BasisLocalization.Get("menu.servers.welcome.server.title"),
                BasisLocalization.Get("menu.servers.welcome.server.body"),
                AddressableAssets.Sprites.Servers,
                BasisLocalization.Get("menu.servers.welcome.ok"));
            if (!acknowledged) return false;

            MarkSeen();
            return true;
        }

        private static async Task<bool> ShowPageAsync(BasisMenuPanel panel, string title, string body, string icon, string buttonLabel)
        {
            DialogBox<bool> dialog = DialogBox<bool>.Create(panel, new Vector2(830, 380), title, body, icon, true);
            if (dialog.Descriptor != null)
            {
                PanelTabGroup actions = PanelTabGroup.CreateNew(dialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);
                actions.Descriptor.SetHeight(60);

                PanelButton okButton = PanelButton.CreateNew(PanelButton.ButtonStyles.AcceptButton, actions.TabButtonParent);
                okButton.Descriptor.SetTitle(buttonLabel);
                okButton.Descriptor.SetWidth(200);
                okButton.Descriptor.SetHeight(60);
                okButton.OnClicked += () =>
                {
                    if (dialog.IsBusy) return;
                    dialog.IsBusy = true;
                    dialog.CloseWithResult(true);
                };
            }
            return await dialog.WaitAsync();
        }
    }

    public sealed class ServersWelcomeFlash : MonoBehaviour
    {
        private static readonly Color FlashColor = new Color(1f, 0.62f, 0.2f, 1f);
        private const float Duration = 10f;
        private const float PulseSpeed = 3.2f;
        private const float SteadyStrength = 0.45f;

        private Graphic _target;
        private bool _pulse;
        private float _elapsed;

        public static void Attach(Graphic target, bool pulse)
        {
            if (target == null) return;
            if (target.TryGetComponent(out ServersWelcomeFlash existing))
            {
                existing._elapsed = 0f;
                existing._pulse = pulse;
                return;
            }
            ServersWelcomeFlash flash = target.gameObject.AddComponent<ServersWelcomeFlash>();
            flash._target = target;
            flash._pulse = pulse;
        }

        private void Update()
        {
            if (_target == null)
            {
                Destroy(this);
                return;
            }

            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed >= Duration)
            {
                Destroy(this);
                return;
            }

            float strength = _pulse
                ? (Mathf.Sin(_elapsed * PulseSpeed) + 1f) * 0.5f
                : SteadyStrength;
            // canvasRenderer.SetColor is render-only; Graphic.color would rebuild the canvas every frame.
            _target.canvasRenderer.SetColor(Color.Lerp(Color.white, FlashColor, strength));
        }

        private void OnDisable()
        {
            if (_target != null)
            {
                _target.canvasRenderer.SetColor(Color.white);
            }
        }
    }
}
