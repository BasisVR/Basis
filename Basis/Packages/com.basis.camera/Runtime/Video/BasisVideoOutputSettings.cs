using UnityEngine;
namespace Basis
{
    public enum BasisVideoTransport
    {
        Platform,
        Web,
    }
    public class BasisVideoOutputSettings
    {
        public const int DefaultWidth = 1920, DefaultHeight = 1080, DefaultWebPort = 8787, DefaultWebQuality = 70;
        public const float DefaultFrameRate = 30f;
        public const string DefaultSenderName = "Basis Camera";
        public int Width = DefaultWidth, Height = DefaultHeight;
        public float FrameRate = DefaultFrameRate;
        public string SenderName = DefaultSenderName, DevicePath = string.Empty;
        public int WebPort = DefaultWebPort, WebQuality = DefaultWebQuality;
        public void ClampSize()
        {
            Width = Mathf.Clamp(Width, 16, 8192);
            Height = Mathf.Clamp(Height, 16, 8192);
        }
    }
}
