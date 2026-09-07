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

        // The feet. Ankle to toe, so a foot occludes the ground it is standing on - without these the plan
        // stopped at the ankle and a body cast no contact shadow at all where it actually meets the floor,
        // which is the one place everybody looks for one.
        new Limb(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, 0.048f),
        new Limb(HumanBodyBones.RightFoot, HumanBodyBones.RightToes, 0.048f),
    };

    /// <summary>
    /// A foot on a rig with no toe bone, as a fraction of the reference height.
    ///
    /// Toes are optional on a humanoid avatar and plenty of rigs leave them unmapped, which would leave
    /// those avatars with the hole this fixes. A ball on the ankle is a poor foot - it has no length and
    /// points nowhere - but it is the only shape expressible from a single bone, since a capsule needs two
    /// transforms to take its direction from and there is no second one to use.
    /// </summary>
    public const float ToelessFootRadiusFactor = 0.055f;

    /// <summary>The head is the one part with no child bone to reach towards, so it gets its own ball.</summary>
    public const float HeadRadiusFactor = 0.075f;

    /// <summary>
    /// How much of a capsule's half length is rounded tip rather than straight body.
    ///
    /// The whole capsule has to end ON its bone - see SharedCapsule for what happens when it does not - so
    /// the rounding has to come out of the limb rather than be added past it. Small, because two limbs meet
    /// at every joint and the gap between their two tapers is a hole in the occlusion.
    /// </summary>
    public const float CapFraction = 0.25f;

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
        /// <summary>
        /// What the body plan alone would have given this limb, before any measurement from the mesh.
        ///
        /// Kept so a debug view can show the two side by side. A fitted radius that has run away from the
        /// plan is the shape of the bug this whole area keeps producing - a capsule wider than the surface
        /// the rays leave from - and it is invisible unless you can see both numbers at once.
        /// </summary>
        public readonly float PlanRadius;

        public ResolvedLimb(Transform from, Transform to, float radius, float extend)
            : this(from, to, radius, extend, radius)
        {
        }

        public ResolvedLimb(Transform from, Transform to, float radius, float extend, float planRadius)
        {
            From = from;
            To = to;
            Radius = radius;
            Extend = extend;
            PlanRadius = planRadius;
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

        // A rig with no toe bone gets a ball on the ankle rather than nothing at all. See
        // ToelessFootRadiusFactor for why it cannot be a capsule.
        AddToelessFoot(animator, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, reference, destination);
        AddToelessFoot(animator, HumanBodyBones.RightFoot, HumanBodyBones.RightToes, reference, destination);

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

        // Everything above is a body-shaped guess scaled by one number. Measure it against the avatar that
        // is actually standing there, where the avatar will let us look.
        FitRadiiToMesh(animator, destination);

        return destination.Count > 0;
    }

    /// <summary>Whether limb radii are measured from the avatar's own meshes. Off falls back to the plan alone.</summary>
    public static bool FitToMesh = true;

    /// <summary>How many vertices one avatar may be measured from. Sampled by stride, not by truncation.</summary>
    public const int FitVertexBudget = 6000;

    /// <summary>
    /// Where a limb's radius sits in the spread of distances measured around it.
    ///
    /// NOT the maximum. A maximum is a bounding radius, and a bounding capsule swallows the surface the rays
    /// start from - which is the whole reason bodies wore black patches. A hair strand, a skirt hem or a
    /// sword weighted to the hips would each set it on their own. Around two thirds puts the capsule inside
    /// the silhouette, which under-occludes slightly and never encloses the surface.
    /// </summary>
    public const float FitRadiusPercentile = 0.65f;

    /// <summary>
    /// How far a measured radius may depart from the body plan's own guess, as a multiplier.
    ///
    /// The measurement is the better number when it works and nonsense when the mesh is not what it looks
    /// like - one enormous weight-painted prop, a mesh authored at a hundred times scale, a rig whose bones
    /// do not sit inside their own geometry. The plan is a poor estimate that is never absurd, so it is what
    /// bounds the good one.
    /// </summary>
    public const float FitMinScale = 0.45f, FitMaxScale = 1.6f;

    private static readonly List<SkinnedMeshRenderer> fitRenderers = new List<SkinnedMeshRenderer>();
    private static readonly List<List<float>> fitDistances = new List<List<float>>();

    /// <summary>
    /// Replaces each limb's radius with one measured from the avatar's own skinned meshes.
    ///
    /// The body plan is proportions: a radius is a fraction of hips-to-head, so every avatar of the same
    /// height gets the same limbs however differently they are actually built. This measures instead - every
    /// sampled vertex is given to the limb segment it sits nearest, and the limb takes a percentile of those
    /// distances. A heavy boot gets a thicker foot and a thin wrist gets a thinner forearm, on the avatar
    /// that is standing there rather than on an average of all of them.
    ///
    /// ⚠️ Silently does nothing when the meshes cannot be read, which is the common case for uploaded
    /// avatars - Read/Write is off by default and the ray scene already treats unreadable geometry as
    /// ordinary (see BasisGlobalIlluminationRayScene). Falling back to the plan is the point: this improves
    /// the fit where it can and is never required for correctness.
    ///
    /// Runs once, at resolve, and is bounded by <see cref="FitVertexBudget"/> - a hundred thousand vertex
    /// avatar is sampled by stride rather than read whole.
    /// </summary>
    private static void FitRadiiToMesh(Animator animator, List<ResolvedLimb> limbs)
    {
        if (!FitToMesh || limbs.Count == 0) { return; }

        fitRenderers.Clear();
        animator.GetComponentsInChildren(true, fitRenderers);
        if (fitRenderers.Count == 0) { return; }

        while (fitDistances.Count < limbs.Count) { fitDistances.Add(new List<float>()); }
        for (int index = 0; index < limbs.Count; index++) { fitDistances[index].Clear(); }

        // Read once, not once per sampled vertex per limb: the positions cannot move during this
        // synchronous pass, and per-vertex transform reads were nearly all of this function's cost.
        Vector3[] limbStarts = new Vector3[limbs.Count];
        Vector3[] limbEnds = new Vector3[limbs.Count];
        for (int index = 0; index < limbs.Count; index++)
        {
            ResolvedLimb limb = limbs[index];
            if (!limb.IsValid) { continue; }
            limbStarts[index] = limb.From.position;
            limbEnds[index] = limb.To.position;
        }

        bool measured = false;
        for (int index = 0; index < fitRenderers.Count; index++)
        {
            measured |= Measure(fitRenderers[index], limbs, limbStarts, limbEnds);
        }
        if (!measured) { return; }

        for (int index = 0; index < limbs.Count; index++)
        {
            List<float> distances = fitDistances[index];
            // Two samples cannot describe a limb, and a limb nothing was weighted to is one the mesh has
            // nothing to say about - the plan's guess stands in both cases.
            if (distances.Count < 8) { continue; }

            distances.Sort();
            int slot = Mathf.Clamp(Mathf.RoundToInt((distances.Count - 1) * FitRadiusPercentile), 0, distances.Count - 1);

            ResolvedLimb limb = limbs[index];
            float fitted = Mathf.Clamp(distances[slot], limb.Radius * FitMinScale, limb.Radius * FitMaxScale);
            limbs[index] = new ResolvedLimb(limb.From, limb.To, fitted, limb.Extend, limb.Radius);
        }
    }

    /// <summary>
    /// Gives every sampled vertex of one renderer to the limb it sits nearest, recording how far off that
    /// limb's axis it was. Returns whether the renderer could be read at all.
    /// </summary>
    private static bool Measure(SkinnedMeshRenderer renderer, List<ResolvedLimb> limbs, Vector3[] limbStarts, Vector3[] limbEnds)
    {
        if (renderer == null) { return false; }
        Mesh mesh = renderer.sharedMesh;
        // Read/Write disabled is not an error and not rare - it is the default for an uploaded avatar.
        if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0) { return false; }

        Transform[] bones = renderer.bones;
        Matrix4x4[] bindposes = mesh.bindposes;
        BoneWeight[] weights = mesh.boneWeights;
        Vector3[] vertices = mesh.vertices;
        // A skinned mesh with no usable bone table cannot be posed, and a vertex in the wrong place would
        // measure the distance between two poses rather than the thickness of a limb.
        if (bones == null || bones.Length == 0 || bindposes.Length != bones.Length || weights.Length != vertices.Length)
        {
            return false;
        }

        // One bone rather than the full four-way blend. This is measuring how thick a limb is, and the
        // vertices that decide that sit in the middle of a limb where one bone already dominates; the
        // ones a blend would move are at the joints, which is exactly where a capsule end is vague anyway.
        // The bind-then-world composition is folded to one matrix per bone here, so the vertex loop below
        // is pure math with no transform interop in it.
        Matrix4x4[] boneMatrices = new Matrix4x4[bones.Length];
        bool[] boneUsable = new bool[bones.Length];
        for (int bone = 0; bone < bones.Length; bone++)
        {
            Transform boneTransform = bones[bone];
            if (boneTransform == null) { continue; }
            boneMatrices[bone] = boneTransform.localToWorldMatrix * bindposes[bone];
            boneUsable[bone] = true;
        }

        int stride = Mathf.Max(1, vertices.Length / FitVertexBudget);
        for (int index = 0; index < vertices.Length; index += stride)
        {
            int bone = weights[index].boneIndex0;
            if (bone < 0 || bone >= bones.Length || !boneUsable[bone]) { continue; }

            Vector3 world = boneMatrices[bone].MultiplyPoint3x4(vertices[index]);

            int nearest = -1;
            float nearestDistance = float.MaxValue;
            for (int limbIndex = 0; limbIndex < limbs.Count; limbIndex++)
            {
                if (!limbs[limbIndex].IsValid) { continue; }
                float distance = DistanceToSegment(world, limbStarts[limbIndex], limbEnds[limbIndex]);
                if (distance < nearestDistance) { nearestDistance = distance; nearest = limbIndex; }
            }

            if (nearest >= 0) { fitDistances[nearest].Add(nearestDistance); }
        }

        return true;
    }

    private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector3 axis = end - start;
        float lengthSquared = axis.sqrMagnitude;
        if (lengthSquared <= 1e-10f) { return Vector3.Distance(point, start); }
        float t = Mathf.Clamp01(Vector3.Dot(point - start, axis) / lengthSquared);
        return Vector3.Distance(point, start + axis * t);
    }

    /// <summary>
    /// Covers a foot whose rig maps no toe bone, and does nothing when one is mapped - the body plan's own
    /// ankle-to-toe limb has already covered that case and a second shape on top would only double the
    /// occlusion there.
    /// </summary>
    private static void AddToelessFoot(Animator animator, HumanBodyBones footBone, HumanBodyBones toeBone,
        float reference, List<ResolvedLimb> destination)
    {
        if (animator.GetBoneTransform(toeBone) != null) { return; }
        Transform foot = animator.GetBoneTransform(footBone);
        if (foot == null) { return; }
        destination.Add(new ResolvedLimb(foot, foot, reference * ToelessFootRadiusFactor, 0f));
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

    /// <summary>
    /// The avatars that currently have a proxy, for debug drawing. Read only, and read only useful while
    /// something is asking for poses - a tracer, or the debug view itself calling <see cref="PoseFor"/>.
    /// </summary>
    public static IReadOnlyDictionary<Animator, BasisAvatarProxyPose> Poses => poses;

    private static Mesh sharedCapsule;

    /// <summary>
    /// The one mesh every limb of every avatar is an instance of. Built once, never written again, so the
    /// acceleration structure holds a single small BLAS for every body in the room and never rebuilds it.
    ///
    /// Authored as a unit: radius 1, ends at y = +/-1, so <see cref="MatrixFor"/> is a pure TRS. Eight
    /// sides and two hemisphere rings is about 150 triangles - the silhouette is what carries occlusion,
    /// and a rounder capsule would move the result by less than one texel of a half resolution gather.
    ///
    /// ⚠️ The +/-1 above is load bearing and this mesh used to break it. Both matrix builders scale Y by
    /// the limb's HALF length, which places the ends on the two bones only if the mesh ends at +/-1. It was
    /// authored as a full sphere sitting on each end of a unit cylinder, so it ended at +/-2 - and every
    /// capsule in the room came out exactly TWICE the length of the bone it stood for, reaching a whole half
    /// limb past each joint. A shin capsule ran from above the knee to twenty centimetres under the floor, a
    /// thigh capsule up into the pelvis, and the legs of every avatar sat inside a solid overlapping column
    /// of them. That is what the black patches around people's legs were: surfaces enclosed by their own
    /// proxy, in both the ray traced gather and ambient occlusion.
    ///
    /// So the cap is a fraction of the half length rather than equal to it. The body stays a cylinder to
    /// within <see cref="CapFraction"/> of each end and the tip rounds off exactly ON the bone. Adjacent
    /// limbs share a joint, so keeping the cap short is what stops a lens shaped hole opening at every knee
    /// and elbow where two tapers meet.
    /// </summary>
    /// <summary>Sides around the capsule, and hemisphere rings per cap. Public so anything drawing this
    /// mesh reads the same numbers that built it rather than re-deriving them and quietly drifting.</summary>
    public const int CapsuleSides = 8, CapsuleRings = 2;

    /// <summary>Vertices per ring row. The row loop is inclusive, hence the +1 on both counts.</summary>
    public static int CapsuleStride => CapsuleSides + 1;
    public static Mesh SharedCapsule()
    {
        if (sharedCapsule != null) { return sharedCapsule; }

        const int sides = CapsuleSides;
        const int rings = CapsuleRings;

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

                // The cap is flattened onto the last CapFraction of the half length, so the pole lands on
                // the bone at +/-1 instead of a full radius past it at +/-2.
                vertices.Add(new Vector3(x * radius, offset * (1f - CapFraction) + capHeight * CapFraction, z * radius));
                // Flattening Y takes normals through the inverse transpose, which divides the y component
                // by the same fraction. The cylinder rows carry capHeight 0 and are untouched by it, which
                // is right - a cylinder's side normal is horizontal however the ends are shaped.
                normals.Add(new Vector3(x * radius, capHeight / CapFraction, z * radius).normalized);
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
