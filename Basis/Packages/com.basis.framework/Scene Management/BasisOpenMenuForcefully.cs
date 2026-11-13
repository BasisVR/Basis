using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using UnityEngine;

public class BasisOpenMenuForcefully : MonoBehaviour
{
    public bool OpenServerMenu = true;
    public string ProviderTitleKey = "menu.provider.servers";
    public void Start()
    {
        if(BasisDeviceManagement.OnInitializationComplete)
        {
            OpenMenu();
        }
        else
        {
            BasisDeviceManagement.OnInitializationCompleted += OpenMenu;
        }
    }
    public void OnDestroy()
    {
        BasisDeviceManagement.OnInitializationCompleted -= OpenMenu;
    }
    public void OpenMenu()
    {
        BasisStartupPrompts.Run(OpenServerMenuAfterPrompts);
    }
    private void OpenServerMenuAfterPrompts()
    {
        if (this == null || BasisConnectionService.HasBootstrapConnection) return;
        BasisMainMenu.Open();
        if (OpenServerMenu)
        {
            BasisMainMenu.OpenWithProvider(BasisLocalization.Get(ProviderTitleKey));
        }
        else
        {
            BasisMainMenu.Open();
        }
    }
}
