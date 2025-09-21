/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */


using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> used by all the panels for Immersive Debugger.
    /// It makes use of <see cref="OVROverlay"/>) and set it up for the corresponding camera.
    /// so the rendering is done in overlay layer (composition layer) instead of projection layer.
    /// It could help the panels display much sharper text. See more in the [OVROverlay doc](https://developer.oculus.com/documentation/unity/unity-ovroverlay/).
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public sealed class OverlayCanvas : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init() // reset static fields in case of domain reload disabled
        {
            FrustumPlanes = new Plane[6];
        }
        private static Plane[] FrustumPlanes = new Plane[6];

        private Camera _camera;
        private RenderTexture _renderTexture;
        private MeshRenderer _meshRenderer;

        Mesh _quad;
        Material _defaultMat;

        private const int MaxTextureSize = 1600;
        private const int MinTextureSize = 200;
        private const float PixelsPerUnit = 1f;

        private readonly bool _scaleViewport = Application.isMobilePlatform;

        /// <summary>
        /// The panel this canvas is used upon, the overlay camera object will be children of this Panel
        /// </summary>
        public OverlayCanvasPanel Panel { get; set; }

        // Start is called before the first frame update
        private void Start()
        {
            var panelTransform = Panel.Transform;
            var rectTransform = Panel.RectTransform;
            var rect = rectTransform.rect;

            var rectWidth = rect.width;
            var rectHeight = rect.height;

            var aspectX = rectWidth >= rectHeight ? 1 : rectWidth / rectHeight;
            var aspectY = rectHeight >= rectWidth ? 1 : rectHeight / rectWidth;

            // if we are scaling the viewport we don't need to add a border
            var pixelBorder = _scaleViewport ? 0 : 8;
            var innerWidth = Mathf.CeilToInt(aspectX * (MaxTextureSize - pixelBorder * 2));
            var innerHeight = Mathf.CeilToInt(aspectY * (MaxTextureSize - pixelBorder * 2));
            var width = innerWidth + pixelBorder * 2;
            var height = innerHeight + pixelBorder * 2;

            var paddedWidth = rectWidth * (width / (float)innerWidth);
            var paddedHeight = rectHeight * (height / (float)innerHeight);

            var insetRectWidth = innerWidth / (float)width;
            var insetRectHeight = innerHeight / (float)height;

            _renderTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                // if we can't scale the viewport, generate mipmaps instead
                useMipMap = !_scaleViewport
            };

            var overlayCamera = new GameObject(name + " Overlay Camera");
            overlayCamera.transform.SetParent(panelTransform, false);
            _camera = overlayCamera.AddComponent<Camera>();
            _camera.stereoTargetEye = StereoTargetEyeMask.None;
            _camera.transform.position = panelTransform.position - panelTransform.forward;
            _camera.orthographic = true;
            _camera.enabled = false;
            _camera.targetTexture = _renderTexture;
            _camera.cullingMask = 1 << gameObject.layer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.clear;
            _camera.orthographicSize = 0.5f * paddedHeight * rectTransform.localScale.y;
            _camera.nearClipPlane = 0.99f;
            _camera.farClipPlane = 1.01f;

            _quad = new Mesh
            {
                name = name + " Overlay Quad",
                vertices = new Vector3[]
                    { new Vector3(-0.5f, -0.5f), new Vector3(-0.5f, 0.5f), new Vector3(0.5f, 0.5f), new Vector3(0.5f, -0.5f) },
                uv = new Vector2[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) },
                triangles = new int[] { 0, 1, 2, 2, 3, 0 },
                bounds = new Bounds(Vector3.zero, Vector3.one)
            };
            _quad.UploadMeshData(true);

            var transparentShader = Shader.Find("UI/IDF Prerendered");
            _defaultMat = new Material(transparentShader)
            {
                mainTexture = _renderTexture,
                color = Color.black,
                mainTextureOffset = new Vector2(0.5f - 0.5f * insetRectWidth, 0.5f - 0.5f * insetRectHeight),
                mainTextureScale = new Vector2(insetRectWidth, insetRectHeight)
            };

            var meshRenderer = new GameObject(name + " MeshRenderer");
            meshRenderer.transform.SetParent(transform, false);
            meshRenderer.AddComponent<MeshFilter>().sharedMesh = _quad;
            _meshRenderer = meshRenderer.AddComponent<MeshRenderer>();
            _meshRenderer.sharedMaterial = _defaultMat;
            meshRenderer.layer = RuntimeSettings.Instance.MeshRendererLayer;
            meshRenderer.transform.localScale = new Vector3(rectWidth, rectHeight, 1);

            var overlay = new GameObject(name + " Overlay");
            overlay.transform.SetParent(transform, false);
            overlay.transform.SetParent(Panel.Interface.Transform, false);
        }

        private void OnDestroy()
        {
            Destroy(_defaultMat);
            Destroy(_quad);
            Destroy(_renderTexture);
        }

        private void OnEnable()
        {
            if (_meshRenderer)
            {
                _meshRenderer.enabled = true;
            }
            if (_camera)
            {
                _camera.enabled = true;
            }
        }

        private void OnDisable()
        {
            if (_meshRenderer)
            {
                _meshRenderer.enabled = false;
            }
            if (_camera)
            {
                _camera.enabled = false;
            }
        }

        private bool ShouldRender(Camera baseCamera)
        {
            if (baseCamera == null)
            {
                return false;
            }

            // Perform Frustum culling
            for (var i = 0; i < 2; i++)
            {
                var eye = (Camera.StereoscopicEye)i;
                var mat = baseCamera.GetStereoProjectionMatrix(eye) * baseCamera.GetStereoViewMatrix(eye);
                GeometryUtility.CalculateFrustumPlanes(mat, FrustumPlanes);
                if (GeometryUtility.TestPlanesAABB(FrustumPlanes, _meshRenderer.bounds))
                {
                    return true;
                }
            }

            return false;
        }

        private void Update()
        {
            var baseCamera = Camera.main;
            if (!ShouldRender(baseCamera))
            {
                return;
            }

            var panelTransform = Panel.Transform;
            var rectTransform = Panel.RectTransform;

            if (_scaleViewport)
            {
                var rect = rectTransform.rect;

                var d = (baseCamera.transform.position - transform.position).magnitude;

                var size = PixelsPerUnit * Mathf.Max(rect.width * panelTransform.lossyScale.x,
                    rect.height * panelTransform.lossyScale.y) / d;

                // quantize to even pixel sizes
                const float quantize = 8f;
                var pixelHeight = Mathf.Ceil(size / quantize * _renderTexture.height) * quantize;

                // clamp between or min size and our max size
                pixelHeight = Mathf.Clamp(pixelHeight, MinTextureSize, _renderTexture.height);

                var innerPixelHeight = pixelHeight - 2;

                _camera.orthographicSize = 0.5f * rect.height * rectTransform.localScale.y *
                    pixelHeight / innerPixelHeight;

                var aspect = (rect.width / rect.height);

                var innerPixelWidth = innerPixelHeight * aspect;
                var pixelWidth = Mathf.Ceil((innerPixelWidth + 2) * 0.5f) * 2;

                var sizeX = pixelWidth / _renderTexture.width;
                var sizeY = pixelHeight / _renderTexture.height;

                var innerSizeX = (innerPixelWidth) / _renderTexture.width;
                var innerSizeY = (innerPixelHeight) / _renderTexture.height;

                // scale the camera rect
                _camera.rect = new Rect((1 - sizeX) / 2, (1 - sizeY) / 2, sizeX, sizeY);

                var src = new Rect(0.5f - (0.5f * innerSizeX), 0.5f - (0.5f * innerSizeY), innerSizeX, innerSizeY);

                _defaultMat.mainTextureOffset = src.min;
                _defaultMat.mainTextureScale = src.size;
            }

            _camera.Render();
        }
    }
}

