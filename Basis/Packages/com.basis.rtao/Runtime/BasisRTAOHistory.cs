using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Basis.Rendering.RTAO
{
    public sealed class BasisRTAOHistory : IDisposable
    {
        public sealed class Entry : IDisposable
        {
            public RenderTexture[] visibilityTextures = new RenderTexture[2];
            public RenderTexture[] depthTextures = new RenderTexture[2];
            public RTHandle[] visibility = new RTHandle[2];
            public RTHandle[] depth = new RTHandle[2];
            public Matrix4x4[] previousViewProjection = { Matrix4x4.identity, Matrix4x4.identity };
            public Vector4[] previousViewPlane = { Vector4.zero, Vector4.zero };
            public int width, height, viewCount;
            public int writeIndex;
            public int lastUsedFrame;
            public int framesRendered;
            public bool valid;

            public RTHandle CurrentVisibility => visibility[writeIndex];
            public RTHandle CurrentDepth => depth[writeIndex];
            public RTHandle PreviousVisibility => visibility[1 - writeIndex];
            public RTHandle PreviousDepth => depth[1 - writeIndex];

            public void Swap()
            {
                writeIndex = 1 - writeIndex;
            }

            public bool Matches(int requestedWidth, int requestedHeight, int requestedViewCount)
            {
                return width == requestedWidth && height == requestedHeight && viewCount == requestedViewCount;
            }

            public void Dispose()
            {
                for (int i = 0; i < 2; i++)
                {
                    if (visibility[i] != null)
                    {
                        RTHandles.Release(visibility[i]);
                        visibility[i] = null;
                    }
                    if (depth[i] != null)
                    {
                        RTHandles.Release(depth[i]);
                        depth[i] = null;
                    }
                    DestroyTexture(ref visibilityTextures[i]);
                    DestroyTexture(ref depthTextures[i]);
                }
                valid = false;

                framesRendered = 0;
            }
        }

        private readonly Dictionary<EntityId, Entry> entries = new Dictionary<EntityId, Entry>();
        private readonly List<EntityId> evictionScratch = new List<EntityId>();

        public int Count => entries.Count;

        public Entry Get(Camera camera, int width, int height, int viewCount, int frameCount)
        {
            EntityId key = camera != null ? camera.GetEntityId() : default;
            if (!entries.TryGetValue(key, out Entry entry))
            {
                entry = new Entry();
                entries.Add(key, entry);
            }

            if (!entry.Matches(width, height, viewCount) || !entry.valid)
            {
                entry.Dispose();
                Allocate(entry, camera, width, height, viewCount);
            }

            entry.lastUsedFrame = frameCount;
            return entry;
        }

        public void Evict(int frameCount, int maxAge = 8)
        {
            evictionScratch.Clear();
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
            {
                if (frameCount - pair.Value.lastUsedFrame > maxAge)
                    evictionScratch.Add(pair.Key);
            }
            for (int i = 0; i < evictionScratch.Count; i++)
            {
                if (entries.TryGetValue(evictionScratch[i], out Entry entry))
                    entry.Dispose();
                entries.Remove(evictionScratch[i]);
            }
        }

        private static void Allocate(Entry entry, Camera camera, int width, int height, int viewCount)
        {
            string cameraName = camera != null ? camera.name : "Unknown";
            for (int i = 0; i < 2; i++)
            {
                entry.visibilityTextures[i] = CreateTexture(width, height, viewCount, GraphicsFormat.R16G16B16A16_SFloat, $"BasisRTAOHistory_{cameraName}_{i}");
                entry.depthTextures[i] = CreateTexture(width, height, viewCount, GraphicsFormat.R16G16_SFloat, $"BasisRTAOHistoryDepth_{cameraName}_{i}");
                entry.visibility[i] = RTHandles.Alloc(entry.visibilityTextures[i]);
                entry.depth[i] = RTHandles.Alloc(entry.depthTextures[i]);
            }
            entry.width = width;
            entry.height = height;
            entry.viewCount = viewCount;
            entry.writeIndex = 0;
            entry.valid = true;
            entry.previousViewProjection[0] = Matrix4x4.identity;
            entry.previousViewProjection[1] = Matrix4x4.identity;
            entry.previousViewPlane[0] = Vector4.zero;
            entry.previousViewPlane[1] = Vector4.zero;
        }

        private static RenderTexture CreateTexture(int width, int height, int viewCount, GraphicsFormat format, string name)
        {
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(width, height, format, GraphicsFormat.None, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = Mathf.Max(1, viewCount),
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = true,
                sRGB = false
            };

            RenderTexture texture = new RenderTexture(descriptor)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            return texture;
        }

        private static void DestroyTexture(ref RenderTexture texture)
        {
            if (texture == null)
                return;
            texture.Release();
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(texture);
            else
                UnityEngine.Object.DestroyImmediate(texture);
            texture = null;
        }

        public void Dispose()
        {
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
                pair.Value.Dispose();
            entries.Clear();
            evictionScratch.Clear();
        }
    }
}
