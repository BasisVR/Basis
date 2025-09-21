using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Basis.VowganUI
{
    public class MenuPanelManager : MonoBehaviour
    {

        public const string REFERENCE_HOME_ROW = "Panel/HomeRow";
        public static PanelDataObject HomeRowAsset;
        public static MenuPanelGroup HomeRowInstance;


        public static void ToggleHomeMenu()
        {
            if (HomeRowInstance)
            {
                HomeRowInstance.Release();
            }
            else
            {
                CreateHomeRowMenu();
            }
        }

        private static void CreateHomeRowMenu()
        {
            if (!HomeRowAsset)
            {
                HomeRowAsset = Addressables.LoadAssetAsync<PanelDataObject>(REFERENCE_HOME_ROW).WaitForCompletion();
            }
            HomeRowInstance = MenuPanelGroup.CreateNew();
            HomeRowInstance.CreatePanelInGroup(HomeRowAsset.Data);
        }
    }
}
