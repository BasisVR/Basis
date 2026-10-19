using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds a far avatar as a REAL <see cref="BasisAvatar"/> from the
/// <see cref="BasisFarLodPayload"/> carried in the bee connector: a runtime skeleton, a
/// humanoid rig rebuilt with <see cref="AvatarBuilder"/> (the same way generic glTF avatars
/// are rebuilt), and a shared low-poly skinned mesh. The result installs through the exact
/// same factory/calibration/registration pipeline as every other avatar — loading avatar,
/// bundle avatar, glTF avatar and far avatar are all the same thing to the rest of the
/// system. Nothing is ever hidden or disabled; swapping representations swaps the avatar.
///
/// Mesh, texture, material and humanoid rig are shared per avatar version across every
/// player wearing it; only the ~20-transform skeleton is per player.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisFarAvatarBuilder
{
    public sealed class SharedAssets
    {
        public string UniqueVersion;
        public BasisFarLodPayload Payload;
        public Mesh Mesh;
        public Texture2D Texture;
        public Material Material;
        public Avatar HumanoidRig;
        public int HipsIndex;

        /// <summary>
        /// The live wearers of this version — the reference itself, not a mirror of one. A separate
        /// count used to decide the teardown while this list only got to veto it, which left every
        /// way the two could disagree able to drive that count to zero under a live renderer: a
        /// release keyed by version string arriving from an instance whose entry had been evicted
        /// and rebuilt under the same key, or a failure path that released a wearer and then
        /// destroyed it so it released again. Membership IS the reference — a wearer joins in
        /// <see cref="BuildAvatar"/> and leaves in <see cref="ReleaseShared"/>, and a release from
        /// anything not in here gives back nothing instead of somebody else's slot.
        /// </summary>
        public readonly List<BasisFarAvatarInstance> Wearers = new List<BasisFarAvatarInstance>(4);

        /// <summary>
        /// The fully wired far avatar for this version, built once and kept inactive under
        /// <see cref="PrototypeHolder"/>. Every wearer after the first is a single
        /// <see cref="Object.Instantiate"/> of it instead of ~23 GameObject creations, four
        /// AddComponents and a 55-slot bone capture on the transmit tick.
        /// </summary>
        public GameObject Prototype;
        public GameObject PrototypeHolder;
    }

    /// <summary>
    /// Logs every shared-asset acquire and release with the resulting wearer count. Off by default;
    /// switch it on to see which versions are being built, shared and retired when a far avatar
    /// renders wrong — a mesh that dies under a live wearer shows up here as the release that
    /// reaches zero too early.
    /// </summary>
    public static bool TraceSharedLifetime;

    /// <summary>
    /// Destroy that also works outside play mode. The builder runs in edit mode too (the SDK far
    /// LOD tester, and the edit-mode tests that cover this lifetime), where Object.Destroy is
    /// refused and would silently leak every partial build.
    /// </summary>
    private static void DestroyObject(Object target)
    {
        if (target == null)
        {
            return;
        }
        if (Application.isPlaying)
        {
            Object.Destroy(target);
        }
        else
        {
            Object.DestroyImmediate(target);
        }
    }

    /// <summary>
    /// Keeps the prototype holder across additive world switches. DontDestroyOnLoad throws
    /// outside play mode, and nothing needs to survive a scene load there anyway.
    /// </summary>
    private static void KeepAlive(GameObject target)
    {
        if (Application.isPlaying)
        {
            Object.DontDestroyOnLoad(target);
        }
    }

    private static readonly Dictionary<string, SharedAssets> SharedByVersion = new Dictionary<string, SharedAssets>(8);
    private static readonly int BaseMapProperty = Shader.PropertyToID("_BaseMap");
    private static readonly int MinBrightnessProperty = Shader.PropertyToID("_MinBrightness");
    private static readonly int MaxBrightnessProperty = Shader.PropertyToID("_MaxBrightness");
    private static Shader sFarAvatarShader;

    // Install phase markers. BasisAvatarFarLOD's FarLodInstall marker reports one number for the
    // whole swap; these split it so a spike attributes to the stage that owns it — first-wearer
    // asset construction, the per-player clone, or the factory swap and remote calibration.

    /// <summary>
    /// Builds this player's far avatar and installs it as their current avatar through the
    /// normal factory path (old avatar deleted, remote calibration, bone-job registration).
    /// Non-blocking: a version whose shared assets exist installs within this call; a first
    /// wearer kicks the payload parse to a worker thread and returns false — the caller keeps
    /// whatever is worn and retries once the parse lands. Returns false with the payload
    /// marked unusable when it refuses to build, so there is no retry spam.
    /// </summary>
    public static bool TryInstall(BasisRemotePlayer remote)
    {
        if (remote == null || remote.IsDestroyed)
        {
            return false;
        }
        if (!ResolvePayload(remote, out string uniqueVersion, out string payloadBase64))
        {
            return false;
        }
        if (WornFarVersion(remote) == uniqueVersion)
        {
            return true;
        }
        if (IsSharedUsable(uniqueVersion))
        {
            return InstallWithPayload(remote, uniqueVersion, null);
        }
        Task<BasisFarLodPayload> parse = StartOrGetParse(uniqueVersion, payloadBase64);
        if (!parse.IsCompleted)
        {
            return false;
        }
        return InstallWithPayload(remote, uniqueVersion, ConsumeParse(uniqueVersion, parse));
    }

    /// <summary>
    /// Starts the worker-thread payload parse for this player's resolved far avatar version
    /// WITHOUT installing anything. Installs are transmit-tick-only: CreateAvatar and the
    /// load error path run on IO/task continuations, where the remote-bone and jiggle
    /// pipelines can have scheduled-but-unjoined jobs — an install there mutates their
    /// TransformAccessArrays mid-flight (the flung-skeleton / Invalid-AABB class). Callers
    /// keep the current avatar worn; the tick's <see cref="TryInstall"/> swaps at the safe
    /// point once the parse lands.
    /// </summary>
    public static void PrewarmParse(BasisRemotePlayer remote)
    {
        if (remote == null || remote.IsDestroyed)
        {
            return;
        }
        if (!ResolvePayload(remote, out string uniqueVersion, out string payloadBase64))
        {
            return;
        }
        if (IsSharedUsable(uniqueVersion) || WornFarVersion(remote) == uniqueVersion)
        {
            return;
        }
        StartOrGetParse(uniqueVersion, payloadBase64);
    }

    /// <summary>
    /// The far avatar payload/version this player should be wearing: the override captured
    /// off the original bundle's connector when present (the current AvatarMetaData may
    /// already point at the loading avatar), else the current connector.
    /// </summary>
    private static bool ResolvePayload(BasisRemotePlayer remote, out string uniqueVersion, out string payloadBase64)
    {
        if (!string.IsNullOrEmpty(remote.FarLodOverridePayload))
        {
            uniqueVersion = remote.FarLodOverrideVersion;
            payloadBase64 = remote.FarLodOverridePayload;
        }
        else
        {
            BasisBundleConnector connector = remote.AvatarMetaData?.BasisBundleConnector;
            uniqueVersion = connector?.UniqueVersion;
            payloadBase64 = connector?.FarLodBase64;
        }
        return !string.IsNullOrEmpty(uniqueVersion) && !string.IsNullOrEmpty(payloadBase64);
    }

    /// <summary>Version of the far avatar this player currently wears, or null.</summary>
    public static string WornFarVersion(BasisRemotePlayer remote)
    {
        // Reads the mirror on BasisAvatar, not the BasisFarAvatarInstance component. The tick
        // asks this for every far-LOD wearer every pass, and a GetComponent per player per tick
        // is the single most expensive thing in that loop. Set together in BuildAvatar; the
        // component is still the release owner.
        BasisAvatar avatar = remote?.BasisAvatar;
        if (avatar != null && avatar.IsFarLodAvatar)
        {
            return avatar.FarLodSharedVersion;
        }
        return null;
    }

    /// <summary>
    /// True when the worn far avatar matches the version the payload resolves to right now —
    /// false for a far avatar left over from a previous avatar record (the tick then swaps
    /// it like any other stale representation).
    /// </summary>
    public static bool IsWearingResolvedVersion(BasisRemotePlayer remote)
    {
        return remote != null && ResolvePayload(remote, out string uniqueVersion, out _) &&
               WornFarVersion(remote) == uniqueVersion;
    }

    /// <summary>Payload parses in flight, keyed by avatar version. Main-thread access only.</summary>
    private static readonly Dictionary<string, Task<BasisFarLodPayload>> sParseInFlight = new Dictionary<string, Task<BasisFarLodPayload>>(4);

    /// <summary>
    /// Starts (or returns the running) worker-thread parse for a version. The parse itself is
    /// pure managed data work — base64 decode, defensive struct parse, and the full mesh
    /// decode to engine-ready arrays — which is exactly the part that used to hitch the main
    /// thread on a first wearer.
    /// </summary>
    private static readonly List<string> sParseSweepScratch = new List<string>(4);

    private static Task<BasisFarLodPayload> StartOrGetParse(string uniqueVersion, string payloadBase64)
    {
        if (sParseInFlight.TryGetValue(uniqueVersion, out Task<BasisFarLodPayload> parse))
        {
            return parse;
        }

        // A parse whose player left before any caller consumed it would pin its decoded
        // payload here forever; drop completed strays once a few stack up (re-parsing a
        // swept version later is correct, just costs the worker again).
        if (sParseInFlight.Count >= 8)
        {
            sParseSweepScratch.Clear();
            foreach (KeyValuePair<string, Task<BasisFarLodPayload>> entry in sParseInFlight)
            {
                if (entry.Value.IsCompleted)
                {
                    sParseSweepScratch.Add(entry.Key);
                }
            }
            for (int Index = 0; Index < sParseSweepScratch.Count; Index++)
            {
                sParseInFlight.Remove(sParseSweepScratch[Index]);
            }
        }

        parse = BasisTasks.Run(() =>
        {
            BasisFarLodPayload payload = BasisFarLodPayload.TryParseBase64(payloadBase64);
            payload?.PrepareDecodedMeshData();
            return payload;
        });
        sParseInFlight[uniqueVersion] = parse;
        return parse;
    }

    /// <summary>
    /// Retires a completed parse task and returns its payload (null when the payload was
    /// refused). TryParseBase64 catches its own failures, so a faulted task is exceptional;
    /// its exception is observed here either way.
    /// </summary>
    private static BasisFarLodPayload ConsumeParse(string uniqueVersion, Task<BasisFarLodPayload> parse)
    {
        if (sParseInFlight.TryGetValue(uniqueVersion, out Task<BasisFarLodPayload> current) && current == parse)
        {
            sParseInFlight.Remove(uniqueVersion);
        }
        if (parse.Status != TaskStatus.RanToCompletion)
        {
            BasisDebug.LogError($"Far avatar payload parse task failed for version {uniqueVersion}: {parse.Exception?.GetBaseException().Message ?? "unknown"}", BasisDebug.LogTag.Avatar);
            return null;
        }
        return parse.Result;
    }

    /// <summary>
    /// Main-thread tail of an install: acquire (or build from a parsed payload) the shared
    /// per-version assets, build the skeleton, and swap it in through the factory.
    /// </summary>
    private static bool InstallWithPayload(BasisRemotePlayer remote, string uniqueVersion, BasisFarLodPayload payload)
    {
        SharedAssets shared;
        using (BasisNetworkMarkers.TransmitFarLodShared.Auto())
        {
            shared = AcquireShared(uniqueVersion, payload);
        }
        if (shared == null)
        {
            remote.MarkFarLodPayloadUnusable();
            return false;
        }

        BasisAvatar avatar;
        using (BasisNetworkMarkers.TransmitFarLodBuild.Auto())
        {
            avatar = BuildAvatar(shared, remote.DisplayName);
        }
        if (avatar == null)
        {
            // A build that failed after wiring its wearer takes that wearer back out itself, so
            // this only has to retire a version nobody ended up on.
            QueueTeardownIfUnworn(shared);
            remote.MarkFarLodPayloadUnusable();
            return false;
        }

        using (BasisNetworkMarkers.TransmitFarLodFactory.Auto())
        {
            BasisAvatarFactory.SetupFarAvatar(remote, avatar);
        }
        if (remote.BasisAvatar != avatar)
        {
            // Calibration failed and the factory recovered onto the fallback (the instance
            // component released the shared assets when it was destroyed) — latch the payload
            // so this doesn't retry every tick. A clone the factory never took is nobody's
            // avatar: destroying it is what hands its wearer slot back, and without that it
            // sits at world origin holding the version alive for the session.
            if (avatar != null)
            {
                DestroyObject(avatar.gameObject);
            }
            remote.MarkFarLodPayloadUnusable();
            return false;
        }
        return true;
    }

    /// <summary>
    /// This player's far avatar, ready for the factory. The first wearer of a version pays for
    /// the prototype build; everyone after that is one <see cref="Object.Instantiate"/> — the
    /// clone's serialized wiring (bone array, root bone, TransformStorage, renderer, animator
    /// rig) is remapped into the copy by Unity, so nothing has to be re-resolved per player.
    /// Returns null when the prototype refuses to build.
    /// </summary>
    private static BasisAvatar BuildAvatar(SharedAssets shared, string displayName)
    {
        if (shared.Prototype == null && !BuildPrototype(shared, displayName))
        {
            return null;
        }

        GameObject clone = Object.Instantiate(shared.Prototype);
        clone.SetActive(true);
        Transform cloneTransform = clone.transform;
        cloneTransform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        if (!clone.TryGetComponent(out BasisAvatar avatar))
        {
            BasisDebug.LogError($"Far avatar clone for {displayName} lost its BasisAvatar component.", BasisDebug.LogTag.Avatar);
            DestroyObject(clone);
            return null;
        }

        // IsFarLodAvatar is [NonSerialized], so the clone starts false — every far LOD gate
        // (WornFarVersion, SeedAfterCalibration, the calibration TPose skip) reads it.
        avatar.IsFarLodAvatar = true;
        // The prototype deliberately carries no version: its OnDestroy must not release the
        // shared assets it belongs to. Only real wearers hold a reference.
        //
        // Added when the clone did not bring one across. This used to be an `if (TryGetComponent)`
        // whose else-branch shipped an avatar that could never release its shared assets — the
        // wearer count then never falls, and the mirror below stays null, which makes the tick's
        // IsWearingResolvedVersion permanently false and reinstalls the avatar every pass.
        if (!clone.TryGetComponent(out BasisFarAvatarInstance instance))
        {
            instance = clone.AddComponent<BasisFarAvatarInstance>();
        }
        instance.SharedVersion = shared.UniqueVersion;
        shared.Wearers.Add(instance);
        // Mirrored onto the avatar in the same breath so WornFarVersion never has to look
        // the component up — it runs for every far-LOD wearer on every transmit tick.
        avatar.FarLodSharedVersion = shared.UniqueVersion;
        // Cloned by value from a hierarchy whose references were remapped, but an avatar built
        // before the rig resolved would carry a null table — fall back rather than ship one.
        if (avatar.TransformStorage?.HumanoidBones == null)
        {
            avatar.TransformStorage = BasisAvatarTransformStorage.CaptureFrom(avatar.Animator);
        }
        if (avatar.FaceVisemeMesh == null || avatar.FaceVisemeMesh.sharedMesh == null)
        {
            BasisDebug.LogError($"Far avatar clone for {displayName} lost its mesh (version {shared.UniqueVersion}, prototype mesh {(shared.Mesh == null ? "destroyed" : "alive")}).", BasisDebug.LogTag.Avatar);
            // Unwired before it dies: it is a counted wearer by this point, and leaving it one
            // means its OnDestroy hands back a slot the caller is about to hand back as well.
            shared.Wearers.Remove(instance);
            instance.SharedVersion = null;
            DestroyObject(clone);
            return null;
        }
        return avatar;
    }

    /// <summary>
    /// Builds the per-version prototype: payload skeleton at its baked T-pose, shared skinned
    /// mesh, humanoid rig, and a wired <see cref="BasisAvatar"/> — a complete avatar the factory
    /// could install as-is. It is left inactive under its build holder and cloned from there for
    /// every wearer. Returns false (and destroys partial state) on failure.
    /// </summary>
    private static bool BuildPrototype(SharedAssets shared, string displayName)
    {
        BasisFarLodPayload payload = shared.Payload;
        int layer = BasisLayerMapper.RemoteAvatarLayer;

        // A prototype whose root died without its holder (or the reverse) leaves the survivor
        // orphaned; the fields are overwritten below, so drop it before it is unreachable.
        if (shared.PrototypeHolder != null)
        {
            DestroyObject(shared.PrototypeHolder);
            shared.PrototypeHolder = null;
            shared.Prototype = null;
        }

        // The root name is part of the humanoid rig's skeleton description and the rig is
        // shared per version, so it must be deterministic — not player-named.
        GameObject root = new GameObject($"Far Avatar {shared.UniqueVersion}") { layer = layer };
        // Built under a holder parked far below the world (nothing visibly flashes) while the
        // root itself stays at LOCAL identity — AvatarBuilder validates the hierarchy against
        // the skeleton description, which declares the root at zero. Parking the root
        // directly would contradict its own description.
        GameObject buildHolder = new GameObject("Far Avatar Build");
        buildHolder.transform.position = new Vector3(0f, -4096f, 0f);
        try
        {
            Transform rootTransform = root.transform;
            rootTransform.SetParent(buildHolder.transform, false);
            rootTransform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            rootTransform.localScale = payload.AuthoredRootScale;

            int boneCount = payload.BoneCount;
            Transform[] bones = new Transform[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                GameObject boneObject = new GameObject(((HumanBodyBones)payload.BoneHumanBodyBone[i]).ToString()) { layer = layer };
                Transform bone = boneObject.transform;
                byte parent = payload.BoneParentIndex[i];
                bone.SetParent(parent == 0xFF ? rootTransform : bones[parent], false);
                bone.SetLocalPositionAndRotation(payload.BoneRestLocalPosition[i], payload.BoneRestLocalRotation[i]);
                bones[i] = bone;
            }
            Transform hips = shared.HipsIndex >= 0 ? bones[shared.HipsIndex] : bones[0];

            GameObject meshObject = new GameObject("Mesh") { layer = layer };
            meshObject.transform.SetParent(rootTransform, false);
            SkinnedMeshRenderer renderer = meshObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = shared.Mesh;
            renderer.sharedMaterial = shared.Material;
            renderer.bones = bones;
            renderer.rootBone = hips;
            if (renderer.sharedMesh == null)
            {
                BasisDebug.LogError($"Far avatar prototype for version {shared.UniqueVersion} has no mesh after assignment (shared mesh {(shared.Mesh == null ? "was already destroyed" : "was rejected by the renderer")}) — refusing to build. Every wearer cloned from this prototype would render nothing.", BasisDebug.LogTag.Avatar);
                DestroyObject(root);
                DestroyObject(buildHolder);
                return false;
            }
            renderer.localBounds = new Bounds(payload.LocalBoundsCenter, payload.LocalBoundsExtents * 2f);
            renderer.quality = SkinQuality.Bone2;
            renderer.updateWhenOffscreen = false;
            renderer.skinnedMotionVectors = false;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            Animator animator = root.AddComponent<Animator>();
            if (shared.HumanoidRig == null)
            {
                shared.HumanoidRig = BuildHumanoidRig(root, bones, payload);
                if (shared.HumanoidRig == null)
                {
                    DestroyObject(root);
                    DestroyObject(buildHolder);
                    return false;
                }
            }
            animator.avatar = shared.HumanoidRig;

            BasisAvatar avatar = root.AddComponent<BasisAvatar>();
            avatar.IsFarLodAvatar = true;
            avatar.Animator = animator;
            avatar.AvatarEyePosition = payload.AvatarEyePosition;
            avatar.AvatarMouthPosition = payload.AvatarMouthPosition;
            // The body IS the face mesh: no visemes to drive, but face-visibility culling
            // then gates remote face/eye work exactly like on a normal avatar.
            avatar.FaceVisemeMesh = renderer;
            avatar.Renders = new Renderer[] { renderer };
            avatar.TransformStorage = BasisAvatarTransformStorage.CaptureFrom(animator);
            avatar.HumanScale = animator.humanScale;

            // SharedVersion is left null on the prototype: its OnDestroy runs like any other
            // instance's, and a version there would release the shared assets it belongs to.
            root.AddComponent<BasisFarAvatarInstance>();

            // Parked and inactive rather than unparented and shipped — the prototype is never
            // worn, only cloned. Inactive keeps its renderer out of culling and its animator
            // off; the holder keeps it out of the scene roots that get walked per frame.
            // It outlives scene changes for the same reason the shared mesh/material do: an
            // additive world switch would otherwise force a rebuild on the next swap.
            root.SetActive(false);
            KeepAlive(buildHolder);
            shared.Prototype = root;
            shared.PrototypeHolder = buildHolder;
            return true;
        }
        catch (System.Exception e)
        {
            BasisDebug.LogError($"Far avatar build failed for {displayName}: {e}", BasisDebug.LogTag.Avatar);
            DestroyObject(root);
            DestroyObject(buildHolder);
            return false;
        }
    }

    /// <summary>
    /// Rebuilds a humanoid rig from the payload skeleton with <see cref="AvatarBuilder"/> —
    /// the same runtime path generic glTF avatars use. The hierarchy is active and parked far
    /// below the world while the synchronous build runs.
    /// </summary>
    private static Avatar BuildHumanoidRig(GameObject root, Transform[] bones, BasisFarLodPayload payload)
    {
        int boneCount = bones.Length;
        HumanBone[] human = new HumanBone[boneCount];
        SkeletonBone[] skeleton = new SkeletonBone[boneCount + 1];
        skeleton[0] = new SkeletonBone
        {
            name = root.name,
            position = Vector3.zero,
            rotation = Quaternion.identity,
            scale = payload.AuthoredRootScale,
        };
        for (int i = 0; i < boneCount; i++)
        {
            HumanBodyBones humanBone = (HumanBodyBones)payload.BoneHumanBodyBone[i];
            human[i] = new HumanBone
            {
                humanName = HumanTrait.BoneName[(int)humanBone],
                boneName = bones[i].name,
                limit = new HumanLimit { useDefaultValues = true },
            };
            skeleton[i + 1] = new SkeletonBone
            {
                name = bones[i].name,
                position = payload.BoneRestLocalPosition[i],
                rotation = payload.BoneRestLocalRotation[i],
                scale = Vector3.one,
            };
        }

        HumanDescription description = new HumanDescription
        {
            human = human,
            skeleton = skeleton,
            armStretch = 0.05f,
            legStretch = 0.05f,
            upperArmTwist = 0.5f,
            lowerArmTwist = 0.5f,
            upperLegTwist = 0.5f,
            lowerLegTwist = 0.5f,
            feetSpacing = 0f,
            hasTranslationDoF = false,
        };

        try
        {
            Avatar built = AvatarBuilder.BuildHumanAvatar(root, description);
            if (built == null || !built.isValid || !built.isHuman)
            {
                BasisDebug.LogError("Far avatar humanoid rig rebuild produced an invalid rig.", BasisDebug.LogTag.Avatar);
                if (built != null)
                {
                    DestroyObject(built);
                }
                return null;
            }
            built.name = root.name;
            return built;
        }
        catch (System.Exception e)
        {
            BasisDebug.LogError($"Far avatar AvatarBuilder threw: {e.Message}", BasisDebug.LogTag.Avatar);
            return null;
        }
    }

    /// <summary>
    /// Acquires the per-version shared assets, building them from <paramref name="payload"/>
    /// when this is the first wearer. The payload arrives pre-parsed (and pre-decoded) from
    /// the worker thread; only texture/mesh construction and the humanoid rig remain here.
    /// </summary>
    private static SharedAssets AcquireShared(string uniqueVersion, BasisFarLodPayload payload)
    {
        if (IsSharedUsable(uniqueVersion) && SharedByVersion.TryGetValue(uniqueVersion, out SharedAssets existing))
        {
            sPendingTeardown.Remove(uniqueVersion);
            if (TraceSharedLifetime)
            {
                BasisDebug.Log($"Far avatar acquire {uniqueVersion} (cached, {existing.Wearers.Count} wearer(s) before this one)", BasisDebug.LogTag.Avatar);
            }
            return existing;
        }

        if (payload == null)
        {
            return null;
        }

        Texture2D texture = payload.CreateTexture();
        if (texture == null)
        {
            BasisDebug.LogError($"Far avatar texture build failed for version {uniqueVersion}.", BasisDebug.LogTag.Avatar);
            return null;
        }

        SharedAssets shared = new SharedAssets
        {
            UniqueVersion = uniqueVersion,
            Payload = payload,
            Texture = texture,
            HipsIndex = payload.FindBone(HumanBodyBones.Hips),
        };

        shared.Mesh = payload.CreateMesh();
        if (shared.Mesh == null)
        {
            BasisDebug.LogError($"Far avatar mesh build failed for version {uniqueVersion}.", BasisDebug.LogTag.Avatar);
            DestroyObject(texture);
            return null;
        }

        if (sFarAvatarShader == null)
        {
            sFarAvatarShader = Shader.Find("Basis/AvatarFarLod");
        }
        if (sFarAvatarShader == null)
        {
            BasisDebug.LogError("Basis/AvatarFarLod shader missing from build — far avatars disabled.", BasisDebug.LogTag.Avatar);
            DestroyObject(texture);
            DestroyObject(shared.Mesh);
            return null;
        }
        shared.Material = new Material(sFarAvatarShader) { enableInstancing = true };
        shared.Material.SetTexture(BaseMapProperty, texture);
        shared.Material.SetFloat(MinBrightnessProperty, payload.MinBrightness);
        shared.Material.SetFloat(MaxBrightnessProperty, payload.MaxBrightness);

        // Mesh and texture now exist as engine objects; the retained payload only feeds the
        // per-version prototype build, which reads none of the heavy arrays.
        payload.ReleaseMeshSourceData();

        SharedByVersion[uniqueVersion] = shared;
        if (TraceSharedLifetime)
        {
            BasisDebug.Log($"Far avatar acquire {uniqueVersion} (built mesh {shared.Mesh.GetEntityId()}, {shared.Mesh.vertexCount} verts) for its first wearer", BasisDebug.LogTag.Avatar);
        }
        return shared;
    }

    /// <summary>
    /// True when this version's shared assets exist AND the engine objects the renderer needs
    /// are still alive. A <see cref="SharedAssets"/> whose Mesh/Material/Texture were destroyed
    /// under it — a release that raced a live wearer, an editor asset teardown, a domain reload
    /// — would otherwise be handed to every wearer from then on, cloning a mesh-less prototype
    /// with no log at all. A dead entry is dropped here so the caller re-parses and rebuilds it.
    /// HumanoidRig is deliberately not tested: it is built lazily in <see cref="BuildPrototype"/>,
    /// so a freshly acquired entry legitimately carries a null rig.
    /// </summary>
    private static bool IsSharedUsable(string uniqueVersion)
    {
        if (!SharedByVersion.TryGetValue(uniqueVersion, out SharedAssets shared))
        {
            return false;
        }
        if (shared.Mesh != null && shared.Material != null && shared.Texture != null)
        {
            return true;
        }
        BasisDebug.LogError($"Far avatar shared assets for version {uniqueVersion} were destroyed under {shared.Wearers.Count} wearer(s) (mesh={shared.Mesh != null} material={shared.Material != null} texture={shared.Texture != null}) — rebuilding.", BasisDebug.LogTag.Avatar);
        DropShared(shared);
        return false;
    }

    /// <summary>
    /// Versions whose last wearer has gone, waiting for the transmit tick to retire them.
    /// Main-thread access only.
    /// </summary>
    private static readonly List<string> sPendingTeardown = new List<string>(4);

    /// <summary>
    /// Retires the versions whose last wearer went away. Called from the top of the transmit tick,
    /// which is a plain main-thread point — the release itself is NOT: it arrives through
    /// <see cref="BasisFarAvatarInstance.OnDestroy"/>, i.e. from inside Unity's destruction pass,
    /// where destroying further GameObjects (the prototype and its DontDestroyOnLoad holder) is not
    /// reliably honoured. Tearing down there stranded a prototype whose mesh had already been freed,
    /// and since every wearer is a clone of that prototype the strand is what a null far LOD mesh
    /// looks like. Same rule the install path already follows for the same reason.
    ///
    /// A version re-acquired before the drain is skipped: the wearer came back (a range-boundary
    /// flip, a swap out and straight back in) and the assets it wants are still the ones it had, so
    /// nothing is rebuilt.
    /// </summary>
    private static readonly List<string> sTeardownScratch = new List<string>(4);

    /// <summary>
    /// Drops wearers whose avatar has been destroyed and returns how many are still live. A
    /// destroyed MonoBehaviour compares equal to null, and one that was reassigned to a different
    /// version no longer belongs to this one, so both are pruned.
    /// </summary>
    private static int PruneWearers(SharedAssets shared)
    {
        for (int Index = shared.Wearers.Count - 1; Index >= 0; Index--)
        {
            BasisFarAvatarInstance wearer = shared.Wearers[Index];
            if (wearer == null || wearer.SharedVersion != shared.UniqueVersion)
            {
                shared.Wearers.RemoveAt(Index);
            }
        }
        return shared.Wearers.Count;
    }

    public static void DrainPendingTeardowns()
    {
        if (sPendingTeardown.Count == 0)
        {
            return;
        }
        // Drained through a scratch copy: DropShared removes the version from the pending list,
        // which would shift the indices out from under a direct walk.
        sTeardownScratch.Clear();
        sTeardownScratch.AddRange(sPendingTeardown);
        sPendingTeardown.Clear();
        for (int Index = 0; Index < sTeardownScratch.Count; Index++)
        {
            if (!SharedByVersion.TryGetValue(sTeardownScratch[Index], out SharedAssets shared))
            {
                continue;
            }
            int live = PruneWearers(shared);
            if (live > 0)
            {
                // Somebody arrived between the queue and the drain — a range-boundary flip, a swap
                // out and straight back in. The assets they want are the ones still here.
                if (TraceSharedLifetime)
                {
                    BasisDebug.Log($"Far avatar teardown skipped for {shared.UniqueVersion} — {live} wearer(s) came back", BasisDebug.LogTag.Avatar);
                }
                continue;
            }
            DropShared(shared);
        }
        sTeardownScratch.Clear();
    }

    /// <summary>
    /// Hands one wearer's reference back. It has to be a wearer of THIS entry: releases arrive
    /// keyed by a version string, and an instance stranded on an entry that was evicted and rebuilt
    /// under the same key (see <see cref="IsSharedUsable"/>) would otherwise retire the rebuilt
    /// version out from under its live wearers, while a wearer that already left would retire it a
    /// second time. Both landed on the drain as "queued with wearers still live". A release from
    /// anything this entry does not hold is now nothing at all.
    /// </summary>
    private static void ReleaseShared(SharedAssets shared, BasisFarAvatarInstance wearer)
    {
        if (!shared.Wearers.Remove(wearer))
        {
            if (TraceSharedLifetime)
            {
                BasisDebug.Log($"Far avatar release {shared.UniqueVersion} ignored — not one of its {shared.Wearers.Count} wearer(s)", BasisDebug.LogTag.Avatar);
            }
            return;
        }
        if (TraceSharedLifetime)
        {
            BasisDebug.Log($"Far avatar release {shared.UniqueVersion} -> {shared.Wearers.Count} wearer(s)", BasisDebug.LogTag.Avatar);
        }
        QueueTeardownIfUnworn(shared);
    }

    /// <summary>
    /// Queues a version nobody is on: the last wearer left, or an install failed after the assets
    /// were built and never produced one. The drain, never this, does the destroying.
    /// </summary>
    private static void QueueTeardownIfUnworn(SharedAssets shared)
    {
        if (shared.Wearers.Count > 0 || sPendingTeardown.Contains(shared.UniqueVersion))
        {
            return;
        }
        sPendingTeardown.Add(shared.UniqueVersion);
    }

    /// <summary>
    /// Tears down a version's shared assets and forgets it. Split out of
    /// <see cref="ReleaseShared"/> so the usability gate can evict a half-dead entry without going
    /// through the wearer list; the fields are nulled so a survivor can never be mistaken for a
    /// live asset, and a wearer whose OnDestroy lands afterwards no-ops on the missing key.
    /// </summary>
    private static void DropShared(SharedAssets shared)
    {
        SharedByVersion.Remove(shared.UniqueVersion);
        sPendingTeardown.Remove(shared.UniqueVersion);
        // Anyone still on the entry when it is evicted mid-life (dead engine objects) is about to
        // lose the mesh they render. Cutting the version off the instance and off the mirror the
        // tick reads makes IsWearingResolvedVersion false for them, so the next pass installs the
        // rebuilt version over the strand instead of leaving it mesh-less for the session — and a
        // release arriving from them later finds no version to hand back, which is the point.
        for (int Index = 0; Index < shared.Wearers.Count; Index++)
        {
            BasisFarAvatarInstance stranded = shared.Wearers[Index];
            if (stranded == null)
            {
                continue;
            }
            stranded.SharedVersion = null;
            if (stranded.TryGetComponent(out BasisAvatar strandedAvatar))
            {
                strandedAvatar.FarLodSharedVersion = null;
            }
        }
        shared.Wearers.Clear();
        // The prototype goes first: it holds the mesh/material/rig below and its own
        // BasisFarAvatarInstance is version-less, so destroying it releases nothing further.
        if (shared.PrototypeHolder != null)
        {
            DestroyObject(shared.PrototypeHolder);
        }
        shared.PrototypeHolder = null;
        shared.Prototype = null;
        if (shared.Material != null)
        {
            DestroyObject(shared.Material);
        }
        if (shared.Texture != null)
        {
            DestroyObject(shared.Texture);
        }
        if (shared.Mesh != null)
        {
            DestroyObject(shared.Mesh);
        }
        if (shared.HumanoidRig != null)
        {
            DestroyObject(shared.HumanoidRig);
        }
        shared.Material = null;
        shared.Texture = null;
        shared.Mesh = null;
        shared.HumanoidRig = null;
    }

    /// <summary>
    /// Release hook for <see cref="BasisFarAvatarInstance"/>. The wearer hands ITSELF back rather
    /// than just its version string, so what it gives up is provably the reference it took.
    /// </summary>
    public static void ReleaseSharedByWearer(BasisFarAvatarInstance wearer)
    {
        // Reference null, not Unity null: this runs from the wearer's own OnDestroy, and asking
        // whether the object is still alive there is exactly the question that could skip a release.
        if (wearer is null || string.IsNullOrEmpty(wearer.SharedVersion))
        {
            return;
        }
        if (SharedByVersion.TryGetValue(wearer.SharedVersion, out SharedAssets shared))
        {
            ReleaseShared(shared, wearer);
        }
    }
}

/// <summary>
/// Rides on every far avatar root so the shared per-version assets are released no matter
/// which path destroys the avatar (normal swap, disconnect, world change).
/// </summary>
public class BasisFarAvatarInstance : MonoBehaviour
{
    public string SharedVersion;

    private void OnDestroy()
    {
        BasisFarAvatarBuilder.ReleaseSharedByWearer(this);
        SharedVersion = null;
    }
}
