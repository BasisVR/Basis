using UnityEngine;

namespace Basis.MediaPipe
{
    public sealed class BasisMediaPipeCamera
    {
        public enum Status { Stopped, Starting, Running, NoDevice, Stalled }
        public const float RetrySeconds = 3f, StallSeconds = 3f, StartupSeconds = 10f;
        public WebCamTexture Texture { get; private set; }
        public string DeviceName { get; private set; }
        public Status State { get; private set; }
        public int Restarts { get; private set; }
        public int RequestedFps => requestedFps;
        public bool IsRunning => Texture != null && Texture.isPlaying;
        public bool IsReady => IsRunning && Texture.didUpdateThisFrame && Texture.width > 16;
        private string requestedDevice;
        private int requestedWidth = 640, requestedHeight = 480, requestedFps = 30;
        private float retryTimer, silentFor;

        public static WebCamDevice[] EnumerateDevices() => WebCamTexture.devices;

        public bool Start(string deviceName = null, int width = 640, int height = 480, int fps = 30)
        {
            Stop();
            requestedDevice = deviceName;
            requestedWidth = width;
            requestedHeight = height;
            requestedFps = fps;
            return Open();
        }

        public void SetRequestedFps(int fps)
        {
            if (fps == requestedFps) return;
            requestedFps = fps;
            if (State == Status.Stopped) return;
            Restarts++;
            Close();
            Open();
        }

        public void Tick(float dt)
        {
            switch (State)
            {
                case Status.NoDevice:
                    retryTimer += dt;
                    if (retryTimer >= RetrySeconds)
                    {
                        retryTimer = 0f;
                        Open();
                    }
                    break;
                case Status.Starting:
                    if (Texture == null)
                    {
                        State = Status.NoDevice;
                        break;
                    }
                    if (Texture.didUpdateThisFrame && Texture.width > 16)
                    {
                        State = Status.Running;
                        silentFor = 0f;
                    }
                    else
                    {
                        silentFor += dt;
                        if (silentFor > StartupSeconds)
                        {
                            Restarts++;
                            Close();
                            Open();
                        }
                    }
                    break;
                case Status.Running:
                    if (Texture == null || !Texture.isPlaying)
                    {
                        State = Status.Stalled;
                        break;
                    }
                    silentFor = Texture.didUpdateThisFrame ? 0f : silentFor + dt;
                    if (silentFor > StallSeconds) State = Status.Stalled;
                    break;
                case Status.Stalled:
                    Restarts++;
                    Close();
                    Open();
                    break;
            }
        }

        public void Stop()
        {
            Close();
            State = Status.Stopped;
        }

        private void Close()
        {
            if (Texture == null) return;
            if (Texture.isPlaying) Texture.Stop();
            Object.Destroy(Texture);
            Texture = null;
        }

        private bool Open()
        {
            silentFor = 0f;
            WebCamDevice[] devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
            {
                if (State != Status.NoDevice) BasisDebug.LogError("BasisMediaPipe: no webcam devices found.");
                State = Status.NoDevice;
                retryTimer = 0f;
                return false;
            }

            DeviceName = ResolveDeviceName(devices, requestedDevice);
            Texture = new WebCamTexture(DeviceName, requestedWidth, requestedHeight, requestedFps)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            Texture.Play();
            State = Status.Starting;
            BasisDebug.Log($"BasisMediaPipe: started webcam '{DeviceName}' ({requestedWidth}x{requestedHeight}@{requestedFps}).");
            return true;
        }

        private static string ResolveDeviceName(WebCamDevice[] devices, string requested)
        {
            if (!string.IsNullOrEmpty(requested))
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    if (devices[i].name == requested) return devices[i].name;
                }
                BasisDebug.LogError($"BasisMediaPipe: requested camera '{requested}' not found; using default.");
            }
            for (int i = 0; i < devices.Length; i++)
            {
                if (devices[i].isFrontFacing) return devices[i].name;
            }
            return devices[0].name;
        }
    }
}
