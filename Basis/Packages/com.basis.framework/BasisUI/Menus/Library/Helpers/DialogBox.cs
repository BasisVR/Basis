using System.Threading.Tasks;
using UnityEngine;

namespace Basis.BasisUI
{
    public class DialogBox<T>
    {
        private TaskCompletionSource<T> _tcs;
        private PanelElementDescriptor _background;
        private PanelElementDescriptor _descriptor;

        public PanelElementDescriptor Descriptor => _descriptor;
        public PanelElementDescriptor Background => _background;

        public bool IsBusy = false;

        private DialogBox() { }

        public static DialogBox<T> Create(
            BasisMenuPanel panel,
            Vector2 size,
            string title = null,
            string description = null,
            string icon = null, 
            bool strongerOverlay = false
        )
        {
            DialogBox<T> box = new DialogBox<T>
            {
                _background = PanelElementDescriptor.CreateNew(
                    strongerOverlay ? PanelElementDescriptor.ElementStyles.OverlayLessOpacity : PanelElementDescriptor.ElementStyles.Overlay,
                    panel)
            };

            box._descriptor = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.LibraryEntryOverlay,
                box._background);

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

            box._tcs = new TaskCompletionSource<T>();

            return box;
        }

        public Task<T> WaitAsync()
        {
            return _tcs.Task;
        }

        public void CloseWithResult(T result)
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

            _tcs?.TrySetResult(result);
        }

        public void Cancel(T fallbackValue = default)
        {
            CloseWithResult(fallbackValue);
        }
    }
}