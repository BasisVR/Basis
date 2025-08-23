using Basis.Scripts.Drivers;
using System;
using System.Text.RegularExpressions;
using UnityEngine;
namespace Basis.Scripts.BasisSdk.Players
{
    public abstract class BasisPlayer : MonoBehaviour
    {
        public bool IsLocal { get; set; }
        public RuntimePlatform GetRuntimePlatform()
        {
            if(IsLocal)
            {
                return Application.platform;
            }
            else
            {
                BasisDebug.LogError ("this is not implemented talk with the creators of basis");
                return RuntimePlatform.WindowsPlayer;
            }
        }

        public string DisplayName;
        public string UUID;
        public string SafeDisplayName;
        public BasisAvatar BasisAvatar;
        public Transform AvatarTransform;
        public Transform PlayerSelf;//yes caching myself is faster.
        // public event Action OnMetaDataUpdated;
        public event Action OnAvatarSwitched;
        public event Action OnAvatarSwitchedFallBack;
        public BasisProgressReport ProgressReportAvatarLoad = new BasisProgressReport();
        public const byte LoadModeNetworkDownloadable = 0;
        public const byte LoadModeLocal = 1;
        public const byte LoadModeError = 2;
        public bool FaceIsVisible;
        public BasisMeshRendererCheck FaceRenderer;
        public BasisProgressReport AvatarProgress = new BasisProgressReport();
        public Action<bool> AudioReceived;
        public delegate void SimulationHandler();
        public SimulationHandler OnPreSimulateBones;
        public bool IsConsideredFallBackAvatar = true;
        public byte AvatarLoadMode;//0 downloading 1 local
        [HideInInspector]
        public BasisLoadableBundle AvatarMetaData;
        [Header("Blink Driver")]
        [SerializeField]
        public BasisFacialBlinkDriver FacialBlinkDriver = new BasisFacialBlinkDriver();

        public void SetSafeDisplayname()
        {
            // Regex pattern to match any <...> tags
            SafeDisplayName = Regex.Replace(DisplayName, "<.*?>", string.Empty);
        }
        public void UpdateFaceVisibility(bool State)
        {
            FaceIsVisible = State;
        }
        public void AvatarSwitchedFallBack()
        {
            OnAvatarSwitchedFallBack?.Invoke();
        }
        public void AvatarSwitched()
        {
            OnAvatarSwitched?.Invoke();
        }
    }
}
