using System;
using System.Collections.Generic;
using TMPro;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{

    [RequireComponent(typeof(LayoutElement))]
    public class PanelElementDescriptor : AddressableUIInstanceBase
    {

        public static RectTransform BuildActionRow(RectTransform parent, string name)
        {
            GameObject rowGO = new GameObject(name, typeof(RectTransform));
            RectTransform rowRect = (RectTransform)rowGO.transform;
            rowRect.SetParent(parent, false);

            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.pivot = new Vector2(0.5f, 1f);

            HorizontalLayoutGroup hlg = rowGO.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = false;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.spacing = 8f;
            hlg.padding = new RectOffset(8, 8, 4, 8);

            ContentSizeFitter fitter = rowGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            LayoutElement layout = rowGO.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;

            return rowRect;
        }

        public static class ElementStyles
        {
            public static string ScrollViewGrid => "Packages/com.basis.sdk/Prefabs/Panel Elements/Scroll View Vertical - Grid Variant.prefab";
            public static string ScrollViewGridLibrary => "Packages/com.basis.sdk/Prefabs/Panel Elements/Scroll View Vertical - Grid Variant For Library.prefab";
            public static string ScrollViewVertical =>
                "Packages/com.basis.sdk/Prefabs/Panel Elements/Scroll View Vertical.prefab";
            public static string ScrollViewVerticalLibrary => "Packages/com.basis.sdk/Prefabs/Panel Elements/Scroll View Vertical For Library Variant.prefab";
            public static string ScrollViewVerticalLibraryParentContentSize => "Packages/com.basis.sdk/Prefabs/Panel Elements/Scroll View Vertical For Library Variant Use Parent For Content Size.prefab";
            public static string ScrollViewHorizontal =>
                "Packages/com.basis.sdk/Prefabs/Panel Elements/Scroll View Horizontal.prefab";
            public static string Group =>
                "Packages/com.basis.sdk/Prefabs/Panel Elements/Panel Element Base.prefab";

            public static string Entry =>
                "Packages/com.basis.sdk/Prefabs/Panel Elements/Panel Element Base - Entry Variant.prefab";

            public static string Overlay => "Panel Elements/Overlay Panel.prefab";
            public static string OverlayLessOpacity => "Packages/com.basis.sdk/Prefabs/Panel Elements/Overlay Panel - Less Opacity Variant.prefab";

            public static string BaseOverlay => "Packages/com.basis.sdk/Prefabs/Panel Elements/Panel Element Base - Overlay.prefab";
            public static string LibraryEntryOverlay => "Packages/com.basis.sdk/Prefabs/Panel Elements/Panel Element Base - Overlay For Library Variant.prefab";
            public static string GroupLargeIcon => "Packages/com.basis.sdk/Prefabs/Panel Elements/Panel Element Base Icon.prefab";
            public static string GroupLargeIconVertical => "Packages/com.basis.sdk/Prefabs/Panel Elements/Panel Element Base Icon Vertical Stacked Content Variant.prefab";


            public static string GroupLargeIconHorizontol => "Packages/com.basis.sdk/Prefabs/Panel Elements/Panel Element Base Icon Horizontal Stacked Content Variant.prefab";
        }

        public static PanelElementDescriptor CreateNew(string style, Component parent) =>
            CreateNew<PanelElementDescriptor>(style, parent);



        [Header("Visuals")]
        [SerializeField] private bool _clearOnAwake;
        [SerializeField] private bool _useDefaultIconForNull;
        [field:SerializeField] public Sprite DefaultIcon { get; private set; }
        [field:SerializeField] public Texture2D DefaultTexture { get; private set; }
        [field:SerializeField] public string DefaultTitle { get; private set; }
        [field:SerializeField] public string DefaultDescription { get; private set; }

        [field: Header("References")]
        [field: SerializeField] public Image ElementBaseImage;
        [field:SerializeField] public Image IconImage { get; private set; }
        [field:SerializeField] public RawImage TextureImage { get; private set; }
        [field:SerializeField] public GameObject IconBackground { get; private set; }
        [field:SerializeField] public TextMeshProUGUI TitleLabel { get; private set; }
        [field:SerializeField] public TextMeshProUGUI DescriptionLabel { get; private set; }

        [field: SerializeField] public RectTransform Header { get; private set; }

        public bool HasElementBaseImage => ElementBaseImage;
        public bool HasIcon => IconImage;
        public bool HasTexture => TextureImage;
        public bool HasTitle => TitleLabel;
        public bool HasDescription => DescriptionLabel;
        public string Description => _description;
        public string Title => _title;
        public string Tooltip => _tooltip;
        public bool HasHeader => Header;
        public RectTransform ContentParent
        {
            get
            {
                // If a custom content parent hasn't been assigned, just use itself.
                if (!_contentParent) _contentParent = rectTransform;
                if (!_contentParent) return null;
                // If the content parent is needed, turn it on.
                // We leave this off by default to better line up out canvas layouts.
                _contentParent.gameObject.SetActive(true);
                return _contentParent;
            }
            set => _contentParent = value;
        }

        [SerializeField] private RectTransform _contentParent;

        protected Sprite _iconSprite;
        protected Texture2D _textureImage;
        protected string _title;
        protected string _description;
        protected string _tooltip;
        private bool _titleSet;
        private bool _descriptionSet;
        private bool _iconSet;

        protected bool _iconIsAddressable;


        public LayoutElement Layout
        {
            get
            {
                if (!_layout) TryGetComponent(out _layout);
                return _layout;
            }
        }
        private LayoutElement _layout;


        protected override void Awake()
        {
            base.Awake();

            // Title/Description labels are display-only across every panel element —
            // stripping raycastTarget removes them from GraphicRaycaster's per-pointer
            // hit test and shrinks ClipperRegistry's per-frame work. Descriptive
            // graphics (icon, background) are almost never interactive either, but
            // we leave those alone because PanelButton uses the root background as
            // its click target on some variants.
            if (TitleLabel != null) TitleLabel.raycastTarget = false;
            if (DescriptionLabel != null) DescriptionLabel.raycastTarget = false;

            // If no background has been manually assigned for an existing icon, assign itself.
            if (IconImage && !IconBackground) IconBackground = IconImage.gameObject;
            if (_clearOnAwake)
            {
                if (!_iconSet) SetIcon((Sprite)null);
                if (!_titleSet) SetTitle(string.Empty);
                if (!_descriptionSet) SetDescription(string.Empty);
            }
            else
            {
                if (!_iconSet) SetIcon(DefaultIcon);
                if (!_titleSet) SetTitle(DefaultTitle);
                if (!_descriptionSet) SetDescription(DefaultDescription);
            }
        }

        public override void OnReleaseEvent()
        {
            base.OnReleaseEvent();
        }

        public void SetIcon(Sprite value)
        {
            if (!HasIcon) return;
            // Disable the object if the sprite is null.

            if (!value && _useDefaultIconForNull)
            {
                value = DefaultIcon;
            }

            _iconSet = true;
            _iconSprite = value;
            IconBackground.gameObject.SetActive(value);
            IconImage.enabled = value;
            IconImage.sprite = value;
        }

        public void SetTexture(Texture2D value)
        {
            if (!HasTexture) return;
            // Disable the object if the texture is null.
            _textureImage = value;
            TextureImage.gameObject.SetActive(value);
            TextureImage.texture = value;
        }

        public void SetIcon(string spriteAddress)
        {
            if (!HasIcon) return;
            if (string.IsNullOrEmpty(spriteAddress)) return;
            _iconIsAddressable = true;
            SetIcon(AddressableAssets.GetSprite(spriteAddress));
        }

        public void SetTitle(string value)
        {
            if (!HasTitle) return;
            // Skip redraw if the text hasn't actually changed — polling updaters
            // call this every tick and TMP's setter unconditionally triggers a rebuild.
            if (_titleSet && string.Equals(_title, value)) return;
            _title = value;
            _titleSet = true;
            TitleLabel.gameObject.SetActive(!string.IsNullOrEmpty(value));
            TitleLabel.SetText(value);
        }

        public void SetDescription(string value)
        {
            if (!HasDescription) return;
            if (_descriptionSet && string.Equals(_description, value)) return;
            _description = value;
            _descriptionSet = true;
            DescriptionLabel.gameObject.SetActive(!string.IsNullOrEmpty(value));
            DescriptionLabel.SetText(value);
        }

        /// <summary>
        /// Sets the hover-tooltip text for this element. Unlike SetDescription, this is not
        /// shown inline on the panel — it only appears in the tooltip bar on hover.
        /// </summary>
        public void SetTooltip(string value)
        {
            _tooltip = value;
        }

        /// <summary>
        /// Sets the description with rich-text parsing left enabled, for trusted
        /// code-built markup (colors, sizes, b/i). Only pass strings assembled in
        /// code — never raw user or remote text — and wrap any interpolated payload
        /// in &lt;noparse&gt; so its angle brackets aren't read as tags.
        /// </summary>
        public void SetRichDescription(string value)
        {
            if (!HasDescription) return;
            _description = value;
            _descriptionSet = true;
            DescriptionLabel.gameObject.SetActive(!string.IsNullOrEmpty(value));
            DescriptionLabel.richText = true;
            DescriptionLabel.SetText(value);
        }

        /// <summary>
        /// Disables rich-text parsing on Title and Description labels. Use for fields
        /// that only display plain strings/numbers — TMP skips tag scanning entirely,
        /// which is a big win on polling-heavy panels (stats, bandwidth, buffers).
        /// </summary>
        public void DisableRichText()
        {
            if (HasTitle) TitleLabel.richText = false;
            if (HasDescription) DescriptionLabel.richText = false;
        }

        private bool _layoutFrozen;

        /// <summary>
        /// Freezes this descriptor's layout size so future text changes don't cascade
        /// layout rebuilds up through parent LayoutGroups. Works by:
        ///  1. Running ForceRebuildLayoutImmediate so the current natural size is captured.
        ///  2. Disabling every ContentSizeFitter in the subtree — each one would otherwise
        ///     recompute on every text change and re-dirty parents.
        ///  3. Pinning a LayoutElement.preferredHeight on the root so parent LayoutGroups
        ///     see a stable value (max of current natural height and minHeight).
        /// Call this AFTER the descriptor has been populated with its initial content.
        /// Pass a <paramref name="minHeight"/> large enough to fit future content — once
        /// frozen, if text grows beyond this the overflow will be clipped.
        /// </summary>
        public void FreezeLayoutSize(float minHeight = 0f)
        {
            if (_layoutFrozen) return;
            _layoutFrozen = true;

            // Settle current content so the natural height we capture is real.
            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);

            GetComponentsInChildren(true, _frozenFitters);
            for (int i = 0; i < _frozenFitters.Count; i++)
            {
                _frozenFitters[i].enabled = false;
            }
            _frozenFitters.Clear();

            if (!TryGetComponent(out LayoutElement le))
            {
                le = gameObject.AddComponent<LayoutElement>();
            }
            float height = Mathf.Max(rectTransform.rect.height, minHeight);
            le.preferredHeight = height;
            le.minHeight = height;
        }

        // Reused buffer so FreezeLayoutSize doesn't allocate per call.
        private static readonly List<ContentSizeFitter> _frozenFitters = new List<ContentSizeFitter>();

        public void SetActive(bool value)
        {
            gameObject.SetActive(value);
        }

        /// <summary>
        /// Shows or hides this element's card background. Turn it off on a group that only wraps
        /// other cards, so the translucent panel fill is not composited twice.
        /// </summary>
        public void SetBackgroundVisible(bool value)
        {
            if (ElementBaseImage != null)
            {
                ElementBaseImage.enabled = value;
            }
        }

        public void SetAnchorPosition(Vector2 pos)
        {
            rectTransform.anchoredPosition = pos;
        }

        public void SetPivot(Vector2 pos)
        {
            rectTransform.pivot = pos;
        }

        public void SetSize(Vector2 size)
        {
            rectTransform.sizeDelta = size;

            Layout.minWidth = size.x;
            Layout.minHeight = size.y;
            Layout.preferredWidth = size.x;
            Layout.preferredHeight = size.y;
        }
        public void SetSizeOfHeader(Vector2 size)
        {
            Header.sizeDelta = size;

            if (Header.TryGetComponent<LayoutElement>(out LayoutElement Layout))
            {
                Layout.minWidth = size.x;
                Layout.minHeight = size.y;
                Layout.preferredWidth = size.x;
                Layout.preferredHeight = size.y;
            }
        }
        public void SetSizeOfImage(Vector2 size)
        {
            if (IconImage != null)
            {
                IconImage.rectTransform.sizeDelta = size;

                if (IconImage.TryGetComponent<LayoutElement>(out LayoutElement Layout))
                {
                    Layout.minWidth = size.x;
                    Layout.minHeight = size.y;
                    Layout.preferredWidth = size.x;
                    Layout.preferredHeight = size.y;
                }
            }
        }
        public void SetSizeOfBackgroundImage(Vector2 size)
        {
            if (IconBackground != null)
            {
                if (IconBackground.TryGetComponent<LayoutElement>(out LayoutElement Layout))
                {
                    Layout.minWidth = size.x;
                    Layout.minHeight = size.y;
                    Layout.preferredWidth = size.x;
                    Layout.preferredHeight = size.y;
                }
            }
        }
        public void SetHeight(float height) => SetSize(new Vector2(rectTransform.sizeDelta.x, height));
        
        // dang this might of caused you guys some headache, fixed it.
        public void SetWidth(float width) => SetSize(new Vector2(width, rectTransform.sizeDelta.y)); 

        public void ForceRebuild()
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
        }

        /// <summary>
        /// Promotes this descriptor's subtree onto its own nested <see cref="Canvas"/> so geometry
        /// changes inside it (live stats, meters) only re-batch this group, not the whole open menu.
        /// Pair with <see cref="FreezeLayoutSize"/> on the live fields: freeze stops the per-tick
        /// reflow, the nested canvas stops the per-tick batch rebuild reaching the root canvas.
        /// Inherits the parent canvas's shader channels so TMP renders identically; Basis input is
        /// collider + per-canvas GraphicRegistry based, so nested children stay interactive without
        /// an extra GraphicRaycaster.
        /// </summary>
        public void IsolateAsCanvas()
        {
            if (TryGetComponent(out Canvas _)) return;

            Canvas parentCanvas = GetComponentInParent<Canvas>();
            Canvas canvas = gameObject.AddComponent<Canvas>();
            if (parentCanvas != null)
            {
                canvas.additionalShaderChannels = parentCanvas.additionalShaderChannels;
            }
        }
#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            if (Application.isPlaying) return;

            if (HasTitle && TitleLabel.text != DefaultTitle)
            {
                Undo.RecordObject(TitleLabel, $"Assigned default Title to {TitleLabel.gameObject.name}: {DefaultTitle}");
                TitleLabel.text = DefaultTitle;
            }

            if(HasElementBaseImage == false)
            {
                if (TryGetComponent(out ElementBaseImage))
                {

                }
            }
            if (HasIcon && IconImage.sprite != DefaultIcon)
            {
                Undo.RecordObject(IconImage, $"Assigned default Icon to {IconImage.gameObject.name}: {DefaultIcon}");
                IconImage.sprite = DefaultIcon;
            }

            if (HasTexture && TextureImage.texture != DefaultTexture)
            {
                Undo.RecordObject(TextureImage, $"Assigned default Texture to {TextureImage.gameObject.name}: {DefaultTexture}");
                TextureImage.texture = DefaultTexture;
            }

            if (HasDescription && DescriptionLabel.text != DefaultDescription)
            {
                Undo.RecordObject(DescriptionLabel, $"Assigned default Description to {DescriptionLabel.gameObject.name}: {DefaultDescription}");
                DescriptionLabel.text = DefaultDescription;
            }
        }
#endif
    }
}
