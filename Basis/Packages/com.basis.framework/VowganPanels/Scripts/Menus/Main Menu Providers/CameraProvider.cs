using Basis.Scripts.Addressable_Driver.Resource;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Basis.VowganUI
{
    public class CameraProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new CameraProvider());
        }


        public override string Title => "Camera";
        public override Sprite Icon => AddressableAssets.GetSprite(AddressableAssets.Sprites.Camera);
        public override bool IconIsAddressable => true;
        public override int Order => 4;

        public static GameObject ActiveCameraInstance;

        public static string CameraPrefabPath = "Packages/com.basis.sdk/Prefabs/UI/Player Held Camera.prefab";

        public override async void RunAction()
        {
            if (ActiveCameraInstance != null)
            {
                BasisHandHeldCameraInteractable handheldCamera = ActiveCameraInstance.GetComponent<BasisHandHeldCameraInteractable>();
                if (handheldCamera)
                    handheldCamera.ReleasePlayerLocks();

                AddressableResourceProcess.ReleaseGameobject(ActiveCameraInstance.gameObject);
                BasisDebug.Log("[OpenCamera] Destroyed previous camera instance.");
                ActiveCameraInstance = null;
            }
            else
            {
                BasisDebug.LogWarning("[OpenCamera] Tried to destroy camera, but none existed.");
            }

            BasisMainMenu.Instance.MenuObjectInstance.PanelRoot.GetPositionAndRotation(
                out Vector3 position,
                out Quaternion rotation);

            BasisMainMenu.Close();

            InstantiationParameters parameters = new(position, rotation, null);
            GameObject data = await AddressableResourceProcess.LoadSystemGameobject(CameraPrefabPath, parameters);
            if (data.TryGetComponent(out BasisHandHeldCamera cam))
            {
                ActiveCameraInstance = cam.gameObject;
            }
        }
    }
}
