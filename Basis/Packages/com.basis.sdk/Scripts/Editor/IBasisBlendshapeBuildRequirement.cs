using System.Collections.Generic;
using UnityEngine;

public interface IBasisBlendshapeBuildRequirement
{
    /// <summary>
    /// Add required blendshape names per SkinnedMeshRenderer.
    /// Keys must be the SkinnedMeshRenderer instances under the build clone.
    /// Values are blendshape names (exact) to keep on that renderer's mesh.
    /// </summary>
    void CollectBlendshapeRequirements(Dictionary<SkinnedMeshRenderer, HashSet<string>> requirements);
}
