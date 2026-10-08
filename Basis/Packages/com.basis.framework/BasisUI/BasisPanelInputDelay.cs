using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Swallows presses that land on a panel in the first moments after it opens. A panel
    /// that appears under the pointer would otherwise take the second half of an accidental
    /// double press as a real click on whatever it happens to have put there.
    /// <para>
    /// The dead time is scoped to the panel that just opened, so everything already on
    /// screen keeps responding immediately and rapid clicking elsewhere is unaffected. The
    /// press is not queued: the pointer has to be released and pressed again deliberately.
    /// </para>
    /// </summary>
    [AutoStaticsCleanup]
    [DisallowMultipleComponent]
    public sealed partial class BasisPanelInputDelay : MonoBehaviour
    {
        /// <summary>
        /// Seconds a freshly opened panel ignores presses, matched to the panel entrance
        /// animation so a panel is only pressable once it has finished arriving.
        /// </summary>
        public static float DeadTime = 0.25f;

        // Nothing can be armed past this, so a press outside the window costs a float compare
        // instead of a walk up the hierarchy.
        private static float _latestArmedUntil;

        private float _armedUntil;

        /// <summary>
        /// Starts the dead time on <paramref name="panel"/> and everything under it. Safe to
        /// call again on the same object; the window restarts from now.
        /// </summary>
        public static void Arm(GameObject panel)
        {
            if (panel == null || DeadTime <= 0f)
            {
                return;
            }

            if (!panel.TryGetComponent(out BasisPanelInputDelay delay))
            {
                delay = panel.AddComponent<BasisPanelInputDelay>();
            }

            delay._armedUntil = Time.unscaledTime + DeadTime;
            if (delay._armedUntil > _latestArmedUntil)
            {
                _latestArmedUntil = delay._armedUntil;
            }
        }

        /// <summary>
        /// True while <paramref name="target"/> belongs to a panel that is still inside its
        /// dead time and should not be taking presses yet.
        /// </summary>
        public static bool IsSuppressed(GameObject target)
        {
            float time = Time.unscaledTime;
            if (time >= _latestArmedUntil || target == null)
            {
                return false;
            }

            BasisPanelInputDelay delay = target.GetComponentInParent<BasisPanelInputDelay>();
            return delay != null && time < delay._armedUntil;
        }
    }
}
