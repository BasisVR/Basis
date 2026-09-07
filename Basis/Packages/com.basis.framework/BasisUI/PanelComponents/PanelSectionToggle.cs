using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public sealed class PanelSectionToggle : PanelDataComponent<bool>
    {
        private const string CollapsedArrow = ">";
        private const string ExpandedArrow = "\u25bc";
        private const float CompactHeightScale = 0.8f;

        // Avoid scaling inappropriately large or sentinel preferred-height values.
        private const float MaxPreferredHeightThreshold = 1000f;

        private const int MaxSectionDepth = 16;

        public static class Styles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Section Toggle.prefab";
            public static string Entry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Section Toggle - Entry Variant.prefab";
        }

        public Toggle ToggleComponent;
        public RectTransform ToggleVisual;

        [Header("Visual Elements")]
        public Graphic Background;

        private TextMeshProUGUI _arrowLabel;
        private GameObject _generatedArrowObject;
        private string _title = string.Empty;
        private PanelSectionToggleMarker _marker;
        private readonly List<PanelSectionContentMarker> _contentMarkers = new();
        private PanelSectionDividerManager _dividerManager;

        private bool _created;
        private bool _compactHeightApplied;

        protected override Selectable InteractableTarget => ToggleComponent;
        internal PanelSectionDividerManager DividerManager => _dividerManager ??= new PanelSectionDividerManager(this, _contentMarkers);
        public bool Expanded => Value;
        public event Action<bool> OnExpandedChanged;

        public static PanelSectionToggle CreateNew(Component parent) =>
            CreateNew<PanelSectionToggle>(Styles.Default, parent);

        public static PanelSectionToggle CreateNewEntry(Component parent) =>
            CreateNew<PanelSectionToggle>(Styles.Entry, parent);

        public static PanelSectionToggle CreateNew(Component parent, string style) =>
            CreateNew<PanelSectionToggle>(style, parent);

        protected override void OnEnable()
        {
            base.OnEnable();
            if (SettingsBinding != null)
            {
                SetValueWithoutNotify(SettingsBinding.RawValue);
            }
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();

            if (ToggleComponent == null)
            {
                BasisDebug.LogError($"{nameof(PanelSectionToggle)} requires {nameof(ToggleComponent)} to be assigned.");
            }

            if (_created)
            {
                RefreshVisualState();
                return;
            }

            _created = true;
            MarkSectionToggleRow();
            ConfigureArrowIndicator();
            ApplyCompactHeightOnce();
            DividerManager.CreateDividerAbove();
        }

        public override void OnReleaseEvent()
        {
            DividerManager.Release();

            if (_generatedArrowObject != null)
            {
                DestroyGeneratedObject(ref _generatedArrowObject);
                _arrowLabel = null;
            }

            if (_marker != null)
            {
                _marker.Toggle = null;
                _marker = null;
            }

            ClearRegisteredContentMarkers();

            _created = false;

            base.OnReleaseEvent();
        }

        public override void AssignBinding(BasisSettingsBinding<bool> binding)
        {
            if (binding == null)
            {
                BasisDebug.LogError($"{nameof(PanelSectionToggle)} requires a non-null binding.");
                SetExpandedWithoutNotify(false);
                return;
            }

            base.AssignBinding(binding);
            ToggleComponent?.SetIsOnWithoutNotify(binding.RawValue);
            RefreshVisualState();
        }

        public override void SetValue(bool value)
        {
            ToggleComponent?.SetIsOnWithoutNotify(value);
            base.SetValue(value);
            OnExpandedChanged?.Invoke(value);
            RefreshVisualState();
        }

        public override void SetValueWithoutNotify(bool value)
        {
            ToggleComponent?.SetIsOnWithoutNotify(value);
            base.SetValueWithoutNotify(value);
            RefreshVisualState();
        }

        public override void OnComponentUsed()
        {
            base.OnComponentUsed();
            SetValue(ToggleComponent != null && ToggleComponent.isOn);
        }

        public void SetTitle(string title)
        {
            _title = title ?? string.Empty;
            Descriptor?.SetTitle(_title);
        }

        public void BindToToggle(BasisSettingsBinding<bool> binding)
        {
            if (binding == null)
            {
                BasisDebug.LogError($"{nameof(PanelSectionToggle)} requires a non-null binding.");
                SetExpandedWithoutNotify(false);
                return;
            }

            AssignBinding(binding);
        }

        public void SetExpanded(bool expanded)
        {
            SetValue(expanded);
        }

        public void SetExpandedWithoutNotify(bool expanded)
        {
            SetValueWithoutNotify(expanded);
        }

        /// <summary>
        /// Takes the header itself off the page, along with the dividers it draws, for a page that
        /// only offers this section in some of its modes. The expanded flag is left alone, so a
        /// section that comes back comes back at whatever the user last left it at. Use
        /// <see cref="PanelSectionToggleHelpers.SetSectionVisible"/> to move the content with it.
        /// </summary>
        public void SetSectionVisible(bool visible)
        {
            gameObject.SetActive(visible);
            DividerManager.SetHidden(!visible);
        }

        public void RegisterContentContainer(Component contentContainer)
        {
            if (contentContainer == null)
            {
                return;
            }

            if (!contentContainer.TryGetComponent(out PanelSectionContentMarker marker))
            {
                marker = contentContainer.gameObject.AddComponent<PanelSectionContentMarker>();
            }

            if (marker.Owner != null && marker.Owner != this)
            {
                marker.Owner.UnregisterContentMarker(marker);
            }

            marker.Owner = this;
            if (!_contentMarkers.Contains(marker))
            {
                _contentMarkers.Add(marker);
            }

            _resetProbeFrame = -1;
        }

        /// <summary>
        /// Fills <paramref name="results"/> with the containers registered as this section's content.
        /// Containers destroyed since they were registered — a lazy section's box after it collapsed —
        /// are skipped, so an empty result means the section currently has nothing built under it.
        /// </summary>
        public void GetContentContainers(List<Transform> results)
        {
            if (results == null)
            {
                return;
            }

            results.Clear();
            for (int i = 0; i < _contentMarkers.Count; i++)
            {
                PanelSectionContentMarker marker = _contentMarkers[i];
                if (marker != null)
                {
                    results.Add(marker.transform);
                }
            }
        }

        // ---- Section reset -------------------------------------------------------------------
        // The header answers the same options gesture its rows do, and answers it for the lot: one
        // press on "Film Look" puts every control under that header back to its default instead of
        // eleven presses on the controls themselves.

        /// <summary>Localization key for a section that is already sitting at every one of its defaults.</summary>
        public const string SectionNoChangesKey = "ui.resetSection.nochanges";

        private readonly List<Transform> _resetContainers = new();
        private readonly List<PanelComponent> _resetProbe = new();
        private int _resetProbeFrame = -1;
        private bool _resetProbeResult;

        /// <summary>
        /// True while anything under this header knows a default to go back to. The gesture poll
        /// asks this every frame the header is hovered and answering means walking the section, so
        /// it is worked out once per frame and again whenever the section's content changes hands.
        /// A closed lazy section has destroyed its rows and correctly answers no — there is nothing
        /// built there to reset.
        /// </summary>
        public override bool HasResetDefault
        {
            get
            {
                if (_resetProbeFrame == Time.frameCount) return _resetProbeResult;
                _resetProbeFrame = Time.frameCount;

                CollectResetTargets(_resetProbe);
                _resetProbeResult = _resetProbe.Count > 0;
                _resetProbe.Clear();
                return _resetProbeResult;
            }
        }

        /// <summary>
        /// The header's own answer to the gesture: put everything under it back, behind a single
        /// confirmation rather than one per row. The window lists what would actually move, so a
        /// section already at its defaults says so instead of offering a reset that does nothing.
        /// </summary>
        public override void RequestReset()
        {
            List<PanelComponent> targets = new();
            CollectResetTargets(targets);
            if (targets.Count == 0) return;

            string label = Descriptor && !string.IsNullOrEmpty(Descriptor.Title) ? Descriptor.Title : _title;

            BasisMenuBase<BasisMainMenu> menu = BasisMenuBase<BasisMainMenu>.Instance;
            if (menu == null)
            {
                // No menu to host a window — reset without asking rather than doing nothing.
                ApplyResetTo(targets);
                return;
            }

            // OpenDialogue refuses while another modal is already up, and would leave that one in
            // Dialogue. Without this the details below would be grafted onto that unrelated window.
            if (menu.Dialogue != null) return;

            List<BasisMenuDialoguePanel.DetailRow> changes = DescribeChanges(targets);
            string body = BasisLocalization.Get("ui.resetPage.confirm", label) + "\n\n" + (changes.Count > 0
                ? BasisLocalization.Get(BasisPanelMoveHandle.ResetChangedKey, changes.Count)
                : BasisLocalization.Get(SectionNoChangesKey));

            menu.OpenDialogue(
                BasisLocalization.Get("ui.resetPage.title", label),
                body,
                BasisLocalization.Get("ui.reset"),
                BasisLocalization.Get("ui.cancel"),
                confirmed =>
                {
                    if (confirmed) ApplyResetTo(targets);
                });

            if (changes.Count > 0 && menu.Dialogue != null) menu.Dialogue.ShowDetails(changes);
        }

        /// <summary>
        /// Resets the section's rows for a caller that has already asked. The header's own
        /// open/closed state is not one of the values a reset is about, so it is left exactly where
        /// the user had it.
        /// </summary>
        public override void ApplyResetToDefault()
        {
            List<PanelComponent> targets = new();
            CollectResetTargets(targets);
            ApplyResetTo(targets);
        }

        /// <summary>
        /// Every control filed under this header that knows a default, the rows of nested sections
        /// included — those live inside this section's own containers. The nested headers themselves
        /// are skipped: each one stands for rows this sweep has already reached, and one whose rows
        /// are not built has nothing to offer either way.
        /// </summary>
        private void CollectResetTargets(List<PanelComponent> results)
        {
            results.Clear();
            GetContentContainers(_resetContainers);

            for (int i = 0; i < _resetContainers.Count; i++)
            {
                PanelComponent[] components = _resetContainers[i].GetComponentsInChildren<PanelComponent>(true);
                for (int j = 0; j < components.Length; j++)
                {
                    PanelComponent component = components[j];
                    if (component is PanelSectionToggle || !component.HasResetDefault) continue;

                    results.Add(component);
                }
            }

            _resetContainers.Clear();
        }

        private static void ApplyResetTo(List<PanelComponent> targets)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                // A page can be rebuilt while the window is open — switching camera mode does it —
                // so what the list was holding is re-checked rather than trusted.
                PanelComponent target = targets[i];
                if (target != null) target.ApplyResetToDefault();
            }
        }

        private static List<BasisMenuDialoguePanel.DetailRow> DescribeChanges(List<PanelComponent> targets)
        {
            List<BasisMenuDialoguePanel.DetailRow> rows = new();
            string entryFormat = BasisLocalization.Get("menu.panel.reset.changed.entry");

            for (int i = 0; i < targets.Count; i++)
            {
                PanelComponent target = targets[i];
                if (target == null ||
                    !target.TryDescribeSettingChange(out string label, out string current, out string standard))
                {
                    continue;
                }

                rows.Add(new BasisMenuDialoguePanel.DetailRow(label, string.Format(entryFormat, current, standard)));
            }

            return rows;
        }

        /// <summary>
        /// Fills <paramref name="results"/> with the sections that have to be open for
        /// <paramref name="section"/> to be reachable, outermost first and ending with the section
        /// itself. Opening only the innermost leaves it inside a closed parent — and while that parent
        /// is a lazy one, the nested header does not exist to be opened at all.
        /// </summary>
        public static void GetSectionChain(PanelSectionToggle section, List<PanelSectionToggle> results)
        {
            if (results == null)
            {
                return;
            }

            results.Clear();
            PanelSectionToggle current = section;
            while (current != null && results.Count < MaxSectionDepth && !results.Contains(current))
            {
                results.Add(current);
                current = GetOwningSection(current.transform);
            }

            results.Reverse();
        }

        /// <summary>
        /// The section whose registered content <paramref name="node"/> sits inside, or null when it
        /// is not filed under one. Walked through the content markers rather than the transform
        /// parents: a section's rows live in containers it registers, not under its header.
        /// </summary>
        public static PanelSectionToggle GetOwningSection(Transform node)
        {
            for (Transform parent = node != null ? node.parent : null; parent != null; parent = parent.parent)
            {
                if (parent.TryGetComponent(out PanelSectionContentMarker marker) && marker.Owner != null)
                {
                    return marker.Owner;
                }
            }

            return null;
        }

        protected override void ApplyValue()
        {
            base.ApplyValue();
            UpdateArrow();
            DividerManager.UpdateVisibility();
        }

        private void RefreshVisualState()
        {
            UpdateArrow();
            DividerManager.UpdateVisibility();
        }

        private void MarkSectionToggleRow()
        {
            if (!TryGetComponent(out _marker))
            {
                _marker = gameObject.AddComponent<PanelSectionToggleMarker>();
            }

            _marker.Toggle = this;
        }

        private void ConfigureArrowIndicator()
        {
            if (_arrowLabel != null)
            {
                return;
            }

            RectTransform arrowParent = ResolveArrowParent();
            if (arrowParent == null)
            {
                return;
            }

            if (ToggleVisual != null)
            {
                ToggleVisual.gameObject.SetActive(false);
            }

            if (Background != null)
            {
                Background.gameObject.SetActive(false);
            }

            _generatedArrowObject = new GameObject("Section Arrow", typeof(RectTransform));
            _generatedArrowObject.layer = arrowParent.gameObject.layer;

            _generatedArrowObject.TryGetComponent(out RectTransform arrowTransform);
            arrowTransform.SetParent(arrowParent, false);
            arrowTransform.anchorMin = Vector2.zero;
            arrowTransform.anchorMax = Vector2.one;
            arrowTransform.offsetMin = Vector2.zero;
            arrowTransform.offsetMax = Vector2.zero;

            _arrowLabel = _generatedArrowObject.AddComponent<TextMeshProUGUI>();
            _arrowLabel.raycastTarget = false;
            _arrowLabel.alignment = TextAlignmentOptions.Center;
            _arrowLabel.textWrappingMode = TextWrappingModes.NoWrap;

            TextMeshProUGUI titleLabel = Descriptor?.TitleLabel;
            if (titleLabel != null)
            {
                _arrowLabel.font = titleLabel.font;
                _arrowLabel.fontSharedMaterial = titleLabel.fontSharedMaterial;
                _arrowLabel.color = titleLabel.color;
                _arrowLabel.fontSize = titleLabel.fontSize;
                _arrowLabel.fontStyle = titleLabel.fontStyle;
            }
        }

        private void ApplyCompactHeightOnce()
        {
            if (_compactHeightApplied)
            {
                return;
            }

            _compactHeightApplied = true;

            LayoutElement[] layouts = GetComponentsInChildren<LayoutElement>(true);
            for (int i = 0; i < layouts.Length; i++)
            {
                ScaleHeight(layouts[i]);
            }

            ScaleRectHeight(rectTransform);
            ScaleRectHeight(Descriptor?.Header);
        }

        private static void ScaleHeight(LayoutElement layout)
        {
            if (layout == null)
            {
                return;
            }

            if (layout.minHeight > 0f)
            {
                layout.minHeight *= CompactHeightScale;
            }

            if (layout.preferredHeight > 0f && layout.preferredHeight < MaxPreferredHeightThreshold)
            {
                layout.preferredHeight *= CompactHeightScale;
            }
        }

        private static void ScaleRectHeight(RectTransform rectTransform)
        {
            if (rectTransform == null || rectTransform.sizeDelta.y <= 0f)
            {
                return;
            }

            Vector2 size = rectTransform.sizeDelta;
            size.y *= CompactHeightScale;
            rectTransform.sizeDelta = size;
        }

        private void UnregisterContentMarker(PanelSectionContentMarker marker)
        {
            if (marker != null)
            {
                _contentMarkers.Remove(marker);
            }
        }

        private void ClearRegisteredContentMarkers()
        {
            for (int i = 0; i < _contentMarkers.Count; i++)
            {
                PanelSectionContentMarker marker = _contentMarkers[i];
                if (marker != null && marker.Owner == this)
                {
                    marker.Owner = null;
                }
            }

            _contentMarkers.Clear();
            _resetProbeFrame = -1;
        }

        internal Image GetDividerSourceImage()
        {
            return (Background as Image) ?? (ToggleComponent?.targetGraphic as Image);
        }

        private static void DestroyGeneratedObject(ref GameObject generatedObject)
        {
            if (generatedObject != null)
            {
                PanelSectionDividerManager.ClearEditorSelectionIfTargeting(generatedObject);
                UnityEngine.Object.Destroy(generatedObject);
            }

            generatedObject = null;
        }

        private RectTransform ResolveArrowParent()
        {
            if (Background != null && Background.transform.parent is RectTransform backgroundParent)
            {
                return backgroundParent;
            }

            if (ToggleVisual != null)
            {
                Transform knobParent = ToggleVisual.parent;
                if (knobParent != null && knobParent.parent is RectTransform switchParent)
                {
                    return switchParent;
                }

                if (knobParent is RectTransform directParent)
                {
                    return directParent;
                }
            }

            return rectTransform;
        }

        private void UpdateArrow()
        {
            if (_arrowLabel != null)
            {
                _arrowLabel.SetText(Expanded ? ExpandedArrow : CollapsedArrow);
            }
        }
    }

    internal sealed class PanelSectionToggleMarker : MonoBehaviour
    {
        public PanelSectionToggle Toggle;
    }

    internal sealed class PanelSectionContentMarker : MonoBehaviour
    {
        public PanelSectionToggle Owner;
    }

    internal sealed class PanelSectionBreaklineMarker : MonoBehaviour
    {
    }
}
