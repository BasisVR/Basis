using System.Collections.Generic;
using UnityEngine;

/// <summary>What the proxy debug view draws.</summary>
public enum BasisAvatarProxyGizmoMode
{
    Off,

    /// <summary>
    /// The capsules themselves, drawn from the SHARED MESH transformed by the SAME matrix the tracer uses.
    /// Not a re-derivation - a hand-drawn approximation of a capsule is exactly the thing that would agree
    /// with your mental model while the traced geometry quietly disagreed with both.
    /// </summary>
    Capsules,

    /// <summary>
    /// The bone segments alone. Where a limb THINKS it runs, without the shape around it - which is what
    /// you want when a capsule looks wrong and the question is whether the bones or the radius are at fault.
    /// </summary>
    Bones,

    /// <summary>
    /// The fitted capsule and, in a second colour, what the body plan alone would have given it. A radius
    /// measured off the mesh that has run away from the plan shows up immediately as two rings far apart -
    /// and a capsule wider than the body it stands for is the shape of every artefact this area produces.
    /// </summary>
    FitAgainstPlan,
}

/// <summary>
/// Draws avatar proxy capsules in world space, through the runtime gizmo system, so they can be looked at
/// in a headset rather than inferred from a screenshot.
///
/// This exists because the proxies are the one part of the ray traced picture that does NOT match what is on
/// screen: the tracer sees capsules on bones while every ray starts from the avatar's real rendered surface,
/// and every artefact in this area so far has come from the two disagreeing somewhere nobody could see. The
/// capsules are drawn from <see cref="BasisAvatarProxy.SharedCapsule"/> transformed by
/// <see cref="BasisAvatarProxy.MatrixFor"/> - the same mesh and the same matrix the acceleration structure
/// is built from - so what you see is what is being traced, not a drawing of what it ought to be.
///
/// Drop it on anything in the scene, or set <see cref="Mode"/> from a console. It finds humanoid avatars
/// itself and asks for their pose, so it works whether or not a tracer is running.
/// </summary>
public sealed class BasisAvatarProxyGizmo : MonoBehaviour
{
    /// <summary>Shared so a developer toggle or a console can drive it without holding the component.</summary>
    public static BasisAvatarProxyGizmoMode Mode = BasisAvatarProxyGizmoMode.Capsules;

    /// <summary>How often the humanoid scan runs. It is a debug view; it does not need to be free.</summary>
    public static float RescanSeconds = 2f;

    public static float LineWidth = 0.004f;

    /// <summary>Legs in their own colour - they are where the artefacts keep turning up.</summary>
    private static readonly Color LimbColour = new Color(0.35f, 0.85f, 1f);
    private static readonly Color LegColour = new Color(1f, 0.75f, 0.2f);
    private static readonly Color PlanColour = new Color(1f, 0.25f, 0.35f);
    private static readonly Color BoneColour = new Color(0.6f, 1f, 0.4f);

    private readonly List<Animator> humanoids = new List<Animator>();
    private readonly List<int> gizmoIds = new List<int>();
    private readonly List<BasisAvatarProxy.ResolvedLimb> limbScratch = new List<BasisAvatarProxy.ResolvedLimb>();
    private float nextScan;
    private int used;
    private BasisAvatarProxyGizmoMode drawn = BasisAvatarProxyGizmoMode.Off;

    private static BasisAvatarProxyGizmo instance;

    /// <summary>
    /// Turned on and off by the Gizmos section of the settings panel, through SMModuleDebugOptions like
    /// every other gizmo. It hosts itself rather than needing a component placed in the scene, because
    /// nothing else in the project ticks the proxy - the ray scenes ask for poses when they build, and a
    /// debug view has to work whether or not one of them is running.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        if (!enabled) { Shutdown(); return; }
        if (instance != null) { return; }

        GameObject host = new GameObject("BasisAvatarProxyGizmo") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(host);
        instance = host.AddComponent<BasisAvatarProxyGizmo>();
    }

    public static void Shutdown()
    {
        if (instance == null) { return; }
        GameObject host = instance.gameObject;
        instance.ReleaseAll();
        instance = null;
        if (Application.isPlaying) { Destroy(host); } else { DestroyImmediate(host); }
    }

    private void OnDisable()
    {
        ReleaseAll();
    }

    private void LateUpdate()
    {
        if (Mode == BasisAvatarProxyGizmoMode.Off)
        {
            if (drawn != BasisAvatarProxyGizmoMode.Off) { ReleaseAll(); }
            drawn = BasisAvatarProxyGizmoMode.Off;
            return;
        }

        // A mode change rebuilds rather than reuses: the shapes differ per mode and a stale one left over
        // would be a line that means nothing, which on a debug view is worse than no line at all.
        if (drawn != Mode) { ReleaseAll(); drawn = Mode; }

        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + Mathf.Max(0.25f, RescanSeconds);
            Rescan();
        }

        used = 0;
        for (int index = 0; index < humanoids.Count; index++)
        {
            Animator animator = humanoids[index];
            if (animator == null) { continue; }
            BasisAvatarProxyPose pose = BasisAvatarProxy.PoseFor(animator);
            if (pose == null) { continue; }

            limbScratch.Clear();
            limbScratch.AddRange(pose.Limbs);
            for (int limbIndex = 0; limbIndex < limbScratch.Count; limbIndex++)
            {
                DrawLimb(limbScratch[limbIndex]);
            }
        }

        // Anything left from a frame with more avatars in it is parked rather than destroyed, so a player
        // walking in and out does not churn the gizmo pool.
        for (int index = used; index < gizmoIds.Count; index++)
        {
            BasisGizmoManager.SetGizmoActive(gizmoIds[index], false);
        }
    }

    private void Rescan()
    {
        humanoids.Clear();
        Animator[] found = FindObjectsByType<Animator>(FindObjectsInactive.Exclude);
        for (int index = 0; index < found.Length; index++)
        {
            if (found[index] != null && found[index].isHuman) { humanoids.Add(found[index]); }
        }
    }

    private static bool IsLeg(in BasisAvatarProxy.ResolvedLimb limb)
    {
        // By name, because a ResolvedLimb has already forgotten which HumanBodyBones it came from - it holds
        // transforms so a per-frame update is two position reads and no lookup.
        if (limb.From == null) { return false; }
        string name = limb.From.name;
        return name.IndexOf("Leg", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Foot", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Knee", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Shin", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Calf", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void DrawLimb(in BasisAvatarProxy.ResolvedLimb limb)
    {
        if (!limb.IsValid) { return; }

        Color colour = IsLeg(limb) ? LegColour : LimbColour;

        if (drawn == BasisAvatarProxyGizmoMode.Bones)
        {
            DrawPolyline(new[] { limb.From.position, limb.To.position }, BoneColour, false);
            return;
        }

        DrawCapsule(BasisAvatarProxy.MatrixFor(limb), colour);

        if (drawn == BasisAvatarProxyGizmoMode.FitAgainstPlan
            && !Mathf.Approximately(limb.Radius, limb.PlanRadius))
        {
            // The same limb at the radius the plan alone would have chosen. Only drawn when the two differ,
            // so an avatar whose meshes could not be read shows one outline rather than two identical ones.
            BasisAvatarProxy.ResolvedLimb planned =
                new BasisAvatarProxy.ResolvedLimb(limb.From, limb.To, limb.PlanRadius, limb.Extend);
            DrawCapsule(BasisAvatarProxy.MatrixFor(planned), PlanColour);
        }
    }

    private void DrawCapsule(Matrix4x4 matrix, Color colour)
    {
        Mesh capsule = BasisAvatarProxy.SharedCapsule();
        if (capsule == null || !capsule.isReadable) { return; }

        Vector3[] vertices = capsule.vertices;
        int stride = BasisAvatarProxy.CapsuleStride;
        if (stride <= 1 || vertices.Length < stride) { return; }
        int rows = vertices.Length / stride;

        // One looped polyline per ring row. The rows ARE the capsule's silhouette, so this reads as the
        // shape without drawing every triangle edge - which at sixteen limbs an avatar would be thousands
        // of lines for no more information.
        Vector3[] ring = new Vector3[stride];
        for (int row = 0; row < rows; row++)
        {
            for (int side = 0; side < stride; side++)
            {
                ring[side] = matrix.MultiplyPoint3x4(vertices[row * stride + side]);
            }
            DrawPolyline(ring, colour, true);
        }
    }

    private void DrawPolyline(Vector3[] points, Color colour, bool loop)
    {
        if (used < gizmoIds.Count)
        {
            int existing = gizmoIds[used];
            BasisGizmoManager.SetGizmoActive(existing, true);
            BasisGizmoManager.UpdateLineGizmo(existing, points);
            used++;
            return;
        }

        if (BasisGizmoManager.CreateLineGizmo("AvatarProxy", out int created, points, LineWidth, colour, loop))
        {
            gizmoIds.Add(created);
            used++;
        }
    }

    private void ReleaseAll()
    {
        for (int index = 0; index < gizmoIds.Count; index++)
        {
            BasisGizmoManager.DestroyGizmo(gizmoIds[index]);
        }
        gizmoIds.Clear();
        used = 0;
    }
}
