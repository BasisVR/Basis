using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// Per-marker material highlight driven by one or more snap-path interactables. Place this
    /// on a marker GameObject (a transform listed in some BasisRotarySnapInteractable's
    /// snapPoints array). Configure each source as an (interactable, highlight material) pair;
    /// while that source's CurrentIndex selects this marker, the highlight is applied to the
    /// target renderer's element-0 slot. When no source is highlighting, the element-0 material
    /// captured at Awake is restored.
    ///
    /// Multiple sources may select the same marker simultaneously (e.g. clock hour and minute
    /// hands both pointing at "12"). The most-recently-activated source's material wins; as
    /// sources move off, the previous winner takes over until the stack is empty.
    /// </summary>
    public class BasisRotarySnapMarkerTint : MonoBehaviour
    {
        [System.Serializable]
        public struct Source
        {
            [Tooltip("Snap-path interactable to watch. This marker's transform must appear in its snapPoints array, otherwise this entry is silently ignored at runtime.")]
            public BasisSnapPathInteractable interactable;

            [Tooltip("Material applied to the renderer's element-0 slot while this interactable selects this marker. If null, no swap happens for this source (the next-priority source or the default takes over).")]
            public Material highlight;
        }

        [Tooltip("Renderer whose element-0 material is swapped. Auto-assigned to GetComponent<Renderer>() at Awake if left null.")]
        public Renderer targetRenderer;

        [Tooltip("Sources that may highlight this marker. The most-recently-activated source wins when multiple are active simultaneously.")]
        public Source[] sources;

        private Material _defaultMaterial;
        private UnityAction<int, int>[] _listeners;
        private int[] _markerIndexInSource;
        private readonly List<int> _activeStack = new List<int>();

        private void Awake()
        {
            if (targetRenderer == null) TryGetComponent(out targetRenderer);
            if (targetRenderer != null) _defaultMaterial = targetRenderer.sharedMaterial;
        }

        private void Start()
        {
            int n = sources != null ? sources.Length : 0;
            _listeners = new UnityAction<int, int>[n];
            _markerIndexInSource = new int[n];
            for (int i = 0; i < n; i++)
            {
                BasisSnapPathInteractable src = sources[i].interactable;
                if (src == null)
                {
                    _markerIndexInSource[i] = -1;
                    continue;
                }
                _markerIndexInSource[i] = IndexOfMarker(src);
                int captured = i;
                _listeners[i] = (previous, target) => HandleSnapChange(captured, previous, target);
                src.OnSnapIndexChanged.AddListener(_listeners[i]);

                if (_markerIndexInSource[i] >= 0 && src.CurrentIndex == _markerIndexInSource[i])
                {
                    Push(i);
                }
            }
            ApplyTopMaterial();
        }

        private void OnDestroy()
        {
            if (_listeners == null) return;
            int n = _listeners.Length;
            for (int i = 0; i < n; i++)
            {
                if (_listeners[i] == null) continue;
                if (sources[i].interactable != null)
                {
                    sources[i].interactable.OnSnapIndexChanged.RemoveListener(_listeners[i]);
                }
            }
        }

        private int IndexOfMarker(BasisSnapPathInteractable source)
        {
            Transform[] pts = source.snapPoints;
            if (pts == null) return -1;
            int n = pts.Length;
            for (int i = 0; i < n; i++)
            {
                if (pts[i] == transform) return i;
            }
            return -1;
        }

        private void HandleSnapChange(int sourceIdx, int previous, int target)
        {
            int my = _markerIndexInSource[sourceIdx];
            if (my < 0) return;
            bool wasActive = previous == my;
            bool isActive = target == my;
            if (wasActive && !isActive) RemoveFromStack(sourceIdx);
            else if (!wasActive && isActive) Push(sourceIdx);
            else return;
            ApplyTopMaterial();
        }

        private void Push(int sourceIdx)
        {
            RemoveFromStack(sourceIdx);
            _activeStack.Add(sourceIdx);
        }

        private void RemoveFromStack(int sourceIdx)
        {
            for (int i = _activeStack.Count - 1; i >= 0; i--)
            {
                if (_activeStack[i] == sourceIdx) { _activeStack.RemoveAt(i); return; }
            }
        }

        private void ApplyTopMaterial()
        {
            if (targetRenderer == null) return;
            Material m = _defaultMaterial;
            if (_activeStack.Count > 0)
            {
                int top = _activeStack[_activeStack.Count - 1];
                if (sources[top].highlight != null) m = sources[top].highlight;
            }
            targetRenderer.sharedMaterial = m;
        }
    }
}
