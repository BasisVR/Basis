using Basis.Scripts.UI.UI_Panels;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using static Basis.BasisUI.PanelButton;

namespace Basis.BasisUI
{
    public class LibraryProviderDialogItemDetails
    {
        #region ShowItemDetails

        /// <summary>
        /// Size pinned on the scrolling page. The dialog's own panel grows to fit its children, so a
        /// page left to size itself off this much content pushes the dialog past the menu panel in
        /// both directions. Pinned here and clipped by <see cref="ClampScrollViewport"/>, the list
        /// stays inside the dialog and scrolls instead.
        /// </summary>
        private const float PageHeight = 620f;
        private const float PageWidth = 1100f;

        /// <summary>
        /// Shows everything the bee file records about one library item — description fields, build
        /// metrics, bounds, the harvested component counts and every platform section it carries.
        /// Resolves when the user closes it.
        /// </summary>
        public static async Task ShowItemDetails(BasisMenuPanel panel, BasisDataStoreItemKeys.ItemKey item, CachedMetaData.CachedContent metadata)
        {
            BasisBundleConnector connector = metadata?.BasisBundleConnector;

            // An embedded item's url IS its addressable name and is what the overlay titles it with;
            // for everything else the url is the one thing this panel must never put on screen.
            string name = connector?.BasisBundleDescription?.AssetBundleName;
            if (string.IsNullOrWhiteSpace(name)) name = metadata?.Name;
            if (string.IsNullOrWhiteSpace(name) && item.EmbeddedSettings.IsEmbedded) name = item.Url;
            if (string.IsNullOrWhiteSpace(name)) name = BasisLocalization.Get("library.notAvailable");

            DialogBox<bool> dialog = DialogBox<bool>.Create(panel, new Vector2(1200, 900),
                name,
                BasisLocalization.Get("library.dialog.details.body"),
                AddressableAssets.Sprites.Information);

            if (dialog.Descriptor == null)
            {
                await dialog.WaitAsync();
                return;
            }

            PanelButton exitButton = PanelButton.CreateNew(ButtonStyles.ExitButton, dialog.Descriptor.Header);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            exitButton.OnClicked += () => dialog.Cancel(false);

            PanelTabPage page = PanelTabPage.CreateVertical(dialog.Descriptor.ContentParent);
            page.Descriptor.SetSize(new Vector2(PageWidth, PageHeight));

            RectTransform content = page.Descriptor.ContentParent;
            ClampScrollViewport(content);

            BuildContentSection(content, item, connector, name);

            if (connector == null)
            {
                Section(content,
                    BasisLocalization.Get("library.dialog.details.section.metrics"),
                    AddressableAssets.Sprites.Polygons,
                    BasisLocalization.Get("library.dialog.details.noMetadata"));
            }
            else
            {
                BuildAuthorshipSection(content, connector);
                BuildMetricsSection(content, connector);
                BuildBoundsSection(content, connector);
                BuildComponentsSection(content, connector);
                BuildPropSpawnSection(content, connector);
                BuildPlatformSections(content, connector);
            }

            dialog.Descriptor.ForceRebuild();

            await dialog.WaitAsync();
        }

        #endregion

        #region Sections

        private static void BuildContentSection(RectTransform parent, BasisDataStoreItemKeys.ItemKey item, BasisBundleConnector connector, string name)
        {
            PanelElementDescriptor section = Section(parent,
                BasisLocalization.Get("library.dialog.details.section.content"),
                AddressableAssets.Sprites.Information);

            BasisBundleDescription description = connector?.BasisBundleDescription;

            Row(section, BasisLocalization.Get("library.dialog.details.name"), name);
            Row(section, BasisLocalization.Get("library.dialog.details.description"),
                string.IsNullOrWhiteSpace(description?.AssetBundleDescription)
                    ? BasisLocalization.Get("library.noDescription")
                    : description.AssetBundleDescription);
            Row(section, BasisLocalization.Get("library.dialog.details.contentType"), item.Mode.ToString());
            Row(section, BasisLocalization.Get("library.dialog.details.tags"),
                description?.Tags != null && description.Tags.Length > 0 ? string.Join(", ", description.Tags) : NoneText());
            Row(section, BasisLocalization.Get("library.dialog.details.contentGroup"), OrNone(description?.ContentGroupId));
            Row(section, BasisLocalization.Get("library.creationDate"), FormatCreationDate(connector?.DateOfCreation));
            Row(section, BasisLocalization.Get("library.dialog.details.uniqueVersion"), OrNone(connector?.UniqueVersion));
        }

        private static void BuildAuthorshipSection(RectTransform parent, BasisBundleConnector connector)
        {
            BasisBundleAuthorship authorship = connector.BasisBundleDescription != null ? connector.BasisBundleDescription.Authorship : default;
            if (!BasisBundleConnector.HasAuthorship(authorship)) return;

            PanelElementDescriptor section = Section(parent,
                BasisLocalization.Get("library.dialog.details.section.authorship"),
                AddressableAssets.Sprites.People,
                BasisLocalization.Get("library.dialog.details.section.authorship.description"));

            CreatorRow(section, BasisLocalization.Get("library.dialog.details.author"), authorship.Author);
            CreatorRow(section, BasisLocalization.Get("library.dialog.details.contact"), authorship.Contact);
            CreatorRow(section, BasisLocalization.Get("library.dialog.details.license"), authorship.License);
            CreatorRow(section, BasisLocalization.Get("library.dialog.details.credits"), authorship.Credits);
        }

        private static void BuildMetricsSection(RectTransform parent, BasisBundleConnector connector)
        {
            BasisBundleConnector.BasisMetaData meta = connector.MetaData;

            PanelElementDescriptor section = Section(parent,
                BasisLocalization.Get("library.dialog.details.section.metrics"),
                AddressableAssets.Sprites.Polygons);

            Row(section, BasisLocalization.Get("library.triangleCount"), meta.TrianglesCount.ToString("N0"));
            Row(section, BasisLocalization.Get("library.materialCount"), meta.MaterialCount.ToString("N0"));
            Row(section, BasisLocalization.Get("library.bonesCount"), meta.BonesCount.ToString("N0"));

            // Both of these were added to the metadata after content had already shipped, so the
            // zero/empty an older bee deserializes with means "never measured", not "none".
            Row(section, BasisLocalization.Get("library.dialog.details.textureMemory"),
                meta.TextureMemoryBytes > 0 ? BasisAvatarTextureStats.FormatBytes(meta.TextureMemoryBytes) : UnknownText());
            Row(section, BasisLocalization.Get("library.dialog.details.graphicsPipeline"), OrUnknown(meta.GraphicsPipeline));
            Row(section, BasisLocalization.Get("library.dialog.details.farLod"), YesNo(!string.IsNullOrEmpty(connector.FarLodBase64)));
        }

        private static void BuildBoundsSection(RectTransform parent, BasisBundleConnector connector)
        {
            PanelElementDescriptor section = Section(parent,
                BasisLocalization.Get("library.dialog.details.section.bounds"),
                AddressableAssets.Sprites.Items,
                BasisLocalization.Get("library.dialog.details.section.bounds.description"));

            Row(section, BasisLocalization.Get("library.dialog.details.boundsCenter"), FormatVector(connector.Bounds.center));
            Row(section, BasisLocalization.Get("library.dialog.details.boundsSize"), FormatVector(connector.Bounds.size));
            Row(section, BasisLocalization.Get("library.dialog.details.boundsExtents"), FormatVector(connector.Bounds.extents));
        }

        private static void BuildComponentsSection(RectTransform parent, BasisBundleConnector connector)
        {
            BasisBundleConnector.BasisComponentName[] components = connector.MetaData.ComponentNames;

            if (components == null || components.Length == 0)
            {
                Section(parent,
                    BasisLocalization.Get("library.dialog.details.section.components"),
                    AddressableAssets.Sprites.List,
                    BasisLocalization.Get("library.dialog.details.section.components.none"));
                return;
            }

            long total = 0;
            for (int Index = 0; Index < components.Length; Index++)
            {
                total += components[Index].count;
            }

            PanelElementDescriptor section = Section(parent,
                BasisLocalization.Get("library.dialog.details.section.components"),
                AddressableAssets.Sprites.List,
                string.Format(BasisLocalization.Get("library.dialog.details.section.components.description"),
                    components.Length.ToString("N0"), total.ToString("N0")));

            foreach (BasisBundleConnector.BasisComponentName component in components
                .OrderByDescending(entry => entry.count)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                Row(section, OrUnknown(component.Name), component.count.ToString("N0"));
            }
        }

        private static void BuildPropSpawnSection(RectTransform parent, BasisBundleConnector connector)
        {
            BasisPropSpawnMetaData propSpawn = connector.MetaData.PropSpawn;
            if (!propSpawn.IsSpecified) return;

            PanelElementDescriptor section = Section(parent,
                BasisLocalization.Get("library.dialog.details.section.propSpawn"),
                AddressableAssets.Sprites.TeleportTo,
                BasisLocalization.Get("library.dialog.details.section.propSpawn.description"));

            Row(section, BasisLocalization.Get("library.placement"), propSpawn.Placement.ToString());

            if (propSpawn.Placement == BasisPropSpawnPlacement.InHand)
            {
                Row(section, BasisLocalization.Get("library.dialog.details.hand"), propSpawn.Hand.ToString());
            }

            Row(section, BasisLocalization.Get("library.dialog.details.distance"),
                propSpawn.ResolvedDistance.ToString("0.##", CultureInfo.InvariantCulture) + " m");
            Row(section, BasisLocalization.Get("library.dialog.details.scale"),
                propSpawn.ResolvedUniformScale.ToString("0.###", CultureInfo.InvariantCulture) + "x");
            Row(section, BasisLocalization.Get("library.dialog.details.alignToSurface"), YesNo(propSpawn.AlignToSurface));
            Row(section, BasisLocalization.Get("library.dialog.details.faceThePlayer"), YesNo(propSpawn.FaceThePlayer));
        }

        private static void BuildPlatformSections(RectTransform parent, BasisBundleConnector connector)
        {
            BasisBundleGenerated[] generated = connector.BasisBundleGenerated;

            if (generated == null || generated.Length == 0)
            {
                Section(parent,
                    BasisLocalization.Get("library.availablePlatforms"),
                    AddressableAssets.Sprites.Computer,
                    NoneText());
                return;
            }

            foreach (BasisBundleGenerated bundle in generated)
            {
                if (bundle == null) continue;

                PanelElementDescriptor section = Section(parent,
                    UserListProvider.GetPlatformLabel(bundle.Platform),
                    UserListProvider.GetPlatformIconAddress(bundle.Platform),
                    BasisLocalization.Get("library.dialog.details.section.platform.description"));

                Row(section, BasisLocalization.Get("library.dialog.details.assetMode"), OrUnknown(bundle.AssetMode));
                Row(section, BasisLocalization.Get("library.dialog.details.assetName"), OrUnknown(bundle.AssetToLoadName));
                Row(section, BasisLocalization.Get("library.dialog.details.size"), BasisAvatarTextureStats.FormatBytes(bundle.EndByte));
                Row(section, BasisLocalization.Get("library.dialog.details.encrypted"), YesNo(bundle.IsEncrypted));
                Row(section, BasisLocalization.Get("library.dialog.details.crc"), bundle.AssetBundleCRC.ToString(CultureInfo.InvariantCulture));
                Row(section, BasisLocalization.Get("library.dialog.details.hash"), OrUnknown(bundle.AssetBundleHash));
                Row(section, BasisLocalization.Get("library.dialog.details.graphicsAPIs"),
                    bundle.GraphicsAPIs != null && bundle.GraphicsAPIs.Length > 0 ? string.Join(", ", bundle.GraphicsAPIs) : UnknownText());
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// The shared scroll-view prefab ships a bare, zero-anchored viewport with no mask, so
        /// content taller than the page draws straight past its bounds — a dialog has no panel-level
        /// mask to catch it. Bound the viewport to the scroll rect and mask it so the sections clip
        /// and scroll instead of spilling out of the dialog.
        /// </summary>
        private static void ClampScrollViewport(RectTransform content)
        {
            if (content == null) return;

            ScrollRect scroll = content.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.viewport == null) return;

            RectTransform viewport = scroll.viewport;
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = new Vector2(-25f, 0f); // clear of the vertical scrollbar
            if (!viewport.TryGetComponent(out RectMask2D _))
            {
                viewport.gameObject.AddComponent<RectMask2D>();
            }
        }

        private static PanelElementDescriptor Section(RectTransform parent, string title, string icon, string description = null)
        {
            PanelElementDescriptor section = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            section.SetTitle(title);
            section.SetIcon(icon);
            section.SetDescription(description ?? string.Empty);
            return section;
        }

        private static void Row(PanelElementDescriptor section, string label, string value)
        {
            PanelElementDescriptor row = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Entry, section.ContentParent);
            row.SetTitle(label);
            row.SetDescription(value);
        }

        private static void CreatorRow(PanelElementDescriptor section, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            PanelElementDescriptor row = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Entry, section.ContentParent);
            row.DisableRichText();
            row.SetTitle(label);
            row.SetDescription(value);
        }

        private static string NoneText() => BasisLocalization.Get("library.dialog.details.none");

        private static string UnknownText() => BasisLocalization.Get("ui.unknown");

        private static string OrNone(string value) => string.IsNullOrWhiteSpace(value) ? NoneText() : value;

        private static string OrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? UnknownText() : value;

        private static string YesNo(bool value) => BasisLocalization.Get(value ? "ui.yes" : "ui.no");

        private static string FormatVector(Vector3 value)
        {
            return $"X {value.x.ToString("0.###", CultureInfo.InvariantCulture)}   " +
                   $"Y {value.y.ToString("0.###", CultureInfo.InvariantCulture)}   " +
                   $"Z {value.z.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        private static string FormatCreationDate(string dateOfCreation)
        {
            if (string.IsNullOrEmpty(dateOfCreation)) return BasisLocalization.Get("library.notAvailable");

            if (!DateTime.TryParse(dateOfCreation, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
            {
                return dateOfCreation;
            }

            return parsed.ToString(CultureInfo.InvariantCulture) + " UTC";
        }

        #endregion
    }
}
