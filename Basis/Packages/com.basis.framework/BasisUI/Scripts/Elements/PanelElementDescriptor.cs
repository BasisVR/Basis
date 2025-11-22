using System;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{

    [RequireComponent(typeof(LayoutElement))]
    public class PanelElementDescriptor : AddressableUIInstanceBase
    {

        public static class ElementStyles
        {
            public static string ScrollViewVertical =>
                "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/Scroll View Vertical.prefab";
            public static string ScrollViewHorizontal =>
                "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/Scroll View Horizontal.prefab";
            public static string Group =>
                "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/Panel Element Base.prefab";
        }

        public static PanelElementDescriptor CreateNew(string style, Component parent) =>
            CreateNew<PanelElementDescriptor>(style, parent);



        [Header("Visuals")]
        [SerializeField] private bool _clearOnAwake;
        [field:SerializeField] public Sprite DefaultIcon { get; private set; }
        [field:SerializeField] public string DefaultTitle { get; private set; }
        [field:SerializeField] public string DefaultDescription { get; private set; }

        [field:Header("References")]
        [field:SerializeField] public Image IconImage { get; private set; }
        [field:SerializeField] public GameObject IconBackground { get; private set; }
        [field:SerializeField] public TextMeshProUGUI TitleLabel { get; private set; }
        [field:SerializeField] public TextMeshProUGUI DescriptionLabel { get; private set; }

        public bool HasIcon => IconImage;
        public bool HasTitle => TitleLabel;
        public bool HasDescription => DescriptionLabel;

        public RectTransform ContentParent
        {
            get
            {
                // If a custom content parent hasn't been assigned, just use itself.
                if (!_contentParent) _contentParent = rectTransform;
                // If the content parent is needed, turn it on.
                // We leave this off by default to better line up out canvas layouts.
                _contentParent.gameObject.SetActive(true);
                return _contentParent;
            }
            set => _contentParent = value;
        }

        [SerializeField] private RectTransform _contentParent;


        public LayoutElement Layout
        {
            get
            {
                if (!_layout) _layout = GetComponent<LayoutElement>();
                return _layout;
            }
        }
        private LayoutElement _layout;


        protected override void Awake()
        {
            base.Awake();

            // If no background has been manually assigned for an existing icon, assign itself.
            if (IconImage && !IconBackground) IconBackground = IconImage.gameObject;
            if (_clearOnAwake)
            {
                SetIcon(null);
                SetTitle(string.Empty);
                SetDescription(string.Empty);
            }
            else
            {
                SetIcon(DefaultIcon);
                SetTitle(DefaultTitle);
                SetDescription(DefaultDescription);
            }
        }

        public void SetIcon(Sprite value)
        {
            if (!HasIcon) return;
            // Disable the object if the sprite is null.
            IconBackground.gameObject.SetActive(value);
            IconImage.sprite = value;
        }

        public void SetTitle(string value)
        {
            if (!HasTitle) return;
            bool titleIsValid = !string.IsNullOrEmpty(value);
            // Disable the object if the title is empty.
            TitleLabel.gameObject.SetActive(titleIsValid);
            TitleLabel.text = value;
        }

        public void SetDescription(string value)
        {
            if (!HasDescription) return;
            bool descriptionIsValid = !string.IsNullOrEmpty(value);
            // Disable the object if the description is empty.
            DescriptionLabel.gameObject.SetActive(descriptionIsValid);
            DescriptionLabel.text = value;
        }

        public void SetActive(bool value)
        {
            gameObject.SetActive(value);
        }

        public void ForceRebuild()
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            if (Application.isPlaying) return;

            if (HasTitle && TitleLabel.text != DefaultTitle)
            {
                Undo.RecordObject(TitleLabel, $"Assigned default Title to {TitleLabel.gameObject.name}: {DefaultTitle}");
                TitleLabel.text = DefaultTitle;
            }

            if (HasIcon && IconImage.sprite != DefaultIcon)
            {
                Undo.RecordObject(IconImage, $"Assigned default Icon to {IconImage.gameObject.name}: {DefaultIcon}");
                IconImage.sprite = DefaultIcon;
            }

            if (HasDescription && DescriptionLabel.text != DefaultDescription)
            {
                Undo.RecordObject(DescriptionLabel, $"Assigned default Description to {DescriptionLabel.gameObject.name}: {DefaultDescription}");
                DescriptionLabel.text = DefaultDescription;
            }
        }
    }
}
