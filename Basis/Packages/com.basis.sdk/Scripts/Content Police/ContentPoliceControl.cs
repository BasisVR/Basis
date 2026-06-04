using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

public static class ContentPoliceControl
{
    public static bool ShaderPrewarmEnabled = false;
    public static bool MaterialCorrectionEnabled = false;

    private static readonly bool PrewarmForcedByGraphicsApi =
        SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 ||
        SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan;

    private static bool ShouldPrewarm => ShaderPrewarmEnabled || PrewarmForcedByGraphicsApi;

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
        if (ChecksRequired.UseContentRemoval)
        {
            SearchAndDestroy = GameObject.Instantiate(SearchAndDestroy, Position, Rotation, DisabledGameobject.transform);
            if (ModifyScale)
            {
                BasisDebug.Log($"Overriding Default scale is now {Scale} for GameObject {SearchAndDestroy.name}");
                SearchAndDestroy.transform.localScale = Scale;
            }
            // Create a list to hold all components in the original GameObject
            UnityEngine.Component[] components = SearchAndDestroy.GetComponentsInChildren<UnityEngine.Component>(true);

            int count = components.Length;

            if (BundledContentHolder.Instance.GetSelector(Selector, out ContentPoliceSelector PoliceCheck))
            {
                harvest ??= new BasisContentHarvest();
                // Renderers harvested during the same walk that strips dangerous components,
                // so shader prewarm runs without paying for a second GetComponentsInChildren.
                List<Renderer> renderersForPrewarm = new List<Renderer>();
                List<SkinnedMeshRenderer> skinnedForHarvest = harvest != null ? new List<SkinnedMeshRenderer>() : null;
                List<BasisAuthoredMotion> authoredForHarvest = harvest != null ? new List<BasisAuthoredMotion>() : null;
                BasisComponentKind[] kinds = harvest != null ? new BasisComponentKind[count] : null;

                // BasisHeadChop is harvested during this walk so the local avatar driver
                // doesn't need a second GetComponentsInChildren pass at calibration. Harvest
                // is appended only when the caller passed a non-null collector — that way the
                // data flows back through the call chain rather than living on BasisAvatar.
                for (int Index = 0; Index < count; Index++)
                {
                    Component component = components[Index];
                    if (kinds != null)
                    {
                        kinds[Index] = BasisContentHarvest.Classify(component);
                    }
                    //do this first before we nuke stuff
                    switch (component)
                    {
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
                            if (kinds != null) kinds[Index] = BasisComponentKind.Removed;
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
                        case Collider collider:

                            if (ChecksRequired.RemoveColliders)
                            {
                                BasisDebug.Log("Remove Collider ", BasisDebug.LogTag.Avatar);
                                GameObject.Destroy(collider);
                                if (kinds != null) kinds[Index] = BasisComponentKind.Removed;
                            }
                            else
                            {
                                if (ChecksRequired.ChangeCollidersToCorrectLayer)
                                {
                                    BasisDebug.Log("Changing Collider To Correct Layer", BasisDebug.LogTag.Avatar);
                                    collider.gameObject.layer = colliderlayer;
                                }
                            }
                            break;
                        case AudioSource source:
                            source.outputAudioMixerGroup = PoliceCheck.AudioMixer;
                            break;
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
                    // Check if the component is a MonoBehaviour and not in the approved list
                    if (component is UnityEngine.Component monoBehaviour)
                    {
                        string monoTypeName = monoBehaviour.GetType().FullName;
                        if (!PoliceCheck.ApprovedTypeNames.Contains(monoTypeName))
                        {
                            BasisDebug.LogError($"MonoBehaviour {monoTypeName} is not approved and will be removed. Request the {Application.productName} team to add it to the approved list, or add it yourself!", BasisDebug.LogTag.System);
                            GameObject.DestroyImmediate(monoBehaviour); // Destroy the unapproved MonoBehaviour immediately
                            if (kinds != null) kinds[Index] = BasisComponentKind.Removed;
                        }
                    }
                }

                if (harvest != null)
                {
                    harvest.Components = components;
                    harvest.Kinds = kinds;
                    harvest.Renderers = renderersForPrewarm;
                    harvest.SkinnedMeshRenderers = skinnedForHarvest;
                    harvest.AuthoredMotions = authoredForHarvest;
                }

                if (MaterialCorrectionEnabled)
                {
                    BasisShaderFallback.MaterialCorrection(renderersForPrewarm, BundledContentHolder.Instance.UrpShader);
                }

                // Compile shader variants for everything we just walked before we set the clone
                // active, so the first frame it's visible doesn't stall on a hitch.
                if (ShouldPrewarm)
                {
                    BasisShaderPrewarm.Warm(renderersForPrewarm, SearchAndDestroy.name);
                }

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
            else
            {
                BasisDebug.LogError("Can't find Police check for " + Selector, BasisDebug.LogTag.Event);
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
            // No content-removal walk happened, so a dedicated Renderer-typed walk is the only
            // way to feed the prewarm here. Cheaper than the full Component[] walk above.
            Renderer[] rawRenderers = SearchAndDestroy.GetComponentsInChildren<Renderer>(true);
            if (harvest != null)
            {
                harvest.Renderers = new List<Renderer>(rawRenderers);
            }
            if (MaterialCorrectionEnabled)
            {
                BasisShaderFallback.MaterialCorrection(rawRenderers, BundledContentHolder.Instance.UrpShader);
            }
            if (ShouldPrewarm)
            {
                BasisShaderPrewarm.Warm(rawRenderers, SearchAndDestroy.name);
            }
        }
        if (harvest != null && SearchAndDestroy != null && SearchAndDestroy.TryGetComponent(out BasisContentBase contentBase))
        {
            contentBase.Harvest = harvest;
        }
        return SearchAndDestroy;
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

        GameObject[] roots = targetScene.GetRootGameObjects();
        // Renderers harvested across all roots during the same walk that strips dangerous
        // components, so shader prewarm runs without paying for a second walk.
        List<Renderer> renderersForPrewarm = new List<Renderer>();
        for (int RootIndex = 0; RootIndex < roots.Length; RootIndex++)
        {
            // Get ALL components in this subtree
            Component[] components = roots[RootIndex].transform.GetComponentsInChildren<Component>(includeInactive);
            // Check if the component is a MonoBehaviour and not in the approved list
            for (int ComponentIndex = 0; ComponentIndex < components.Length; ComponentIndex++)
            {
                Component component = components[ComponentIndex];
                //do this first before we nuke stuff
                switch (component)
                {
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
                // Check if the component is a MonoBehaviour and not in the approved list
                if (component is UnityEngine.Component monoBehaviour)
                {
                    string monoTypeName = monoBehaviour.GetType().FullName;
                    if (!policeCheck.ApprovedTypeNames.Contains(monoTypeName))
                    {
                        BasisDebug.LogError($"MonoBehaviour {monoTypeName} is not approved and will be removed. Request the {Application.productName} team to add it to the approved list, or add it yourself!");
                        GameObject.DestroyImmediate(monoBehaviour); // Destroy the unapproved MonoBehaviour immediately
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
        if (MaterialCorrectionEnabled)
        {
            BasisShaderFallback.MaterialCorrection(renderersForPrewarm, BundledContentHolder.Instance.UrpShader);
        }

        // Warm shaders for every renderer we just collected. One call per scene scrub.
        if (ShouldPrewarm)
        {
            BasisShaderPrewarm.Warm(renderersForPrewarm, targetScene.name);
        }
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

    private static void ScrubDangerousPersistentListeners(Component[] comps, ContentPoliceSelector police)
    {
        if (comps == null) return;
        HashSet<string> approved = police != null ? police.ApprovedTypeNames : null;

        HashSet<object> visited = EventWalkVisited;
        visited.Clear();
        for (int i = 0; i < comps.Length; i++)
        {
            Component c = comps[i];
            if (c == null) continue;
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
                    else if (ShouldRecurseInto(et))
                        plan.Add(new WalkField(f, WalkFieldKind.RecurseList));
                    continue;
                }

                if (ft.IsGenericType && typeof(IList).IsAssignableFrom(ft))
                {
                    Type[] args = ft.GetGenericArguments();
                    Type et = args.Length == 1 ? args[0] : null;
                    if (et != null && typeof(UnityEventBase).IsAssignableFrom(et))
                        plan.Add(new WalkField(f, WalkFieldKind.EventList));
                    else if (et != null && ShouldRecurseInto(et))
                        plan.Add(new WalkField(f, WalkFieldKind.RecurseList));
                    continue;
                }

                if (!ShouldRecurseInto(ft)) continue;
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
                BasisDebug.LogWarning($"[ContentPolice] Disabling persistent UnityEvent listener -> {(target != null ? target.GetType().FullName : "<static>")}.{methodName}");
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
        BasisDebug.LogWarning($"[ContentPolice] Stripping {existing.Length} AnimationEvent(s) from clip '{clip.name}'");
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
