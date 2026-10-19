using Unity.Scripting.LifecycleManagement;
using System.Collections;
using Basis.BTween;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Keeps nudging the row a search jumped to until the pointer reaches it. One punch as the page
    /// lands is easy to miss — the menu has just switched tab, opened sections and re-laid itself out
    /// under the user — so the row goes on asking for attention, and stops the moment it has it.
    /// </summary>
    [AutoStaticsCleanup]
    public sealed partial class PanelSearchBounce : MonoBehaviour
    {
        /// <summary>Quiet time between nudges. Long enough to read as a heartbeat rather than a wobble.</summary>
        private const float Interval = 1.1f;

        private const float Punch = 1.06f;

        private static PanelSearchBounce _active;

        private PanelComponent[] _controls;
        private Coroutine _routine;

        /// <summary>
        /// Starts nudging <paramref name="row"/>, stopping whatever the last result was nudging — two
        /// rows bouncing at once is two answers to a question with one.
        /// </summary>
        public static void Play(RectTransform row)
        {
            Stop();
            if (row == null) return;

            if (!row.TryGetComponent(out PanelSearchBounce bounce))
            {
                bounce = row.gameObject.AddComponent<PanelSearchBounce>();
            }

            _active = bounce;
            bounce.Begin();
        }

        /// <summary>Ends the current nudge, for a caller that has taken the user somewhere else.</summary>
        public static void Stop()
        {
            if (_active != null) _active.StopBounce();
            _active = null;
        }

        private bool PointerInside
        {
            get
            {
                if (_controls == null) return false;
                for (int i = 0; i < _controls.Length; i++)
                {
                    PanelComponent control = _controls[i];
                    if (control != null && control.PointerInside) return true;
                }
                return false;
            }
        }

        private void Begin()
        {
            StopBounce();
            _controls = GetComponentsInChildren<PanelComponent>(true);
            if (isActiveAndEnabled) _routine = StartCoroutine(Bounce());
        }

        private IEnumerator Bounce()
        {
            do
            {
                UIAnimations.PunchScale(transform, Punch);
                yield return new WaitForSecondsRealtime(Interval);
            }
            while (!PointerInside);

            _routine = null;
            Settle();
        }

        private void StopBounce()
        {
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }

            Settle();
        }

        private void Settle()
        {
            if (this != null) transform.localScale = Vector3.one;
        }

        // A row torn down or scrolled out of an unloaded page never gets its pointer, so the loop
        // stops with it rather than waiting for a hover that cannot arrive.
        private void OnDisable()
        {
            if (_active == this) _active = null;
            StopBounce();
        }
    }
}
