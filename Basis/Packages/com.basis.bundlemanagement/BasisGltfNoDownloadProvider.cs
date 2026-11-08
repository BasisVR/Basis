using System;
using System.Threading.Tasks;
using GLTFast.Loading;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

[NoAutoStaticsCleanup]
public sealed class BasisGltfNoDownloadProvider : IDownloadProvider
{
    public static readonly BasisGltfNoDownloadProvider Instance = new BasisGltfNoDownloadProvider();
    public Task<IDownload> Request(Uri url)
    {
        BasisDebug.LogWarning($"glTF content asked for an external resource ({Describe(url)}); content must embed every buffer and image, refused.", BasisDebug.LogTag.Event);
        return Task.FromResult<IDownload>(new Refused());
    }
    public Task<ITextureDownload> RequestTexture(Uri url, bool nonReadable)
    {
        BasisDebug.LogWarning($"glTF content asked for an external texture ({Describe(url)}); content must embed every buffer and image, refused.", BasisDebug.LogTag.Event);
        return Task.FromResult<ITextureDownload>(new Refused());
    }
    private static string Describe(Uri url)
    {
        if (url == null) return "no uri";
        return url.IsAbsoluteUri ? url.Scheme : "relative uri";
    }
    private sealed class Refused : ITextureDownload
    {
        public bool Success => false;
        public string Error => "External glTF resources are not allowed.";
        public byte[] Data => null;
        public string Text => null;
        public bool? IsBinary => null;
        public Texture2D Texture => null;
        public void Dispose() { }
    }
}
