using UnityEngine.UI;

namespace Basis.Shims
{
    public static class BasisUIExtensions
    {
        /// <summary>
        /// Shim for Toggle.onValueChanged.AddListener to prevent direct access to onValueChanged and avoid usage of UnityEvent
        /// </summary>
        /// <param name="toggle"></param>
        /// <param name="callback"></param>
        public static void AddListener(this Toggle toggle, System.Action<bool> callback)
        {
            toggle.onValueChanged.AddListener((value) => callback(value));
        }
    }
}
