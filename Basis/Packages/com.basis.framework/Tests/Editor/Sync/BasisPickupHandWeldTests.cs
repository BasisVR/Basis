using Unity.Scripting.LifecycleManagement;
using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Common;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// The local hand-weld grab: seat-point selection (ComputeClosestBoundsOffset) composed with the real
    /// BasisParentConstraint, driven the way OnInteractStart (:601-628) and UpdateHeldPoseFromInput
    /// (:1032-1034) drive it. WeldToHand makes the constraint source the post-IK wrist bone
    /// (IKWorldData) instead of the pre-IK hand target, so everything here is measured against that pose.
    /// </summary>
    [NoAutoStaticsCleanup]
    public class BasisPickupHandWeldTests
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly MethodInfo ClosestOffsetM =
            typeof(BasisPickupInteractable).GetMethod("ComputeClosestBoundsOffset", NP);
        static readonly MethodInfo GripOffsetsM =
            typeof(BasisPickupInteractable).GetMethod("TryGetGripOffsets", NP);

        readonly System.Collections.Generic.List<GameObject> _cleanup = new System.Collections.Generic.List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(ClosestOffsetM, "ComputeClosestBoundsOffset moved");
            Assert.IsNotNull(GripOffsetsM, "TryGetGripOffsets moved");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _cleanup)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _cleanup.Clear();
        }

        // ── positive controls: what a healthy weld does ──

        [Test]
        public void Grab_SeatsNearestSurfacePointAtTheWeldPose()
        {
            BasisPickupInteractable prop = MakeProp("box", out Transform t);
            AddBox(prop, Vector3.one * 0.2f);
            t.SetPositionAndRotation(new Vector3(0f, 1f, 0f), Quaternion.identity);

            // Hand a clear distance outside the collider - the case LerpToHandOnPickup is designed for.
            Vector3 handPos = new Vector3(0f, 1f, -0.5f);
            Quaternion handRot = Quaternion.identity;

            Vector3 seated = Seat(prop, t, handPos, handRot);
            t.position = seated;

            Assert.LessOrEqual(Vector3.Distance(NearestSurfacePoint(prop, handPos), handPos), 0.01f,
                "after the grab lerp the nearest surface point must sit on the weld pose");
        }

        [Test]
        public void HeldProp_IsRigidUnderWeldMotion()
        {
            BasisPickupInteractable prop = MakeProp("rigid", out Transform t);
            AddBox(prop, Vector3.one * 0.2f);
            t.SetPositionAndRotation(new Vector3(0f, 1f, 0f), Quaternion.identity);

            Vector3 handPos = new Vector3(0f, 1f, -0.4f);
            var (constraint, _) = Grab(prop, t, handPos, Quaternion.identity);
            Assert.IsTrue(constraint.Evaluate(out Vector3 seatedPos, out Quaternion seatedRot));
            t.SetPositionAndRotation(seatedPos, seatedRot);
            Vector3 gripAtGrab = InWeldFrame(t, handPos, Quaternion.identity);

            // The solved wrist swings and rolls through a normal reach.
            Vector3 movedPos = new Vector3(0.7f, 1.6f, 0.3f);
            Quaternion movedRot = Quaternion.Euler(35f, -110f, 80f);
            constraint.UpdateSourcePositionAndRotation(0, movedPos, movedRot);
            Assert.IsTrue(constraint.Evaluate(out Vector3 pos, out Quaternion rot));
            t.SetPositionAndRotation(pos, rot);

            Vector3 gripNow = InWeldFrame(t, movedPos, movedRot);
            Assert.LessOrEqual(Vector3.Distance(gripNow, gripAtGrab), 0.001f,
                "a welded prop must hold a constant pose in the wrist frame as the wrist moves");
        }

        // ── seat-point selection failures ──

        [Test]
        public void WeldPoseInsideCollider_KeepsTheGrabTimePose()
        {
            BasisPickupInteractable prop = MakeProp("swallowed", out Transform t);
            AddBox(prop, Vector3.one * 0.4f);
            Vector3 start = new Vector3(0f, 1f, 0f);
            t.SetPositionAndRotation(start, Quaternion.identity);

            // Wrist already inside the prop: it is in the grip, so there is nothing to pull in and the
            // seat must not shove a collider face onto the wrist.
            Vector3 handPos = start + new Vector3(0.05f, 0f, 0.05f);
            Vector3 seated = Seat(prop, t, handPos, Quaternion.identity);

            Assert.LessOrEqual(Vector3.Distance(seated, start), 0.001f,
                "a wrist inside the collider must hold the grab-time pose rather than snapping the prop");
        }

        [Test]
        public void TriggerVolume_DoesNotHijackTheSeatPoint()
        {
            // Colliders live on children, so ScanColliders takes its GetComponentsInChildren<Collider>(true)
            // path - which sweeps in triggers and inactive colliders alongside the real surface.
            BasisPickupInteractable prop = MakeProp("with-trigger", out Transform t);
            Vector3 start = new Vector3(0f, 1f, 0f);
            t.SetPositionAndRotation(start, Quaternion.identity);

            var body = new GameObject("body");
            body.transform.SetParent(t, false);
            body.AddComponent<BoxCollider>().size = Vector3.one * 0.2f;

            var volume = new GameObject("hover-volume");
            volume.transform.SetParent(t, false);
            BoxCollider trigger = volume.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = Vector3.one * 3f;

            Vector3 handPos = start + new Vector3(0f, 0f, -0.5f);
            t.position = Seat(prop, t, handPos, Quaternion.identity);

            Assert.LessOrEqual(Vector3.Distance(NearestVisualSurfacePoint(prop, handPos, trigger), handPos), 0.05f,
                "the seat point must come from the prop's real collider, not from a trigger volume that " +
                "happens to enclose the hand");
        }

        [Test]
        public void NonConvexMeshCollider_StillSeatsViaBounds()
        {
            LogAssert.ignoreFailingMessages = true;
            try
            {
                BasisPickupInteractable prop = MakeProp("mesh-prop", out Transform t);
                MeshCollider mc = prop.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = Quad();
                mc.convex = false;

                Vector3 start = new Vector3(0f, 1f, 0f);
                t.SetPositionAndRotation(start, Quaternion.identity);
                Vector3 hand = start + new Vector3(0f, 0f, -0.5f);
                Vector3 seated = Seat(prop, t, hand, Quaternion.identity);

                // Collider.ClosestPoint is unsupported on a non-convex MeshCollider, so the seat has to
                // come off the bounds - otherwise a distance grab leaves the prop where it was.
                Assert.Greater(Vector3.Distance(seated, start), 0.02f,
                    "a mesh-collider prop grabbed at range must still be pulled to the hand");
                Assert.Less(Vector3.Distance(seated, hand), Vector3.Distance(start, hand),
                    "the seat must move the prop toward the hand, not away from it");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        // ── the weld frame: palm, not wrist bone ──

        [Test]
        public void WeldFrame_SitsAtThePalmNotTheWristBone()
        {
            Hand hand = MakeHand(Quaternion.Euler(0f, 40f, 0f));
            hand.Wrist.position = new Vector3(0f, 1.2f, 0.3f);

            Assert.IsTrue(Frame(hand, out BasisHandFrame frame));
            Assert.LessOrEqual(Vector3.Distance(frame.Position, Vector3.Lerp(hand.Wrist.position, hand.Middle.position, 0.5f)), 0.001f,
                "the weld origin must be half way from the wrist bone to the middle finger's knuckle");
            Assert.Greater(Vector3.Distance(frame.Position, hand.Wrist.position), 0.03f,
                "the humanoid hand bone is the wrist — welding there seats every prop behind the hand");
            Assert.LessOrEqual(Mathf.Abs(frame.HandLength - 0.09f), 0.001f,
                "hand length is wrist to middle knuckle, the unit grip offsets travel in");
            Assert.IsTrue(frame.Canonical);
        }

        [Test]
        public void HandFrame_DoesNotMoveWhenTheFingersCurl()
        {
            Hand hand = MakeHand(Quaternion.identity);
            Assert.IsTrue(Frame(hand, out BasisHandFrame before));

            // The finger driver rotates the proximal bones every frame; that must not drag the held prop.
            hand.Middle.localRotation = Quaternion.Euler(70f, 0f, 0f);
            hand.Index.localRotation = Quaternion.Euler(60f, 0f, 0f);
            hand.Little.localRotation = Quaternion.Euler(80f, 0f, 0f);
            Assert.IsTrue(Frame(hand, out BasisHandFrame after));

            Assert.LessOrEqual(Vector3.Distance(before.Position, after.Position), 1e-5f,
                "curling a finger rotates the proximal bone, it does not move its origin");
            Assert.LessOrEqual(Quaternion.Angle(before.Rotation, after.Rotation), 0.01f);
        }

        [Test]
        public void HandFrame_IsTheSameOnTwoRigsWithDifferentBindRotations()
        {
            // The whole point of building off joint positions: two avatars posed identically but rigged with
            // different wrist bind orientations must hand back the same frame, so one streamed grip decodes
            // into either of them.
            Hand a = MakeHand(Quaternion.identity);
            Hand b = MakeHand(Quaternion.Euler(35f, -80f, 140f));
            b.Wrist.position = a.Wrist.position;
            foreach ((Transform from, Transform to) in new[] { (a.Middle, b.Middle), (a.Index, b.Index), (a.Little, b.Little) })
            {
                to.position = from.position;
            }

            Assert.IsTrue(Frame(a, out BasisHandFrame fa));
            Assert.IsTrue(Frame(b, out BasisHandFrame fb));

            Assert.Greater(Quaternion.Angle(a.Wrist.rotation, b.Wrist.rotation), 45f, "the two rigs must actually differ");
            Assert.LessOrEqual(Vector3.Distance(fa.Position, fb.Position), 0.001f);
            Assert.LessOrEqual(Quaternion.Angle(fa.Rotation, fb.Rotation), 0.05f,
                "a grip expressed in this frame must reconstruct the same on a differently rigged avatar");
        }

        [Test]
        public void HandFrame_FallsBackToTheWristWhenTheAvatarHasNoFingers()
        {
            Hand hand = MakeHand(Quaternion.Euler(10f, 20f, 30f));
            hand.Mapping.HasLeftMiddle[0] = false;
            hand.Mapping.HasLeftIndex[0] = false;
            hand.Mapping.HasLeftLittle[0] = false;

            Assert.IsTrue(Frame(hand, out BasisHandFrame frame));
            Assert.IsFalse(frame.Canonical, "a frame that had to use the bind rotation must say so");
            Assert.LessOrEqual(Vector3.Distance(frame.Position, hand.Wrist.position), 1e-5f);
            Assert.LessOrEqual(Quaternion.Angle(frame.Rotation, hand.Wrist.rotation), 0.01f);
        }

        [Test]
        public void NormalisedGrip_HoldsProportionallyOnADifferentlySizedHand()
        {
            Hand sender = MakeHand(Quaternion.identity);
            Hand observer = MakeHand(Quaternion.Euler(0f, 90f, 0f), scale: 2f);
            Assert.IsTrue(Frame(sender, out BasisHandFrame sf));
            Assert.IsTrue(Frame(observer, out BasisHandFrame of));

            // Encode against the sender's hand, decode against the observer's twice-as-large one.
            Vector3 propWorld = sf.Position + sf.Rotation * new Vector3(0.02f, 0f, 0.06f);
            Vector3 wire = (Quaternion.Inverse(sf.Rotation) * (propWorld - sf.Position)) / sf.HandLength;
            Vector3 decoded = of.Position + of.Rotation * (wire * of.HandLength);

            Vector3 inObserverHand = Quaternion.Inverse(of.Rotation) * (decoded - of.Position);
            Assert.LessOrEqual(Vector3.Distance(inObserverHand, new Vector3(0.04f, 0f, 0.12f)), 0.001f,
                "a hand twice the size must hold the grip twice as far out, not at the sender's absolute reach");
        }

        [Test]
        public void PalmSeat_PutsThePropInTheHandRatherThanBehindIt()
        {
            Hand hand = MakeHand(Quaternion.identity);
            hand.Wrist.position = new Vector3(0f, 1.2f, 0f);
            Assert.IsTrue(Frame(hand, out BasisHandFrame frame));

            BasisPickupInteractable prop = MakeProp("palm-seated", out Transform t);
            AddBox(prop, Vector3.one * 0.1f);
            t.SetPositionAndRotation(new Vector3(0f, 1.2f, 0.8f), Quaternion.identity);

            t.position = Seat(prop, t, frame.Position, frame.Rotation);

            Assert.LessOrEqual(Vector3.Distance(NearestSurfacePoint(prop, frame.Position), frame.Position), 0.01f,
                "the seat must land the prop's nearest surface on the palm");
            Assert.Greater(Vector3.Distance(NearestSurfacePoint(prop, hand.Wrist.position), hand.Wrist.position), 0.03f,
                "and therefore clear of the wrist bone, where the old weld buried it");
        }

        [Test]
        public void ColliderlessProp_KeepsTheGrabPoseInsteadOfSnappingItsPivotToTheHand()
        {
            BasisPickupInteractable prop = MakeProp("no-collider", out Transform t);
            Vector3 start = new Vector3(0.4f, 1f, 0.9f);
            t.SetPositionAndRotation(start, Quaternion.identity);

            Vector3 hand = new Vector3(0f, 1.2f, 0f);
            Vector3 seated = Seat(prop, t, hand, Quaternion.Euler(0f, 25f, 0f));

            // Returning a zero offset would drive pos = hand exactly, teleporting the prop's PIVOT onto the
            // hand — for a prop whose mesh is nowhere near its pivot that is a metre-scale jump.
            Assert.LessOrEqual(Vector3.Distance(seated, start), 0.001f,
                "with no collider to seat against, the grab must keep the pose it was grabbed at");
        }

        // ── authored grip: where and which way up the object arrives ──

        [Test]
        public void GripPoint_ArrivesInTheHandTheWayItWasAuthored()
        {
            BasisPickupInteractable prop = MakeProp("sword", out Transform t);
            AddBox(prop, new Vector3(0.06f, 0.06f, 1.2f));
            // Lying flat on the floor, pointing along +X — nothing like how it should be held.
            t.SetPositionAndRotation(new Vector3(1.5f, 0.02f, 2f), Quaternion.Euler(0f, 90f, 0f));
            Transform grip = MakeGrip(prop, new Vector3(0f, 0f, -0.5f), Quaternion.Euler(0f, 0f, 25f));

            Vector3 handPos = new Vector3(0f, 1.2f, 0.2f);
            Quaternion handRot = Quaternion.Euler(-30f, 55f, 15f);
            BasisParentConstraint constraint = GripGrab(prop, t, handPos, handRot);

            Assert.IsTrue(constraint.Evaluate(out Vector3 pos, out Quaternion rot));
            t.SetPositionAndRotation(pos, rot);

            Assert.LessOrEqual(Vector3.Distance(grip.position, handPos), 0.001f,
                "the authored grip must land on the hand");
            Assert.LessOrEqual(Quaternion.Angle(grip.rotation, handRot), 0.05f,
                "and take the hand's orientation — otherwise the object arrives at whatever angle it was lying at");
        }

        [Test]
        public void GripPoint_StaysInTheHandThroughAReach()
        {
            BasisPickupInteractable prop = MakeProp("gripped", out Transform t);
            AddBox(prop, Vector3.one * 0.2f);
            t.SetPositionAndRotation(new Vector3(0.5f, 0.3f, 1.1f), Quaternion.Euler(15f, 200f, 40f));
            Transform grip = MakeGrip(prop, new Vector3(0.03f, -0.08f, 0.12f), Quaternion.Euler(10f, 0f, -70f));

            BasisParentConstraint constraint = GripGrab(prop, t, new Vector3(0f, 1.1f, 0.1f), Quaternion.identity);

            Vector3 movedPos = new Vector3(-0.6f, 1.7f, 0.5f);
            Quaternion movedRot = Quaternion.Euler(70f, -140f, 95f);
            constraint.UpdateSourcePositionAndRotation(0, movedPos, movedRot);
            Assert.IsTrue(constraint.Evaluate(out Vector3 pos, out Quaternion rot));
            t.SetPositionAndRotation(pos, rot);

            Assert.LessOrEqual(Vector3.Distance(grip.position, movedPos), 0.001f);
            Assert.LessOrEqual(Quaternion.Angle(grip.rotation, movedRot), 0.05f);
        }

        [Test]
        public void TriggerOnlyRoot_SeatsAgainstTheChildGeometry()
        {
            // A hover/proximity volume on the root used to short-circuit collider resolution, hiding the real
            // geometry — and since the seat skips triggers, such a prop never got seated at all.
            BasisPickupInteractable prop = MakeProp("zoned", out Transform t);
            Vector3 start = new Vector3(0f, 1f, 0f);
            t.SetPositionAndRotation(start, Quaternion.identity);
            BoxCollider zone = prop.gameObject.AddComponent<BoxCollider>();
            zone.isTrigger = true;
            zone.size = Vector3.one * 3f;

            var body = new GameObject("body");
            body.transform.SetParent(t, false);
            BoxCollider solid = body.AddComponent<BoxCollider>();
            solid.size = Vector3.one * 0.2f;

            CollectionAssert.Contains(prop.GetColliders(), solid,
                "a root carrying only triggers must fall through to the child geometry");

            Vector3 hand = start + new Vector3(0f, 0f, -0.6f);
            t.position = Seat(prop, t, hand, Quaternion.identity);
            Assert.LessOrEqual(Vector3.Distance(solid.ClosestPoint(hand), hand), 0.01f,
                "and that geometry is what the grab seats against");
        }

        [Test]
        public void SolidRootCollider_StillWinsOverChildren()
        {
            BasisPickupInteractable prop = MakeProp("solid-root", out Transform t);
            BoxCollider root = prop.gameObject.AddComponent<BoxCollider>();
            root.size = Vector3.one * 0.2f;

            var body = new GameObject("decoration");
            body.transform.SetParent(t, false);
            BoxCollider child = body.AddComponent<BoxCollider>();

            Collider[] resolved = prop.GetColliders();
            CollectionAssert.Contains(resolved, root);
            CollectionAssert.DoesNotContain(resolved, child,
                "a solid collider on self still short-circuits the search, as documented");
        }

        // ── the cost of welding to the solved bone rather than the hand target ──

        [TestCase(0.02f)]
        [TestCase(0.05f)]
        [TestCase(0.12f)]
        public void WeldPoseDivergingFromHandTarget_CarriesThePropAwayFromTheTarget(float solveError)
        {
            BasisPickupInteractable prop = MakeProp("diverging", out Transform t);
            AddBox(prop, Vector3.one * 0.2f);
            t.SetPositionAndRotation(new Vector3(0f, 1f, 0f), Quaternion.identity);

            // Grab while the FBIK happens to be solving the wrist exactly onto its target.
            Vector3 handTarget = new Vector3(0f, 1f, -0.4f);
            var (constraint, _) = Grab(prop, t, handTarget, Quaternion.identity);

            // Reach out: the solve can no longer land the wrist on the target (arm length, shoulder and
            // wrist limits, per-group smoothing), so IKWorldData separates from OutgoingWorldData.
            Vector3 solvedWrist = handTarget + new Vector3(0f, 0f, -solveError);

            constraint.UpdateSourcePositionAndRotation(0, handTarget, Quaternion.identity);
            Assert.IsTrue(constraint.Evaluate(out Vector3 drivenByTarget, out _));
            constraint.UpdateSourcePositionAndRotation(0, solvedWrist, Quaternion.identity);
            Assert.IsTrue(constraint.Evaluate(out Vector3 drivenByWrist, out _));

            // Characterisation: the weld passes solve error straight through to the prop 1:1. Every
            // centimetre the FBIK cannot deliver is a centimetre between the prop and the real controller.
            Assert.AreEqual(solveError, Vector3.Distance(drivenByTarget, drivenByWrist), 0.001f,
                "welding to the solved wrist transfers the whole FBIK solve error onto the held prop");
        }

        // ── rig ──

        BasisPickupInteractable MakeProp(string name, out Transform t)
        {
            var go = new GameObject(name);
            _cleanup.Add(go);
            t = go.transform;
            return go.AddComponent<BasisPickupInteractable>();
        }

        struct Hand
        {
            public BasisTransformMapping Mapping;
            public Transform Wrist;
            public Transform Middle;
            public Transform Index;
            public Transform Little;
        }

        /// <summary>
        /// A left hand: wrist bone plus the three proximal knuckles the frame is built from, parented as a rig
        /// parents them. <paramref name="bind"/> is the wrist's bind rotation — the per-avatar convention the
        /// frame must be immune to; <paramref name="scale"/> sizes the hand.
        /// </summary>
        Hand MakeHand(Quaternion bind, float scale = 1f)
        {
            var wristGo = new GameObject("LeftHand");
            _cleanup.Add(wristGo);
            wristGo.transform.rotation = bind;

            Transform Knuckle(string name, Vector3 fromWrist)
            {
                var go = new GameObject(name);
                go.transform.SetParent(wristGo.transform, false);
                go.transform.position = wristGo.transform.position + fromWrist * scale;
                return go.transform;
            }

            var mapping = new BasisTransformMapping();
            mapping.leftHand = wristGo.transform;
            mapping.HasleftHand = true;
            mapping.LeftMiddle[0] = Knuckle("LeftMiddleProximal", new Vector3(0f, 0f, 0.09f));
            mapping.HasLeftMiddle[0] = true;
            mapping.LeftIndex[0] = Knuckle("LeftIndexProximal", new Vector3(0.02f, 0f, 0.085f));
            mapping.HasLeftIndex[0] = true;
            mapping.LeftLittle[0] = Knuckle("LeftLittleProximal", new Vector3(-0.03f, 0f, 0.075f));
            mapping.HasLeftLittle[0] = true;

            return new Hand
            {
                Mapping = mapping,
                Wrist = wristGo.transform,
                Middle = mapping.LeftMiddle[0],
                Index = mapping.LeftIndex[0],
                Little = mapping.LeftLittle[0],
            };
        }

        static bool Frame(Hand hand, out BasisHandFrame frame)
        {
            hand.Wrist.GetPositionAndRotation(out Vector3 p, out Quaternion r);
            return BasisHandGrip.TryGetFrame(hand.Mapping, left: true, p, r, out frame);
        }

        static Transform MakeGrip(BasisPickupInteractable prop, Vector3 localPos, Quaternion localRot)
        {
            var go = new GameObject("Grip");
            go.transform.SetParent(prop.transform, false);
            go.transform.SetLocalPositionAndRotation(localPos, localRot);
            prop.GripPoint = go.transform;
            return go.transform;
        }

        /// <summary>Mirrors the OnInteractStart branch a welded grab takes when a grip point is authored.</summary>
        static BasisParentConstraint GripGrab(
            BasisPickupInteractable prop, Transform t, Vector3 weldPos, Quaternion weldRot)
        {
            t.GetPositionAndRotation(out Vector3 objectPos, out Quaternion objectRot);

            object[] args = { objectPos, objectRot, null, null };
            Assert.IsTrue((bool)GripOffsetsM.Invoke(prop, args), "an authored grip must produce offsets");

            var constraint = new BasisParentConstraint
            {
                sources = new BasisConstraintSourceData[] { new() { weight = 1f } },
                Enabled = true,
                GlobalWeight = 1f,
            };
            constraint.SetRestPositionAndRotation(objectPos, objectRot);
            constraint.SetOffsetPositionAndRotation(0, (Vector3)args[2], (Quaternion)args[3]);
            constraint.UpdateSourcePositionAndRotation(0, weldPos, weldRot);
            return constraint;
        }

        static void AddBox(BasisPickupInteractable prop, Vector3 size)
        {
            BoxCollider box = prop.gameObject.AddComponent<BoxCollider>();
            box.size = size;
        }

        /// <summary>Mirrors OnInteractStart :615-628 for a VR weld grab (LerpToHandOnPickup on).</summary>
        (BasisParentConstraint constraint, Vector3 offsetPos) Grab(
            BasisPickupInteractable prop, Transform t, Vector3 weldPos, Quaternion weldRot)
        {
            t.GetPositionAndRotation(out Vector3 objectPos, out Quaternion objectRot);

            var constraint = new BasisParentConstraint
            {
                sources = new BasisConstraintSourceData[] { new() { weight = 1f } },
                Enabled = true,
            };
            constraint.SetRestPositionAndRotation(objectPos, objectRot);

            var offsetPos = (Vector3)ClosestOffsetM.Invoke(prop, new object[] { weldPos, weldRot, objectPos });
            Quaternion offsetRot = Quaternion.Inverse(weldRot) * objectRot;
            constraint.SetOffsetPositionAndRotation(0, offsetPos, offsetRot);
            constraint.UpdateSourcePositionAndRotation(0, weldPos, weldRot);
            constraint.GlobalWeight = 1f;   // the 0.05 s lerp has finished
            return (constraint, offsetPos);
        }

        /// <summary>Grab and settle, returning the prop's world position once the grab lerp has completed.</summary>
        Vector3 Seat(BasisPickupInteractable prop, Transform t, Vector3 weldPos, Quaternion weldRot)
        {
            var (constraint, _) = Grab(prop, t, weldPos, weldRot);
            Assert.IsTrue(constraint.Evaluate(out Vector3 pos, out _), "constraint must evaluate while held");
            return pos;
        }

        static Vector3 InWeldFrame(Transform t, Vector3 weldPos, Quaternion weldRot)
            => Quaternion.Inverse(weldRot) * (t.position - weldPos);

        static Vector3 NearestSurfacePoint(BasisPickupInteractable prop, Vector3 query)
        {
            Vector3 best = query;
            float bestSq = float.MaxValue;
            foreach (Collider c in prop.GetColliders())
            {
                if (c == null || !c.enabled) continue;
                Vector3 p = c.ClosestPoint(query);
                float d = (p - query).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = p; }
            }
            return best;
        }

        static Vector3 NearestVisualSurfacePoint(BasisPickupInteractable prop, Vector3 query, Collider ignore)
        {
            Vector3 best = query;
            float bestSq = float.MaxValue;
            foreach (Collider c in prop.GetColliders())
            {
                if (c == null || !c.enabled || c == ignore) continue;
                Vector3 p = c.ClosestPoint(query);
                float d = (p - query).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = p; }
            }
            return best;
        }

        static Mesh Quad()
        {
            var m = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(-0.2f, -0.2f, 0f), new Vector3(0.2f, -0.2f, 0f),
                    new Vector3(0.2f, 0.2f, 0f), new Vector3(-0.2f, 0.2f, 0f),
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            m.RecalculateNormals();
            return m;
        }
    }
}
