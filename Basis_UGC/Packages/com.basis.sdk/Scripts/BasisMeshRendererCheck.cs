using System;
using UnityEngine;

public class BasisMeshRendererCheck : MonoBehaviour
{
    // An Action delegate that will be triggered when visibility changes
    public Action<bool> Check;
    public Action DestroyCalled;
    // This method is called when the renderer becomes visible to any camera
    private void OnBecameVisible()
    {
        // Invoke the delegate with 'true' to indicate that the object is visible
        Check?.Invoke(true);
    }

    // This method is called when the renderer is no longer visible to any camera
    private void OnBecameInvisible()
    {
        // Invoke the delegate with 'false' to indicate that the object is not visible
        Check?.Invoke(false);
    }
    public void OnDestroy()
    {
        DestroyCalled?.Invoke();
    }
}