using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One walk of the content hierarchy, shared by every check in a pass.
///
/// <para>Each check used to do its own <c>GetComponentsInChildren</c> — a fresh array per call, per
/// check — and then filter it with LINQ. On a dense avatar that is several thousand components
/// copied per pass before a single thing has been validated. This walks once, keeps its lists, and
/// hands them out.</para>
///
/// <para><see cref="All"/> includes EditorOnly objects because a missing script has to be reported
/// wherever it is; <see cref="Active"/> excludes EditorOnly subtrees because nothing under one
/// reaches the upload, so its meshes and textures are none of the validator's business.</para>
/// </summary>
[NoAutoStaticsCleanup]
public sealed class BasisValidationHierarchyScan
{
    public readonly List<Transform> All = new List<Transform>();
    public readonly List<Transform> Active = new List<Transform>();
    public readonly List<Renderer> Renderers = new List<Renderer>();
    public readonly List<SkinnedMeshRenderer> SkinnedMeshes = new List<SkinnedMeshRenderer>();

    private static readonly List<Renderer> RendererScratch = new List<Renderer>(4);
    private static readonly List<SkinnedMeshRenderer> SkinnedScratch = new List<SkinnedMeshRenderer>(4);

    public void Rebuild(Transform root)
    {
        All.Clear();
        Active.Clear();
        Renderers.Clear();
        SkinnedMeshes.Clear();

        if (root == null)
        {
            return;
        }

        // The root itself counts as active even if it is tagged EditorOnly: it is the thing being
        // authored, and the old collector treated it the same way.
        Walk(root, true);
    }

    private void Walk(Transform transform, bool active)
    {
        All.Add(transform);

        if (active)
        {
            Active.Add(transform);

            transform.GetComponents(RendererScratch);
            int count = RendererScratch.Count;
            for (int Index = 0; Index < count; Index++)
            {
                Renderers.Add(RendererScratch[Index]);
            }

            transform.GetComponents(SkinnedScratch);
            count = SkinnedScratch.Count;
            for (int Index = 0; Index < count; Index++)
            {
                SkinnedMeshes.Add(SkinnedScratch[Index]);
            }
        }

        int childCount = transform.childCount;
        for (int Index = 0; Index < childCount; Index++)
        {
            Transform child = transform.GetChild(Index);
            Walk(child, active && !child.gameObject.CompareTag("EditorOnly"));
        }
    }
}
