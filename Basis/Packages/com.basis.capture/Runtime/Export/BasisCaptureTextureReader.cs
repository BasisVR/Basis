using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
namespace Basis.Capture
{
    public sealed class BasisCaptureTextureReader
    {
        public readonly Texture texture;
        public readonly bool normalMap;
        public readonly string path;
        public readonly bool hdr;
        public string warning;
        private volatile bool done;
        public bool Done => done;
        public BasisCaptureTextureReader(Texture texture, bool normalMap, string pathWithoutExtension)
        {
            this.texture = texture;
            this.normalMap = normalMap;
            hdr = IsHdr(texture);
            path = pathWithoutExtension + (hdr ? ".exr" : ".png");
        }
        public static bool IsHdr(Texture t) => t is Texture2D t2 && (t2.format == TextureFormat.BC6H || t2.format == TextureFormat.RGBAHalf || t2.format == TextureFormat.RGBAFloat || t2.format == TextureFormat.RGHalf || t2.format == TextureFormat.RHalf || t2.format == TextureFormat.RFloat || t2.format == TextureFormat.RGFloat || t2.format == TextureFormat.RGB9e5Float);
        public static bool Supported(Texture t) => t != null && t.dimension == TextureDimension.Tex2D && t.width > 0 && t.height > 0;
        public void Begin()
        {
            try
            {
                int w = texture.width, h = texture.height;
                bool srgb = !hdr && !normalMap && texture.isDataSRGB;
                RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                Graphics.Blit(texture, rt);
                AsyncGPUReadback.Request(rt, 0, hdr ? TextureFormat.RGBAHalf : TextureFormat.RGBA32, request =>
                {
                    if (request.hasError) { warning = "readback error"; done = true; return; }
                    byte[] bytes = request.GetData<byte>().ToArray();
                    Task.Run(() => Encode(bytes, w, h));
                });
                RenderTexture.ReleaseTemporary(rt);
            }
            catch (Exception e)
            {
                warning = e.Message;
                done = true;
            }
        }
        private void Encode(byte[] bytes, int w, int h)
        {
            try
            {
                if (normalMap && !hdr) Unpack(bytes);
                byte[] file = hdr ? ImageConversion.EncodeArrayToEXR(bytes, GraphicsFormat.R16G16B16A16_SFloat, (uint)w, (uint)h, 0, Texture2D.EXRFlags.CompressZIP) : ImageConversion.EncodeArrayToPNG(bytes, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h);
                File.WriteAllBytes(path, file);
            }
            catch (Exception e)
            {
                warning = e.Message;
            }
            done = true;
        }
        private static void Unpack(byte[] px)
        {
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                float x = (px[i] / 255f) * (px[i + 3] / 255f) * 2f - 1f, y = px[i + 1] / 255f * 2f - 1f;
                float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                px[i] = (byte)Mathf.Clamp(Mathf.RoundToInt((x * 0.5f + 0.5f) * 255f), 0, 255);
                px[i + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt((y * 0.5f + 0.5f) * 255f), 0, 255);
                px[i + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt((z * 0.5f + 0.5f) * 255f), 0, 255);
                px[i + 3] = 255;
            }
        }
    }
}
