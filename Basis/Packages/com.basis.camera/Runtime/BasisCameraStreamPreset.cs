using System.Collections.Generic;
using Basis.BasisUI;
using UnityEngine;

namespace Basis
{
    public readonly struct BasisCameraStreamPreset
    {
        public const float FrameRateTolerance = 0.5f;

        public readonly string Key;
        public readonly BasisVideoTransport Transport;
        public readonly int Width, Height, WebQuality;
        public readonly float FrameRate;

        public BasisCameraStreamPreset(string key, BasisVideoTransport transport, int width, int height, float frameRate, int webQuality)
        {
            Key = key;
            Transport = transport;
            Width = width;
            Height = height;
            FrameRate = frameRate;
            WebQuality = webQuality;
        }

        public bool Matches(BasisVideoTransport transport, int width, int height, float frameRate, int webQuality)
        {
            if (transport != Transport || width != Width || height != Height) return false;
            if (Mathf.Abs(frameRate - FrameRate) > FrameRateTolerance) return false;
            return Transport != BasisVideoTransport.Web || webQuality == WebQuality;
        }

        public bool Matches(BasisVideoTransport transport, BasisVideoOutputSettings settings) =>
            settings != null && Matches(transport, settings.Width, settings.Height, settings.FrameRate, settings.WebQuality);
    }

    public static class BasisCameraStreamPresets
    {
        public const string CustomKey = "camera.streamPreset.custom";

        public static readonly BasisCameraStreamPreset[] All =
        {
            new BasisCameraStreamPreset("camera.streamPreset.platform720p60", BasisVideoTransport.Platform, 1280, 720, 60f, 0),
            new BasisCameraStreamPreset("camera.streamPreset.platform1080p30", BasisVideoTransport.Platform, 1920, 1080, 30f, 0),
            new BasisCameraStreamPreset("camera.streamPreset.platform1080p60", BasisVideoTransport.Platform, 1920, 1080, 60f, 0),
            new BasisCameraStreamPreset("camera.streamPreset.platform1440p60", BasisVideoTransport.Platform, 2560, 1440, 60f, 0),
            new BasisCameraStreamPreset("camera.streamPreset.platform2160p30", BasisVideoTransport.Platform, 3840, 2160, 30f, 0),
            new BasisCameraStreamPreset("camera.streamPreset.web720p30", BasisVideoTransport.Web, 1280, 720, 30f, 60),
            new BasisCameraStreamPreset("camera.streamPreset.web1080p30", BasisVideoTransport.Web, 1920, 1080, 30f, 70),
            new BasisCameraStreamPreset("camera.streamPreset.web1080p60", BasisVideoTransport.Web, 1920, 1080, 60f, 65),
            new BasisCameraStreamPreset("camera.streamPreset.web1080p30hq", BasisVideoTransport.Web, 1920, 1080, 30f, 90),
        };

        public static readonly string[] OptionKeys = BuildOptionKeys();

        public static List<BasisCameraStreamPreset> Available()
        {
            List<BasisCameraStreamPreset> presets = new List<BasisCameraStreamPreset>(All.Length);
            for (int Index = 0; Index < All.Length; Index++)
            {
                if (BasisCameraVideoPlatform.IsAvailable(All[Index].Transport)) presets.Add(All[Index]);
            }
            return presets;
        }

        public static int IndexOf(IReadOnlyList<BasisCameraStreamPreset> presets, BasisVideoTransport transport, BasisVideoOutputSettings settings)
        {
            if (presets == null || settings == null) return -1;
            for (int Index = 0; Index < presets.Count; Index++)
            {
                if (presets[Index].Matches(transport, settings)) return Index;
            }
            return -1;
        }

        public static string KeyFor(BasisVideoTransport transport, BasisVideoOutputSettings settings)
        {
            int index = IndexOf(All, transport, settings);
            return index >= 0 ? All[index].Key : CustomKey;
        }

        public static string KeyFor(BasisVideoTransport transport, int width, int height, float frameRate, int webQuality)
        {
            for (int Index = 0; Index < All.Length; Index++)
            {
                if (All[Index].Matches(transport, width, height, frameRate, webQuality)) return All[Index].Key;
            }
            return CustomKey;
        }

        public static string Label(string key) => BasisLocalization.Get(key, BasisCameraVideoPlatform.BackendName);

        public static string Tooltip(string key) => BasisLocalization.Get(key + ".tooltip", BasisCameraVideoPlatform.BackendName);

        private static string[] BuildOptionKeys()
        {
            string[] keys = new string[All.Length + 1];
            for (int Index = 0; Index < All.Length; Index++) keys[Index] = All[Index].Key;
            keys[All.Length] = CustomKey;
            return keys;
        }
    }
}
