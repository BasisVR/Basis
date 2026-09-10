#if BASIS_HAS_SPOUT && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR))
using Klak.Spout;
using System;
using UnityEngine;
using UnityEngine.Rendering;
namespace Basis
{
    public sealed class BasisSpoutVideoOutputSink : IBasisVideoOutputSink
    {
        private const string BlitShaderPath = "Hidden/Klak/Spout/Blit";
        private const float RegistrationGraceSeconds = 2f;
        private SpoutSender sender;
        private SpoutResources resources;
        private string senderName;
        private float registrationDeadline;
        private bool registrationChecked;
        public string FailureMessage { get; private set; }
        public bool SupportsAlpha => true;
        public bool Start(BasisVideoOutputSettings settings, GameObject host)
        {
            FailureMessage = null;

            GraphicsDeviceType device = SystemInfo.graphicsDeviceType;
            if (device != GraphicsDeviceType.Direct3D11 && device != GraphicsDeviceType.Direct3D12)
            {
                FailureMessage = $"Spout needs Direct3D11 or Direct3D12; this player is running on {device}.";
                BasisDebug.LogError(FailureMessage, BasisDebug.LogTag.Camera);
                return false;
            }

            Shader blitShader = Shader.Find(BlitShaderPath);
            if (blitShader == null)
            {
                FailureMessage = $"Spout blit shader '{BlitShaderPath}' was stripped from the build — keep it in Always Included Shaders.";
                BasisDebug.LogError(FailureMessage, BasisDebug.LogTag.Camera);
                return false;
            }
            resources = ScriptableObject.CreateInstance<SpoutResources>();
            resources.blitShader = blitShader;
            sender = host.AddComponent<SpoutSender>();
            sender.SetResources(resources);
            sender.spoutName = settings.SenderName;
            sender.keepAlpha = false;
            sender.captureMethod = CaptureMethod.Texture;

            senderName = settings.SenderName;
            registrationDeadline = Time.unscaledTime + RegistrationGraceSeconds;
            registrationChecked = false;
            return true;
        }
        public void PushFrame(RenderTexture frame, bool keepAlpha)
        {
            if (sender == null) return;
            sender.keepAlpha = keepAlpha;
            sender.sourceTexture = frame;
            VerifyRegistration();
        }
        public void Stop()
        {
            if (sender != null)
            {
                sender.sourceTexture = null;
                UnityEngine.Object.Destroy(sender);
            }
            if (resources != null) UnityEngine.Object.Destroy(resources);
            sender = null;
            resources = null;
            senderName = null;
        }
        private void VerifyRegistration()
        {
            if (registrationChecked || Time.unscaledTime < registrationDeadline) return;
            registrationChecked = true;

            string[] names;
            try
            {
                names = SpoutManager.GetSourceNames();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Spout plugin unreachable ({e.GetType().Name}: {e.Message}). KlakSpout.dll may be missing from the build.", BasisDebug.LogTag.Camera);
                return;
            }

            for (int Index = 0; Index < names.Length; Index++)
            {
                if (!string.Equals(names[Index], senderName, StringComparison.Ordinal)) continue;
                BasisDebug.Log($"Spout sender '{senderName}' is published — select it as a Spout2 Capture source.", BasisDebug.LogTag.Camera);
                return;
            }

            BasisDebug.LogError($"Spout sender '{senderName}' never registered (Spout reports: {(names.Length == 0 ? "no senders" : string.Join(", ", names))}). " + "Spout needs the player on Direct3D11 or Direct3D12 — it cannot work on Vulkan.", BasisDebug.LogTag.Camera);
        }
    }
}
#endif
