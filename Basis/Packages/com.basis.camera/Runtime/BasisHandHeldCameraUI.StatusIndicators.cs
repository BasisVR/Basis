using UnityEngine;
using UnityEngine.UI;

public partial class BasisHandHeldCameraUI
{
    private const string StatusIndicatorName = "BasisToggleStatusIndicator";
    private const float StatusIndicatorHeight = 22f;

    private static readonly Color StatusOnColor = new Color(0.22f, 0.82f, 0.34f, 1f);
    private static readonly Color StatusOffColor = new Color(0.86f, 0.24f, 0.24f, 1f);

    private bool TryGetToggleState(BasisCameraButtonAction action, out bool isOn)
    {
        isOn = false;
        if (HHC == null)
            return false;

        switch (action)
        {
            case BasisCameraButtonAction.ToggleSelfie:
                isOn = selfieBool;
                return true;
            default:
                return false;
        }
    }

    private void SetupToggleIndicator(BasisCameraButtonDescriptor descriptor)
    {
        if (descriptor == null || descriptor.button == null)
            return;

        if (!TryGetToggleState(descriptor.action, out _))
            return;

        descriptor.statusIndicator = GetOrCreateStatusIndicator(descriptor.button);
        descriptor.button.onClick.AddListener(() => RefreshToggleIndicator(descriptor));
        RefreshToggleIndicator(descriptor);
    }

    private void RefreshToggleIndicator(BasisCameraButtonDescriptor descriptor)
    {
        if (descriptor == null || descriptor.statusIndicator == null)
            return;

        if (!TryGetToggleState(descriptor.action, out bool isOn))
            return;

        descriptor.statusIndicator.color = isOn ? StatusOnColor : StatusOffColor;
    }

    private void RefreshAllToggleIndicators()
    {
        if (ScriptableButtons == null)
            return;

        foreach (var descriptor in ScriptableButtons)
            RefreshToggleIndicator(descriptor);
    }

    private static Image GetOrCreateStatusIndicator(Button button)
    {
        Transform existing = button.transform.Find(StatusIndicatorName);
        if (existing != null && existing.TryGetComponent(out Image existingImage))
            return existingImage;

        var go = new GameObject(StatusIndicatorName, typeof(RectTransform), typeof(Image));
        go.layer = button.gameObject.layer;

        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(button.transform, false);
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(0f, StatusIndicatorHeight);
        rt.anchoredPosition = Vector2.zero;

        var image = go.GetComponent<Image>();
        image.raycastTarget = false;
        return image;
    }
}
