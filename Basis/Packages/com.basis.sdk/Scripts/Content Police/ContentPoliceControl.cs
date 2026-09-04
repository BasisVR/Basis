using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Constraints;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

public static class ContentPoliceControl
{
    public static bool ShaderPrewarmEnabled = false;
    public static bool MaterialCorrectionEnabled = false;
    // Independent of MaterialCorrection: swaps materials matching the user's
    // shader-name/keyword blocklist (BasisShaderFallback.SetBlocklist) to the fallback.
    public static bool ShaderBlocklistEnabled = false;
    public static bool VerboseLogging = false;

    // Reused renderer buffer for the no-content-removal path when no harvest is
    // supplied. Main-thread only; consumed synchronously by prewarm/correction.
    private static readonly List<Renderer> NoRemovalRendererScratch = new List<Renderer>(64);
    private const string MediaPlayerStreamingAssemblyQualifiedTypeName = "BasisMediaPlayerStreaming, BasisMediaPlayer";
    private const string MediaPlayerStreamingTypeName = "BasisMediaPlayerStreaming";
    private const string MediaPlayerStreamingAutoStartFieldName = "ConfigureOnStart";
    private static Type mediaPlayerStreamingType;
    private static FieldInfo mediaPlayerStreamingAutoStartField;
    private static bool mediaPlayerStreamingPolicyResolved;
    private static bool mediaPlayerStreamingPolicyInvalidLogged;

    // Server-pushed admin lock, mirrored from BasisNetworkModeration.GlobalCilboxLocked by the
    // shim bridge. While set, the avatar content walk strips the Cilbox sandbox host + proxies so
    // no avatar script runs. Avatar content only — props and worlds keep their own Cilbox.
    public static bool AvatarCilboxLocked = false;

    // Cilbox lives in com.cnlohr.cilbox which this assembly does not reference, so the lock strip
    // matches by full type name: the avatar sandbox host and its per-behaviour proxies.
    private static readonly HashSet<string> LockedAvatarCilboxTypeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Cilbox.CilboxAvatarBasis",
        "Cilbox.CilboxProxy",
    };

    // Fast path, not an exception: Transform and RectTransform are both on every selector's
    // approved list, so the lookup below can only ever say yes. Skips a FullName + hash probe
    // for one component per GameObject, which on a bone-heavy avatar is most of the walk.
    private static bool IsAlwaysApproved(Component component) => component is Transform;

    private static bool IsAvatarOrPropSelector(BundledContentHolder.Selector selector)
        => selector == BundledContentHolder.Selector.Avatar || selector == BundledContentHolder.Selector.Prop;

    // BasisSDK intentionally does not reference the media-player assembly. Resolve the optional
    // runtime type/field lazily and cache them so the normal Content Police walk only pays a Type
    // reference comparison per component. If the media-player assembly is not loaded yet, leave
    // the policy unresolved so a later content load can retry.
    private static bool TryGetMediaPlayerStreamingPolicy(out Type streamingType, out FieldInfo autoStartField)
    {
        if (!mediaPlayerStreamingPolicyResolved)
        {
            Type resolvedType = Type.GetType(MediaPlayerStreamingAssemblyQualifiedTypeName, throwOnError: false);
            if (resolvedType != null)
            {
                mediaPlayerStreamingType = resolvedType;
                mediaPlayerStreamingAutoStartField = resolvedType.GetField(
                    MediaPlayerStreamingAutoStartFieldName,
                    BindingFlags.Public | BindingFlags.Instance);
                mediaPlayerStreamingPolicyResolved = true;

                if ((mediaPlayerStreamingAutoStartField == null || mediaPlayerStreamingAutoStartField.FieldType != typeof(bool)) &&
                    !mediaPlayerStreamingPolicyInvalidLogged)
                {
                    mediaPlayerStreamingPolicyInvalidLogged = true;
                    BasisDebug.LogWarning(
                        $"[ContentPolice] {MediaPlayerStreamingTypeName} no longer exposes a public bool {MediaPlayerStreamingAutoStartFieldName}; avatar/prop auto-start could not be disabled.",
                        BasisDebug.LogTag.Event);
                }
            }
        }

        streamingType = mediaPlayerStreamingType;
        autoStartField = mediaPlayerStreamingAutoStartField;
        return streamingType != null && autoStartField != null && autoStartField.FieldType == typeof(bool);
    }

    private static void DisableMediaPlayerStreamingAutoStart(Component component, Type streamingType, FieldInfo autoStartField)
    {
        if (component != null && component.GetType() == streamingType)
        {
            autoStartField.SetValue(component, false);
        }
    }

    private static void DisableMediaPlayerStreamingAutoStart(GameObject root, BundledContentHolder.Selector selector)
    {
        if (!IsAvatarOrPropSelector(selector) || root == null ||
            !TryGetMediaPlayerStreamingPolicy(out Type streamingType, out FieldInfo autoStartField))
        {
            return;
        }

        Component[] streamingComponents = root.GetComponentsInChildren(streamingType, true);
        for (int i = 0; i < streamingComponents.Length; i++)
        {
            autoStartField.SetValue(streamingComponents[i], false);
        }
    }

    /// <summary>
    /// Creates a copy of a GameObject, removes any unapproved MonoBehaviours, and returns the cleaned copy through instantiation. 
    /// </summary>
    /// <param name="SearchAndDestroy">The original GameObject to copy and clean.</param>
    /// <param name="ChecksRequired">Whether to remove unapproved MonoBehaviours or not.</param>
    /// <param name="Position">The position to instantiate the cleaned copy.</param>
    /// <param name="Rotation">The rotation to instantiate the cleaned copy.</param>
    /// <param name="Parent">The parent transform for the instantiated copy. Defaults to null.</param>
    /// <returns>A copy of the GameObject with unapproved scripts removed.</returns>
    public static GameObject ContentControl(GameObject DisabledGameobject, GameObject SearchAndDestroy, ChecksRequired ChecksRequired, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, BundledContentHolder.Selector Selector, Transform Parent = null,int colliderlayer = -1, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null, BasisContentHarvest harvest = null)
    {
        ContentControlState state = BeginContentControl(DisabledGameobject, SearchAndDestroy, ChecksRequired, Position, Rotation, ModifyScale, Scale, Selector, Parent, colliderlayer, HarvestedHeadChop, harvest);
        return FinishContentControl(state);
    }

    // Phase one of the content walk: the atomic GameObject.Instantiate (the single largest cost,
    // and one Unity can't chunk) plus the police-selector lookup. The clone is parked under the
    // inactive host, so it stays dormant — no Awake/OnEnable/event fires — until FinishContentControl
    // runs the strip/scrub and activates it. That dormancy is what lets the heavy component walk run
    // on a later frame: deferring it is as safe as the original single-frame walk. Main-thread only.
    public static ContentControlState BeginContentControl(GameObject DisabledGameobject, GameObject SearchAndDestroy, ChecksRequired ChecksRequired, Vector3 Position, Quaternion Rotation, bool ModifyScale, Vector3 Scale, BundledContentHolder.Selector Selector, Transform Parent = null, int colliderlayer = -1, List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = null, BasisContentHarvest harvest = null)
    {
        ContentControlState state = default;
        state.Checks = ChecksRequired;
        state.Selector = Selector;
        state.Parent = Parent;
        state.ColliderLayer = colliderlayer;
        state.HarvestedHeadChop = HarvestedHeadChop;
        if (ChecksRequired.UseContentRemoval)
        {
            if (DisabledGameobject == null)
            {
                BasisDebug.LogErrorOnce("Content host was destroyed before instantiation; load was torn down (shutdown or device-management teardown).", BasisDebug.LogTag.Event);
                return state;
            }
            SearchAndDestroy = GameObject.Instantiate(SearchAndDestroy, Position, Rotation, DisabledGameobject.transform);
            if (ModifyScale)
            {
                if (VerboseLogging)
                {
                    BasisDebug.Log($"Overriding Default scale is now {Scale} for GameObject {SearchAndDestroy.name}");
                }
                SearchAndDestroy.transform.localScale = Scale;
            }
            state.Clone = SearchAndDestroy;
            if (BundledContentHolder.Instance.GetSelector(Selector, out ContentPoliceSelector PoliceCheck))
            {
                harvest ??= new BasisContentHarvest();
                state.Harvest = harvest;
                state.Police = PoliceCheck;
                state.RemovalWalkPending = true;
            }
            else
            {
                BasisDebug.LogError("Can't find Police check for " + Selector, BasisDebug.LogTag.Event);
                state.Harvest = harvest;
            }
        }
        else
        {
            if (Parent == null)
            {
                SearchAndDestroy = GameObject.Instantiate(SearchAndDestroy, Position, Rotation);
            }
            else
            {
                SearchAndDestroy = GameObject.Instantiate(SearchAndDestroy, Position, Rotation, Parent);
            }
            // Avatar/prop media streaming must not auto-start from authored data. This path skips
            // the normal component-removal walk, so apply the content-specific rewrite explicitly
            // immediately after Instantiate; Unity Start has not run yet.
            DisableMediaPlayerStreamingAutoStart(SearchAndDestroy, Selector);

            // No content-removal walk happened, so a dedicated Renderer-typed walk is the only
            // way to feed the prewarm here. Cheaper than the full component walk above. Only the
            // renderer bucket is filled; the harvest's other buckets stay null so EnsureHarvest
            // still rebuilds a full snapshot for the avatar driver.
            List<Renderer> rawRenderers;
            if (harvest != null)
            {
                harvest.RentRenderers();
                rawRenderers = harvest.Renderers;
            }
            else
            {
                rawRenderers = NoRemovalRendererScratch;
            }
            SearchAndDestroy.GetComponentsInChildren(true, rawRenderers);
            bool blockShaders = ShaderBlocklistEnabled && BasisShaderFallback.HasBlocklist;
            if (MaterialCorrectionEnabled || blockShaders)
            {
                BasisShaderFallback.MaterialCorrection(rawRenderers, BundledContentHolder.Instance.UrpShader, MaterialCorrectionEnabled, blockShaders);
            }
            if (ShaderPrewarmEnabled)
            {
                BasisShaderPrewarm.Warm(rawRenderers, SearchAndDestroy.name);
            }
            BasisGraphicsStatePrewarm.WarmResident(SearchAndDestroy.name);
            state.Clone = SearchAndDestroy;
            state.Harvest = harvest;
        }
        return state;
    }

    // Phase two: the component walk, MonoBehaviour/event strip, persistent-listener scrub, and the
    // final reparent + SetActive. Every security strip still completes before the clone goes active.
    public static GameObject FinishContentControl(ContentControlState state)
    {
        GameObject SearchAndDestroy = state.Clone;
        if (state.RemovalWalkPending)
        {
            // The clone is parked under the inactive host; if the load was torn down during the
            // frame gap it (and its host) are gone, so there is nothing left to scrub or activate.
            if (SearchAndDestroy != null)
            {
                ChecksRequired ChecksRequired = state.Checks;
                ContentPoliceSelector PoliceCheck = state.Police;
                BundledContentHolder.Selector Selector = state.Selector;
                Transform Parent = state.Parent;
                int colliderlayer = state.ColliderLayer;
                List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop = state.HarvestedHeadChop;
                BasisContentHarvest harvest = state.Harvest;
                // Buffers are loaned from the harvest pool and filled in place, so the
                // component walk allocates nothing on a warm pool. Renderers (for prewarm)
                // and per-slot kind tags are collected in this same pass.
                harvest ??= new BasisContentHarvest();
                harvest.RentBuffers();
                List<Component> components = harvest.Components;
                SearchAndDestroy.GetComponentsInChildren(true, components);
                int count = components.Count;
                List<Renderer> renderersForPrewarm = harvest.Renderers;
                List<SkinnedMeshRenderer> skinnedForHarvest = harvest.SkinnedMeshRenderers;
                List<BasisAuthoredMotion> authoredForHarvest = harvest.AuthoredMotions;
                List<BasisComponentKind> kinds = harvest.Kinds;

                // BasisHeadChop is harvested during this walk so the local avatar driver
                // doesn't need a second GetComponentsInChildren pass at calibration. Harvest
                // is appended only when the caller passed a non-null collector — that way the
                // data flows back through the call chain rather than living on BasisAvatar.
                BasisConstraintConversion.Report constraintReport = default;
                Type streamingType = null;
                FieldInfo streamingAutoStartField = null;
                bool sanitizeStreamingAutoStart = IsAvatarOrPropSelector(Selector) &&
                    TryGetMediaPlayerStreamingPolicy(out streamingType, out streamingAutoStartField);
                for (int Index = 0; Index < count; Index++)
                {
                    Component component = components[Index];
                    kinds.Add(BasisContentHarvest.Classify(component));
                    //do this first before we nuke stuff
                    switch (component)
                    {
                        case Component streaming when sanitizeStreamingAutoStart && streaming.GetType() == streamingType:
                            DisableMediaPlayerStreamingAutoStart(streaming, streamingType, streamingAutoStartField);
                            break;
                        case BasisHeadChop headChop:
                            // Authoring-only component: harvest its targets (when a collector
                            // was supplied — local-avatar loads only) and then destroy it so
                            // it never persists at runtime. Remote avatars never harvest, so
                            // they end up with the component stripped and no chop processing.
                            if (HarvestedHeadChop != null && headChop.Targets != null && headChop.Targets.Length > 0)
                            {
                                HarvestedHeadChop.AddRange(headChop.Targets);
                            }
                            GameObject.DestroyImmediate(headChop);
                            kinds[Index] = BasisComponentKind.Removed;
                            break;
                        case BasisAuthoredMotion authoredMotion:
                            authoredForHarvest?.Add(authoredMotion);
                            break;
                        case Animator animator:
                            // AnimationEvents dispatch via SendMessage(methodName, arg),
                            // which invokes any method of that name on any component on
                            // this GameObject — even non-public methods on approved
                            // scripts, bypassing the whitelist below. Disable runtime
                            // dispatch AND strip the serialized events so no playback
                            // path (different animator, Playables, a whitelisted script
                            // flipping fireEvents back on) can revive them.
                            if (ChecksRequired.DisableAnimatorEvents)
                            {
                                animator.fireEvents = false;
                            }
                            StripEventsFromRuntimeController(animator.runtimeAnimatorController);
                            break;
                        case Animation legacyAnimation:
                            // Legacy Animation has no fireEvents toggle; stripping
                            // each state's clip events is the only defense.
                            // Stripped by default unless AllowLegacyEvents is explicitly enabled.
                            if (!ChecksRequired.AllowLegacyEvents)
                            {
                                StripEventsFromLegacyAnimation(legacyAnimation);
                            }
                            break;
                        // Rewrite Unity and Animation Rigging constraints onto the batched Basis
                        // solver, then drop the original. Done here rather than in a pass of its own
                        // because this walk already has every component in hand, and done before the
                        // content is activated so the originals never evaluate even once.
                        case Component convertible when BasisConstraintConversion.TryConvert(
                            convertible, ref constraintReport):
                            GameObject.DestroyImmediate(convertible);
                            kinds[Index] = BasisComponentKind.Removed;
                            continue;
                        case Collider collider:

                            if (ChecksRequired.RemoveColliders)
                            {
                                if (VerboseLogging)
                                {
                                    BasisDebug.Log("Remove Collider ", BasisDebug.LogTag.Avatar);
                                }
                                GameObject.Destroy(collider);
                                kinds[Index] = BasisComponentKind.Removed;
                            }
                            else
                            {
                                if (ChecksRequired.ChangeCollidersToCorrectLayer)
                                {
                                    if (VerboseLogging)
                                    {
                                        BasisDebug.Log("Changing Collider To Correct Layer", BasisDebug.LogTag.Avatar);
                                    }
                                    collider.gameObject.layer = colliderlayer;
                                }
                            }
                            break;
                        case AudioSource source:
                            source.outputAudioMixerGroup = PoliceCheck.AudioMixer;
                            break;
                        case AudioListener audioListener:
                            GameObject.DestroyImmediate(audioListener);
                            kinds[Index] = BasisComponentKind.Removed;
                            continue;
                        // Every Renderer subclass listed individually so future per-type
                        // handling (e.g. particles needing different prewarm, sprite atlases
                        // needing texture warmup, VFX assets needing graph compile) can be
                        // wired into the existing case without restructuring the switch.
                        case MeshRenderer meshRenderer:
                            renderersForPrewarm.Add(meshRenderer);
                            break;
                        case SkinnedMeshRenderer skinnedMeshRenderer:
                            renderersForPrewarm.Add(skinnedMeshRenderer);
                            skinnedForHarvest?.Add(skinnedMeshRenderer);
                            break;
                        case ParticleSystemRenderer particleSystemRenderer:
                            renderersForPrewarm.Add(particleSystemRenderer);
                            break;
                        case LineRenderer lineRenderer:
                            renderersForPrewarm.Add(lineRenderer);
                            break;
                        case TrailRenderer trailRenderer:
                            renderersForPrewarm.Add(trailRenderer);
                            break;
                        case BillboardRenderer billboardRenderer:
                            renderersForPrewarm.Add(billboardRenderer);
                            break;
                        case SpriteRenderer spriteRenderer:
                            renderersForPrewarm.Add(spriteRenderer);
                            break;
                        case UnityEngine.Tilemaps.TilemapRenderer tilemapRenderer:
                            renderersForPrewarm.Add(tilemapRenderer);
                            break;
                        case UnityEngine.VFX.VFXRenderer vfxRenderer:
                            renderersForPrewarm.Add(vfxRenderer);
                            break;
                    }
                    // Admin Cilbox lock: strip the sandbox host + proxies from avatar content even
                    // though they're normally approved, so no avatar script can run. Avatar selector
                    // only — props and worlds keep their own Cilbox.
                    if (AvatarCilboxLocked && Selector == BundledContentHolder.Selector.Avatar
                        && component != null && LockedAvatarCilboxTypeNames.Contains(component.GetType().FullName))
                    {
                        GameObject.DestroyImmediate(component);
                        kinds[Index] = BasisComponentKind.Removed;
                        continue;
                    }

                    // Any component off the approved list is removed, engine built-ins included.
                    if (component != null && !IsAlwaysApproved(component))
                    {
                        Type componentType = component.GetType();
                        if (!PoliceCheck.IsTypeApproved(componentType))
                        {
                            BasisDebug.LogErrorUnreported($"Component {componentType.FullName} is not approved and will be removed. Request the {Application.productName} team to add it to the approved list, or add it yourself!", BasisDebug.LogTag.System);
                            GameObject.DestroyImmediate(component);
                            kinds[Index] = BasisComponentKind.Removed;
                        }
                    }
                }

                bool blockShaders = ShaderBlocklistEnabled && BasisShaderFallback.HasBlocklist;
                // One line per load, never per component: a busy instance converts hundreds of these
                // at once and the notifier fans logging out to disk and the network.
                if (VerboseLogging && constraintReport.DidAnything)
                {
                    BasisDebug.Log($"Content Police converted constraints: {constraintReport}");
                }

                if (MaterialCorrectionEnabled || blockShaders)
                {
                    BasisShaderFallback.MaterialCorrection(renderersForPrewarm, BundledContentHolder.Instance.UrpShader, MaterialCorrectionEnabled, blockShaders);
                }

                // Compile shader variants for everything we just walked before we set the clone
                // active, so the first frame it's visible doesn't stall on a hitch.
                if (ShaderPrewarmEnabled)
                {
                    BasisShaderPrewarm.Warm(renderersForPrewarm, SearchAndDestroy.name);
                }
                BasisGraphicsStatePrewarm.WarmResident(SearchAndDestroy.name);

                // Persistent UnityEvent listeners are the second attack surface:
                // a Button.onClick wired in the editor to Application.OpenURL /
                // File.WriteAllText / Process.Start fires the moment Awake runs.
                // Strip dangerous ones here while the clone is still parked under
                // the disabled host so no MonoBehaviour callback has executed yet.
                if (ChecksRequired.ScrubPersistentUnityEvents)
                {
                    ScrubDangerousPersistentListeners(components, PoliceCheck);
                }

                // Instantiate the cleaned GameObject copy
                if (Parent == null)
                {
                    SearchAndDestroy.transform.parent = null;
                    SearchAndDestroy.SetActive(true);
                }
                else
                {
                    SearchAndDestroy.transform.parent = Parent;
                    SearchAndDestroy.SetActive(true);
                }
            }
        }
        if (state.Harvest != null && SearchAndDestroy != null && SearchAndDestroy.TryGetComponent(out BasisContentBase contentBase))
        {
            contentBase.Harvest = state.Harvest;
        }
        return SearchAndDestroy;
    }

    // Carries the parked clone plus the inputs the deferred strip/scrub pass needs, so the loader
    // can put a frame between BeginContentControl and FinishContentControl. Treat as an opaque handle.
    public struct ContentControlState
    {
        public GameObject Clone;
        public bool RemovalWalkPending;
        public ContentPoliceSelector Police;
        public ChecksRequired Checks;
        public BundledContentHolder.Selector Selector;
        public Transform Parent;
        public int ColliderLayer;
        public List<BasisHeadChop.HeadChopTarget> HarvestedHeadChop;
        public BasisContentHarvest Harvest;
    }
    /// <summary>
    /// Scrubs a scene by removing any unapproved MonoBehaviours and applying optional safety checks.
    /// </summary>
    public static void ContentControl(ChecksRequired checks, BundledContentHolder.Selector selector, Scene targetScene, bool includeInactive = true)
    {
        if (!checks.UseContentRemoval)
        {
            return;
        }

        if (!BundledContentHolder.Instance.GetSelector(selector, out ContentPoliceSelector policeCheck))
        {
            BasisDebug.LogError("Can't find Police check for " + selector, BasisDebug.LogTag.Event);
            return;
        }
        if (!targetScene.IsValid() || !targetScene.isLoaded)
        {
            BasisDebug.LogError("Target scene is not valid or not loaded.");
            return;
        }

        List<GameObject> roots = new List<GameObject>();
        targetScene.GetRootGameObjects(roots);
        // Renderers harvested across all roots during the same walk that strips dangerous
        // components, so shader prewarm runs without paying for a second walk. Both scratch
        // lists are reused across roots so the scrub allocates only these once.
        List<Renderer> renderersForPrewarm = new List<Renderer>();
        List<Component> components = new List<Component>();
        BasisConstraintConversion.Report constraintReport = default;
        Type streamingType = null;
        FieldInfo streamingAutoStartField = null;
        bool sanitizeStreamingAutoStart = IsAvatarOrPropSelector(selector) &&
            TryGetMediaPlayerStreamingPolicy(out streamingType, out streamingAutoStartField);
        for (int RootIndex = 0; RootIndex < roots.Count; RootIndex++)
        {
            roots[RootIndex].transform.GetComponentsInChildren(includeInactive, components);
            // Check if the component is a MonoBehaviour and not in the approved list
            for (int ComponentIndex = 0; ComponentIndex < components.Count; ComponentIndex++)
            {
                Component component = components[ComponentIndex];
                //do this first before we nuke stuff
                switch (component)
                {
                    case Component streaming when sanitizeStreamingAutoStart && streaming.GetType() == streamingType:
                        DisableMediaPlayerStreamingAutoStart(streaming, streamingType, streamingAutoStartField);
                        break;
                    case Animator animator:
                        // See the Animator case in the GameObject overload for the
                        // full threat-model comment. AnimationEvents fire via
                        // SendMessage and reach private methods on approved scripts,
                        // so we disable runtime dispatch AND strip the serialized
                        // events off every referenced clip.
                        if (checks.DisableAnimatorEvents)
                        {
                            animator.fireEvents = false;
                        }
                        StripEventsFromRuntimeController(animator.runtimeAnimatorController);
                        break;
                    case Animation legacyAnimation:
                        if (!checks.AllowLegacyEvents)
                        {
                            StripEventsFromLegacyAnimation(legacyAnimation);
                        }
                        break;
                    // See the GameObject overload: constraints are rewritten onto the batched
                    // Basis solver from inside this walk rather than a pass of its own.
                    case Component convertible when BasisConstraintConversion.TryConvert(
                        convertible, ref constraintReport):
                        GameObject.DestroyImmediate(convertible);
                        continue;
                    case AudioListener audioListener:
                        GameObject.DestroyImmediate(audioListener);
                        continue;
                    // Every Renderer subclass listed individually — see the GameObject
                    // overload for the future-proofing rationale.
                    case MeshRenderer meshRenderer:
                        renderersForPrewarm.Add(meshRenderer);
                        break;
                    case SkinnedMeshRenderer skinnedMeshRenderer:
                        renderersForPrewarm.Add(skinnedMeshRenderer);
                        break;
                    case ParticleSystemRenderer particleSystemRenderer:
                        renderersForPrewarm.Add(particleSystemRenderer);
                        break;
                    case LineRenderer lineRenderer:
                        renderersForPrewarm.Add(lineRenderer);
                        break;
                    case TrailRenderer trailRenderer:
                        renderersForPrewarm.Add(trailRenderer);
                        break;
                    case BillboardRenderer billboardRenderer:
                        renderersForPrewarm.Add(billboardRenderer);
                        break;
                    case SpriteRenderer spriteRenderer:
                        renderersForPrewarm.Add(spriteRenderer);
                        break;
                    case UnityEngine.Tilemaps.TilemapRenderer tilemapRenderer:
                        renderersForPrewarm.Add(tilemapRenderer);
                        break;
                    case UnityEngine.VFX.VFXRenderer vfxRenderer:
                        renderersForPrewarm.Add(vfxRenderer);
                        break;
                }
                // Any component off the approved list is removed, engine built-ins included.
                if (component != null && !IsAlwaysApproved(component))
                {
                    string typeName = component.GetType().FullName;
                    if (!policeCheck.ApprovedTypeNames.Contains(typeName))
                    {
                        BasisDebug.LogErrorUnreported($"Component {typeName} is not approved and will be removed. Request the {Application.productName} team to add it to the approved list, or add it yourself!");
                        GameObject.DestroyImmediate(component);
                    }
                }
            }

            if (checks.ScrubPersistentUnityEvents)
            {
                ScrubDangerousPersistentListeners(components, policeCheck);
            }
        }

        // Replace materials with broken shaders before warming, so prewarm runs against the
        // fallback material instead of the magenta InternalErrorShader. Scene scrub is only
        // ever called for World content, so no avatar-skip gate is needed here.
        bool blockShaders = ShaderBlocklistEnabled && BasisShaderFallback.HasBlocklist;
        // One line per load, never per component: a busy instance converts hundreds of these
        // at once and the notifier fans logging out to disk and the network.
        if (VerboseLogging && constraintReport.DidAnything)
        {
            BasisDebug.Log($"Content Police converted constraints: {constraintReport}");
        }

        if (MaterialCorrectionEnabled || blockShaders)
        {
            BasisShaderFallback.MaterialCorrection(renderersForPrewarm, BundledContentHolder.Instance.UrpShader, MaterialCorrectionEnabled, blockShaders);
        }

        // Warm shaders for every renderer we just collected. One call per scene scrub.
        if (ShaderPrewarmEnabled)
        {
            BasisShaderPrewarm.Warm(renderersForPrewarm, targetScene.name);
        }
        BasisGraphicsStatePrewarm.WarmResident(targetScene.name);
    }

    // ------------------------------------------------------------------
    // Persistent UnityEvent scrubbing.
    //
    // Unity's PersistentCall stores (target, methodName, mode, argument). At
    // Awake the event resolves the method by name via reflection — so a
    // Button.onClick saved as (null, "OpenURL") with the type-name encoded in
    // the private m_TargetAssemblyTypeName field calls UnityEngine.Application.OpenURL
    // without any script on the prefab. Walking the surviving components and
    // disabling listeners that point at dangerous types/methods closes this
    // without breaking legit listeners that target an approved component.
    // ------------------------------------------------------------------

    private const int MaxEventWalkDepth = 6;

    // Static calls (null target) use a private assembly-type-name field we
    // can't portably read. Deny them outright — persistent listeners pointing
    // at free-floating static methods are rare in legit prop prefabs and
    // every known escape hatch here is a static call.
    // Target types we always refuse, regardless of method name.
    private static readonly HashSet<string> BlockedTargetFullNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "UnityEngine.Application",
        "UnityEngine.PlayerPrefs",
        "UnityEngine.SceneManagement.SceneManager",
        "UnityEngine.Resources",
        "UnityEngine.AssetBundle",
        "System.Environment",
        "System.AppDomain",
        "System.Activator",
        "System.Type",
        "System.Reflection.Assembly",
        "System.Reflection.MethodBase",
    };

    // Namespace prefixes that describe "anything that can reach outside Unity".
    private static readonly string[] BlockedTargetNamespacePrefixes = new string[]
    {
        "System.IO.",
        "System.Diagnostics.",
        "System.Net.",
        "System.Reflection.",
        "System.Runtime.InteropServices.",
        "Microsoft.Win32.",
        "UnityEngine.Networking.",
    };

    // Method names that should never be reachable through a persistent listener
    // regardless of target type. Intentionally short: each entry is an
    // unambiguous escape hatch name that won't false-positive on legit UI
    // wiring. The primary gate is the target-type allow-list above; this is
    // defense in depth for when an approved component exposes (or wraps) a
    // dangerous API under a distinctive name.
    private static readonly HashSet<string> BlockedMethodNames = new HashSet<string>(StringComparer.Ordinal)
    {
        // Browser / shell-out
        "OpenURL",
        // Process lifetime
        "Quit",
        // Scene escape
        "LoadScene", "LoadSceneAsync",
        // Filesystem writes ("save functions")
        "WriteAllText", "WriteAllBytes", "WriteAllLines",
        "AppendAllText", "AppendAllLines",
        // Reflection / assembly loading
        "LoadFrom", "LoadFile", "LoadAssembly",
        // Environment tampering
        "SetEnvironmentVariable",
    };

    private static readonly HashSet<object> EventWalkVisited = new HashSet<object>(ReferenceEqualityComparerLocal.Instance);

    private static void ScrubDangerousPersistentListeners(IReadOnlyList<Component> comps, ContentPoliceSelector police)
    {
        if (comps == null) return;
        HashSet<string> approved = police != null ? police.ApprovedTypeNames : null;

        HashSet<object> visited = EventWalkVisited;
        visited.Clear();
        for (int i = 0; i < comps.Count; i++)
        {
            Component c = comps[i];
            if (c == null) continue;
            // Component runtime type is exact here, so skipping an event-free type is sound.
            if (!CanReachUnityEvent(c.GetType())) continue;
            WalkForUnityEvents(c, visited, approved, 0);
        }
    }

    private enum WalkFieldKind : byte { DirectEvent, EventList, RecurseList, RecurseRef }

    private readonly struct WalkField
    {
        public readonly FieldInfo Field;
        public readonly WalkFieldKind Kind;
        public WalkField(FieldInfo field, WalkFieldKind kind) { Field = field; Kind = kind; }
    }

    private static readonly Dictionary<Type, WalkField[]> WalkPlanCache = new Dictionary<Type, WalkField[]>();

    private static WalkField[] GetWalkPlan(Type t)
    {
        if (WalkPlanCache.TryGetValue(t, out WalkField[] cached)) return cached;

        List<WalkField> plan = new List<WalkField>();
        Type cursor = t;
        while (cursor != null
            && cursor != typeof(object)
            && cursor != typeof(UnityEngine.Object)
            && cursor != typeof(Component)
            && cursor != typeof(MonoBehaviour)
            && cursor != typeof(Behaviour))
        {
            FieldInfo[] fields = cursor.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                Type ft = f.FieldType;

                if (ft.IsPrimitive || ft.IsPointer || ft.IsEnum) continue;
                if (ft == typeof(string) || ft == typeof(IntPtr) || ft == typeof(UIntPtr)) continue;

                if (typeof(UnityEventBase).IsAssignableFrom(ft))
                {
                    plan.Add(new WalkField(f, WalkFieldKind.DirectEvent));
                    continue;
                }

                if (typeof(UnityEngine.Object).IsAssignableFrom(ft)) continue;

                if (ft.IsArray)
                {
                    if (ft.GetArrayRank() != 1) continue;
                    Type et = ft.GetElementType();
                    if (et != null && typeof(UnityEventBase).IsAssignableFrom(et))
                        plan.Add(new WalkField(f, WalkFieldKind.EventList));
                    else if (ShouldRecurseInto(et) && EdgeCanReachUnityEvent(et))
                        plan.Add(new WalkField(f, WalkFieldKind.RecurseList));
                    continue;
                }

                if (ft.IsGenericType && typeof(IList).IsAssignableFrom(ft))
                {
                    Type[] args = ft.GetGenericArguments();
                    Type et = args.Length == 1 ? args[0] : null;
                    if (et != null && typeof(UnityEventBase).IsAssignableFrom(et))
                        plan.Add(new WalkField(f, WalkFieldKind.EventList));
                    else if (et != null && ShouldRecurseInto(et) && EdgeCanReachUnityEvent(et))
                        plan.Add(new WalkField(f, WalkFieldKind.RecurseList));
                    continue;
                }

                if (!ShouldRecurseInto(ft) || !EdgeCanReachUnityEvent(ft)) continue;
                plan.Add(new WalkField(f, WalkFieldKind.RecurseRef));
            }
            cursor = cursor.BaseType;
        }

        WalkField[] result = plan.ToArray();
        WalkPlanCache[t] = result;
        return result;
    }

    private static void WalkForUnityEvents(object obj, HashSet<object> visited, HashSet<string> approved, int depth)
    {
        if (obj == null) return;
        if (depth > MaxEventWalkDepth) return;

        Type t = obj.GetType();
        if (t.IsPrimitive || t == typeof(string) || t.IsEnum) return;

        if (!t.IsValueType && !visited.Add(obj)) return;

        if (obj is UnityEventBase evt)
        {
            ScrubEvent(evt, approved);
            return;
        }

        // Never follow UnityEngine.Object references away from the clone we
        // own — they lead into unrelated scene/asset graphs.
        if (obj is UnityEngine.Object && depth > 0) return;

        WalkField[] plan = GetWalkPlan(t);
        for (int i = 0; i < plan.Length; i++)
        {
            FieldInfo f = plan[i].Field;
            object value;
            try { value = f.GetValue(obj); }
            catch { continue; }
            if (value == null) continue;

            switch (plan[i].Kind)
            {
                case WalkFieldKind.DirectEvent:
                    if (value is UnityEventBase ue) ScrubEvent(ue, approved);
                    break;
                case WalkFieldKind.EventList:
                    if (value is IList eventList) WalkList(eventList, true, visited, approved, depth);
                    break;
                case WalkFieldKind.RecurseList:
                    if (value is IList recurseList) WalkList(recurseList, false, visited, approved, depth);
                    break;
                case WalkFieldKind.RecurseRef:
                    WalkForUnityEvents(value, visited, approved, depth + 1);
                    break;
            }
        }
    }

    private static void WalkList(IList list, bool elementsAreEvents, HashSet<object> visited, HashSet<string> approved, int depth)
    {
        int count;
        try { count = list.Count; }
        catch { return; }
        for (int j = 0; j < count; j++)
        {
            object element;
            try { element = list[j]; }
            catch { continue; }
            if (element == null) continue;
            if (elementsAreEvents)
            {
                if (element is UnityEventBase ue) ScrubEvent(ue, approved);
            }
            else
            {
                WalkForUnityEvents(element, visited, approved, depth + 1);
            }
        }
    }

    private static bool ShouldRecurseInto(Type t)
    {
        if (t == null) return false;
        if (t.IsPointer || t.IsPrimitive || t.IsEnum) return false;
        if (t == typeof(string) || t == typeof(IntPtr) || t == typeof(UIntPtr)) return false;
        if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return false;
        if (typeof(Delegate).IsAssignableFrom(t)) return false;
        if (IsBclNamespace(t)) return false;
        if (!ContainsManagedReferences(t)) return false;
        return true;
    }

    private static readonly Dictionary<Type, bool> EventReachCache = new Dictionary<Type, bool>();

    // True when an instance of t could, through the same field graph WalkForUnityEvents
    // traverses, reach a UnityEventBase. Lets the scrub skip components/branches that can
    // never hold a persistent listener (Transforms, renderers, colliders, event-free
    // scripts) — the overwhelming majority of a loaded hierarchy. Mirrors GetWalkPlan's
    // field selection exactly so it can never see less than the walk does.
    //
    // SOUNDNESS: pruning a security walk must never produce a false negative. A recursion
    // edge whose declared type is a non-sealed class or interface is assumed reachable,
    // because the runtime value may be a derived type (e.g. [SerializeReference]) whose
    // graph isn't visible from the declared type. Only sealed classes and value types have
    // a known exact runtime type, so only those are pruned when provably event-free.
    private static bool CanReachUnityEvent(Type t)
    {
        if (t == null) return false;
        if (EventReachCache.TryGetValue(t, out bool cached)) return cached;
        // Seed false so a reference cycle terminates: a pure cycle introduces no new
        // event-bearing type, and any real event is found at the node that declares it.
        EventReachCache[t] = false;

        bool result = false;
        Type cursor = t;
        while (cursor != null
            && cursor != typeof(object)
            && cursor != typeof(UnityEngine.Object)
            && cursor != typeof(Component)
            && cursor != typeof(MonoBehaviour)
            && cursor != typeof(Behaviour))
        {
            FieldInfo[] fields = cursor.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < fields.Length; i++)
            {
                Type ft = fields[i].FieldType;
                if (ft.IsPrimitive || ft.IsPointer || ft.IsEnum) continue;
                if (ft == typeof(string) || ft == typeof(IntPtr) || ft == typeof(UIntPtr)) continue;

                if (typeof(UnityEventBase).IsAssignableFrom(ft)) { result = true; break; }
                if (typeof(UnityEngine.Object).IsAssignableFrom(ft)) continue;

                if (ft.IsArray)
                {
                    if (ft.GetArrayRank() != 1) continue;
                    Type et = ft.GetElementType();
                    if (et != null && typeof(UnityEventBase).IsAssignableFrom(et)) { result = true; break; }
                    if (et != null && ShouldRecurseInto(et) && EdgeCanReachUnityEvent(et)) { result = true; break; }
                    continue;
                }

                if (ft.IsGenericType && typeof(IList).IsAssignableFrom(ft))
                {
                    Type[] args = ft.GetGenericArguments();
                    Type et = args.Length == 1 ? args[0] : null;
                    if (et != null && typeof(UnityEventBase).IsAssignableFrom(et)) { result = true; break; }
                    if (et != null && ShouldRecurseInto(et) && EdgeCanReachUnityEvent(et)) { result = true; break; }
                    continue;
                }

                if (!ShouldRecurseInto(ft)) continue;
                if (EdgeCanReachUnityEvent(ft)) { result = true; break; }
            }
            if (result) break;
            cursor = cursor.BaseType;
        }

        EventReachCache[t] = result;
        return result;
    }

    // A recursion edge (candidate has already passed ShouldRecurseInto). Non-sealed classes
    // and interfaces may hold a derived runtime type we can't analyze, so treat them as
    // reachable; sealed classes and structs have an exact type and are analyzed precisely.
    private static bool EdgeCanReachUnityEvent(Type candidate)
    {
        if (candidate.IsInterface || (candidate.IsClass && !candidate.IsSealed)) return true;
        return CanReachUnityEvent(candidate);
    }

    private static bool IsBclNamespace(Type t)
    {
        string ns = t.Namespace;
        if (ns == null) return false;
        return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
            || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    private static readonly Dictionary<Type, bool> ManagedReferenceCache = new Dictionary<Type, bool>();

    private static bool ContainsManagedReferences(Type t)
    {
        if (t == null) return false;
        if (!t.IsValueType) return true;
        if (t.IsPrimitive || t.IsEnum || t.IsPointer) return false;
        if (t == typeof(IntPtr) || t == typeof(UIntPtr)) return false;

        if (ManagedReferenceCache.TryGetValue(t, out bool cached)) return cached;
        ManagedReferenceCache[t] = false;

        bool result = false;
        FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        for (int i = 0; i < fields.Length; i++)
        {
            Type ft = fields[i].FieldType;
            if (ft.IsPointer || ft.IsPrimitive || ft.IsEnum) continue;
            if (ft == typeof(IntPtr) || ft == typeof(UIntPtr)) continue;
            if (ft.IsValueType)
            {
                if (ContainsManagedReferences(ft)) { result = true; break; }
            }
            else
            {
                result = true;
                break;
            }
        }

        ManagedReferenceCache[t] = result;
        return result;
    }

    private static void ScrubEvent(UnityEventBase evt, HashSet<string> approved)
    {
        if (evt == null) return;
        int count = evt.GetPersistentEventCount();
        for (int i = 0; i < count; i++)
        {
            UnityEngine.Object target = evt.GetPersistentTarget(i);
            string methodName = evt.GetPersistentMethodName(i);
            if (IsDangerousListener(target, methodName, approved))
            {
                if (VerboseLogging)
                {
                    BasisDebug.LogWarning($"[ContentPolice] Disabling persistent UnityEvent listener -> {(target != null ? target.GetType().FullName : "<static>")}.{methodName}");
                }
                evt.SetPersistentListenerState(i, UnityEventCallState.Off);
            }
        }
    }

    private static bool IsDangerousListener(UnityEngine.Object target, string methodName, HashSet<string> approved)
    {
        if (string.IsNullOrEmpty(methodName)) return true;

        // Null target means a static call with the target-type name stored in
        // a private field. We can't read it portably, so rip it.
        if (target == null) return true;

        string typeName = target.GetType().FullName;
        if (typeName == null) return true;

        if (BlockedTargetFullNames.Contains(typeName)) return true;

        for (int i = 0; i < BlockedTargetNamespacePrefixes.Length; i++)
        {
            if (typeName.StartsWith(BlockedTargetNamespacePrefixes[i], StringComparison.Ordinal))
                return true;
        }

        // If the target type isn't in the content-police approved list the
        // listener survived only because the target is an asset reference
        // outside the clone (scriptable object, material, etc.) — refuse.
        if (approved != null && !approved.Contains(typeName)) return true;

        if (BlockedMethodNames.Contains(methodName)) return true;

        return false;
    }

    // ------------------------------------------------------------------
    // AnimationEvent scrubbing.
    //
    // AnimationClip.events[] is a serialized payload of (method, time,
    // parameter) tuples. During playback Unity fires each event via
    // gameObject.SendMessage(method, parameter, DontRequireReceiver) on
    // the Animator/Animation component's GameObject. SendMessage hits
    // ANY method of that name on ANY component on that GameObject —
    // public or private, on approved scripts too — so a malicious clip
    // can invoke arbitrary methods that were never meant to be reachable
    // from an animation track.
    //
    // animator.fireEvents = false closes the primary gate, but it is a
    // runtime bool that any surviving approved script can flip, and the
    // legacy Animation component has no equivalent flag at all. The
    // authoritative fix is to clear AnimationClip.events on every clip
    // reached through the loaded subtree. The clips come from the user
    // bundle (unique per load), so clearing them is safe — no shared SDK
    // asset is mutated.
    // ------------------------------------------------------------------

    private static readonly AnimationEvent[] EmptyAnimationEvents = new AnimationEvent[0];

    private static void StripEventsFromRuntimeController(RuntimeAnimatorController controller)
    {
        // AnimatorOverrideController can wrap another controller; cap the
        // walk to defend against pathological chains.
        int safety = 8;
        while (controller != null && safety-- > 0)
        {
            AnimationClip[] clips = controller.animationClips;
            if (clips != null)
            {
                for (int i = 0; i < clips.Length; i++)
                {
                    ClearAnimationEventsOnClip(clips[i]);
                }
            }
            // AnimatorOverrideController.animationClips returns only the
            // effective (overridden) set — walk to the base controller so
            // un-overridden base clips are also scrubbed.
            AnimatorOverrideController overrideController = controller as AnimatorOverrideController;
            if (overrideController == null) break;
            RuntimeAnimatorController next = overrideController.runtimeAnimatorController;
            if (ReferenceEquals(next, controller)) break;
            controller = next;
        }
    }

    private static void StripEventsFromLegacyAnimation(Animation legacyAnimation)
    {
        if (legacyAnimation == null) return;
        foreach (AnimationState state in legacyAnimation)
        {
            if (state == null) continue;
            ClearAnimationEventsOnClip(state.clip);
        }
    }

    private static void ClearAnimationEventsOnClip(AnimationClip clip)
    {
        if (clip == null) return;
        AnimationEvent[] existing = clip.events;
        if (existing == null || existing.Length == 0) return;
        if (VerboseLogging)
        {
            BasisDebug.LogWarning($"[ContentPolice] Stripping {existing.Length} AnimationEvent(s) from clip '{clip.name}'");
        }
        clip.events = EmptyAnimationEvents;
    }

    // .NET Standard 2.0 has no ReferenceEqualityComparer; supply one so the
    // visited set uses identity, not UnityEngine.Object.Equals (which is
    // value-equal across destroyed objects).
    private sealed class ReferenceEqualityComparerLocal : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparerLocal Instance = new ReferenceEqualityComparerLocal();
        public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
        public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
    }
}
/// <summary>
/// Defines the checks required for content control.
/// </summary>
public struct ChecksRequired
{
    public bool UseContentRemoval;
    public bool DisableAnimatorEvents;
    public bool RemoveColliders;
    public bool ChangeCollidersToCorrectLayer;
    // When true, ContentPoliceControl walks every surviving component and
    // disables any persistent UnityEvent listener whose target type or method
    // name looks like an escape hatch (Application.OpenURL, File.*,
    // Process.Start, assembly loading, etc.). Opt-in so legacy prop bundles
    // with wired-up Buttons keep working; cilbox-initiated spawns enable it.
    public bool ScrubPersistentUnityEvents;
    // When false (default), legacy Animation clip events are stripped.
    // Set to true only if legacy animation events are explicitly trusted.
    public bool AllowLegacyEvents;
    public ChecksRequired(bool useContentRemoval, bool disableAnimatorEvents, bool removeColliders,bool changeColidersToCorrectLayer)
    {
        UseContentRemoval = useContentRemoval;
        DisableAnimatorEvents = disableAnimatorEvents;
        RemoveColliders = removeColliders;
        ChangeCollidersToCorrectLayer = changeColidersToCorrectLayer;
        ScrubPersistentUnityEvents = false;
        AllowLegacyEvents = false;
    }
}
