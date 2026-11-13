using Unity.Scripting.LifecycleManagement;
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
[AutoStaticsCleanup]
public sealed partial class BasisAvatarProxyGizmo : MonoBehaviour
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
    private readonly Dictionary<Transform, bool> legByBone = new Dictionary<Transform, bool>();
    private readonly Vector3[] ring = new Vector3[BasisAvatarProxy.CapsuleSides];
    private readonly Vector3[] segment = new Vector3[2];
    private float nextScan;
    private int used;
    private int active;
    private BasisAvatarProxyGizmoMode drawn = BasisAvatarProxyGizmoMode.Off;

    private static Mesh ringSource;
    private static Vector3[] ringLocal;
    private static int ringRows;

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

        if (drawn != BasisAvatarProxyGizmoMode.Bones && !EnsureRing()) { return; }

        used = 0;
        for (int index = 0; index < humanoids.Count; index++)
        {
            Animator animator = humanoids[index];
            if (animator == null) { continue; }
            BasisAvatarProxyPose pose = BasisAvatarProxy.PoseFor(animator);
            if (pose == null) { continue; }

            List<BasisAvatarProxy.ResolvedLimb> limbs = pose.Limbs;
            for (int limbIndex = 0; limbIndex < limbs.Count; limbIndex++)
            {
                DrawLimb(limbs[limbIndex]);
            }
        }

        // Anything left from a frame with more avatars in it is parked rather than destroyed, so a player
        // walking in and out does not churn the gizmo pool.
        for (int index = used; index < active; index++)
        {
            BasisGizmoManager.SetGizmoActive(gizmoIds[index], false);
        }
        active = used;
    }

    private void Rescan()
    {
        humanoids.Clear();
        legByBone.Clear();
        Animator[] found = FindObjectsByType<Animator>(FindObjectsInactive.Exclude);
        for (int index = 0; index < found.Length; index++)
        {
            if (found[index] != null && found[index].isHuman) { humanoids.Add(found[index]); }
        }
    }

    private static bool EnsureRing()
    {
        Mesh capsule = BasisAvatarProxy.SharedCapsule();
        if (capsule == null) { ringSource = null; ringLocal = null; ringRows = 0; return false; }
        if (capsule == ringSource) { return ringRows > 0; }

        ringSource = capsule;
        ringLocal = null;
        ringRows = 0;
        if (!capsule.isReadable) { return false; }

        Vector3[] vertices = capsule.vertices;
        int stride = BasisAvatarProxy.CapsuleStride;
        int sides = BasisAvatarProxy.CapsuleSides;
        if (sides < 2 || vertices.Length < stride) { return false; }
        int rows = vertices.Length / stride;

        ringLocal = new Vector3[rows * sides];
        for (int row = 0; row < rows; row++)
        {
            Vector3 first = vertices[row * stride];
            if (first.x * first.x + first.z * first.z < 1e-8f) { continue; }
            System.Array.Copy(vertices, row * stride, ringLocal, ringRows * sides, sides);
            ringRows++;
        }
        return ringRows > 0;
    }

    private bool IsLeg(Transform bone)
    {
        // By name, because a ResolvedLimb has already forgotten which HumanBodyBones it came from - it holds
        // transforms so a per-frame update is two position reads and no lookup.
        if (legByBone.TryGetValue(bone, out bool leg)) { return leg; }
        string name = bone.name;
        leg = name.IndexOf("Leg", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Foot", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Knee", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Shin", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Calf", System.StringComparison.OrdinalIgnoreCase) >= 0;
        legByBone.Add(bone, leg);
        return leg;
    }

    private void DrawLimb(in BasisAvatarProxy.ResolvedLimb limb)
    {
        if (!limb.IsValid) { return; }

        if (drawn == BasisAvatarProxyGizmoMode.Bones)
        {
            segment[0] = limb.From.position;
            segment[1] = limb.To.position;
            DrawPolyline(segment, BoneColour, false);
            return;
        }

        DrawCapsule(BasisAvatarProxy.MatrixFor(limb), IsLeg(limb.From) ? LegColour : LimbColour);

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
        // One looped polyline per ring row. The rows ARE the capsule's silhouette, so this reads as the
        // shape without drawing every triangle edge - which at sixteen limbs an avatar would be thousands
        // of lines for no more information.
        int sides = ring.Length;
        for (int row = 0; row < ringRows; row++)
        {
            int offset = row * sides;
            for (int side = 0; side < sides; side++)
            {
                ring[side] = matrix.MultiplyPoint3x4(ringLocal[offset + side]);
            }
            DrawPolyline(ring, colour, true);
        }
    }

    private void DrawPolyline(Vector3[] points, Color colour, bool loop)
    {
        if (used < gizmoIds.Count)
        {
            BasisGizmoManager.UpdateLineGizmo(gizmoIds[used], points, colour);
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
        active = 0;
    }
}
