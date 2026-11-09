using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Keeps an accept button disabled for a moment after a prompt opens, so a click or trigger press that was
    /// already on its way cannot accept something the player has not read.
    /// </summary>
    public static class BasisPromptArming
    {
        public const float Seconds = 1f;

        public static void Arm(PanelButton button) => Arm(button, BasisLocalization.Get("prompt.armWait"), Seconds);

        public static void Arm(PanelButton button, string waitText, float seconds)
        {
            if (button == null) return;
            button.SetInteractable(false, waitText);
            button.gameObject.AddComponent<Arming>().Begin(button, seconds);
        }

        private sealed class Arming : MonoBehaviour
        {
            private PanelButton button;
            private float readyAt;

            public void Begin(PanelButton target, float seconds)
            {
                button = target;
                readyAt = Time.unscaledTime + seconds;
            }

            private void Update()
            {
                if (Time.unscaledTime < readyAt) return;
                if (button != null) button.SetInteractable(true);
                Destroy(this);
            }
        }
    }
}
