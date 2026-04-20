using Basis.Scripts.UI;
using UnityEngine;

namespace Basis.BasisUI
{
    public class BasisMenuInstance : AddressableInstanceBase
    {

        public static class Styles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Basis Menu Instance.prefab";
        }

        public Transform PanelRoot;

        public static BasisMenuInstance CreateNew()
        {
            return AddressableInstanceBase.CreateNew<BasisMenuInstance>(Styles.Default);
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            BasisUINeedsVisibleTrackers.Instance.Add(this);
        }

        public override void OnReleaseEvent()
        {
            base.OnReleaseEvent();
            BasisUINeedsVisibleTrackers.Instance.Remove(this);
        }
    }
}
