using Basis.Scripts.UI;
using Basis.Scripts.UI.UI_Panels;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Builds the world-space control panel on the back of an image pickup: a spawner label plus
    /// Hide/Save/Delete buttons. Uses the Basis world-UI stack so the VR laser and desktop cursor can click it.
    /// Built on demand by <see cref="BasisImagePickupObject.SetBackPanelVisible"/> the first time the menu
    /// opens, then reused — the returned object is the canvas root the pickup toggles.
    /// </summary>
    public static class BasisImagePickupBackPanel
    {
        private const float PanelPixels = 400f;

        public static GameObject Build(Transform parent, BasisImagePickupObject pickup, float worldHeight)
        {
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0) uiLayer = 5;

            var canvasObject = new GameObject("BackPanel", typeof(RectTransform));
            canvasObject.layer = uiLayer;
            canvasObject.SetActive(false);

            var canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.SetParent(parent, false);
            canvasRect.localPosition = new Vector3(0f, 0f, 0.03f);
            canvasRect.localRotation = Quaternion.Euler(0f, 180f, 0f);
            canvasRect.sizeDelta = new Vector2(PanelPixels, PanelPixels);
            canvasRect.localScale = Vector3.one * (worldHeight / PanelPixels);

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var scaler = canvasObject.AddComponent<CanvasScaler>();

            var rayCaster = canvasObject.AddComponent<BasisGraphicUIRayCaster>();
            rayCaster.Canvas = canvas;

            var component = canvasObject.AddComponent<BasisUIComponent>();
            component.Canvas = canvas;
            component.CanvasScaler = scaler;
            component.GraphicUIRayCaster = rayCaster;

            bool localUnknown = pickup.IsOwner && (string.IsNullOrEmpty(pickup.OwnerName) || pickup.OwnerName == "Unknown");
            string spawnerLabel = localUnknown ? "Spawned locally" : $"Spawned by {pickup.OwnerName}";
            CreateLabel(canvasRect, uiLayer, spawnerLabel, new Vector2(0f, 150f), new Vector2(PanelPixels - 20f, 80f), 30f);

            pickup.HideLabel = CreateButton(canvasRect, uiLayer, pickup.IsHidden ? "Show" : "Hide", new Vector2(-130f, -120f), pickup.OnHidePressed);
            CreateButton(canvasRect, uiLayer, "Save", new Vector2(0f, -120f), pickup.OnSavePressed);
            pickup.DeleteLabel = CreateButton(canvasRect, uiLayer, "Delete", new Vector2(130f, -120f), pickup.OnDeletePressed);

            canvasObject.SetActive(true);
            return canvasObject;
        }

        private static TextMeshProUGUI CreateButton(RectTransform parent, int layer, string label, Vector2 anchoredPosition, UnityAction onClick)
        {
            var buttonObject = new GameObject(label + "Button", typeof(RectTransform));
            buttonObject.layer = layer;

            var rect = (RectTransform)buttonObject.transform;
            rect.SetParent(parent, false);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = new Vector2(120f, 80f);

            var image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.80f, 0.82f, 0.88f, 0.95f);
            image.raycastTarget = true;

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);

            return CreateLabel(rect, layer, label, Vector2.zero, new Vector2(120f, 80f), 28f);
        }

        private static TextMeshProUGUI CreateLabel(RectTransform parent, int layer, string text, Vector2 anchoredPosition, Vector2 size, float fontSize)
        {
            var labelObject = new GameObject("Label", typeof(RectTransform));
            labelObject.layer = layer;

            var rect = (RectTransform)labelObject.transform;
            rect.SetParent(parent, false);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var tmp = labelObject.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null) tmp.font = TMP_Settings.defaultFontAsset;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.black;
            tmp.raycastTarget = false;
            Shader distanceField = GetDistanceFieldShader();
            if (distanceField != null) tmp.fontMaterial.shader = distanceField;
            return tmp;
        }

        private static Shader _distanceFieldShader;

        private static Shader GetDistanceFieldShader()
        {
            if (_distanceFieldShader == null)
                _distanceFieldShader = Shader.Find("TextMeshPro/Distance Field");
            return _distanceFieldShader;
        }
    }
}
