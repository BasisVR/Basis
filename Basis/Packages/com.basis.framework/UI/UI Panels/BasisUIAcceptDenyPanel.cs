using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Enums;
using Basis.Scripts.UI.UI_Panels;
using System;
using TMPro;
using UnityEngine.UI;

public class BasisUIAcceptDenyPanel : BasisUIBase
{
    public static string CursorRequest = "BasisUIAcceptDenyPanel";
    public const string LoadPath = "Packages/com.basis.sdk/Prefabs/UI/Accept Deny Panel.prefab";

    // UI Buttons (set these in the inspector)
    public Button YesButton;
    public Button NoButton;
    public TextMeshProUGUI Text;

    // The callback to return result
    private Action<bool> _callback;

    public override void DestroyEvent()
    {
        BasisCursorManagement.LockCursor(CursorRequest);
    }

    public override void InitalizeEvent()
    {
        BasisCursorManagement.UnlockCursor(CursorRequest);

        // Add listeners
        YesButton.onClick.AddListener(OnYesClicked);
        NoButton.onClick.AddListener(OnNoClicked);
    }

    private void OnYesClicked()
    {
        _callback?.Invoke(true);
        CloseThisMenu();
    }

    private void OnNoClicked()
    {
        _callback?.Invoke(false);
        CloseThisMenu();
    }


    public void Setup(string information, Action<bool> callback)
    {
        // Display information (not shown, but assumed to be set in UI)
        _callback = callback;
        Text.text = information;
    }

    public static void OpenAcceptDenyPanel(string information, Action<bool> callback)
    {
        AddressableGenericResource resource = new AddressableGenericResource(LoadPath, AddressableExpectedResult.SingleItem);
        BasisUIBase baseUI = OpenMenuNow(resource);
        BasisUIAcceptDenyPanel uiPanel = (BasisUIAcceptDenyPanel)baseUI;
        uiPanel.Setup(information, callback);
    }
}
