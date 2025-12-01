using UnityEngine;

namespace Basis.BasisUI
{
    public class BasisMenuInstance : AddressableInstanceBase
    {

        public static class Styles
        {
            public static string Default => "Packages/com.basis.framework/BasisUI/Prefabs/Basis Menu Instance.prefab";
        }

        public Transform PanelRoot;

        public static BasisMenuInstance CreateNew()
        {
            return AddressableInstanceBase.CreateNew<BasisMenuInstance>(Styles.Default);
        }

    }
}
