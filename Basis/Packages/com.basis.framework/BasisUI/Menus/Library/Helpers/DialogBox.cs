using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Lightweight helper that creates a standard overlay + descriptor dialog and
    /// exposes the descriptor and a close method. This encapsulates repeated
    /// overlay construction so callers can build dialogs and keep a reference.
    /// </summary>
    public class DialogBox
    {
        private PanelElementDescriptor _background;
        private PanelElementDescriptor _descriptor;
        public PanelElementDescriptor Descriptor => _descriptor;
        public PanelElementDescriptor Background => _background;
        public bool IsBusy = false;
        //public event Action Closed;
        private DialogBox() { }

        public static DialogBox Create(BasisMenuPanel panel, Vector2 size, string title = null, string description = null, string icon = null)
        {
            DialogBox box = new DialogBox
            {
                _background = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Overlay, panel)
            };

            box._descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.LibraryEntryOverlay, box._background);
            box._descriptor.rectTransform.localPosition = Vector3.zero;
            box._descriptor.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            box._descriptor.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            box._descriptor.rectTransform.anchoredPosition = Vector2.zero;
            box._descriptor.SetSize(size);

            if (!string.IsNullOrEmpty(title))
                box._descriptor.SetTitle(title);

            if (!string.IsNullOrEmpty(description))
                box._descriptor.SetDescription(description);

            if (icon != null)
                box._descriptor.SetIcon(icon);

            return box;
        }

        public async Task CloseAsync()
        {
            if (_descriptor != null)
            {
                UnityEngine.Object.Destroy(_descriptor.gameObject);
                _descriptor = null;
            }

            if (_background != null)
            {
                UnityEngine.Object.Destroy(_background.gameObject);
                _background = null;
            }

            await Task.Yield();

            // Closed?.Invoke();
            // Closed = null;
        }
    }
}
