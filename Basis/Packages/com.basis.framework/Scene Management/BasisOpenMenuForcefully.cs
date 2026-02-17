using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using UnityEngine;

public class BasisOpenMenuForcefully : MonoBehaviour
{
    public bool OpenServerMenu = true;
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
        BasisMainMenu.Open();
        if (OpenServerMenu)
        {
            int count = BasisMainMenu.Providers.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisMenuActionProvider<BasisMainMenu> provider = BasisMainMenu.Providers[Index];
                if (provider.Title == ServersProvider.TitleStatic)
                {
                    provider.RunAction();
                }
            }
        }
    }
}
