using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using static BundledContentHolder;
[AutoStaticsCleanup]
public static partial class BasisWorldSceneGate
{
    public sealed class Ticket
    {
        public string ScenePath;
        public ChecksRequired Checks;
        public Selector Selector;
        public Action<IList<Renderer>> RendererPostprocessor;
        public Scene Scene;
        public bool Claimed, Scanned;
        public readonly List<Renderer> Renderers = new List<Renderer>();
        private List<Renderer> gatedRenderers;
        private bool[] previousForceRenderingOff;
        internal void GateRenderers(Scene scene)
        {
            List<GameObject> roots = new List<GameObject>();
            scene.GetRootGameObjects(roots);
            gatedRenderers = new List<Renderer>();
            List<Renderer> found = new List<Renderer>();
            for (int Index = 0; Index < roots.Count; Index++)
            {
                roots[Index].GetComponentsInChildren(true, found);
                gatedRenderers.AddRange(found);
            }
            previousForceRenderingOff = new bool[gatedRenderers.Count];
            for (int Index = 0; Index < gatedRenderers.Count; Index++)
            {
                Renderer renderer = gatedRenderers[Index];
                if (renderer == null) continue;
                previousForceRenderingOff[Index] = renderer.forceRenderingOff;
                renderer.forceRenderingOff = true;
            }
        }
        public void RestoreRenderers()
        {
            if (gatedRenderers == null || previousForceRenderingOff == null) return;
            for (int Index = 0; Index < gatedRenderers.Count; Index++)
            {
                Renderer renderer = gatedRenderers[Index];
                if (renderer != null) renderer.forceRenderingOff = previousForceRenderingOff[Index];
            }
            gatedRenderers = null;
            previousForceRenderingOff = null;
        }
    }
    private static readonly List<Ticket> pending = new List<Ticket>();
    private static readonly HashSet<Scene> claimedScenes = new HashSet<Scene>();
    private static bool hooked;
    public static int PendingCount => pending.Count;
    public static Ticket Expect(string scenePath, ChecksRequired checks, Selector selector, Action<IList<Renderer>> rendererPostprocessor = null)
    {
        Ticket ticket = new Ticket { ScenePath = scenePath ?? string.Empty, Checks = checks, Selector = selector, RendererPostprocessor = rendererPostprocessor };
        pending.Add(ticket);
        HookFirst();
        return ticket;
    }
    public static void Release(Ticket ticket)
    {
        pending.Remove(ticket);
        if (pending.Count == 0) Unhook();
    }
    public static void Claim(Ticket ticket, Scene scene)
    {
        if (ticket == null || ticket.Claimed) return;
        ticket.Claimed = true;
        ticket.Scene = scene;
        claimedScenes.RemoveWhere(claimed => !claimed.IsValid() || !claimed.isLoaded);
        claimedScenes.Add(scene);
        ticket.Renderers.Clear();
        try
        {
            ticket.GateRenderers(scene);
            ticket.Scanned = ContentPoliceControl.ScrubScene(ticket.Checks, ticket.Selector, scene, true, ticket.RendererPostprocessor, ticket.Renderers);
        }
        catch (Exception ex)
        {
            ticket.Scanned = false;
            BasisDebug.LogError($"Content Police failed on scene {scene.path}: {ex}", BasisDebug.LogTag.Event);
        }
        if (!ticket.Scanned) Lock(scene);
    }
    public static bool IsClaimed(Scene scene) => scene.IsValid() && claimedScenes.Contains(scene);
    public static void Lock(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;
        List<GameObject> roots = new List<GameObject>();
        scene.GetRootGameObjects(roots);
        for (int Index = 0; Index < roots.Count; Index++)
        {
            if (roots[Index] != null) roots[Index].SetActive(false);
        }
    }
    public static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        for (int Index = 0; Index < pending.Count; Index++)
        {
            Ticket ticket = pending[Index];
            if (!ticket.Claimed && string.Equals(ticket.ScenePath, scene.path, StringComparison.OrdinalIgnoreCase))
            {
                Claim(ticket, scene);
                return;
            }
        }
    }
    public static bool IsFirstSceneLoadedHandler()
    {
        FieldInfo field = SceneLoadedField();
        if (field == null) return false;
        UnityAction<Scene, LoadSceneMode> current = field.GetValue(null) as UnityAction<Scene, LoadSceneMode>;
        if (current == null) return false;
        Delegate[] list = current.GetInvocationList();
        return list.Length > 0 && list[0].Method == ((UnityAction<Scene, LoadSceneMode>)OnSceneLoaded).Method && list[0].Target == null;
    }
    private static FieldInfo SceneLoadedField()
    {
        FieldInfo field = typeof(SceneManager).GetField("sceneLoaded", BindingFlags.NonPublic | BindingFlags.Static);
        return field != null && field.FieldType == typeof(UnityAction<Scene, LoadSceneMode>) ? field : null;
    }
    private static void HookFirst()
    {
        if (hooked && IsFirstSceneLoadedHandler()) return;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        hooked = true;
        FieldInfo field = SceneLoadedField();
        if (field != null)
        {
            try
            {
                UnityAction<Scene, LoadSceneMode> first = OnSceneLoaded;
                UnityAction<Scene, LoadSceneMode> existing = field.GetValue(null) as UnityAction<Scene, LoadSceneMode>;
                field.SetValue(null, existing == null ? first : (UnityAction<Scene, LoadSceneMode>)Delegate.Combine(first, existing));
                return;
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Could not order the world scan ahead of other sceneLoaded handlers: {ex.Message}", BasisDebug.LogTag.Event);
            }
        }
        SceneManager.sceneLoaded += OnSceneLoaded;
    }
    private static void Unhook()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        hooked = false;
    }
}
