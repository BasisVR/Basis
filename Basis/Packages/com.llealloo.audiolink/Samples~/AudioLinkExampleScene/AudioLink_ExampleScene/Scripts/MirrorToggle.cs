
using UnityEngine;
#if VRC_SDK_VRCSDK2 || VRC_SDK_VRCSDK3
using VRC.SDKBase;
#endif

#if UDON
using UdonSharp;
using VRC.Udon;

namespace AudioLink
{
    public class MirrorToggle : UdonSharpBehaviour
    {

        public GameObject mirror;

        void Start()
        {
            
        }

        public override void Interact()
        {
            bool toggle = !mirror.activeSelf;
            mirror.SetActive(toggle);
            InteractionText = "Mirror is " + (string)((toggle == true) ? "ON" : "OFF") + " (local)";
        }

    }
}

#else
namespace AudioLink
{
    public class MirrorToggle : MonoBehaviour { }
}
#endif