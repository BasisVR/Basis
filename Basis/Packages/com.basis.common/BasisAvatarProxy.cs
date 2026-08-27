using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// An avatar's body as a handful of capsules on its bones, instead of its actual skinned mesh.
///
/// Lives in Common because global illumination and ambient occlusion both trace the same avatars and hit
/// the same wall. Sharing it is not only about duplicated source: BasisAvatarProxyPose below makes the two
/// effects share the per frame WORK as well, so the bones are read and the limb matrices built once for a
/// room however many tracers are looking at it.
///
/// The dynamic path re-bakes a SkinnedMeshRenderer into a real mesh and swaps that mesh into the
/// acceleration structure. That is expensive enough to need a per-frame budget, the budget is spent
/// round-robin, and the result is that the body which occludes and bounces light is NOT the body on
/// screen - it is that avatar's pose from up to (avatars / budget) x interval frames ago, staggered
/// differently for every person in the room. When an avatar's turn finally comes round its traced pose
/// jumps several frames at once, which the temporal filter can only smear. That is the artifacting:
/// occlusion trailing a limb, contact shadows detached from the foot casting them, and a wrongness that
/// looks random precisely because everyone is stale by a different amount.
///
/// None of that is a tuning problem. The backend offers no BLAS refit and no vertex-buffer instance
/// (<c>MeshInstanceDesc</c> takes a Mesh), so a deformed mesh can only be updated by removing and
/// re-adding it - a full rebuild, every pose, forever.
///
/// So the body stops being geometry that deforms and becomes geometry that MOVES. One unit capsule mesh
/// is shared by every limb of every avatar, so there is exactly one BLAS for all of them and it never
/// changes; a pose update is UpdateInstanceTransform per limb, which needs no bake, no readback and no
/// rebuild. Every avatar updates every frame and the staleness is gone, not reduced.
///
/// What it costs is detail: fingers, hair and loose clothing are not in these capsules. At the
/// resolutions this runs at - a half resolution gather through a denoiser - the bounce off a hand is a
/// soft patch of light either way, and a silhouette in roughly the right place every frame beats an
/// exact one several frames late.
/// </summary>
public static class BasisAvatarProxy
{
    /// <summary>One limb: the bone it starts at, the bone it reaches to, and how thick it is.</summary>
    public readonly struct Limb
    {
        public readonly HumanBodyBones From;
        public readonly HumanBodyBones To;
        /// <summary>Radius as a fraction of the body's reference height, so it scales with the avatar.</summary>
        public readonly float RadiusFactor;

        public Limb(HumanBodyBones from, HumanBodyBones to, float radiusFactor)
        {
            From = from;
            To = to;
            RadiusFactor = radiusFactor;
        }
    }

    /// <summary>
    /// The body plan. Deliberately coarse: this is what casts occlusion and bounces colour, and the parts
    /// that matter are the ones with area - torso, head, upper and lower limbs. Hands and feet are the far
    /// ends of the forearm and shin capsules rather than capsules of their own, because a separate instance
    /// per extremity doubles the count to move light by less than the denoiser's own blur radius.
    /// </summary>
    public static readonly Limb[] Body =
    {
        new Limb(HumanBodyBones.Hips, HumanBodyBones.Spine, 0.115f),
        new Limb(HumanBodyBones.Spine, HumanBodyBones.Chest, 0.105f),
        new Limb(HumanBodyBones.Chest, HumanBodyBones.Neck, 0.100f),
        new Limb(HumanBodyBones.Neck, HumanBodyBones.Head, 0.045f),

        new Limb(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, 0.042f),
        new Limb(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, 0.034f),
        new Limb(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, 0.042f),
        new Limb(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, 0.034f),

        new Limb(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, 0.058f),
        new Limb(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, 0.046f),
        new Limb(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, 0.058f),
        new Limb(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, 0.046f),
    };

    /// <summary>The head is the one part with no child bone to reach towards, so it gets its own ball.</summary>
    public const float HeadRadiusFactor = 0.075f;

    /// <summary>
    /// A limb resolved against one avatar: the two transforms to read each frame, and the radius already
    /// scaled to that avatar's size. Held as transforms rather than bone enums so a per-frame update is two
    /// position reads and no lookup.
    /// </summary>
    public readonly struct ResolvedLimb
    {
        public readonly Transform From;
        public readonly Transform To;
        public readonly float Radius;
        /// <summary>Extends the capsule past its end bone. Used for the head, which reaches past its joint.</summary>
        public readonly float Extend;

        public ResolvedLimb(Transform from, Transform to, float radius, float extend)
        {
            From = from;
            To = to;
            Radius = radius;
            Extend = extend;
        }

        public bool IsValid => From != null && To != null;
    }

    /// <summary>
    /// Resolves the body plan against an animator, or returns false if it is not a humanoid this can
    /// describe. A non-humanoid avatar has no bone map to hang capsules on, so the caller keeps whatever it
    /// was doing before rather than getting a body-shaped guess.
    /// </summary>
    /// <summary>The layer Basis puts the local player's own avatar on, resolved once.</summary>
    private static int localAvatarLayer = -2;

    private static bool IsLocalAvatar(Animator animator)
    {
        if (localAvatarLayer == -2) { localAvatarLayer = LayerMask.NameToLayer("LocalPlayerAvatar"); }
        return localAvatarLayer >= 0 && animator.gameObject.layer == localAvatarLayer;
    }

    public static bool TryResolve(Animator animator, List<ResolvedLimb> destination)
    {
        destination.Clear();
        if (animator == null || !animator.isHuman) { return false; }

        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (hips == null || head == null) { return false; }

        // Hips to head, which is most of the body and present on every humanoid rig, is what the radii are
        // measured against. Absolute radii would fit one avatar and swallow or starve every other, and this
        // room is full of avatars authored at wildly different scales.
        float reference = Vector3.Distance(hips.position, head.position);
        if (reference <= 0.0001f) { return false; }

        bool local = IsLocalAvatar(animator);
        for (int index = 0; index < Body.Length; index++)
        {
            Limb limb = Body[index];
            // Same reason as the head ball below: this one reaches the head bone, and in VR that is where
            // the camera is standing.
            if (local && limb.To == HumanBodyBones.Head) { continue; }
            Transform from = animator.GetBoneTransform(limb.From);
            Transform to = animator.GetBoneTransform(limb.To);
            // Chest and Neck are both optional on a humanoid rig. A missing joint collapses its two
            // capsules into the gap rather than leaving a hole in the torso.
            if (from == null || to == null) { continue; }
            destination.Add(new ResolvedLimb(from, to, reference * limb.RadiusFactor, 0f));
        }

        // Your own head is not in your own trace, because in VR your camera is INSIDE it. Basis already
        // scales the local head bone to zero so the mesh does not render into your eyes
        // (BasisLocalAvatarDriver.ScaleHeadToZero); a capsule built from the bone POSITION ignores that
        // scale and puts a solid ball around the viewpoint, which each eye then renders the inside of - a
        // circle over the whole view. The structure is shared by every camera, so this cannot be decided
        // per camera: the cost is that your own head casts no bounce in a mirror or a photo, which is a far
        // smaller error than a disc across both eyes.
        if (!local)
        {
            Transform neck = animator.GetBoneTransform(HumanBodyBones.Neck) ?? animator.GetBoneTransform(HumanBodyBones.Chest) ?? hips;
            destination.Add(new ResolvedLimb(neck, head, reference * HeadRadiusFactor, reference * HeadRadiusFactor));
        }

        return destination.Count > 0;
    }

    /// <summary>
    /// Where the shared unit capsule has to be put to become this limb. The capsule is authored along +Y
    /// with radius 1 and its ends at y = +/-1, so the scale is (radius, half length, radius) and the
    /// rotation takes +Y onto the bone direction.
    /// </summary>
    public static Matrix4x4 MatrixFor(in ResolvedLimb limb)
    {
        Vector3 start = limb.From.position;
        Vector3 end = limb.To.position;
        Vector3 axis = end - start;
        float length = axis.magnitude;

        if (length <= 0.0001f)
        {
            // A collapsed joint still has a body part sitting on it, so it becomes a ball rather than
            // vanishing - which is also what stops a degenerate rig punching holes in the occlusion.
            return Matrix4x4.TRS(start, Quaternion.identity, new Vector3(limb.Radius, limb.Radius, limb.Radius));
        }

        Vector3 direction = axis / length;
        if (limb.Extend > 0f)
        {
            end += direction * limb.Extend;
            length += limb.Extend;
        }

        Quaternion rotation = Quaternion.FromToRotation(Vector3.up, direction);
        Vector3 centre = (start + end) * 0.5f;
        return Matrix4x4.TRS(centre, rotation, new Vector3(limb.Radius, length * 0.5f, limb.Radius));
    }

    private static readonly Dictionary<Animator, BasisAvatarProxyPose> poses = new Dictionary<Animator, BasisAvatarProxyPose>();
    private static readonly List<ResolvedLimb> resolveScratch = new List<ResolvedLimb>();
    private static readonly List<Animator> posePruneScratch = new List<Animator>();

    /// <summary>
    /// The shared pose for this avatar, resolving its bones on first request. Null when the rig is not a
    /// humanoid this can describe - the caller then keeps whatever it was doing instead of getting a guess.
    /// </summary>
    public static BasisAvatarProxyPose PoseFor(Animator animator)
    {
        if (animator == null) { return null; }
        if (poses.TryGetValue(animator, out BasisAvatarProxyPose existing)) { return existing; }

        if (!TryResolve(animator, resolveScratch)) { return null; }
        BasisAvatarProxyPose pose = new BasisAvatarProxyPose();
        pose.Resolve(resolveScratch);
        poses.Add(animator, pose);
        layoutDirty = true;
        return pose;
    }

    /// <summary>Drops avatars that have gone. Cheap, and safe to call on the rescan cadence.</summary>
    public static void PrunePoses()
    {
        posePruneScratch.Clear();
        foreach (KeyValuePair<Animator, BasisAvatarProxyPose> entry in poses)
        {
            if (entry.Key == null) { posePruneScratch.Add(entry.Key); }
        }
        for (int index = 0; index < posePruneScratch.Count; index++) { poses.Remove(posePruneScratch[index]); }
        if (posePruneScratch.Count > 0) { layoutDirty = true; }
        posePruneScratch.Clear();
    }

    /// <summary>
    /// Samples every avatar once for this frame.
    ///
    /// Driven from one hook rather than from whichever tracer records first, and that is the point. The
    /// capsules and the avatar's own mesh have to be reading the same instant: sampled during a render
    /// graph recording they were read at whatever moment that particular effect happened to be recorded,
    /// which is a different moment from when the renderer was culled and submitted, and a different moment
    /// again for the second effect in the frame. At rest nothing moves between those moments and everything
    /// looks perfect; the instant an avatar moves, each consumer is reading a slightly different pose and
    /// the capsules stop lining up with the body. One sample, one instant, shared by everyone.
    /// </summary>
    private static readonly List<ResolvedLimb> flatLimbs = new List<ResolvedLimb>();
    private static int lastUpdateFrame = -1;
    private static bool layoutDirty;

    /// <summary>
    /// Samples every avatar once for this frame, on worker threads. Returns false when the frame has
    /// already been sampled, so a second tracer reads the same matrices rather than re-reading the bones
    /// at a different instant.
    /// </summary>
    public static bool UpdateAllPoses(int frame)
    {
        if (layoutDirty) { RebuildLayout(); }
        if (frame == lastUpdateFrame) { return false; }
        lastUpdateFrame = frame;
        BasisAvatarProxyJobs.Run();
        return true;
    }

    /// <summary>
    /// Flattens every avatar's limbs into the arrays the jobs walk, handing each pose its offset. Only on
    /// join or leave - a TransformAccessArray rebuild costs more than the job saves if done per frame.
    /// </summary>
    private static void RebuildLayout()
    {
        layoutDirty = false;
        flatLimbs.Clear();
        foreach (KeyValuePair<Animator, BasisAvatarProxyPose> entry in poses)
        {
            if (entry.Key == null) { continue; }
            entry.Value.Offset = flatLimbs.Count;
            flatLimbs.AddRange(entry.Value.Limbs);
        }
        BasisAvatarProxyJobs.Rebuild(flatLimbs);
        lastUpdateFrame = -1;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Install()
    {
        RenderPipelineManager.beginFrameRendering -= OnBeginFrameRendering;
        RenderPipelineManager.beginFrameRendering += OnBeginFrameRendering;
    }

    private static void OnBeginFrameRendering(ScriptableRenderContext context, Camera[] cameras)
    {
        // The last point at which every pose write for the frame has landed and nothing has started
        // drawing yet - after Basis has run its IK on onBeforeRender, before the first camera is culled.
        UpdateAllPoses(Time.renderedFrameCount);
    }

    public static void ClearPoses()
    {
        poses.Clear();
        flatLimbs.Clear();
        BasisAvatarProxyJobs.Release();
        layoutDirty = false;
        lastUpdateFrame = -1;
    }

    public static int PoseCount => poses.Count;

    private static Mesh sharedCapsule;

    /// <summary>
    /// The one mesh every limb of every avatar is an instance of. Built once, never written again, so the
    /// acceleration structure holds a single small BLAS for every body in the room and never rebuilds it.
    ///
    /// Authored as a unit: radius 1, ends at y = +/-1, so <see cref="MatrixFor"/> is a pure TRS. Eight
    /// sides and two hemisphere rings is about 150 triangles - the silhouette is what carries occlusion,
    /// and a rounder capsule would move the result by less than one texel of a half resolution gather.
    /// </summary>
    public static Mesh SharedCapsule()
    {
        if (sharedCapsule != null) { return sharedCapsule; }

        const int sides = 8;
        const int rings = 2;

        List<Vector3> vertices = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<int> triangles = new List<int>();

        // Two hemispheres, each swept from its pole to the equator, with the cylinder body filling the gap
        // between the two equators. Rows are laid out top to bottom so one strip loop stitches all of them.
        int rows = (rings + 1) * 2;
        for (int row = 0; row <= rows; row++)
        {
            bool upper = row <= rings + 1;
            int withinCap = upper ? row : row - (rings + 1);
            float t = withinCap / (float)(rings + 1);
            float polar = t * Mathf.PI * 0.5f;

            float radius = upper ? Mathf.Sin(polar) : Mathf.Cos(polar);
            float capHeight = upper ? Mathf.Cos(polar) : -Mathf.Sin(polar);
            float offset = upper ? 1f : -1f;

            for (int side = 0; side <= sides; side++)
            {
                float angle = side / (float)sides * Mathf.PI * 2f;
                float x = Mathf.Cos(angle);
                float z = Mathf.Sin(angle);

                Vector3 normal = new Vector3(x * radius, capHeight, z * radius).normalized;
                // The cap centre sits at the end of the cylinder, so the sphere is offset rather than scaled.
                vertices.Add(new Vector3(x * radius, capHeight + offset, z * radius));
                normals.Add(normal);
            }
        }

        int stride = sides + 1;
        for (int row = 0; row < rows; row++)
        {
            for (int side = 0; side < sides; side++)
            {
                int a = row * stride + side;
                int b = a + 1;
                int c = a + stride;
                int d = c + 1;
                triangles.Add(a); triangles.Add(c); triangles.Add(b);
                triangles.Add(b); triangles.Add(c); triangles.Add(d);
            }
        }

        Mesh mesh = new Mesh { name = "BasisAvatarProxyCapsule", hideFlags = HideFlags.HideAndDontSave };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        // Read/Write has to stay on: the compute backend copies these normals into its own arena.
        sharedCapsule = mesh;
        return sharedCapsule;
    }

    /// <summary>Drops the shared mesh. For tests and for a full teardown of the tracer.</summary>
    public static void ReleaseSharedCapsule()
    {
        if (sharedCapsule == null) { return; }
        if (Application.isPlaying) { Object.Destroy(sharedCapsule); }
        else { Object.DestroyImmediate(sharedCapsule); }
        sharedCapsule = null;
    }
}

/// <summary>
/// One avatar's limb matrices for one frame, shared by everything that traces that avatar.
///
/// Global illumination and ambient occlusion each keep their own acceleration structure - instance handles
/// belong to the structure that issued them, so those cannot be shared - but the work in FRONT of that is
/// identical: read the same bones, build the same capsule matrices. Doing it twice is pure waste, and it
/// scales with the number of people in the room. Whoever renders first each frame pays for the poses and
/// everyone after reads them.
/// </summary>
public sealed class BasisAvatarProxyPose
{
    public readonly List<BasisAvatarProxy.ResolvedLimb> Limbs = new List<BasisAvatarProxy.ResolvedLimb>();

    /// <summary>Where this avatar's limbs begin in the shared arrays the jobs write.</summary>
    public int Offset { get; internal set; } = -1;

    public int Count => Limbs.Count;

    /// <summary>This limb's matrix for the current frame, read straight out of the job output.</summary>
    public Matrix4x4 MatrixAt(int index)
    {
        if (Offset < 0 || index < 0 || index >= Limbs.Count) { return Matrix4x4.identity; }
        return BasisAvatarProxyJobs.MatrixAt(Offset + index);
    }

    internal void Resolve(List<BasisAvatarProxy.ResolvedLimb> limbs)
    {
        Limbs.Clear();
        Limbs.AddRange(limbs);
        Offset = -1;
    }

    /// <summary>
    /// Brings every avatar up to date for this frame. Returns false when another tracer already did it,
    /// which is the whole saving - the second effect in a frame reads and does not recompute.
    /// </summary>
    public bool Update(int frame)
    {
        return BasisAvatarProxy.UpdateAllPoses(frame);
    }
}
