using Basis.Scripts.Addressable_Driver.Resource;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Basis.VowganUI
{
    public class MirrorProvider : BasisMenuActionProvider
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMainMenu.AddActionProvider(new MirrorProvider());
        }

        public override string Title => "Mirror";
        public override Sprite Icon => null;
        public override int Order => 4;


        public static bool HasMirror;
        public static BasisPersonalMirror PersonalMirrorInstance;

        public static string MirrorPath =
            "Packages/com.basis.sdk/Prefabs/UI/Personal Mirror Prefab/PersonalMirror.prefab";

        public override async void RunAction()
        {
            if (HasMirror)
            {
                HasMirror = false;
                if (PersonalMirrorInstance != null)
                {
                    AddressableResourceProcess.ReleaseGameobject(PersonalMirrorInstance.gameObject);
                    PersonalMirrorInstance = null;
                }
            }
            else
            {
                HasMirror = true;

                BasisMainMenu.Instance.MenuInstance.PanelRoot.GetPositionAndRotation(
                    out Vector3 position,
                    out Quaternion rotation);

                BasisMainMenu.Close();

                InstantiationParameters parameters = new InstantiationParameters(position, rotation, null);
                GameObject data = await AddressableResourceProcess.LoadSystemGameobject(MirrorPath, parameters);
                if (data.TryGetComponent(out PersonalMirrorInstance))
                {
                }
            }
            BasisUIManagement.CloseAllMenus();
        }
    }
}
