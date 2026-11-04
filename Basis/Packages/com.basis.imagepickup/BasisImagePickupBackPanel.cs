using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Builds the world-space control panel on the back of an image pickup: a spawner card plus
    /// Hide/Copy/Save/Delete buttons, styled from the active Basis UI theme through
    /// <see cref="BasisWorldPanelKit"/> so it matches the menu and follows theme changes.
    /// Built on demand by <see cref="BasisImagePickupObject.SetBackPanelVisible"/> the first time the menu
    /// opens, then reused — the returned object is the canvas root the pickup toggles.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisImagePickupBackPanel
    {
        private const float PanelPixels = 520f;
        private const float MinPanelPixelsHigh = 330f;
        private const float Margin = 22f;
        private const float ButtonHeight = 64f;
        private const float Gap = 12f;
        private const string EyeOffIconAddress = "Packages/com.basis.sdk/Textures/Runtime/eye-off-outline.png";

        public static GameObject Build(Transform parent, BasisImagePickupObject pickup, float worldWidth, float worldHeight)
        {
            worldWidth = Mathf.Max(0.01f, worldWidth);
            worldHeight = Mathf.Max(0.01f, worldHeight);
            float pixelsWide = PanelPixels;
            float scale = worldWidth / pixelsWide;
            float pixelsHigh = worldHeight / scale;
            if (pixelsHigh < MinPanelPixelsHigh)
            {
                pixelsHigh = MinPanelPixelsHigh;
                scale = worldHeight / pixelsHigh;
                pixelsWide = worldWidth / scale;
            }
            RectTransform canvasRect = BasisWorldPanelKit.CreateCanvas("BackPanel", parent, new Vector2(pixelsWide, pixelsHigh), out GameObject canvasObject);
            canvasRect.localPosition = new Vector3(0f, 0f, 0.03f);
            canvasRect.localRotation = Quaternion.Euler(0f, 180f, 0f);
            canvasRect.localScale = Vector3.one * scale;
            BasisWorldPanelKit.CreateWindow(canvasRect);

            float innerWidth = Mathf.Min(pixelsWide, PanelPixels) - 2f * Margin;
            Image card = BasisWorldPanelKit.CreateStyledImage("Spawner", canvasRect, Vector2.zero, new Vector2(innerWidth, 110f), BasisWorldPanelKit.StyleCard, new Color(0.2f, 0.19f, 0.27f, 1f));
            BasisWorldPanelKit.AnchorTop(card.rectTransform, Margin + 55f);
            pickup.SpawnerLabel = BasisWorldPanelKit.CreateLabel(card.rectTransform, Vector2.zero, new Vector2(innerWidth - 24f, 96f), BasisWorldPanelKit.StyleHeader, TextAlignmentOptions.Center);
            pickup.SpawnerLabel.textWrappingMode = TextWrappingModes.Normal;

            bool canCopy = BasisClipboard.CanCopyImages;
            float halfWidth = (innerWidth - Gap) * 0.5f;
            float leftX = -(halfWidth + Gap) * 0.5f;
            float rightX = -leftX;
            float bottomRow = Margin + ButtonHeight * 0.5f;
            float topRow = bottomRow + ButtonHeight + Gap;

            pickup.HideLabel = AddButton(canvasRect, leftX, topRow, halfWidth, BasisWorldPanelKit.AddressableIcon(EyeOffIconAddress), BasisWorldPanelKit.StyleButton, pickup.OnHidePressed);
            pickup.SaveLabel = AddButton(canvasRect, rightX, topRow, halfWidth, BasisWorldPanelKit.AddressableIcon(AddressableAssets.Sprites.FileTray), BasisWorldPanelKit.StyleButton, pickup.OnSavePressed);
            if (canCopy)
            {
                pickup.CopyLabel = AddButton(canvasRect, leftX, bottomRow, halfWidth, BasisWorldPanelKit.CopyIcon, BasisWorldPanelKit.StyleButton, pickup.OnCopyPressed);
                pickup.DeleteLabel = AddButton(canvasRect, rightX, bottomRow, halfWidth, BasisWorldPanelKit.AddressableIcon(AddressableAssets.Sprites.Trash), BasisWorldPanelKit.StyleButtonDanger, pickup.OnDeletePressed);
            }
            else
            {
                pickup.DeleteLabel = AddButton(canvasRect, 0f, bottomRow, innerWidth, BasisWorldPanelKit.AddressableIcon(AddressableAssets.Sprites.Trash), BasisWorldPanelKit.StyleButtonDanger, pickup.OnDeletePressed);
            }
            pickup.RefreshPanelLabels();

            canvasObject.SetActive(true);
            return canvasObject;
        }

        private static TextMeshProUGUI AddButton(RectTransform parent, float x, float fromBottom, float width, Sprite icon, string style, UnityAction onClick)
        {
            BasisWorldPanelKit.WorldButton button = BasisWorldPanelKit.CreateButton(parent, new Vector2(x, 0f), new Vector2(width, ButtonHeight), icon, string.Empty, style, onClick);
            BasisWorldPanelKit.AnchorBottom(button.Background.rectTransform, fromBottom);
            return button.Label;
        }
    }
}
