using System;
using System.Collections.Generic;
using UnityEngine;

public class BasisMeshRendererCheck : MonoBehaviour
{
    public const int InvisibleDelayFrames = 30;
    // An Action delegate that will be triggered when visibility changes
    public Action<bool> Check;
    public Action DestroyCalled;
    private int reported = -1;
    private int invisibleSince = -1;
    private static readonly List<BasisMeshRendererCheck> pending = new List<BasisMeshRendererCheck>();
    private static bool hooked;
    // This method is called when the renderer becomes visible to any camera
    private void OnBecameVisible()
    {
        if (invisibleSince >= 0)
        {
            invisibleSince = -1;
            pending.Remove(this);
        }
        if (reported == 1) return;
        reported = 1;
        // Invoke the delegate with 'true' to indicate that the object is visible
        Check?.Invoke(true);
    }

    // This method is called when the renderer is no longer visible to any camera
    private void OnBecameInvisible()
    {
        if (reported == 0 || invisibleSince >= 0) return;
        invisibleSince = Time.frameCount;
        pending.Add(this);
        if (!hooked)
        {
            hooked = true;
            Application.onBeforeRender += FlushPending;
        }
    }
    private static void FlushPending()
    {
        int count = pending.Count;
        if (count == 0) return;
        int frame = Time.frameCount;
        for (int i = count - 1; i >= 0; i--)
        {
            BasisMeshRendererCheck check = pending[i];
            if (frame - check.invisibleSince < InvisibleDelayFrames) continue;
            pending.RemoveAt(i);
            check.invisibleSince = -1;
            check.reported = 0;
            // Invoke the delegate with 'false' to indicate that the object is not visible
            try { check.Check?.Invoke(false); }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        pending.Clear();
    }
    public void OnDestroy()
    {
        if (invisibleSince >= 0)
        {
            invisibleSince = -1;
            pending.Remove(this);
        }
        DestroyCalled?.Invoke();
    }
}
