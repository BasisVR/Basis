using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using UnityEngine;
namespace Metaphira.Modules.CameraOverride
{
    public class CameraOverrideModule : MonoBehaviour
    {
        [NonSerialized] public const int RENDER_MODE_DISABLED = 0;
        [NonSerialized] public const int RENDER_MODE_VR = 1;
        [NonSerialized] public const int RENDER_MODE_DESKTOP = 2;

        [NonSerialized] public readonly string[] DEPENDENCIES = new string[] { };
        [NonSerialized] public readonly string VERSION = "1.0.0";

        [SerializeField] public bool shouldMaintainAspectRatio;
        [SerializeField] public Vector2 aspectRatio;
        [SerializeField] public Color clearColor;

        [SerializeField] private GameObject[] bounds;

        [SerializeField] private RenderTexture internalTexture;

        private Camera referenceCamera;
        private Rect screenSize;

        private Camera targetCamera;
        private int renderMode;

        bool initialized = false;
        void Start()
        {
            _Init();
        }
        public void _Init()
        {
            if (initialized) { return; }
            initialized = true;
            referenceCamera = this.transform.Find("ReferenceCamera").GetComponent<Camera>();

            foreach (GameObject bound in bounds)
            {
                Vector3 boundPosition = bound.transform.position;
                Vector3 boundScale = bound.transform.lossyScale;
                float minScale = Mathf.Min(Mathf.Min(boundScale.x, boundScale.y), boundScale.z);

                GameObject overlay = bound.transform.GetChild(0).gameObject;
                overlay.GetComponent<MeshRenderer>().material.SetVector("_ActivationRange", new Vector4(boundPosition.x, boundPosition.y, boundPosition.z, minScale / 2));
            }
            BasisNetworkPlayer.OnPlayerJoined += OnPlayerJoined;
        }

        private void Update()
        {
            Rect currentScreenSize = referenceCamera.pixelRect;
            if (screenSize != currentScreenSize)
            {
                screenSize = currentScreenSize;

                updateCamera();
            }
        }

        public void OnPlayerJoined(BasisNetworkPlayer player)
        {
            if (!player.IsLocal) return;

            setBoundsActive(player.IsUserInVR());
        }

        private void setBoundsActive(bool active)
        {
            foreach (GameObject bound in bounds)
            {
                bound.SetActive(active);
            }
        }

        public void _SetRenderMode(int newRenderMode)
        {
            renderMode = newRenderMode;
            updateRenderMode();
            updateCamera();
        }

        public void _SetTargetCamera(Camera newCamera)
        {
            if (targetCamera != null)
            {
                targetCamera.enabled = false;
            }

            targetCamera = newCamera;
            updateRenderMode();
            updateCamera();
        }

        private void updateRenderMode()
        {
            if (renderMode == RENDER_MODE_DISABLED)
            {
                setBoundsActive(false);
            }
            else if (renderMode == RENDER_MODE_VR)
            {
                setBoundsActive(true);
            }
            else if (renderMode == RENDER_MODE_DESKTOP)
            {
                setBoundsActive(false);
            }
        }

        private void updateCamera()
        {
            if (!initialized) { return; }
            if (renderMode == RENDER_MODE_DISABLED)
            {
                if (targetCamera != null) targetCamera.enabled = false;
                // targetCamera.gameObject.SetActive(false);
                referenceCamera.enabled = false;
                // referenceCamera.gameObject.SetActive(false);
            }
            else if (renderMode == RENDER_MODE_VR)
            {
                if (targetCamera == null) return;

                targetCamera.enabled = true;
                referenceCamera.enabled = false;
                targetCamera.targetTexture = internalTexture;

                if (shouldMaintainAspectRatio)
                {
                    internalTexture.width = (int)aspectRatio.x;
                    internalTexture.height = (int)aspectRatio.y;
                }

                foreach (GameObject bound in bounds)
                {
                    GameObject overlay = bound.transform.GetChild(0).gameObject;
                    overlay.GetComponent<MeshRenderer>().material.SetFloat("_MaintainAspectRatio", shouldMaintainAspectRatio ? 1.0f : 0.0f);
                }
            }
            else if (renderMode == RENDER_MODE_DESKTOP)
            {
                if (targetCamera == null) return;

                targetCamera.targetTexture = null;
              //  targetCamera.stereoTargetEye = StereoTargetEyeMask.None;
                targetCamera.depth = 52;
                targetCamera.enabled = true;

              //  referenceCamera.stereoTargetEye = StereoTargetEyeMask.None;
                referenceCamera.depth = 51;
                referenceCamera.backgroundColor = clearColor;
                referenceCamera.enabled = true;

                if (shouldMaintainAspectRatio)
                {
                    // https://github.com/RyanNielson/Letterboxer/blob/6e079d5b57c134978f690bbdf5326559ae3b4442/Assets/Letterboxer/Letterboxer.cs#L89
                    HandleMaintainAspectRatio();
                }
            }
        }

        private void HandleMaintainAspectRatio()
        {
            float targetAspect = aspectRatio.x / (float)aspectRatio.y;
            float windowAspect = referenceCamera.pixelWidth / (float)referenceCamera.pixelHeight;
            float scaleHeight = windowAspect / targetAspect;

            targetCamera.rect = scaleHeight < 1.0f ? GetLetterboxRect(scaleHeight) : GetPillarboxRect(scaleHeight);
        }

        private Rect GetLetterboxRect(float scaleHeight)
        {
            return new Rect(0, (1f - scaleHeight) / 2f, 1f, scaleHeight);
        }

        private Rect GetPillarboxRect(float scaleHeight)
        {
            float scalewidth = 1.0f / scaleHeight;

            return new Rect((1f - scalewidth) / 2f, 0, scalewidth, 1f);
        }
    }
}
