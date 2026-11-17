using Basis.EventDriver;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using GatorDragonGames.JigglePhysics;
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
namespace Basis.Capture
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public sealed class BasisCaptureSession
    {
        public enum SessionState { Recording, Finalizing, Done, Failed }
        public struct Options
        {
            public bool remotes, scene, geometry, microphone, peerMode;
            public uint sessionId;
            public ushort requester;
            public long requesterStartTicks;
            public float maxSeconds, rateHz;
            public string folder;
            public Func<ushort, bool> includeRemote;
        }
        private sealed class CameraEntry { public Camera camera; public int id, firstFrame, lastFrame; public string name; public bool main; public float[] last; }
        public static BasisCaptureSession Current { get; private set; }
        private static readonly List<BasisCaptureSession> active = new List<BasisCaptureSession>();
        public static event Action<BasisCaptureSession> Changed;
        public readonly Options options;
        public readonly string Folder;
        public SessionState State { get; private set; }
        public string Error { get; private set; }
        public int Frame { get; private set; }
        public double Elapsed { get; private set; }
        public long StartServerTicks { get; private set; }
        public BasisCaptureAssets Assets => assets;
        public int ActorCount => actors.Count;
        public long BytesWritten => writer?.BytesWritten ?? 0;
        private static readonly ProfilerMarker sampleMarker = new ProfilerMarker("BasisCapture.Sample");
        private readonly List<BasisCaptureActor> actors = new List<BasisCaptureActor>();
        private readonly Dictionary<string, BasisCaptureActor> live = new Dictionary<string, BasisCaptureActor>();
        private readonly Dictionary<Camera, CameraEntry> cameras = new Dictionary<Camera, CameraEntry>();
        private readonly List<CameraEntry> cameraList = new List<CameraEntry>();
        private readonly HashSet<string> seen = new HashSet<string>();
        private readonly BasisCaptureBuffer buffer = new BasisCaptureBuffer();
        private readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        private Camera[] cameraScratch = new Camera[16];
        private BasisCaptureFrameWriter writer;
        private BasisCaptureMicWriter mic;
        private BasisCaptureAssets assets;
        private BasisCaptureScene scene;
        private NativeList<JobHandle> handles;
        private int lastUnityFrame = -1, lastFrameBytes = 1 << 16;
        private double nextSample;
        private Vector3 actorCentre;
        private BasisCaptureEnvironment environment;
        private DateTime startUtc;
        public static long ServerTicksNow() => DateTime.UtcNow.Ticks + BasisNetworkManagement.RemoteTimeDelta();
        public static string DefaultRoot => Path.Combine(Application.persistentDataPath, "Captures");
        public static BasisCaptureSession Start(Options options)
        {
            if (Current != null && Current.State == SessionState.Recording) return null;
            BasisCaptureSession s = new BasisCaptureSession(options);
            if (s.State == SessionState.Failed) { BasisDebug.LogError("Capture could not start: " + s.Error); return null; }
            Current = s;
            active.Add(s);
            Application.onBeforeRender -= Tick;
            Application.onBeforeRender += Tick;
            Application.quitting -= OnQuit;
            Application.quitting += OnQuit;
            Changed?.Invoke(s);
            return s;
        }
        private BasisCaptureSession(Options options)
        {
            this.options = options;
            try
            {
                startUtc = DateTime.UtcNow;
                Folder = options.folder ?? Path.Combine(DefaultRoot, startUtc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss") + (options.peerMode ? "_peer" : ""));
                Directory.CreateDirectory(Folder);
                StartServerTicks = ServerTicksNow();
                writer = new BasisCaptureFrameWriter(Path.Combine(Folder, BasisCaptureFormat.FramesFile));
                assets = new BasisCaptureAssets(Folder, options.geometry);
                scene = options.scene ? new BasisCaptureScene() : null;
                if (scene != null) BasisRuntimeSpawnRegistry.OnRegistryChanged += OnRegistryChanged;
                if (options.microphone)
                {
                    Directory.CreateDirectory(Path.Combine(Folder, BasisCaptureFormat.AudioFolder));
                    mic = new BasisCaptureMicWriter(Path.Combine(Folder, BasisCaptureFormat.AudioFolder, BasisCaptureFormat.SafeFileName(LocalUuid()) + ".wav"), LocalOpusSettings.MicrophoneSampleRate, StartServerTicks);
                }
                handles = new NativeList<JobHandle>(16, Allocator.Persistent);
                clock.Start();
                State = SessionState.Recording;
                WriteManifest();
            }
            catch (Exception e)
            {
                Error = e.Message;
                State = SessionState.Failed;
                Cleanup();
            }
        }
        private void OnRegistryChanged(BasisRuntimeSpawnRegistry.RegistryChangeType type, BasisRuntimeSpawnRegistry.SpawnInstance instance) { if (scene != null) scene.RegistryChanged = true; }
        private static void OnQuit()
        {
            Application.quitting -= OnQuit;
            foreach (BasisCaptureSession s in active.ToArray()) { if (s.State == SessionState.Recording) s.Stop(); if (s.State == SessionState.Finalizing) s.FinishNow(); }
            active.Clear();
        }
        public void Stop()
        {
            if (State != SessionState.Recording) return;
            State = SessionState.Finalizing;
            mic?.Dispose();
            if (scene != null) BasisRuntimeSpawnRegistry.OnRegistryChanged -= OnRegistryChanged;
            foreach (BasisCaptureActor a in actors) if (a.endFrame < 0) a.endFrame = Frame - 1;
            foreach (CameraEntry c in cameraList) if (c.lastFrame < 0) c.lastFrame = Frame - 1;
            try { writer?.Dispose(); } catch (Exception e) { Error = e.Message; }
            writer = null;
            WriteManifest();
            Changed?.Invoke(this);
        }
        private void FinishNow()
        {
            WriteManifest();
            State = SessionState.Done;
            Cleanup();
        }
        [BeforeRenderOrder(int.MaxValue)]
        private static void Tick()
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                BasisCaptureSession s = active[i];
                s.TickOne();
                if (s.State == SessionState.Done || s.State == SessionState.Failed) active.RemoveAt(i);
            }
            if (active.Count == 0) Application.onBeforeRender -= Tick;
        }
        private void TickOne()
        {
            BasisCaptureSession s = this;
            try
            {
                if (s.State == SessionState.Recording) s.Sample();
                if (s.State == SessionState.Finalizing)
                {
                    s.assets.Process();
                    if (!s.assets.Busy && (s.environment == null || s.environment.Done)) { s.FinishNow(); Changed?.Invoke(s); }
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Capture failed: {e}");
                s.Error = e.Message;
                if (s.State == SessionState.Recording) s.Stop();
                s.FinishNow();
                s.State = SessionState.Failed;
                Changed?.Invoke(s);
            }
        }
        private void Sample()
        {
            if (Time.frameCount == lastUnityFrame) return;
            lastUnityFrame = Time.frameCount;
            if (options.rateHz > 0f)
            {
                double now = clock.Elapsed.TotalSeconds, interval = 1.0 / options.rateHz;
                if (Frame > 0 && now + 0.25 * interval < nextSample) { assets.Process(); if (options.maxSeconds > 0f && now >= options.maxSeconds) Stop(); return; }
                nextSample = nextSample + interval < now ? now + interval : nextSample + interval;
            }
            using ProfilerMarker.AutoScope scope = sampleMarker.Auto();
            BasisLateJiggleCompletion.CompleteIfPending();
            RefreshActors();
            handles.Clear();
            foreach (BasisCaptureActor a in live.Values) handles.Add(a.Schedule());
            if (scene != null) handles.Add(scene.Schedule());
            JobHandle.CompleteAll(handles.AsArray());
            if (live.TryGetValue("local", out BasisCaptureActor localActor) && !BasisLocalAvatarDriver.IsNormalHead && BasisLocalAvatarDriver.Mapping != null && BasisLocalAvatarDriver.Mapping.Hashead)
            {
                localActor.OverrideScale(BasisLocalAvatarDriver.Mapping.head, BasisLocalAvatarDriver.HeadScale);
                for (int i = 0; i < BasisLocalAvatarDriver.HeadChopEntriesLength; i++) localActor.OverrideScale(BasisLocalAvatarDriver.HeadChopEntries[i].Target, BasisLocalAvatarDriver.HeadChopEntries[i].NormalScale);
            }
            Elapsed = clock.Elapsed.TotalSeconds;
            buffer.Reset(writer.Rent(lastFrameBytes));
            buffer.Int(Frame);
            buffer.Double(Elapsed);
            buffer.Long(ServerTicksNow());
            int records = buffer.Reserve(4);
            int count = 0;
            Vector3 centre = Vector3.zero;
            int players = 0;
            foreach (BasisCaptureActor a in live.Values) { a.Write(buffer); count++; if (a.kind == "player" && a.root != null) { centre += a.root.position; players++; } }
            if (players > 0) actorCentre = centre / players;
            if (scene != null) { scene.Write(buffer, Frame); count += 2; }
            count += WriteCameras();
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.Data.AsSpan(records), count);
            lastFrameBytes = Math.Max(lastFrameBytes, buffer.Length);
            writer.Submit(buffer.Data, buffer.Length);
            buffer.Data = null;
            Frame++;
            if (environment == null && options.scene && options.geometry && Elapsed > 0.5)
            {
                environment = new BasisCaptureEnvironment(actorCentre + Vector3.up * 1.3f, Folder);
                environment.Begin(BasisLocalCameraDriver.CameraInstance);
            }
            assets.Process();
            if (writer.Failure != null) throw writer.Failure;
            if (options.maxSeconds > 0f && Elapsed >= options.maxSeconds) Stop();
        }
        private void RefreshActors()
        {
            seen.Clear();
            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            if (local != null && local.BasisAvatar != null) Track("local", local.BasisAvatar, true, local.UUID, local.DisplayName, LocalNetId(), local.AvatarMetaData, BasisLocalAvatarDriver.JiggleRigs);
            if (options.remotes)
            {
                foreach (KeyValuePair<ushort, BasisRemotePlayer> pair in BasisNetworkPlayers.RemotePlayers)
                {
                    BasisRemotePlayer rp = pair.Value;
                    if (rp == null || rp.BasisAvatar == null) continue;
                    if (options.includeRemote != null && !options.includeRemote(pair.Key)) continue;
                    Track("net:" + pair.Key, rp.BasisAvatar, false, rp.UUID, rp.DisplayName, pair.Key, rp.AvatarMetaData, rp.RemoteAvatarDriver?.JiggleRigs);
                }
            }
            if (scene != null && (Frame == 0 || scene.RegistryChanged))
            {
                int before = scene.skinnedRoots.Count;
                scene.Scan(assets, Frame);
                for (int i = before; i < scene.skinnedRoots.Count; i++)
                {
                    (Transform root, Animator animator, string source) = scene.skinnedRoots[i];
                    BasisCaptureActor a = NewActor("object", root.name, null, 0, false);
                    a.avatarName = source;
                    a.Build(root, root, animator, null, assets);
                    live["obj:" + root.GetEntityId()] = a;
                }
            }
            foreach (KeyValuePair<string, BasisCaptureActor> pair in live) if (pair.Key.StartsWith("obj:", StringComparison.Ordinal)) { if (pair.Value.Alive) seen.Add(pair.Key); }
            List<string> gone = null;
            foreach (KeyValuePair<string, BasisCaptureActor> pair in live) if (!seen.Contains(pair.Key)) (gone ??= new List<string>()).Add(pair.Key);
            if (gone != null) foreach (string key in gone) { live[key].endFrame = Frame - 1; live[key].Dispose(); live.Remove(key); }
        }
        private void Track(string key, BasisAvatar avatar, bool isLocal, string uuid, string displayName, ushort netId, BasisLoadableBundle bundle, IList<JiggleRig> jiggle)
        {
            seen.Add(key);
            if (live.TryGetValue(key, out BasisCaptureActor existing))
            {
                if (existing.Alive && ReferenceEquals(existing.owner, avatar)) return;
                existing.endFrame = Frame - 1;
                existing.Dispose();
                live.Remove(key);
            }
            BasisCaptureActor a = NewActor("player", uuid, displayName, netId, isLocal);
            a.avatarName = avatar.name;
            a.avatarUrl = bundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation;
            a.avatarVersion = bundle?.BasisRemoteBundleEncrypted?.RemoteVersionTag;
            a.Build(avatar.transform, avatar, avatar.Animator, jiggle, options.peerMode ? null : assets);
            if (isLocal) foreach (Renderer r in BasisLocalAvatarDriver.FirstPersonOnly) if (r != null) a.hiddenRenderers.Add(r);
            live[key] = a;
        }
        private BasisCaptureActor NewActor(string kind, string uuid, string displayName, ushort netId, bool isLocal)
        {
            BasisCaptureActor a = new BasisCaptureActor { id = actors.Count, kind = kind, uuid = uuid, displayName = displayName, netId = netId, isLocal = isLocal, startFrame = Frame };
            actors.Add(a);
            return a;
        }
        private int WriteCameras()
        {
            int n = Camera.allCamerasCount;
            if (cameraScratch.Length < n) cameraScratch = new Camera[n * 2];
            n = Camera.GetAllCameras(cameraScratch);
            Camera main = BasisLocalCameraDriver.CameraInstance;
            int written = 0;
            for (int i = 0; i < n; i++)
            {
                Camera c = cameraScratch[i];
                if (c == null) continue;
                if (!cameras.TryGetValue(c, out CameraEntry e))
                {
                    e = new CameraEntry { camera = c, id = cameraList.Count, firstFrame = Frame, lastFrame = -1, name = c.name, main = c == main };
                    cameras[c] = e;
                    cameraList.Add(e);
                }
                Transform t = c.transform;
                Matrix4x4 m = t.localToWorldMatrix;
                float[] v = { c.fieldOfView, c.nearClipPlane, c.farClipPlane, c.aspect, c.usePhysicalProperties ? 1f : 0f, c.focalLength, c.sensorSize.x, c.sensorSize.y, c.lensShift.x, c.lensShift.y, c.stereoEnabled ? 1f : 0f, c.targetTexture != null ? 1f : 0f };
                bool changed = e.last == null;
                for (int k = 0; !changed && k < 16; k++) changed = m[k] != e.last[k];
                for (int k = 0; !changed && k < v.Length; k++) changed = v[k] != e.last[16 + k];
                if (!changed) continue;
                e.last ??= new float[16 + v.Length];
                for (int k = 0; k < 16; k++) e.last[k] = m[k];
                Array.Copy(v, 0, e.last, 16, v.Length);
                int start = buffer.BeginRecord(BasisCaptureFormat.RecordCamera, e.id);
                buffer.Byte((byte)(c.isActiveAndEnabled ? 1 : 0));
                buffer.Matrix(m);
                buffer.Int(v.Length);
                for (int k = 0; k < v.Length; k++) buffer.Float(v[k]);
                buffer.EndRecord(start);
                written++;
            }
            Array.Clear(cameraScratch, 0, n);
            return written;
        }
        private static ushort LocalNetId() => BasisNetworkPlayer.LocalPlayer != null ? BasisNetworkPlayer.LocalPlayer.playerId : (ushort)0;
        private static string LocalUuid() => BasisLocalPlayer.Instance != null && !string.IsNullOrEmpty(BasisLocalPlayer.Instance.UUID) ? BasisLocalPlayer.Instance.UUID : "local";
        public void WriteManifest()
        {
            try
            {
                BasisCaptureJson j = new BasisCaptureJson();
                j.BeginObject().Prop("format", "basis.capture").Prop("version", BasisCaptureFormat.Version).Prop("createdUtc", startUtc.ToString("o")).Prop("state", State.ToString()).Prop("error", Error);
                j.BeginObject("app").Prop("version", Application.version).Prop("unity", Application.unityVersion).Prop("platform", Application.platform.ToString()).EndObject();
                j.BeginObject("space").Prop("units", "meters").Prop("up", "+Y").Prop("forward", "+Z").Prop("handedness", "left").Prop("matrixOrder", "column-major").EndObject();
                BasisLocalPlayer local = BasisLocalPlayer.Instance;
                j.BeginObject("recorder").Prop("uuid", LocalUuid()).Prop("displayName", local != null ? local.DisplayName : null).Prop("netId", (int)LocalNetId()).Prop("peerMode", options.peerMode).Prop("session", (long)options.sessionId).Prop("requester", (int)options.requester).Prop("requesterStartTicks", options.requesterStartTicks).EndObject();
                j.BeginObject("time").Prop("startServerTicks", StartServerTicks).Prop("ticksPerSecond", TimeSpan.TicksPerSecond).Prop("frames", Frame).Prop("duration", Elapsed).EndObject();
                j.BeginObject("options").Prop("remotes", options.remotes).Prop("scene", options.scene).Prop("geometry", options.geometry).Prop("microphone", options.microphone).Prop("rateHz", options.rateHz).Prop("playerConsent", options.includeRemote != null).EndObject();
                WriteEnvironment(j);
                j.BeginArray("content");
                foreach (BasisRuntimeSpawnRegistry.SpawnInstance si in BasisRuntimeSpawnRegistry.GetAll()) j.BeginObject().Prop("mode", si.SpawnMode.ToString()).Prop("method", si.SpawnMethod.ToString()).Prop("url", si.Url).Prop("loadedNetId", si.LoadedNetID).Prop("creator", si.UUIDOfCreator).Prop("uniqueVersion", si.bundleConnector != null ? si.bundleConnector.UniqueVersion : null).Prop("spawnedUtc", si.SpawnedUtc.ToString("o")).EndObject();
                j.EndArray();
                j.BeginArray("actors");
                foreach (BasisCaptureActor a in actors) a.WriteManifest(j);
                j.EndArray();
                if (scene != null) scene.WriteManifest(j); else j.BeginArray("sceneObjects").EndArray();
                j.BeginArray("cameras");
                foreach (CameraEntry c in cameraList) j.BeginObject().Prop("id", c.id).Prop("name", c.name).Prop("main", c.main).Prop("firstFrame", c.firstFrame).Prop("lastFrame", c.lastFrame).EndObject();
                j.EndArray();
                j.BeginArray("lightmaps");
                LightmapData[] maps = options.scene ? LightmapSettings.lightmaps : null;
                for (int i = 0; maps != null && i < maps.Length; i++) j.BeginObject().Prop("index", i).Prop("color", maps[i].lightmapColor != null ? assets.TextureFor(maps[i].lightmapColor, false) : -1).Prop("direction", maps[i].lightmapDir != null ? assets.TextureFor(maps[i].lightmapDir, false) : -1).EndObject();
                j.EndArray();
                assets.WriteManifest(j);
                j.BeginArray("audio");
                if (mic != null) j.BeginObject().Prop("uuid", LocalUuid()).Prop("file", $"{BasisCaptureFormat.AudioFolder}/{BasisCaptureFormat.SafeFileName(LocalUuid())}.wav").Prop("sampleRate", mic.SampleRate).Prop("startServerTicks", mic.StartServerTicks).Prop("samples", mic.Samples).EndObject();
                j.EndArray();
                j.EndObject();
                File.WriteAllText(Path.Combine(Folder, BasisCaptureFormat.ManifestFile), j.ToString(), new System.Text.UTF8Encoding(false));
                assets.WriteMaterials();
            }
            catch (Exception e)
            {
                BasisDebug.LogError("Capture manifest write failed: " + e.Message);
            }
        }
        private static readonly Vector3[] probeDirections = { Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
        private void WriteEnvironment(BasisCaptureJson j)
        {
            j.BeginObject("environment").Prop("ambientMode", RenderSettings.ambientMode.ToString()).Prop("ambientLight", RenderSettings.ambientLight).Prop("ambientSkyColor", RenderSettings.ambientSkyColor).Prop("ambientEquatorColor", RenderSettings.ambientEquatorColor).Prop("ambientGroundColor", RenderSettings.ambientGroundColor).Prop("ambientIntensity", RenderSettings.ambientIntensity).Prop("reflectionIntensity", RenderSettings.reflectionIntensity);
            j.Prop("fog", RenderSettings.fog).Prop("fogMode", RenderSettings.fogMode.ToString()).Prop("fogColor", RenderSettings.fogColor).Prop("fogDensity", RenderSettings.fogDensity).Prop("fogStart", RenderSettings.fogStartDistance).Prop("fogEnd", RenderSettings.fogEndDistance);
            j.Prop("skybox", RenderSettings.skybox != null && options.scene ? assets.MaterialFor(RenderSettings.skybox) : -1).Prop("sun", RenderSettings.sun != null ? RenderSettings.sun.name : null);
            Color[] results = new Color[probeDirections.Length];
            RenderSettings.ambientProbe.Evaluate(probeDirections, results);
            WriteProbe(j, "ambientProbe", results);
            j.Prop("probePosition", actorCentre);
            try
            {
                LightProbes.GetInterpolatedProbe(actorCentre, null, out UnityEngine.Rendering.SphericalHarmonicsL2 sh);
                sh.Evaluate(probeDirections, results);
                WriteProbe(j, "lightProbe", results);
            }
            catch (Exception e) { j.Prop("lightProbeError", e.Message); }
            WritePost(j);
            if (environment != null) j.BeginObject("hdri").Prop("file", environment.Done && environment.warning == null ? environment.file : null).Prop("sky", environment.Done && environment.warning == null ? environment.skyFile : null).Prop("position", environment.position).Prop("warning", environment.warning).Prop("width", BasisCaptureEnvironment.Width).Prop("height", BasisCaptureEnvironment.Height).EndObject();
            j.EndObject();
        }
        private static void WritePost(BasisCaptureJson j)
        {
            try
            {
                UnityEngine.Rendering.VolumeStack stack = UnityEngine.Rendering.VolumeManager.instance.stack;
                Camera cam = BasisLocalCameraDriver.CameraInstance;
                bool post = cam != null && cam.TryGetComponent(out UnityEngine.Rendering.Universal.UniversalAdditionalCameraData data) && data.renderPostProcessing;
                j.BeginObject("post").Prop("enabled", post);
                var ca = stack?.GetComponent<UnityEngine.Rendering.Universal.ColorAdjustments>();
                if (ca != null && ca.active) j.Prop("exposure", ca.postExposure.value).Prop("contrast", ca.contrast.value).Prop("saturation", ca.saturation.value).Prop("hueShift", ca.hueShift.value).Prop("colorFilter", ca.colorFilter.value);
                var tm = stack?.GetComponent<UnityEngine.Rendering.Universal.Tonemapping>();
                j.Prop("tonemapping", tm != null && tm.active ? tm.mode.value.ToString() : "None");
                var wb = stack?.GetComponent<UnityEngine.Rendering.Universal.WhiteBalance>();
                if (wb != null && wb.active) j.Prop("temperature", wb.temperature.value).Prop("tint", wb.tint.value);
                var bl = stack?.GetComponent<UnityEngine.Rendering.Universal.Bloom>();
                if (bl != null && bl.active) j.Prop("bloomIntensity", bl.intensity.value).Prop("bloomThreshold", bl.threshold.value).Prop("bloomScatter", bl.scatter.value).Prop("bloomTint", bl.tint.value);
                j.EndObject();
            }
            catch (Exception e) { j.Prop("postError", e.Message); }
        }
        private static void WriteProbe(BasisCaptureJson j, string name, Color[] c)
        {
            j.BeginObject(name).Prop("up", c[0]).Prop("down", c[1]).Prop("forward", c[2]).Prop("back", c[3]).Prop("right", c[4]).Prop("left", c[5]).EndObject();
        }
        private void Cleanup()
        {
            foreach (BasisCaptureActor a in live.Values) a.Dispose();
            live.Clear();
            scene?.Dispose();
            if (handles.IsCreated) handles.Dispose();
            mic?.Dispose();
            try { writer?.Dispose(); } catch { }
            writer = null;
        }
    }
}
