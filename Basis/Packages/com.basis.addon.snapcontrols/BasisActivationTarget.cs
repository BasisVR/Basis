using System;
using UnityEngine;
using UnityEngine.Events;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// Generic on/off attach point for any interactable that has discrete states.
    /// Holds an optional list of GameObjects to toggle and a pair of UnityEvents
    /// fired on activation/deactivation. Call <see cref="Activate"/> / <see cref="Deactivate"/>
    /// to drive transitions; use <see cref="ApplyActiveState"/> with <c>fireEvents = false</c>
    /// for silent initial setup.
    ///
    /// Example callers: a snap-path interactable activating the current snap point,
    /// a multi-position lever activating the current detent, a radial-menu selector
    /// activating the focused item.
    /// </summary>
    public class BasisActivationTarget : MonoBehaviour
    {
        [Tooltip("GameObjects toggled active=true when this target is activated, active=false when deactivated.")]
        public GameObject[] enableWhileActive = Array.Empty<GameObject>();

        [Tooltip("Fired on activation. Not fired by ApplyActiveState(_, fireEvents: false).")]
        public UnityEvent OnActivated;

        [Tooltip("Fired on deactivation. Not fired by ApplyActiveState(_, fireEvents: false).")]
        public UnityEvent OnDeactivated;

        public bool IsActive { get; private set; }

        public void Activate() => ApplyActiveState(true, fireEvents: true);
        public void Deactivate() => ApplyActiveState(false, fireEvents: true);

        public void ApplyActiveState(bool active, bool fireEvents = true)
        {
            IsActive = active;
            int n = enableWhileActive.Length;
            for (int i = 0; i < n; i++)
            {
                GameObject go = enableWhileActive[i];
                if (go != null) go.SetActive(active);
            }
            if (!fireEvents) return;
            if (active) OnActivated?.Invoke();
            else OnDeactivated?.Invoke();
        }
    }
}
