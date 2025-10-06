using UnityEngine;

namespace Basis.VowganUI
{
    public class BasisMenuInstance : AddressableInstanceBase
    {

        public static class Styles
        {
            public static string Default => "VowganUI/Menu";
        }

        public Transform PanelRoot;

        public static BasisMenuInstance CreateNew()
        {
            return AddressableInstanceBase.CreateNew<BasisMenuInstance>(Styles.Default);
        }

    }
}
