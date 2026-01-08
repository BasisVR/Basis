using System;
using System.Collections.Generic;
using UnityEngine;

public static class BasisBlendshapeBuildHooks
{
    /// <summary>
    /// Subscribers can add blendshape names to keep for specific renderers.
    /// </summary>
    public static event Action<Dictionary<SkinnedMeshRenderer, HashSet<string>>> OnCollectRequirements;

    public static void Collect(Dictionary<SkinnedMeshRenderer, HashSet<string>> req)
        => OnCollectRequirements?.Invoke(req);
}
