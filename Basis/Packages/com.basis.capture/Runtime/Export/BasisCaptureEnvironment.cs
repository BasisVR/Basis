using System;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
namespace Basis.Capture
{
    public sealed class BasisCaptureEnvironment
    {
        public static readonly string[] ExcludedLayers = { "Player", "UI", "LocalPlayerAvatar", "RemotePlayerAvatar", "OverlayUI", "HandHeldCameraUI" };
        private static readonly Vector3[] Forward = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        private static readonly Vector3[] Up = { Vector3.up, Vector3.up, Vector3.back, Vector3.forward, Vector3.up, Vector3.up };
        public const int FaceSize = 256, Width = 1024, Height = 512;
        public readonly Vector3 position;
        public readonly string path;
        public string file, skyFile, warning;
        private readonly string skyPath;
        private readonly float[][] faces = new float[12][];
        private readonly Quaternion[] rotations = new Quaternion[6];
        private int pending;
        private volatile bool done;
        public bool Done => done;
        public BasisCaptureEnvironment(Vector3 position, string folder)
        {
            this.position = position;
            file = $"{BasisCaptureFormat.AssetsFolder}/environment.exr";
            skyFile = $"{BasisCaptureFormat.AssetsFolder}/sky.exr";
            path = Path.Combine(folder, file);
            skyPath = Path.Combine(folder, skyFile);
        }
        public static int CullingMask(int source)
        {
            int mask = source;
            foreach (string layer in ExcludedLayers)
            {
                int l = LayerMask.NameToLayer(layer);
                if (l >= 0) mask &= ~(1 << l);
            }
            return mask;
        }
        public void Begin(Camera template)
        {
            GameObject go = new GameObject("BasisCaptureEnvironment") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                Camera cam = go.AddComponent<Camera>();
                cam.enabled = false;
                if (template != null) cam.CopyFrom(template);
                cam.fieldOfView = 90f;
                cam.aspect = 1f;
                cam.nearClipPlane = 0.05f;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.cullingMask = CullingMask(template != null ? template.cullingMask : ~0);
                cam.allowHDR = true;
                cam.allowMSAA = false;
                UniversalAdditionalCameraData data = go.AddComponent<UniversalAdditionalCameraData>();
                data.renderPostProcessing = false;
                data.antialiasing = AntialiasingMode.None;
                RenderTexture rt = new RenderTexture(FaceSize, FaceSize, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                rt.Create();
                cam.targetTexture = rt;
                pending = 12;
                int sceneMask = cam.cullingMask;
                for (int f = 0; f < 12; f++)
                {
                    int side = f % 6;
                    cam.cullingMask = f < 6 ? sceneMask : 0;
                    cam.clearFlags = f < 6 ? cam.clearFlags : CameraClearFlags.Skybox;
                    rotations[side] = Quaternion.LookRotation(Forward[side], Up[side]);
                    go.transform.SetPositionAndRotation(position, rotations[side]);
                    cam.Render();
                    int face = f;
                    AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat, r => Received(face, r));
                }
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
            catch (Exception e)
            {
                warning = e.Message;
                done = true;
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }
        }
        private void Received(int face, AsyncGPUReadbackRequest r)
        {
            if (r.hasError) warning = "environment readback failed";
            else faces[face] = r.GetData<float>().ToArray();
            if (--pending == 0) Task.Run(Assemble);
        }
        private void Assemble()
        {
            try
            {
                if (warning != null) return;
                Write(0, path);
                Write(6, skyPath);
            }
            catch (Exception e)
            {
                warning = e.Message;
            }
            finally
            {
                done = true;
            }
        }
        private void Write(int first, string target)
        {
            {
                float[] eq = new float[Width * Height * 4];
                float tan = Mathf.Tan(45f * Mathf.Deg2Rad);
                Quaternion[] inv = new Quaternion[6];
                for (int f = 0; f < 6; f++) inv[f] = Quaternion.Inverse(rotations[f]);
                for (int y = 0; y < Height; y++)
                {
                    double phi = ((y + 0.5) / Height - 0.5) * Math.PI;
                    for (int x = 0; x < Width; x++)
                    {
                        double theta = (0.5 - (x + 0.5) / Width) * 2.0 * Math.PI;
                        double bx = Math.Cos(phi) * Math.Cos(theta), by = Math.Cos(phi) * Math.Sin(theta), bz = Math.Sin(phi);
                        Vector3 dir = new Vector3((float)-bx, (float)bz, (float)-by);
                        int best = 0;
                        float bestDot = -2f;
                        for (int f = 0; f < 6; f++) { float d = Vector3.Dot(dir, Forward[f]); if (d > bestDot) { bestDot = d; best = f; } }
                        Vector3 local = inv[best] * dir;
                        float u = (local.x / local.z / tan) * 0.5f + 0.5f, v = (local.y / local.z / tan) * 0.5f + 0.5f;
                        int px = Mathf.Clamp((int)(u * FaceSize), 0, FaceSize - 1), py = Mathf.Clamp((int)(v * FaceSize), 0, FaceSize - 1);
                        float[] src = faces[first + best];
                        int s = (py * FaceSize + px) * 4, o = (y * Width + x) * 4;
                        eq[o] = src[s]; eq[o + 1] = src[s + 1]; eq[o + 2] = src[s + 2]; eq[o + 3] = 1f;
                    }
                }
                byte[] exr = ImageConversion.EncodeArrayToEXR(eq, GraphicsFormat.R32G32B32A32_SFloat, Width, Height, 0, Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP);
                File.WriteAllBytes(target, exr);
            }
        }
    }
}
