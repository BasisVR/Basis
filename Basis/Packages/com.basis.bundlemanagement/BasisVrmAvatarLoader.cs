using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using GatorDragonGames.JigglePhysics;
using GLTFast;
using UnityEngine;

public static class BasisVrmAvatarLoader
{
    public const long MaxTriangles = 8_000_000;
    public const long MaxMemoryBytes = 3L * 1024 * 1024 * 1024;
    public const int MaxJsonBytes = 64 * 1024 * 1024;
    public const string CachePlatform = "Generic";
    private const int VisemeAa = 10, VisemeE = 11, VisemeIh = 12, VisemeOh = 13, VisemeOu = 14, MaxCollidersPerRig = 32;
    private static readonly (string Expression, string Shape)[] BakedExpressions = { ("happy", "vrm happy"), ("relaxed", "vrm relaxed smile"), ("surprised", "vrm surprised"), ("sad", "vrm sad") };
    private static readonly ConditionalWeakTable<BasisBundleConnector, BasisVrmDocument> documents = new ConditionalWeakTable<BasisBundleConnector, BasisVrmDocument>();
    private sealed class Scene
    {
        public readonly Dictionary<uint, GameObject> Nodes = new Dictionary<uint, GameObject>();
        public readonly Dictionary<uint, List<Renderer>> NodeRenderers = new Dictionary<uint, List<Renderer>>();
        public readonly Dictionary<int, List<Renderer>> MeshRenderers = new Dictionary<int, List<Renderer>>();
        public readonly List<(Renderer Renderer, int[] Materials)> Slots = new List<(Renderer, int[])>();
        public Transform Node(int index) => index >= 0 && Nodes.TryGetValue((uint)index, out GameObject node) && node != null ? node.transform : null;
        public SkinnedMeshRenderer Resolve(BasisVrmDocument.Bind bind)
        {
            List<Renderer> renderers = Renderers(bind);
            for (int Index = 0; renderers != null && Index < renderers.Count; Index++)
            {
                if (renderers[Index] is SkinnedMeshRenderer smr && smr.sharedMesh != null && bind.Index >= 0 && bind.Index < smr.sharedMesh.blendShapeCount) return smr;
            }
            return null;
        }
        public List<Renderer> Renderers(BasisVrmDocument.Bind bind)
        {
            if (bind.Node >= 0) return NodeRenderers.TryGetValue((uint)bind.Node, out List<Renderer> byNode) ? byNode : null;
            return bind.Mesh >= 0 && MeshRenderers.TryGetValue(bind.Mesh, out List<Renderer> byMesh) ? byMesh : null;
        }
    }
    public static bool TryGetDocument(BasisBundleConnector connector, out BasisVrmDocument document)
    {
        document = null;
        return connector != null && documents.TryGetValue(connector, out document);
    }
    public static async Task LoadTemplate(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, string progressKey, CancellationToken cancellationToken, long maxDownloadBytes)
    {
        BasisLoadableBundle bundle = wrapper.LoadableBundle;
        bundle.BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle();
        if (wrapper.HasGltfTemplate) return;
        string location = bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
        bool networkSourced = bundle.BasisRemoteBundleEncrypted.IsNetworkSourced;
        bool remote = !(!networkSourced && BasisIOManagement.TryResolveLocalBeePath(location, out _));
        report.ReportProgress(progressKey, 5, "Reading VRM");
        byte[] data = null;
        string version = null;
        if (remote)
        {
            (data, version) = await ReadCache(wrapper, location, cancellationToken);
        }
        bool fromCache = data != null;
        if (data == null)
        {
            (byte[] fetched, string fetchError) = await Fetch(location, networkSourced, maxDownloadBytes, cancellationToken);
            if (fetched == null) throw new Exception($"VRM avatar {location} could not be read: {fetchError}");
            data = fetched;
        }
        cancellationToken.ThrowIfCancellationRequested();
        report.ReportProgress(progressKey, 50, "Checking VRM");
        (BasisGltfCheckResult check, BasisVrmDocument document, string parseError, string hash, long triangleCount) = await BasisTasks.Run(() =>
        {
            BasisGltfCheckResult result = BasisGltfCheck.Inspect(data, true, MaxTriangles, MaxMemoryBytes, true, out long triangles, out long bytes);
            BasisVrmDocument parsed = null;
            string error = null;
            if (result == BasisGltfCheckResult.Ok) BasisVrmDocument.TryParse(data, out parsed, out error);
            return (result, parsed, error, Hash(data), triangles);
        });
        if (check != BasisGltfCheckResult.Ok) throw new Exception($"VRM avatar {location} was refused by the glTF safety check ({check}).");
        if (document == null) throw new Exception($"VRM avatar {location}: {parseError}");
        version = hash;
        if (remote && !fromCache) await WriteCache(wrapper, location, data, version);
        cancellationToken.ThrowIfCancellationRequested();
        string name = !string.IsNullOrWhiteSpace(document.Title) ? document.Title.Trim() : SafeFileName(location);
        string thumbnail = document.ThumbnailOffset >= 0 && document.ThumbnailOffset + document.ThumbnailLength <= data.LongLength ? Convert.ToBase64String(data, (int)document.ThumbnailOffset, document.ThumbnailLength) : null;
        bundle.BasisBundleConnector = BuildConnector(name, document, version, data.LongLength, triangleCount, thumbnail);
        report.ReportProgress(progressKey, 60, "Building VRM avatar");
        if (!await BuildTemplate(wrapper, data, document, name)) throw new Exception($"VRM avatar {location} could not be built.");
        report.ReportProgress(progressKey, 100, "VRM avatar ready");
    }
    public static BasisBundleConnector BuildConnector(string name, BasisVrmDocument document, string version, long length, long triangles = 0, string thumbnail = null)
    {
        string description = string.IsNullOrWhiteSpace(document?.Author) ? "VRM avatar" : "VRM avatar by " + document.Author.Trim();
        BasisBundleGenerated generated = new BasisBundleGenerated(version ?? string.Empty, BasisBundleConnector.GltfAssetMode, name, 0, false, string.Empty, BasisBundleConnector.GenericPlatform, length);
        BasisBundleConnector.BasisMetaData metaData = new BasisBundleConnector.BasisMetaData { ContentKind = BasisBundleConnector.AvatarContentKind, TrianglesCount = triangles, GraphicsPipeline = "URP" };
        BasisBundleDescription bundleDescription = new BasisBundleDescription(name, description);
        if (document != null) bundleDescription.Authorship = new BasisBundleAuthorship { Author = document.Author, Contact = document.Contact, License = document.LicenseText, Credits = document.Credits };
        BasisBundleConnector connector = new BasisBundleConnector(version ?? string.Empty, bundleDescription, new[] { generated }, thumbnail, new BasisBounds(Vector3.zero, Vector3.one), metaData);
        if (document != null) documents.AddOrUpdate(connector, document);
        return connector;
    }
    private static string Hash(byte[] data)
    {
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(data), 0, 16).Replace("-", string.Empty).ToLowerInvariant();
    }
    private static string SafeFileName(string location)
    {
        try
        {
            string path = Uri.TryCreate(location, UriKind.Absolute, out Uri uri) ? (uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath)) : location;
            string name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? "VRM Avatar" : name;
        }
        catch (ArgumentException)
        {
            return "VRM Avatar";
        }
    }
    private static async Task<string> CachedPath(string location)
    {
        (bool found, BasisBEEExtensionMeta meta) = await BasisLoadHandler.IsMetaDataOnDiscAsync(location);
        string path = found ? meta?.StoredLocal?.DownloadedBeeFileLocation : null;
        return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }
    private static async Task<(byte[] Data, string Version)> ReadCache(BasisTrackedBundleWrapper wrapper, string location, CancellationToken cancellationToken)
    {
        try
        {
            (bool found, BasisBEEExtensionMeta meta) = await BasisLoadHandler.IsMetaDataOnDiscAsync(location);
            string path = found ? meta?.StoredLocal?.DownloadedBeeFileLocation : null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return (null, null);
            if (!await BasisBeeManagement.CacheIsCurrentForRequestedVersionAsync(wrapper, meta, location, true, cancellationToken)) return (null, null);
            byte[] data = await BasisTasks.Run(() => File.ReadAllBytes(path));
            if (BasisVrmDocument.LooksLikeGlb(data) && BitConverter.ToUInt32(data, 8) == data.Length && string.Equals(await BasisTasks.Run(() => Hash(data)), meta.UniqueVersion, StringComparison.OrdinalIgnoreCase))
            {
                BasisDebug.Log($"VRM avatar {location} served from the disk cache.", BasisDebug.LogTag.Event);
                return (data, meta.UniqueVersion);
            }
            BasisDebug.LogWarning($"Cached VRM for {location} did not match its record; downloading again.", BasisDebug.LogTag.Event);
            BasisContentVersion.Invalidate(location);
        }
        catch (Exception exception)
        {
            BasisDebug.LogWarning($"VRM cache read for {location} failed ({exception.Message}); downloading again.", BasisDebug.LogTag.Event);
        }
        return (null, null);
    }
    private static async Task WriteCache(BasisTrackedBundleWrapper wrapper, string location, byte[] data, string version)
    {
        try
        {
            string path = BasisIOManagement.GenerateFilePath(BasisIOManagement.SourceCacheTag(location) + "." + version + ".vrm", BasisBeeConstants.AssetBundlesFolder);
            await BasisTasks.Run(() =>
            {
                string temporary = path + ".part";
                File.WriteAllBytes(temporary, data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            });
            string tag = string.Empty;
            if (!BasisServerHostedBee.IsHostedUrl(location))
            {
                try
                {
                    BeeResult<BasisIOManagement.BasisRemoteValidator> validator = await BasisIOManagement.FetchRemoteValidatorAsync(location);
                    if (validator.IsSuccess && validator.Value.HasValue) tag = validator.Value.Tag ?? string.Empty;
                }
                catch (Exception exception)
                {
                    BasisDebug.Log($"VRM validator fetch for {location} failed ({exception.Message}).", BasisDebug.LogTag.Event);
                }
            }
            await BasisLoadHandler.AddDiscInfo(new BasisBEEExtensionMeta
            {
                StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted.Clone(),
                StoredLocal = new BasisStoredEncryptedBundle { DownloadedBeeFileLocation = path },
                UniqueVersion = version,
                DownloadedPlatform = CachePlatform,
                CachedVersionTag = tag,
                LastValidatedUnixUtc = BasisContentVersion.NowUnixUtc(),
            });
            BasisStorageManagement.EnforceCacheSizeLimit();
        }
        catch (Exception exception)
        {
            BasisDebug.LogWarning($"VRM avatar {location} loaded but could not be cached ({exception.Message}).", BasisDebug.LogTag.Event);
        }
    }
    public static async Task<string> LoadMetaOnly(BasisTrackedBundleWrapper wrapper, CancellationToken cancellationToken)
    {
        BasisLoadableBundle bundle = wrapper.LoadableBundle;
        string location = bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
        bool networkSourced = bundle.BasisRemoteBundleEncrypted.IsNetworkSourced;
        string source = location;
        string cached = BasisIOManagement.TryResolveLocalBeePath(location, out _) && !networkSourced ? null : await CachedPath(location);
        if (cached != null)
        {
            source = new Uri(cached).AbsoluteUri;
            networkSourced = false;
        }
        (byte[] header, long available, string error) = await ReadRange(source, networkSourced, 0, 20, cancellationToken);
        if (header == null) return error;
        if (!BasisVrmDocument.TryGetJsonChunk(header, available < 0 ? long.MaxValue : available, out int length, out error)) return error;
        if (length > MaxJsonBytes) return $"the VRM's glTF JSON is {length} bytes, over the {MaxJsonBytes} byte limit";
        (byte[] json, long ignored, string jsonError) = await ReadRange(source, networkSourced, 20, length, cancellationToken);
        if (json == null) return jsonError;
        (BasisVrmDocument document, string parseError) = await BasisTasks.Run(() =>
        {
            BasisVrmDocument.TryParseJson(System.Text.Encoding.UTF8.GetString(json), length, out BasisVrmDocument parsed, out string failure);
            return (parsed, failure);
        });
        if (document == null) return parseError;
        string thumbnail = null;
        if (document.ThumbnailOffset >= 0)
        {
            (byte[] image, long unused, string imageError) = await ReadRange(source, networkSourced, document.ThumbnailOffset, document.ThumbnailLength, cancellationToken);
            if (image != null) thumbnail = Convert.ToBase64String(image);
        }
        string name = !string.IsNullOrWhiteSpace(document.Title) ? document.Title.Trim() : SafeFileName(location);
        bundle.BasisBundleConnector = BuildConnector(name, document, string.Empty, BitConverter.ToUInt32(header, 8), 0, thumbnail);
        return null;
    }
    private static async Task<(byte[] Data, long Available, string Error)> ReadRange(string location, bool networkSourced, long start, int length, CancellationToken cancellationToken)
    {
        if (!networkSourced && BasisIOManagement.TryResolveLocalBeePath(location, out string localPath))
        {
            return await BasisTasks.Run(() =>
            {
                using FileStream stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (start + length > stream.Length) return ((byte[])null, stream.Length, "the file is shorter than its glTF header says");
                byte[] buffer = new byte[length];
                stream.Position = start;
                int read = 0;
                while (read < length)
                {
                    int got = stream.Read(buffer, read, length - read);
                    if (got <= 0) return ((byte[])null, stream.Length, "the file ended early");
                    read += got;
                }
                return (buffer, stream.Length, (string)null);
            });
        }
        if (BasisIOManagement.IsLocalBeeUrl(location)) return (null, -1, "the file could not be found");
        (byte[] data, string error) = await BasisIOManagement.DownloadRangeAsync(location, start, length, cancellationToken);
        return (data, -1, error);
    }
    private static async Task<(byte[] Data, string Error)> Fetch(string location, bool networkSourced, long maxBytes, CancellationToken cancellationToken)
    {
        (byte[] header, long available, string headerError) = await ReadRange(location, networkSourced, 0, 20, cancellationToken);
        if (header == null) return (null, headerError);
        if (!BasisVrmDocument.LooksLikeGlb(header)) return (null, "the file is not a binary glTF (VRM) file");
        long total = BitConverter.ToUInt32(header, 8);
        if (total > maxBytes || total > int.MaxValue) return (null, $"the file is {total} bytes, over the {maxBytes} byte limit");
        if (available >= 0 && total > available) return (null, "the file is shorter than its glTF header says");
        (byte[] data, long ignored, string error) = await ReadRange(location, networkSourced, 0, (int)total, cancellationToken);
        return (data, error);
    }
    private static async Task<bool> BuildTemplate(BasisTrackedBundleWrapper wrapper, byte[] data, BasisVrmDocument document, string name)
    {
        GltfImport gltf = Application.isPlaying ? new GltfImport(BasisGltfNoDownloadProvider.Instance) : new GltfImport(BasisGltfNoDownloadProvider.Instance, new UninterruptedDeferAgent());
        GameObject holder = null;
        Avatar humanoidAvatar = null;
        List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        try
        {
            ImportSettings importSettings = new ImportSettings { NodeNameMethod = NameImportMethod.Original, AnimationMethod = AnimationMethod.None, GenerateMipMaps = true };
            if (!await gltf.Load(data, null, importSettings))
            {
                BasisDebug.LogError("VRM load: glTF import failed for " + name);
                return false;
            }
            holder = new GameObject("VrmAvatarTemplate " + name);
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(holder);
            holder.SetActive(false);
            Scene scene = new Scene();
            InstantiationSettings instantiationSettings = new InstantiationSettings { Mask = ComponentType.Mesh, SceneObjectCreation = SceneObjectCreation.Always };
            GameObjectInstantiator instantiator = new GameObjectInstantiator(gltf, holder.transform, null, instantiationSettings);
            instantiator.NodeCreated += (index, go) => scene.Nodes[index] = go;
            instantiator.MeshAdded += (go, nodeIndex, meshName, meshResult, joints, rootJoint, weights, numeration) =>
            {
                if (!go.TryGetComponent(out Renderer renderer)) return;
                if (!scene.NodeRenderers.TryGetValue(nodeIndex, out List<Renderer> byNode)) scene.NodeRenderers[nodeIndex] = byNode = new List<Renderer>();
                byNode.Add(renderer);
                if (!scene.MeshRenderers.TryGetValue(meshResult.meshIndex, out List<Renderer> byMesh)) scene.MeshRenderers[meshResult.meshIndex] = byMesh = new List<Renderer>();
                byMesh.Add(renderer);
                scene.Slots.Add((renderer, meshResult.materialIndices));
            };
            if (!await gltf.InstantiateMainSceneAsync(instantiator) || instantiator.SceneTransform == null)
            {
                BasisDebug.LogError("VRM load: scene instantiation failed for " + name);
                return false;
            }
            Transform root = instantiator.SceneTransform;
            root.name = SanitizeName(name);
            root.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.localScale = Vector3.one;
            MakeNamesUnique(root);
            NormalizeBlendShapeWeights(root);
            if (document.SpecVersion == 0)
            {
                Quaternion flip = Quaternion.Euler(0f, 180f, 0f);
                for (int Index = 0; Index < root.childCount; Index++)
                {
                    Transform child = root.GetChild(Index);
                    child.GetLocalPositionAndRotation(out Vector3 position, out Quaternion rotation);
                    child.SetLocalPositionAndRotation(flip * position, flip * rotation);
                }
            }
            BasisGenericAvatarData avatarData = BuildAvatarData(root, document, scene);
            if (avatarData == null) return false;
            Animator animator = root.gameObject.AddComponent<Animator>();
            humanoidAvatar = BasisGltfAvatarLoader.BuildHumanoidAvatar(holder, root.gameObject, avatarData);
            if (humanoidAvatar == null) return false;
            animator.avatar = humanoidAvatar;
            await BasisVrmMaterials.LoadShaderAsync();
            BasisVrmMaterials.Apply(gltf, document, scene.Slots, owned);
            BasisAvatar basisAvatar = root.gameObject.AddComponent<BasisAvatar>();
            basisAvatar.Animator = animator;
            basisAvatar.AnimatorHumanScale = Vector3.one / Basis.Scripts.BasisSdk.Helpers.BasisHelpers.SafeHumanScale(animator.humanScale);
            WireFace(basisAvatar, document, scene);
            BakeExpressions(basisAvatar, document, scene);
            WireEyesAndMouth(basisAvatar, root, document, scene);
            WireFirstPerson(basisAvatar, document, scene);
            AddSprings(root, document, scene);
            basisAvatar.Renders = root.GetComponentsInChildren<Renderer>(true);
            basisAvatar.BasisBundleDescription = wrapper.LoadableBundle.BasisBundleConnector?.BasisBundleDescription;
            basisAvatar.TransformStorage = BasisAvatarTransformStorage.CaptureFrom(animator);
            basisAvatar.HumanScale = animator.humanScale;
            wrapper.GltfImport = gltf;
            wrapper.GltfTemplateHolder = holder;
            wrapper.GltfBuiltAvatar = humanoidAvatar;
            wrapper.GltfOwnedObjects = owned;
            wrapper.GltfTemplateAvatarRoot = root.gameObject;
            BasisDebug.Log($"VRM {document.SpecVersion}.x avatar template ready: {name}", BasisDebug.LogTag.Event);
            return true;
        }
        catch (Exception exception)
        {
            BasisDebug.LogError($"VRM load threw: {exception}");
            return false;
        }
        finally
        {
            if (wrapper.GltfTemplateAvatarRoot == null)
            {
                if (holder != null) Release(holder);
                if (humanoidAvatar != null) Release(humanoidAvatar);
                foreach (UnityEngine.Object leftover in owned) if (leftover != null) Release(leftover);
                gltf.Dispose();
            }
        }
    }
    private static void Release(UnityEngine.Object target)
    {
        if (Application.isPlaying) UnityEngine.Object.Destroy(target);
        else UnityEngine.Object.DestroyImmediate(target);
    }
    private static string SanitizeName(string name)
    {
        string cleaned = name.Replace('/', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "VRM Avatar" : cleaned;
    }
    private static void MakeNamesUnique(Transform root)
    {
        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int Index = 0; Index < all.Length; Index++)
        {
            Transform current = all[Index];
            string baseName = string.IsNullOrEmpty(current.name) ? "Node" : current.name.Replace('/', '_');
            string candidate = baseName;
            for (int Suffix = 1; !used.Add(candidate); Suffix++) candidate = baseName + "_" + Suffix;
            if (!string.Equals(candidate, current.name, StringComparison.Ordinal)) current.name = candidate;
        }
    }
    private static BasisGenericAvatarData BuildAvatarData(Transform root, BasisVrmDocument document, Scene scene)
    {
        List<BasisGenericAvatarData.BasisHumanBoneEntry> humanBones = new List<BasisGenericAvatarData.BasisHumanBoneEntry>();
        foreach (KeyValuePair<HumanBodyBones, int> bone in document.Bones)
        {
            Transform node = scene.Node(bone.Value);
            if (node == null || !node.IsChildOf(root)) continue;
            humanBones.Add(new BasisGenericAvatarData.BasisHumanBoneEntry { HumanName = HumanTrait.BoneName[(int)bone.Key], BoneName = node.name, UseDefaultValues = true });
        }
        if (humanBones.Count == 0)
        {
            BasisDebug.LogError("VRM load: no humanoid bones resolved.");
            return null;
        }
        List<BasisGenericAvatarData.BasisSkeletonBoneEntry> skeleton = new List<BasisGenericAvatarData.BasisSkeletonBoneEntry>();
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int Index = 0; Index < all.Length; Index++)
        {
            Transform current = all[Index];
            skeleton.Add(new BasisGenericAvatarData.BasisSkeletonBoneEntry { Name = current.name, Position = current.localPosition, Rotation = current.localRotation, Scale = current.localScale });
        }
        return new BasisGenericAvatarData { RootNodeName = root.name, HumanBones = humanBones.ToArray(), SkeletonBones = skeleton.ToArray() };
    }
    private static void WireFace(BasisAvatar avatar, BasisVrmDocument document, Scene scene)
    {
        (string Expression, int Slot)[] vowels = { ("aa", VisemeAa), ("ee", VisemeE), ("ih", VisemeIh), ("oh", VisemeOh), ("ou", VisemeOu) };
        Dictionary<SkinnedMeshRenderer, int> votes = new Dictionary<SkinnedMeshRenderer, int>();
        foreach ((string expression, int slot) in vowels)
        {
            if (!document.Expressions.TryGetValue(expression, out List<BasisVrmDocument.Bind> binds)) continue;
            foreach (BasisVrmDocument.Bind bind in binds)
            {
                SkinnedMeshRenderer smr = scene.Resolve(bind);
                if (smr != null) votes[smr] = votes.TryGetValue(smr, out int count) ? count + 1 : 1;
            }
        }
        SkinnedMeshRenderer face = null;
        int best = 0;
        foreach (KeyValuePair<SkinnedMeshRenderer, int> vote in votes)
        {
            if (vote.Value > best)
            {
                best = vote.Value;
                face = vote.Key;
            }
        }
        if (face != null)
        {
            int[] movement = new int[avatar.FaceVisemeMovement.Length];
            for (int Index = 0; Index < movement.Length; Index++) movement[Index] = -1;
            BasisVisemeProfile[] profiles = new BasisVisemeProfile[movement.Length];
            for (int Index = 0; Index < profiles.Length; Index++) profiles[Index] = BasisVisemeProfile.Default;
            bool scaled = false;
            foreach ((string expression, int slot) in vowels)
            {
                if (slot >= movement.Length || !document.Expressions.TryGetValue(expression, out List<BasisVrmDocument.Bind> binds)) continue;
                float weight = 0f;
                foreach (BasisVrmDocument.Bind bind in binds)
                {
                    if (scene.Resolve(bind) != face || bind.Weight <= weight) continue;
                    weight = bind.Weight;
                    movement[slot] = bind.Index;
                }
                if (movement[slot] >= 0 && weight < 0.999f)
                {
                    profiles[slot].OutMax = Mathf.Clamp(weight, 0f, 1f) * 100f;
                    scaled = true;
                }
            }
            avatar.FaceVisemeMesh = face;
            avatar.FaceVisemeMovement = movement;
            if (scaled) avatar.FaceVisemeProfiles = profiles;
        }
        if (!WireBlink(avatar, document, "blink", scene, face)) WireBlinkPair(avatar, document, scene, face);
    }
    private static bool WireBlink(BasisAvatar avatar, BasisVrmDocument document, string expression, Scene scene, SkinnedMeshRenderer preferred)
    {
        if (!document.Expressions.TryGetValue(expression, out List<BasisVrmDocument.Bind> binds) || binds.Count == 0) return false;
        SkinnedMeshRenderer mesh = null;
        float strongest = 0f;
        foreach (BasisVrmDocument.Bind bind in binds)
        {
            SkinnedMeshRenderer smr = scene.Resolve(bind);
            if (smr == null) continue;
            if (smr == preferred)
            {
                mesh = smr;
                break;
            }
            if (bind.Weight > strongest)
            {
                strongest = bind.Weight;
                mesh = smr;
            }
        }
        if (mesh == null) return false;
        List<int> shapes = new List<int>();
        foreach (BasisVrmDocument.Bind bind in binds)
        {
            if (scene.Resolve(bind) == mesh && !shapes.Contains(bind.Index)) shapes.Add(bind.Index);
        }
        avatar.FaceBlinkMesh = mesh;
        avatar.BlinkViseme = shapes.ToArray();
        return true;
    }
    private static void WireBlinkPair(BasisAvatar avatar, BasisVrmDocument document, Scene scene, SkinnedMeshRenderer preferred)
    {
        if (!WireBlink(avatar, document, "blinkLeft", scene, preferred)) return;
        SkinnedMeshRenderer leftMesh = avatar.FaceBlinkMesh;
        if (!document.Expressions.TryGetValue("blinkRight", out List<BasisVrmDocument.Bind> binds)) return;
        List<int> shapes = new List<int>(avatar.BlinkViseme);
        foreach (BasisVrmDocument.Bind bind in binds)
        {
            if (scene.Resolve(bind) == leftMesh && !shapes.Contains(bind.Index)) shapes.Add(bind.Index);
        }
        avatar.BlinkViseme = shapes.ToArray();
    }
    private static void NormalizeBlendShapeWeights(Transform root)
    {
        HashSet<Mesh> done = new HashSet<Mesh>();
        List<(string Name, float Weight, int Start, int Length)> frames = new List<(string, float, int, int)>();
        List<int> indices = new List<int>();
        List<Vector3> deltaVertices = new List<Vector3>(), deltaNormals = new List<Vector3>(), deltaTangents = new List<Vector3>();
        Vector3[] vertices = null, normals = null, tangents = null;
        foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh mesh = smr.sharedMesh;
            if (mesh == null || !mesh.isReadable || mesh.blendShapeCount == 0 || !done.Add(mesh)) continue;
            bool needed = false;
            for (int Shape = 0; Shape < mesh.blendShapeCount && !needed; Shape++) needed = mesh.GetBlendShapeFrameWeight(Shape, mesh.GetBlendShapeFrameCount(Shape) - 1) != 100f;
            if (!needed) continue;
            int count = mesh.vertexCount;
            if (vertices == null || vertices.Length != count)
            {
                vertices = new Vector3[count];
                normals = new Vector3[count];
                tangents = new Vector3[count];
            }
            frames.Clear();
            indices.Clear();
            deltaVertices.Clear();
            deltaNormals.Clear();
            deltaTangents.Clear();
            for (int Shape = 0; Shape < mesh.blendShapeCount; Shape++)
            {
                int frameCount = mesh.GetBlendShapeFrameCount(Shape);
                float last = mesh.GetBlendShapeFrameWeight(Shape, frameCount - 1);
                float scale = last > 0f ? 100f / last : 1f;
                string name = mesh.GetBlendShapeName(Shape);
                for (int Frame = 0; Frame < frameCount; Frame++)
                {
                    mesh.GetBlendShapeFrameVertices(Shape, Frame, vertices, normals, tangents);
                    int frameStart = indices.Count;
                    for (int Index = 0; Index < count; Index++)
                    {
                        if (vertices[Index] == Vector3.zero && normals[Index] == Vector3.zero && tangents[Index] == Vector3.zero) continue;
                        indices.Add(Index);
                        deltaVertices.Add(vertices[Index]);
                        deltaNormals.Add(normals[Index]);
                        deltaTangents.Add(tangents[Index]);
                    }
                    frames.Add((name, mesh.GetBlendShapeFrameWeight(Shape, Frame) * scale, frameStart, indices.Count - frameStart));
                }
            }
            mesh.ClearBlendShapes();
            Array.Clear(vertices, 0, count);
            Array.Clear(normals, 0, count);
            Array.Clear(tangents, 0, count);
            foreach ((string name, float weight, int frameStart, int length) in frames)
            {
                for (int Index = frameStart; Index < frameStart + length; Index++)
                {
                    int vertex = indices[Index];
                    vertices[vertex] = deltaVertices[Index];
                    normals[vertex] = deltaNormals[Index];
                    tangents[vertex] = deltaTangents[Index];
                }
                mesh.AddBlendShapeFrame(name, weight, vertices, normals, tangents);
                for (int Index = frameStart; Index < frameStart + length; Index++)
                {
                    int vertex = indices[Index];
                    vertices[vertex] = Vector3.zero;
                    normals[vertex] = Vector3.zero;
                    tangents[vertex] = Vector3.zero;
                }
            }
        }
    }
    private static void BakeExpressions(BasisAvatar avatar, BasisVrmDocument document, Scene scene)
    {
        Vector3[] vertices = null, normals = null, tangents = null, frameVertices = null, frameNormals = null, frameTangents = null;
        foreach ((string expression, string shape) in BakedExpressions)
        {
            if (!document.Expressions.TryGetValue(expression, out List<BasisVrmDocument.Bind> binds) || binds.Count == 0) continue;
            Dictionary<Mesh, List<(int Shape, float Weight)>> perMesh = new Dictionary<Mesh, List<(int, float)>>();
            foreach (BasisVrmDocument.Bind bind in binds)
            {
                List<Renderer> renderers = scene.Renderers(bind);
                if (renderers == null || bind.Weight <= 0f) continue;
                foreach (Renderer renderer in renderers)
                {
                    if (!(renderer is SkinnedMeshRenderer smr) || smr.sharedMesh == null || !smr.sharedMesh.isReadable || bind.Index < 0 || bind.Index >= smr.sharedMesh.blendShapeCount) continue;
                    if (!perMesh.TryGetValue(smr.sharedMesh, out List<(int, float)> list)) perMesh[smr.sharedMesh] = list = new List<(int, float)>();
                    list.Add((bind.Index, bind.Weight));
                }
            }
            foreach (KeyValuePair<Mesh, List<(int Shape, float Weight)>> entry in perMesh)
            {
                Mesh mesh = entry.Key;
                if (mesh.GetBlendShapeIndex(shape) >= 0) continue;
                int count = mesh.vertexCount;
                if (vertices == null || vertices.Length != count)
                {
                    vertices = new Vector3[count];
                    normals = new Vector3[count];
                    tangents = new Vector3[count];
                    frameVertices = new Vector3[count];
                    frameNormals = new Vector3[count];
                    frameTangents = new Vector3[count];
                }
                else
                {
                    Array.Clear(vertices, 0, count);
                    Array.Clear(normals, 0, count);
                    Array.Clear(tangents, 0, count);
                }
                foreach ((int source, float weight) in entry.Value)
                {
                    int frame = mesh.GetBlendShapeFrameCount(source) - 1;
                    if (frame < 0) continue;
                    float frameWeight = mesh.GetBlendShapeFrameWeight(source, frame);
                    float scale = weight * (frameWeight > 0f ? 100f / frameWeight : 1f);
                    mesh.GetBlendShapeFrameVertices(source, frame, frameVertices, frameNormals, frameTangents);
                    for (int Index = 0; Index < count; Index++)
                    {
                        vertices[Index] += frameVertices[Index] * scale;
                        normals[Index] += frameNormals[Index] * scale;
                        tangents[Index] += frameTangents[Index] * scale;
                    }
                }
                mesh.AddBlendShapeFrame(shape, 100f, vertices, normals, tangents);
            }
            if (expression == "happy" && avatar.FaceVisemeMesh != null && perMesh.ContainsKey(avatar.FaceVisemeMesh.sharedMesh)) avatar.laughterBlendTarget = avatar.FaceVisemeMesh.sharedMesh.GetBlendShapeIndex(shape);
        }
    }
    private static void WireEyesAndMouth(BasisAvatar avatar, Transform root, BasisVrmDocument document, Scene scene)
    {
        Transform head = Bone(document, HumanBodyBones.Head, scene);
        if (head == null) return;
        Transform leftEye = Bone(document, HumanBodyBones.LeftEye, scene), rightEye = Bone(document, HumanBodyBones.RightEye, scene);
        Vector3 eye;
        if (document.HasEyeOffset) eye = head.TransformPoint(document.ConvertLocal(document.EyeOffset));
        else if (leftEye != null && rightEye != null) eye = Vector3.Lerp(leftEye.position, rightEye.position, 0.5f);
        else eye = head.position + root.TransformDirection(new Vector3(0f, 0.06f, 0.08f));
        Vector3 mouth = eye + root.TransformDirection(new Vector3(0f, -0.065f, 0.035f));
        avatar.AvatarEyePosition = Basis.Scripts.BasisSdk.Helpers.BasisHelpers.AvatarPositionConversion(root.InverseTransformPoint(eye));
        avatar.AvatarMouthPosition = Basis.Scripts.BasisSdk.Helpers.BasisHelpers.AvatarPositionConversion(root.InverseTransformPoint(mouth));
    }
    private static Transform Bone(BasisVrmDocument document, HumanBodyBones bone, Scene scene) => document.Bones.TryGetValue(bone, out int index) ? scene.Node(index) : null;
    private static void WireFirstPerson(BasisAvatar avatar, BasisVrmDocument document, Scene scene)
    {
        List<Renderer> hidden = new List<Renderer>(), only = new List<Renderer>();
        foreach (BasisVrmDocument.Annotation annotation in document.FirstPerson)
        {
            if (!annotation.ThirdPersonOnly && !annotation.FirstPersonOnly) continue;
            List<Renderer> renderers = scene.Renderers(new BasisVrmDocument.Bind { Node = annotation.Node, Mesh = annotation.Mesh });
            if (renderers == null) continue;
            foreach (Renderer renderer in renderers)
            {
                List<Renderer> target = annotation.ThirdPersonOnly ? hidden : only;
                if (renderer != null && !target.Contains(renderer)) target.Add(renderer);
            }
        }
        avatar.FirstPersonHiddenRenderers = hidden.ToArray();
        avatar.FirstPersonOnlyRenderers = only.ToArray();
    }
    private static void AddSprings(Transform root, BasisVrmDocument document, Scene scene)
    {
        JiggleColliderSerializable[][] groups = BuildColliders(document, scene);
        List<(Transform Bone, BasisVrmDocument.Spring Spring)> roots = new List<(Transform, BasisVrmDocument.Spring)>();
        foreach (BasisVrmDocument.Spring spring in document.Springs)
        {
            if (spring.Roots == null) continue;
            foreach (int index in spring.Roots)
            {
                Transform node = scene.Node(index);
                if (node == null || node == root || !node.IsChildOf(root) || node.childCount == 0) continue;
                roots.Add((node, spring));
            }
        }
        roots.Sort((a, b) => Depth(a.Bone).CompareTo(Depth(b.Bone)));
        List<Transform> used = new List<Transform>();
        foreach ((Transform bone, BasisVrmDocument.Spring spring) in roots)
        {
            bool covered = false;
            for (int Index = 0; Index < used.Count && !covered; Index++) covered = bone.IsChildOf(used[Index]);
            if (covered) continue;
            used.Add(bone);
            List<JiggleColliderSerializable> colliders = new List<JiggleColliderSerializable>();
            if (spring.ColliderGroups != null)
            {
                foreach (int group in spring.ColliderGroups)
                {
                    if (group < 0 || group >= groups.Length) continue;
                    foreach (JiggleColliderSerializable collider in groups[group])
                    {
                        if (colliders.Count < MaxCollidersPerRig) colliders.Add(collider);
                    }
                }
            }
            JiggleRigData data = JiggleRigData.Default();
            data.rootBone = bone;
            JiggleTreeInputParameters parameters = JiggleTreeInputParameters.Default();
            parameters.stiffness = new JiggleTreeCurvedFloat(Mathf.Clamp01(0.3f + 0.2f * Mathf.Max(0f, spring.Stiffness)));
            parameters.drag = new JiggleTreeCurvedFloat(Mathf.Clamp01(spring.Drag));
            parameters.gravity = new JiggleTreeCurvedFloat(Mathf.Clamp(spring.Gravity, 0f, 2f));
            parameters.angleLimitToggle = false;
            bool collide = colliders.Count > 0 && spring.HitRadius > 0f;
            parameters.advancedToggle = collide;
            parameters.collisionToggle = collide;
            parameters.stretch = new JiggleTreeCurvedFloat(0f);
            Vector3 lossy = bone.lossyScale;
            parameters.collisionRadius = new JiggleTreeCurvedFloat(Mathf.Max(0f, spring.HitRadius) * (Mathf.Abs(lossy.x) + Mathf.Abs(lossy.y) + Mathf.Abs(lossy.z)) / 3f);
            data.jiggleTreeInputParameters = parameters;
            data.jiggleColliders = colliders.ToArray();
            data.BuildNormalizedDistanceFromRootList();
            bone.gameObject.AddComponent<JiggleRig>().Configure(data);
        }
    }
    private static JiggleColliderSerializable[][] BuildColliders(BasisVrmDocument document, Scene scene)
    {
        JiggleColliderSerializable?[] built = new JiggleColliderSerializable?[document.Colliders.Count];
        for (int Index = 0; Index < built.Length; Index++)
        {
            BasisVrmDocument.Collider source = document.Colliders[Index];
            Transform node = scene.Node(source.Node);
            if (node == null || source.Radius <= 0f) continue;
            Vector3 offset = document.ConvertLocal(source.Offset);
            if (!source.Capsule)
            {
                built[Index] = new JiggleColliderSerializable { transform = node, collider = new JiggleCollider { type = JiggleCollider.JiggleColliderType.Sphere, radius = source.Radius, localOffset = offset } };
                continue;
            }
            Vector3 tail = document.ConvertLocal(source.Tail), axis = tail - offset;
            GameObject holder = new GameObject("VRM Collider " + Index);
            holder.transform.SetParent(node, false);
            holder.transform.SetLocalPositionAndRotation((offset + tail) * 0.5f, axis.sqrMagnitude > 1e-10f ? Quaternion.FromToRotation(Vector3.up, axis) : Quaternion.identity);
            built[Index] = new JiggleColliderSerializable { transform = holder.transform, collider = new JiggleCollider { type = JiggleCollider.JiggleColliderType.Capsule, radius = source.Radius, height = axis.magnitude, capsuleAxis = JiggleCollider.CapsuleAxis.Y } };
        }
        JiggleColliderSerializable[][] groups = new JiggleColliderSerializable[document.ColliderGroups.Count][];
        for (int Group = 0; Group < groups.Length; Group++)
        {
            List<JiggleColliderSerializable> members = new List<JiggleColliderSerializable>();
            foreach (int index in document.ColliderGroups[Group])
            {
                if (index >= 0 && index < built.Length && built[index].HasValue) members.Add(built[index].Value);
            }
            groups[Group] = members.ToArray();
        }
        return groups;
    }
    private static int Depth(Transform transform)
    {
        int depth = 0;
        for (Transform cursor = transform; cursor != null; cursor = cursor.parent) depth++;
        return depth;
    }
}
