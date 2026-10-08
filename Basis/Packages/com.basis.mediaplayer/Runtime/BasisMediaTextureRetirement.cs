using Unity.Scripting.LifecycleManagement;
#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Holds a retired output RenderTexture past the session that was drawing into
/// it, and issues the plugin's collect event for as long as it does.
///
/// On Vulkan the plugin builds an image view over Unity's VkImage and cannot
/// destroy it at close time, because Unity may still have command buffers in
/// flight; <c>bm_session_close</c> parks it for a later render event instead.
/// Releasing the RenderTexture at the same time would destroy the image while
/// that view still exists, so the texture outlives the close. The parked
/// objects are destroyed by a render event and a closed session issues none of
/// its own, which is why this issues the collect event while it waits.
///
/// Textures are held until the events have been issued, so an app that stops
/// rendering holds them until it renders again. That is the same bound the
/// plugin's own parking has; releasing on a timer instead would destroy the
/// very image the wait protects.
///
/// Android only. The D3D11 path rebuilds its consumer on registration and has
/// no equivalent requirement.
/// </summary>
[AutoStaticsCleanup]
internal static partial class BasisMediaTextureRetirement
{
    /// <summary>
    /// Collect events a retired texture is held for. It has to exceed the
    /// deepest frames-in-flight Unity runs, which is why the plugin sizes its
    /// descriptor ring at 16 as well; what waits on this retires against those
    /// same frame counters.
    /// </summary>
    const int HoldEvents = 16;

    struct Entry
    {
        internal RenderTexture Texture;
        internal int Remaining;
    }

    static readonly List<Entry> pending = new List<Entry>();
    [NoAutoStaticsCleanup] static CommandBuffer collect;
    static Driver driver;
    static bool quitting;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init() => Application.quitting += () => quitting = true;

    /// <summary>
    /// Take over <paramref name="texture"/> if it is one the plugin has built
    /// views over. Anything else (cover art, a Texture2D) is left to its
    /// owner.
    /// </summary>
    internal static void Retire(Texture texture)
    {
        // Every player is destroyed on the way out, and each one closes as it
        // goes: retiring then would build the driver GameObject during the
        // quit, which Unity refuses outright. Nothing needs releasing at that
        // point either: the process is taking the device with it.
        if (quitting) return;
        if (!(texture is RenderTexture target)) return;

        if (collect == null)
        {
            collect = new CommandBuffer { name = "BasisMedia collect" };
            // No session is looked up for this one, which is the point: by the
            // time anything is waiting here, the session it belonged to has been
            // closed and its handle retired.
            collect.IssuePluginEventAndData(
                BasisMediaNative.bm_render_event_func(),
                BasisMediaNative.RenderEventCollect,
                IntPtr.Zero);
        }
        pending.Add(new Entry { Texture = target, Remaining = HoldEvents });

        if (driver == null)
        {
            // Outlives the player's GameObject on purpose: Stop is routinely
            // followed by the object going away, and the retention has to
            // survive that.
            var host = new GameObject(nameof(BasisMediaTextureRetirement))
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            driver = host.AddComponent<Driver>();
        }
        driver.enabled = true;
    }

    static void Tick()
    {
        if (pending.Count == 0)
        {
            if (driver != null) driver.enabled = false;
            return;
        }
        Graphics.ExecuteCommandBuffer(collect);
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            Entry entry = pending[i];
            entry.Remaining--;
            if (entry.Remaining > 0)
            {
                pending[i] = entry;
                continue;
            }
            pending.RemoveAt(i);
            if (entry.Texture == null) continue;
            entry.Texture.Release();
            UnityEngine.Object.Destroy(entry.Texture);
        }
    }

    sealed class Driver : MonoBehaviour
    {
        int lastFrame = -1;

        void OnEnable() => RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

        void OnDisable() => RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

        void OnEndCameraRendering(ScriptableRenderContext context, Camera camera) => Run();

        void Update()
        {
            // A scriptable pipeline ignores camera command buffers, which is
            // why the player's own render event rides endCameraRendering
            // there; this covers the case where no pipeline is driving one.
            if (GraphicsSettings.currentRenderPipeline == null) Run();
        }

        void Run()
        {
            if (lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            Tick();
        }
    }
}
#endif
