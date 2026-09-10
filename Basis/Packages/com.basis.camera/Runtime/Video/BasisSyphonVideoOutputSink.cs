#if BASIS_HAS_SYPHON && (UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR))
using Klak.Syphon;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
namespace Basis
{
    public sealed class BasisSyphonVideoOutputSink : IBasisVideoOutputSink
    {
        private const string BlitShaderPath = "Hidden/Klak/Syphon/Blit";
        private const float RegistrationGraceSeconds = 2f;
        private SyphonServer server;
        private SyphonResources resources;
        private string serverName;
        private float registrationDeadline;
        private bool registrationChecked;
        public string FailureMessage { get; private set; }
        public bool SupportsAlpha => true;
        public bool Start(BasisVideoOutputSettings settings, GameObject host)
        {
            FailureMessage = null;

            GraphicsDeviceType device = SystemInfo.graphicsDeviceType;
            if (device != GraphicsDeviceType.Metal)
            {
                FailureMessage = $"Syphon needs Metal; this player is running on {device}.";
                BasisDebug.LogError(FailureMessage, BasisDebug.LogTag.Camera);
                return false;
            }

            Shader blitShader = Shader.Find(BlitShaderPath);
            if (blitShader == null)
            {
                FailureMessage = $"Syphon blit shader '{BlitShaderPath}' was stripped from the build — keep it in Always Included Shaders.";
                BasisDebug.LogError(FailureMessage, BasisDebug.LogTag.Camera);
                return false;
            }
            resources = ScriptableObject.CreateInstance<SyphonResources>();
            resources.blitShader = blitShader;
            server = host.AddComponent<SyphonServer>();
            server.Resources = resources;
            server.KeepAlpha = false;
            server.CaptureMethod = CaptureMethod.Texture;
            server.ServerName = settings.SenderName;

            serverName = settings.SenderName;
            registrationDeadline = Time.unscaledTime + RegistrationGraceSeconds;
            registrationChecked = false;
            return true;
        }
        public void PushFrame(RenderTexture frame, bool keepAlpha)
        {
            if (server == null) return;
            server.KeepAlpha = keepAlpha;
            if (server.SourceTexture != frame) server.SourceTexture = frame;
            VerifyRegistration();
        }
        public void Stop()
        {
            if (server != null)
            {
                server.SourceTexture = null;
                UnityEngine.Object.Destroy(server);
            }
            if (resources != null) UnityEngine.Object.Destroy(resources);
            server = null;
            resources = null;
            serverName = null;
        }
        private void VerifyRegistration()
        {
            if (registrationChecked || Time.unscaledTime < registrationDeadline) return;
            registrationChecked = true;

            List<string> names = new List<string>();
            try
            {
                names.AddRange(SyphonServerDirectory.EnumerateServerNames());
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Syphon plugin unreachable ({e.GetType().Name}: {e.Message}). KlakSyphon.bundle may be missing from the build.", BasisDebug.LogTag.Camera);
                return;
            }

            for (int Index = 0; Index < names.Count; Index++)
            {
                string entry = names[Index];
                if (string.IsNullOrEmpty(entry)) continue;
                int slash = entry.LastIndexOf('/');
                string published = slash >= 0 ? entry.Substring(slash + 1) : entry;
                if (!string.Equals(published, serverName, StringComparison.Ordinal)) continue;
                BasisDebug.Log($"Syphon server '{serverName}' is published — select it as a Syphon source.", BasisDebug.LogTag.Camera);
                return;
            }

            BasisDebug.LogError($"Syphon server '{serverName}' never registered (Syphon reports: {(names.Count == 0 ? "no servers" : string.Join(", ", names))}). " + "Syphon needs the player on Metal, with KlakSyphon.bundle present in the build.", BasisDebug.LogTag.Camera);
        }
    }
}
#endif
