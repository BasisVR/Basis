using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using UnityEngine;

public struct BasisCameraSubjectHit
{
    public IBasisPlayer Player;
    public float Entry, FocusDepth;
    public Vector3 Point;
    public bool FromSkeleton;
}

[AutoStaticsCleanup]
public static partial class BasisCameraSubjectPicker
{
    /// <summary>
    /// Nearest a body may be and still be what the click meant. At five centimetres the operator's
    /// own hand and forearm — which are wrapped around this camera, a few centimetres from the
    /// lens — were the closest capsules to almost every ray, so every click focused on them and the
    /// focus plane collapsed to the minimum. Nothing this close is a subject; the lens will not
    /// even focus there on a long one.
    /// </summary>
    public const float MinimumEntryDistance = 0.25f;
    public const float OccluderBias = 0.02f;
    public const float LimbRadiusRatio = 0.2f;
    public const float TorsoRadiusRatio = 0.35f;
    public const float HeadSpanRatio = 1.3f;
    public const float HeadRadiusRatio = 0.8f;

    /// <summary>
    /// Broad-phase sphere around the hips, as a multiple of the hips-to-neck length. Everything a
    /// body can reach lives inside it — feet ~1.9x, an outstretched arm ~1.8x, the crown ~1.5x —
    /// so a player rejected here cannot possibly be under the cursor.
    /// </summary>
    public const float SubjectReachRatio = 3f;

    private const int MaxOccluderSteps = 8;
    private const float OccluderStepBias = 0.01f;

    private static readonly List<IBasisPlayer> candidates = new List<IBasisPlayer>(64);
    private static readonly HashSet<Transform> playerRoots = new HashSet<Transform>();
    private static int candidateFrame = -1;
    private static bool playerRootsBuilt;

    public static bool IntersectRayBounds(Ray ray, Bounds bounds, out float entry, out float exit)
    {
        entry = 0f;
        exit = 0f;
        Vector3 direction = ray.direction;
        float length = direction.magnitude;
        if (length < 1e-6f) return false;
        return IntersectRayBounds(ray.origin, direction / length, bounds, out entry, out exit);
    }

    private static bool IntersectRayBounds(Vector3 origin, Vector3 direction, Bounds bounds, out float entry, out float exit)
    {
        entry = 0f;
        exit = 0f;
        Vector3 min = bounds.min, max = bounds.max;
        float near = float.NegativeInfinity, far = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float slabOrigin = origin[axis], slabDirection = direction[axis], low = min[axis], high = max[axis];
            if (Mathf.Abs(slabDirection) < 1e-8f)
            {
                if (slabOrigin < low || slabOrigin > high) return false;
                continue;
            }
            float inverse = 1f / slabDirection;
            float first = (low - slabOrigin) * inverse, second = (high - slabOrigin) * inverse;
            if (first > second)
            {
                float swap = first;
                first = second;
                second = swap;
            }
            if (first > near) near = first;
            if (second < far) far = second;
            if (near > far) return false;
        }

        if (far < 0f || float.IsInfinity(near) || float.IsInfinity(far)) return false;
        entry = near;
        exit = far;
        return true;
    }

    public static float ResolveFocusDepth(Ray ray, Bounds bounds, float entry, float exit)
    {
        float toCentre = Vector3.Dot(bounds.center - ray.origin, ray.direction.normalized);
        return Mathf.Clamp(toCentre, Mathf.Max(entry, 0f), Mathf.Max(exit, 0f));
    }

    /// <summary>
    /// Whether a sphere is close enough to the ray to be worth testing properly. This is the whole
    /// broad phase, and at a thousand players it is what everything else costs nothing next to:
    /// two transform reads and a dozen floats per player, versus walking every renderer they own.
    /// </summary>
    public static bool RaySphereOverlaps(Vector3 origin, Vector3 direction, Vector3 centre, float radius, float maxDistance)
    {
        Vector3 toCentre = centre - origin;
        float depth = Vector3.Dot(toCentre, direction);
        if (depth < -radius || depth > maxDistance + radius) return false;

        float clamped = Mathf.Clamp(depth, 0f, maxDistance);
        Vector3 separation = toCentre - direction * clamped;
        return Vector3.Dot(separation, separation) <= radius * radius;
    }

    /// <summary>
    /// Closest approach between a ray and a bone segment. A capsule is exactly the set of points
    /// within its radius of that segment, so the returned distance decides the hit outright;
    /// <paramref name="axisDepth"/> is where the ray passes through the middle of the limb, which
    /// is the depth worth focusing on rather than the surface facing the lens.
    /// </summary>
    public static void ClosestRayToSegment(Ray ray, Vector3 a, Vector3 b, out float axisDepth, out float distanceSquared)
    {
        ClosestRayToSegment(ray.origin, ray.direction.normalized, a, b, out axisDepth, out distanceSquared);
    }

    private static void ClosestRayToSegment(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, out float axisDepth, out float distanceSquared)
    {
        Vector3 segment = b - a, toOrigin = origin - a;
        float segmentLengthSquared = Vector3.Dot(segment, segment);
        float alongBoth = Vector3.Dot(segment, direction);
        float alongSegment = Vector3.Dot(segment, toOrigin);
        float alongRay = Vector3.Dot(direction, toOrigin);

        float denominator = segmentLengthSquared - alongBoth * alongBoth;
        float s = denominator > 1e-8f ? Mathf.Clamp01((alongSegment - alongRay * alongBoth) / denominator) : 0f;

        axisDepth = s * alongBoth - alongRay;
        Vector3 separation = toOrigin + direction * axisDepth - segment * s;
        distanceSquared = Vector3.Dot(separation, separation);
    }

    public static bool IntersectRayCapsule(Ray ray, Vector3 a, Vector3 b, float radius, out float entry, out float axisDepth)
    {
        return IntersectRayCapsule(ray.origin, ray.direction.normalized, a, b, radius, out entry, out axisDepth);
    }

    private static bool IntersectRayCapsule(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, float radius, out float entry, out float axisDepth)
    {
        ClosestRayToSegment(origin, direction, a, b, out axisDepth, out float distanceSquared);
        float radiusSquared = radius * radius;
        if (distanceSquared > radiusSquared)
        {
            entry = 0f;
            return false;
        }

        entry = axisDepth - Mathf.Sqrt(Mathf.Max(0f, radiusSquared - distanceSquared));
        return true;
    }

    public static bool TryGetPlayerBounds(IBasisPlayer player, out Bounds bounds)
    {
        bounds = default;
        if (player == null || player.IsDestroyed) return false;
        BasisAvatar avatar = player.BasisAvatar;
        if (avatar == null) return false;

        bool found = false;
        Encapsulate(avatar.Renders, ref bounds, ref found);
        if (!found) Encapsulate(avatar.SkinnedMeshRenderers, ref bounds, ref found);
        return found;
    }

    public static void CollectPlayers(List<IBasisPlayer> into)
    {
        into.Clear();
        BasisLocalPlayer local = BasisLocalPlayer.Instance;
        if (local != null && !local.IsDestroyed) into.Add(local);
        foreach (KeyValuePair<ushort, BasisRemotePlayer> pair in BasisNetworkPlayers.RemotePlayers)
        {
            BasisRemotePlayer remote = pair.Value;
            if (remote == null || remote.IsDestroyed) continue;
            into.Add(remote);
        }
    }

    private static void EnsureCandidates()
    {
        int frame = Time.frameCount;
        if (candidateFrame == frame) return;
        candidateFrame = frame;
        playerRootsBuilt = false;
        CollectPlayers(candidates);
    }

    /// <param name="owner">The camera doing the picking. Everything it owns — its own hierarchy and
    /// anything it spawned into the world — is stepped over rather than focused on.</param>
    public static bool TryRaycastWorld(Ray ray, float maxDistance, LayerMask layers, BasisHandHeldCamera owner, out RaycastHit nearest, out float distance)
    {
        nearest = default;
        distance = float.PositiveInfinity;
        Vector3 direction = ray.direction;
        float length = direction.magnitude;
        if (length < 1e-6f) return false;
        direction /= length;

        EnsureCandidates();
        float travelled = 0f;
        for (int step = 0; step < MaxOccluderSteps; step++)
        {
            float remaining = maxDistance - travelled;
            if (remaining <= 0f) return false;

            Ray probe = new Ray(ray.origin + direction * travelled, direction);
            if (!Physics.Raycast(probe, out RaycastHit hit, remaining, layers, QueryTriggerInteraction.Ignore)) return false;

            Transform hitTransform = hit.collider != null ? hit.collider.transform : null;
            bool skip = hitTransform == null
                || (owner != null && owner.OwnsTransform(hitTransform))
                || IsPlayerOwned(hitTransform);
            if (!skip)
            {
                nearest = hit;
                distance = travelled + hit.distance;
                return true;
            }

            travelled += hit.distance + OccluderStepBias;
        }
        return false;
    }

    /// <param name="armsHoldTheCamera">True while the local player is holding this camera, which
    /// makes their arms part of the rig rather than a subject: a hand wrapped around the body sits
    /// closer to the lens than anything the shot is about, and would win every click.</param>
    public static bool TryPickSubject(Ray ray, float maxDistance, float occluderDistance, float padding, bool armsHoldTheCamera, out BasisCameraSubjectHit hit)
    {
        hit = default;
        Vector3 direction = ray.direction;
        float length = direction.magnitude;
        if (length < 1e-6f) return false;
        direction /= length;
        Vector3 origin = ray.origin;

        EnsureCandidates();
        float best = float.PositiveInfinity;
        float ceiling = Mathf.Min(maxDistance, occluderDistance);
        bool found = false;
        int count = candidates.Count;
        for (int index = 0; index < count; index++)
        {
            IBasisPlayer player = candidates[index];
            if (player == null || player.IsDestroyed) continue;
            BasisAvatar avatar = player.BasisAvatar;
            if (avatar == null) continue;

            float entry, depth;
            bool fromSkeleton = TryReadTorso(avatar, out Vector3 hips, out Vector3 neck, out float torsoLength);
            if (fromSkeleton)
            {
                if (!RaySphereOverlaps(origin, direction, hips, torsoLength * SubjectReachRatio + padding, ceiling)) continue;
                bool skipArms = armsHoldTheCamera && player.IsLocal;
                if (!SolveSkeleton(avatar, origin, direction, padding, hips, neck, torsoLength, skipArms, out entry, out depth)) continue;
            }
            else if (!TryResolveHitBounds(avatar, origin, direction, out entry, out depth)) continue;

            if (entry < MinimumEntryDistance || entry > maxDistance) continue;
            if (entry >= occluderDistance - OccluderBias) continue;
            if (entry >= best) continue;

            depth = Mathf.Max(depth, entry);
            best = entry;
            found = true;
            hit = new BasisCameraSubjectHit
            {
                Player = player,
                Entry = entry,
                FocusDepth = depth,
                Point = origin + direction * depth,
                FromSkeleton = fromSkeleton,
            };
        }
        return found;
    }

    public static bool HasSkeleton(BasisAvatar avatar)
    {
        return TryReadTorso(avatar, out _, out _, out _);
    }

    /// <summary>
    /// Hit-tests the avatar's live skeleton as a set of bone capsules. Renderer bounds are the
    /// bind pose baked at import — arms out, a box near two metres wide on a standing avatar — so
    /// they answer "somewhere near this person", not "this person". The capsules follow the pose,
    /// and their radii come off the bone lengths themselves so a chibi and a dragon both size
    /// correctly without a per-avatar tuning value.
    /// </summary>
    public static bool TryPickSkeleton(BasisAvatar avatar, Ray ray, float padding, bool skipArms, out float entry, out float axisDepth)
    {
        entry = 0f;
        axisDepth = 0f;
        if (!TryReadTorso(avatar, out Vector3 hips, out Vector3 neck, out float torsoLength)) return false;

        Vector3 direction = ray.direction;
        float length = direction.magnitude;
        if (length < 1e-6f) return false;

        return SolveSkeleton(avatar, ray.origin, direction / length, padding, hips, neck, torsoLength, skipArms, out entry, out axisDepth);
    }

    private static bool TryReadTorso(BasisAvatar avatar, out Vector3 hips, out Vector3 neck, out float torsoLength)
    {
        hips = default;
        neck = default;
        torsoLength = 0f;
        if (avatar == null || avatar.TransformStorage == null || !avatar.TransformStorage.HasData) return false;

        Transform hipsBone = Bone(avatar, HumanBodyBones.Hips);
        Transform spineTopBone = Bone(avatar, HumanBodyBones.Neck)
            ?? Bone(avatar, HumanBodyBones.Head)
            ?? Bone(avatar, HumanBodyBones.UpperChest)
            ?? Bone(avatar, HumanBodyBones.Chest)
            ?? Bone(avatar, HumanBodyBones.Spine);
        if (hipsBone == null || spineTopBone == null) return false;

        hips = hipsBone.position;
        neck = spineTopBone.position;
        torsoLength = Vector3.Distance(hips, neck);
        return torsoLength > 1e-4f;
    }

    private static bool SolveSkeleton(BasisAvatar avatar, Vector3 origin, Vector3 direction, float padding, Vector3 hips, Vector3 neck, float torsoLength, bool skipArms, out float entry, out float axisDepth)
    {
        entry = 0f;
        axisDepth = 0f;

        float torsoRadius = torsoLength * TorsoRadiusRatio;
        Transform leftUpperArm = Bone(avatar, HumanBodyBones.LeftUpperArm), rightUpperArm = Bone(avatar, HumanBodyBones.RightUpperArm);
        if (leftUpperArm != null && rightUpperArm != null)
        {
            float shoulderSpan = Vector3.Distance(leftUpperArm.position, rightUpperArm.position);
            if (shoulderSpan > 1e-4f) torsoRadius = shoulderSpan * 0.5f;
        }

        bool found = false;
        Consider(origin, direction, hips, neck, torsoRadius + padding, ref found, ref entry, ref axisDepth);

        Transform head = Bone(avatar, HumanBodyBones.Head);
        if (head != null)
        {
            Vector3 headPosition = head.position;
            float headSpan = Mathf.Max(Vector3.Distance(headPosition, neck), torsoLength * 0.18f) * HeadSpanRatio;
            // Up the neck, not the head bone's own up axis. Unity's humanoid rig puts no constraint
            // on how a bone is oriented — the muscle system handles that — so on a rig authored with
            // the head bone rolled, head.up points out of an ear and the capsule was laid across the
            // face instead of up through it.
            Consider(origin, direction, headPosition, headPosition + HeadAxis(headPosition, neck) * headSpan, headSpan * HeadRadiusRatio + padding, ref found, ref entry, ref axisDepth);
        }

        if (!skipArms)
        {
            ConsiderChain(avatar, origin, direction, padding, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, ref found, ref entry, ref axisDepth);
            ConsiderChain(avatar, origin, direction, padding, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, ref found, ref entry, ref axisDepth);
        }
        ConsiderChain(avatar, origin, direction, padding, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, ref found, ref entry, ref axisDepth);
        ConsiderChain(avatar, origin, direction, padding, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, ref found, ref entry, ref axisDepth);

        return found;
    }

    private static Vector3 HeadAxis(Vector3 head, Vector3 neck)
    {
        Vector3 axis = head - neck;
        return axis.sqrMagnitude > 1e-8f ? axis.normalized : Vector3.up;
    }

    private static void ConsiderChain(BasisAvatar avatar, Vector3 origin, Vector3 direction, float padding, HumanBodyBones root, HumanBodyBones middle, HumanBodyBones tip, ref bool found, ref float entry, ref float axisDepth)
    {
        Transform a = Bone(avatar, root), b = Bone(avatar, middle);
        if (a == null || b == null) return;

        Vector3 upper = a.position, joint = b.position;
        float upperRadius = Vector3.Distance(upper, joint) * LimbRadiusRatio;
        Consider(origin, direction, upper, joint, upperRadius + padding, ref found, ref entry, ref axisDepth);

        Transform c = Bone(avatar, tip);
        if (c == null) return;

        Vector3 end = c.position;
        float lowerRadius = Mathf.Max(Vector3.Distance(joint, end) * LimbRadiusRatio, upperRadius * 0.6f);
        Consider(origin, direction, joint, end, lowerRadius + padding, ref found, ref entry, ref axisDepth);
        Consider(origin, direction, end, end, lowerRadius + padding, ref found, ref entry, ref axisDepth);
    }

    private static void Consider(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, float radius, ref bool found, ref float entry, ref float axisDepth)
    {
        if (radius <= 0f) return;
        if (!IntersectRayCapsule(origin, direction, a, b, radius, out float candidateEntry, out float candidateDepth)) return;
        if (candidateEntry < MinimumEntryDistance) return;
        if (found && candidateEntry >= entry) return;

        entry = candidateEntry;
        axisDepth = candidateDepth;
        found = true;
    }

    private static Transform Bone(BasisAvatar avatar, HumanBodyBones bone)
    {
        Transform found = avatar.TransformStorage.Get(bone);
        return found != null ? found : null;
    }

    private static bool TryResolveHitBounds(BasisAvatar avatar, Vector3 origin, Vector3 direction, out float entry, out float depth)
    {
        entry = 0f;
        depth = 0f;
        Bounds bounds = default;
        float exit = 0f;
        bool found = false;
        if (!TryNearestRendererBounds(avatar.Renders, origin, direction, ref bounds, ref entry, ref exit, ref found))
        {
            TryNearestRendererBounds(avatar.SkinnedMeshRenderers, origin, direction, ref bounds, ref entry, ref exit, ref found);
        }
        if (!found) return false;

        float toCentre = Vector3.Dot(bounds.center - origin, direction);
        depth = Mathf.Clamp(toCentre, Mathf.Max(entry, 0f), Mathf.Max(exit, 0f));
        return true;
    }

    private static bool TryNearestRendererBounds<T>(T[] renderers, Vector3 origin, Vector3 direction, ref Bounds bounds, ref float entry, ref float exit, ref bool found) where T : Renderer
    {
        int count = renderers != null ? renderers.Length : 0;
        for (int index = 0; index < count; index++)
        {
            T renderer = renderers[index];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
            Bounds candidate = renderer.bounds;
            if (!IntersectRayBounds(origin, direction, candidate, out float candidateEntry, out float candidateExit)) continue;
            if (candidateEntry < MinimumEntryDistance) continue;
            if (found && candidateEntry >= entry) continue;
            bounds = candidate;
            entry = candidateEntry;
            exit = candidateExit;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// Whether a collider belongs to any player, so it cannot occlude one. Compares hierarchy roots
    /// against a set built at most once per pick — walking every player per hit was the other place
    /// a full room turned a click into a linear scan.
    /// </summary>
    private static bool IsPlayerOwned(Transform hitTransform)
    {
        if (!playerRootsBuilt)
        {
            playerRootsBuilt = true;
            playerRoots.Clear();
            int count = candidates.Count;
            for (int index = 0; index < count; index++)
            {
                IBasisPlayer player = candidates[index];
                if (player == null || player.IsDestroyed) continue;
                Transform root = player.Transform;
                if (root != null) playerRoots.Add(root.root);
                Transform avatar = player.AvatarTransform;
                if (avatar != null) playerRoots.Add(avatar.root);
            }
        }
        return playerRoots.Contains(hitTransform.root);
    }

    private static void Encapsulate<T>(T[] renderers, ref Bounds bounds, ref bool found) where T : Renderer
    {
        int count = renderers != null ? renderers.Length : 0;
        for (int index = 0; index < count; index++)
        {
            T renderer = renderers[index];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
            Bounds candidate = renderer.bounds;
            if (!found)
            {
                bounds = candidate;
                found = true;
                continue;
            }
            bounds.Encapsulate(candidate);
        }
    }
}
