using UnityEngine;

namespace UnityEngine.Animations.Rigging
{
    [System.Serializable]
    public struct BasisTwoBoneIKConstraintHandData : IAnimationJobData, BasisITwoBoneIKConstraintHandData
    {
        [SerializeField] Transform m_Root;
        [SerializeField] Transform m_Mid;
        [SerializeField] Transform m_Tip;

        [SyncSceneToStream, SerializeField] public Vector3 TargetPosition;
        [SyncSceneToStream, SerializeField] public Quaternion TargetRotation;

        [SyncSceneToStream, SerializeField] public Vector3 HintPosition;
        [SyncSceneToStream, SerializeField] public Quaternion HintRotation;

        Vector3 BasisITwoBoneIKConstraintHandData.targetPosition => TargetPosition;
        Quaternion BasisITwoBoneIKConstraintHandData.targetRotation => TargetRotation;

        Vector3 BasisITwoBoneIKConstraintHandData.hintPosition => HintPosition;
        Quaternion BasisITwoBoneIKConstraintHandData.HintRotation => HintRotation;

        [SyncSceneToStream, SerializeField] bool m_HintWeight;

        // Chest capsule
        [SyncSceneToStream, SerializeField] Transform m_ChestCapsuleStart;
        [SyncSceneToStream, SerializeField] Transform m_ChestCapsuleEnd;
        [SyncSceneToStream, SerializeField, Min(0f)] float m_ChestRadius;
        [SyncSceneToStream, SerializeField, Min(0f)] float m_CollisionSkin;
        [SyncSceneToStream, SerializeField] bool m_CollisionsEnabled;

        // Hand capsule (in TIP local space)
        [Header("Hand Capsule (tip local space)")]
        [SyncSceneToStream, SerializeField] Vector3 m_HandLocalStart;
        [SyncSceneToStream, SerializeField] Vector3 m_HandLocalEnd;
        [SyncSceneToStream, SerializeField, Min(0f)] float m_HandRadius;
        [SyncSceneToStream, SerializeField] float m_HandSkin;
        [SyncSceneToStream, SerializeField] bool m_UseHandCapsule;

        [Header("Elbow Protection")]
        [SyncSceneToStream, SerializeField] bool m_ProtectElbow;

        public Transform root { get => m_Root; set => m_Root = value; }
        public Transform mid { get => m_Mid; set => m_Mid = value; }
        public Transform tip { get => m_Tip; set => m_Tip = value; }

        public bool hintWeight { get => m_HintWeight; set => m_HintWeight = value; }
        string BasisITwoBoneIKConstraintHandData.hintWeightFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintWeight));

        string BasisITwoBoneIKConstraintHandData.TargetpositionVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition));
        string BasisITwoBoneIKConstraintHandData.TargetrotationVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation));
        string BasisITwoBoneIKConstraintHandData.HintpositionVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintPosition));
        string BasisITwoBoneIKConstraintHandData.HintrotationVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintRotation));

        // Hand binding property names
        string BasisITwoBoneIKConstraintHandData.HandLocalStartVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HandLocalStart));
        string BasisITwoBoneIKConstraintHandData.HandLocalEndVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HandLocalEnd));
        string BasisITwoBoneIKConstraintHandData.HandRadiusFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HandRadius));
        string BasisITwoBoneIKConstraintHandData.HandSkinFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HandSkin));
        string BasisITwoBoneIKConstraintHandData.UseHandCapsuleBoolProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_UseHandCapsule));
        string BasisITwoBoneIKConstraintHandData.ProtectElbowBoolProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ProtectElbow));

        // Chest binding property names (ADDED)
        string BasisITwoBoneIKConstraintHandData.ChestRadiusFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ChestRadius));
        string BasisITwoBoneIKConstraintHandData.CollisionSkinFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_CollisionSkin));
        string BasisITwoBoneIKConstraintHandData.CollisionsEnabledBoolProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_CollisionsEnabled));

        [SyncSceneToStream, SerializeField] public Vector3 M_CalibratedOffset;
        [SyncSceneToStream, SerializeField] public Quaternion M_CalibratedRotation;

        public Vector3 CalibratedOffset => M_CalibratedOffset;
        public Quaternion CalibratedRotation => M_CalibratedRotation;

        // expose to binder
        public Transform chestCapsuleStart { get => m_ChestCapsuleStart; set => m_ChestCapsuleStart = value; }
        public Transform chestCapsuleEnd { get => m_ChestCapsuleEnd; set => m_ChestCapsuleEnd = value; }
        public float chestRadius { get => m_ChestRadius; set => m_ChestRadius = value; }
        public float collisionSkin { get => m_CollisionSkin; set => m_CollisionSkin = value; }
        public bool collisionsEnabled { get => m_CollisionsEnabled; set => m_CollisionsEnabled = value; }

        public Vector3 handLocalStart { get => m_HandLocalStart; set => m_HandLocalStart = value; }
        public Vector3 handLocalEnd { get => m_HandLocalEnd; set => m_HandLocalEnd = value; }
        public float handRadius { get => m_HandRadius; set => m_HandRadius = value; }
        public float handSkin { get => m_HandSkin; set => m_HandSkin = value; }
        public bool useHandCapsule { get => m_UseHandCapsule; set => m_UseHandCapsule = value; }

        public bool protectElbow { get => m_ProtectElbow; set => m_ProtectElbow = value; }

        bool IAnimationJobData.IsValid() =>
            (m_Tip != null && m_Mid != null && m_Root != null &&
             m_Tip.IsChildOf(m_Mid) && m_Mid.IsChildOf(m_Root));

        void IAnimationJobData.SetDefaultValues()
        {
            m_Root = null;
            m_Mid = null;
            m_Tip = null;
            m_HintWeight = true;

            m_ChestCapsuleStart = null;
            m_ChestCapsuleEnd = null;
            m_CollisionsEnabled = true;

            // Hand defaults can be authored in inspector; left intentionally blank here.
        }
    }

    [DisallowMultipleComponent, AddComponentMenu("Animation Rigging/Two Bone IK Constraint (Hands, Hand Capsule vs Chest)")]
    [HelpURL("https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/manual/constraints/TwoBoneIKConstraint.html")]
    public class BasisTwoBoneIKConstraintHand : RigConstraint<BasisTwoBoneIKConstraintHandJob, BasisTwoBoneIKConstraintHandData, BasisTwoBoneIKConstraintJobBinderHand<BasisTwoBoneIKConstraintHandData>>
    {
        [Header("Visualization")]
        [SerializeField] bool m_DrawGizmos = true;
        [SerializeField] bool m_DrawWhenUnselected = false;
        #if UNITY_EDITOR
        [SerializeField] Color m_InnerColor = new Color(0.2f, 0.8f, 1f, 0.9f);   // chest inner
        [SerializeField] Color m_SkinColor = new Color(1f, 0.6f, 0.2f, 0.9f);    // chest skin
        [SerializeField] Color m_HandColor = new Color(0.6f, 1f, 0.6f, 0.9f);    // hand capsule
#endif
        [SerializeField, Range(2, 12)] int m_CircleSlices = 6;

        protected override void OnValidate()
        {
            base.OnValidate();
            m_Data.hintWeight = m_Data.hintWeight;
            m_CircleSlices = Mathf.Clamp(m_CircleSlices, 2, 12);
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (m_DrawWhenUnselected) DrawGizmosInternal();
        }

        void OnDrawGizmosSelected()
        {
            if (!m_DrawWhenUnselected) DrawGizmosInternal();
        }

        void DrawGizmosInternal()
        {
            if (!m_DrawGizmos) return;

            // Chest
            var start = m_Data.chestCapsuleStart;
            var end = m_Data.chestCapsuleEnd;
            if (start != null && end != null)
            {
                Vector3 a = start.position;
                Vector3 b = end.position;

                float rInner = Mathf.Max(0f, m_Data.chestRadius);
                float rOuter = Mathf.Max(0f, m_Data.chestRadius + m_Data.collisionSkin);

                DrawWireCapsule(a, b, rInner, m_InnerColor, m_CircleSlices);
                if (rOuter > rInner + 1e-5f)
                    DrawWireCapsule(a, b, rOuter, m_SkinColor, m_CircleSlices);
            }

            // Hand capsule preview at current Target (editor-time only, not exact runtime pose)
            if (m_Data.useHandCapsule && m_Data.tip != null)
            {
                Vector3 tp = m_Data.TargetPosition;
                Quaternion tr = m_Data.TargetRotation;

                Vector3 hs = tp + tr * m_Data.handLocalStart;
                Vector3 he = tp + tr * m_Data.handLocalEnd;
                float hr = m_Data.handRadius + m_Data.handSkin;

                DrawWireCapsule(hs, he, hr, m_HandColor, m_CircleSlices);
            }
        }

        static void DrawWireCapsule(Vector3 a, Vector3 b, float radius, Color color, int slices)
        {
            if (radius <= 0f) return;
            using (new UnityEditor.Handles.DrawingScope(color))
            {
                Vector3 axis = (b - a);
                float len = axis.magnitude;
                Vector3 dir = (len > 1e-6f) ? axis / len : Vector3.up;

                Vector3 x = Vector3.Normalize(Vector3.Cross(dir, Vector3.up));
                if (x.sqrMagnitude < 1e-6f) x = Vector3.Normalize(Vector3.Cross(dir, Vector3.right));
                Vector3 y = Vector3.Cross(dir, x);

                UnityEditor.Handles.DrawWireDisc(a, x, radius);
                UnityEditor.Handles.DrawWireDisc(a, y, radius);
                UnityEditor.Handles.DrawWireDisc(b, x, radius);
                UnityEditor.Handles.DrawWireDisc(b, y, radius);

                UnityEditor.Handles.DrawLine(a + x * radius, b + x * radius);
                UnityEditor.Handles.DrawLine(a - x * radius, b - x * radius);
                UnityEditor.Handles.DrawLine(a + y * radius, b + y * radius);
                UnityEditor.Handles.DrawLine(a - y * radius, b - y * radius);

                slices = Mathf.Max(2, slices);
                for (int i = 1; i < slices; ++i)
                {
                    float t = i / (float)slices;
                    Vector3 p = Vector3.Lerp(a, b, t);
                    UnityEditor.Handles.DrawWireDisc(p, x, radius);
                    UnityEditor.Handles.DrawWireDisc(p, y, radius);
                }
            }
        }
#endif
    }

    [Unity.Burst.BurstCompile]
    public struct BasisTwoBoneIKConstraintHandJob : IWeightedAnimationJob
    {
        public ReadWriteTransformHandle root;
        public ReadWriteTransformHandle mid;
        public ReadWriteTransformHandle tip;

        public Vector3Property hintPosition;
        public Vector3Property targetPosition;

        public Vector4Property hintRotation;
        public Vector4Property targetRotation;

        public AffineTransform targetOffset;
        public BoolProperty hintWeight;

        // Chest collision capsule (CONVERTED to properties)
        public ReadOnlyTransformHandle chestStart;
        public ReadOnlyTransformHandle chestEnd;
        public FloatProperty chestRadius;
        public FloatProperty collisionSkin;
        public BoolProperty collisionsEnabled;

        // Hand capsule (tip local space, bound as properties)
        public Vector3Property handLocalStart;
        public Vector3Property handLocalEnd;
        public FloatProperty handRadius;
        public FloatProperty handSkin;
        public BoolProperty useHandCapsule;

        // Elbow protection (bound)
        public BoolProperty protectElbow;

        public FloatProperty jobWeight { get; set; }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float w = jobWeight.Get(stream);
            if (w <= 0f)
            {
                BasisAnimationRuntimeUtils.PassThrough(stream, root);
                BasisAnimationRuntimeUtils.PassThrough(stream, mid);
                BasisAnimationRuntimeUtils.PassThrough(stream, tip);
                return;
            }

            // Read inputs
            Vector3 tgtPos = targetPosition.Get(stream);
            Quaternion tgtRot = Vector4ToRotation(targetRotation.Get(stream));

            Vector3 hintPos = hintPosition.Get(stream);
            Quaternion hintRot = Vector4ToRotation(hintRotation.Get(stream));

            // ---- COLLISION: HAND CAPSULE vs CHEST CAPSULE ----
            if (collisionsEnabled.Get(stream) && chestStart.IsValid(stream) && chestEnd.IsValid(stream) && useHandCapsule.Get(stream))
            {
                Vector3 chestA = chestStart.GetPosition(stream);
                Vector3 chestB = chestEnd.GetPosition(stream);
                float chestR = Mathf.Max(0f, chestRadius.Get(stream) + collisionSkin.Get(stream));

                // Build the hand capsule from intended target pose (ensures whole hand cleared)
                Vector3 hsLocal = handLocalStart.Get(stream);
                Vector3 heLocal = handLocalEnd.Get(stream);
                float hRad = Mathf.Max(0f, handRadius.Get(stream) + handSkin.Get(stream));

                Vector3 handA = tgtPos + (tgtRot * hsLocal);
                Vector3 handB = tgtPos + (tgtRot * heLocal);

                // compute minimal push to separate whole hand capsule from chest capsule
                Vector3 correction = BasisAnimationRuntimeUtils.CapsuleCapsuleResolve(handA, handB, hRad, chestA, chestB, chestR);
                if (correction.sqrMagnitude > 0f)
                {
                    tgtPos += correction;
                    // Optional: nudge hint slightly in same direction so elbow steers around chest
                    hintPos += correction * 0.25f;
                }
            }
            else if (collisionsEnabled.Get(stream) && chestStart.IsValid(stream) && chestEnd.IsValid(stream))
            {
                // Fallback: point target clamp if hand capsule not used
                Vector3 a = chestStart.GetPosition(stream);
                Vector3 b = chestEnd.GetPosition(stream);
                float r = Mathf.Max(0f, chestRadius.Get(stream) + collisionSkin.Get(stream));
                tgtPos = BasisAnimationRuntimeUtils.PushOutFromCapsule(tgtPos, a, b, r);
                Vector3 nudgedHint = BasisAnimationRuntimeUtils.PushOutFromCapsule(hintPos, a, b, r * 0.9f);
                hintPos = Vector3.Lerp(hintPos, nudgedHint, 0.6f);
            }

            var target = new AffineTransform(tgtPos, tgtRot);
            var hint = new AffineTransform(hintPos, hintRot);

            // First solve
            BasisAnimationRuntimeUtils.SolveTwoBoneIKArms(stream, root, mid, tip, target, hint, hintWeight.Get(stream), targetOffset);

            // ---- ELBOW COLLISION PASS (optional) ----
            if (protectElbow.Get(stream) && collisionsEnabled.Get(stream) && chestStart.IsValid(stream) && chestEnd.IsValid(stream))
            {
                Vector3 a = chestStart.GetPosition(stream);
                Vector3 b = chestEnd.GetPosition(stream);
                float r = Mathf.Max(0f, chestRadius.Get(stream) + collisionSkin.Get(stream));

                Vector3 B = mid.GetPosition(stream);
                Vector3 pushedB = BasisAnimationRuntimeUtils.PushOutFromCapsule(B, a, b, r);
                if ((pushedB - B).sqrMagnitude > 1e-10f)
                {
                    BasisAnimationRuntimeUtils.SwingElbowAroundAC(stream, root, mid, tip, pushedB);
                    // Re-lock wrist to target after elbow swing
                    BasisAnimationRuntimeUtils.SolveTwoBoneIKArms(stream, root, mid, tip, target, hint, hintWeight.Get(stream), targetOffset);
                }
            }
        }

        public static Quaternion Vector4ToRotation(Vector4 Rotation)
        {
            return new Quaternion(Rotation.x, Rotation.y, Rotation.z, Rotation.w);
        }
    }

    public interface BasisITwoBoneIKConstraintHandData
    {
        Transform root { get; }
        Transform mid { get; }
        Transform tip { get; }
        Vector3 targetPosition { get; }
        Quaternion targetRotation { get; }
        Vector3 hintPosition { get; }
        Quaternion HintRotation { get; }

        Vector3 CalibratedOffset { get; }
        Quaternion CalibratedRotation { get; }

        string hintWeightFloatProperty { get; }
        string TargetpositionVector3Property { get; }
        string TargetrotationVector3Property { get; }
        string HintpositionVector3Property { get; }
        string HintrotationVector3Property { get; }

        Transform chestCapsuleStart { get; }
        Transform chestCapsuleEnd { get; }

        // NOTE: these remain as data fields for inspector authoring,
        // but are also exposed via property-name getters below.
        float chestRadius { get; }
        float collisionSkin { get; }
        bool collisionsEnabled { get; }

        // Hand capsule exposure
        Vector3 handLocalStart { get; }
        Vector3 handLocalEnd { get; }
        float handRadius { get; }
        float handSkin { get; }
        bool useHandCapsule { get; }

        // Elbow
        bool protectElbow { get; }

        // Hand binding property names
        string HandLocalStartVector3Property { get; }
        string HandLocalEndVector3Property { get; }
        string HandRadiusFloatProperty { get; }
        string HandSkinFloatProperty { get; }
        string UseHandCapsuleBoolProperty { get; }
        string ProtectElbowBoolProperty { get; }

        // Chest binding property names (ADDED)
        string ChestRadiusFloatProperty { get; }
        string CollisionSkinFloatProperty { get; }
        string CollisionsEnabledBoolProperty { get; }
    }

    public class BasisTwoBoneIKConstraintJobBinderHand<T> : AnimationJobBinder<BasisTwoBoneIKConstraintHandJob, T> where T : struct, IAnimationJobData, BasisITwoBoneIKConstraintHandData
    {
        public override BasisTwoBoneIKConstraintHandJob Create(Animator animator, ref T data, Component component)
        {
            BasisTwoBoneIKConstraintHandJob job = new BasisTwoBoneIKConstraintHandJob
            {
                root = ReadWriteTransformHandle.Bind(animator, data.root),
                mid = ReadWriteTransformHandle.Bind(animator, data.mid),
                tip = ReadWriteTransformHandle.Bind(animator, data.tip),

                targetPosition = Vector3Property.Bind(animator, component, data.TargetpositionVector3Property),
                targetRotation = Vector4Property.Bind(animator, component, data.TargetrotationVector3Property),

                hintPosition = Vector3Property.Bind(animator, component, data.HintpositionVector3Property),
                hintRotation = Vector4Property.Bind(animator, component, data.HintrotationVector3Property),

                targetOffset = new AffineTransform(data.CalibratedOffset, data.CalibratedRotation),

                chestStart = (data.chestCapsuleStart != null) ? ReadOnlyTransformHandle.Bind(animator, data.chestCapsuleStart) : default,
                chestEnd = (data.chestCapsuleEnd != null) ? ReadOnlyTransformHandle.Bind(animator, data.chestCapsuleEnd) : default,

                // Chest params (now bound as properties)
                chestRadius = FloatProperty.Bind(animator, component, data.ChestRadiusFloatProperty),
                collisionSkin = FloatProperty.Bind(animator, component, data.CollisionSkinFloatProperty),
                collisionsEnabled = BoolProperty.Bind(animator, component, data.CollisionsEnabledBoolProperty),

                // Hand capsule params (bound)
                handLocalStart = Vector3Property.Bind(animator, component, data.HandLocalStartVector3Property),
                handLocalEnd = Vector3Property.Bind(animator, component, data.HandLocalEndVector3Property),
                handRadius = FloatProperty.Bind(animator, component, data.HandRadiusFloatProperty),
                handSkin = FloatProperty.Bind(animator, component, data.HandSkinFloatProperty),
                useHandCapsule = BoolProperty.Bind(animator, component, data.UseHandCapsuleBoolProperty),

                // Elbow
                protectElbow = BoolProperty.Bind(animator, component, data.ProtectElbowBoolProperty),
                hintWeight = BoolProperty.Bind(animator, component, data.hintWeightFloatProperty),
            };
            return job;
        }

        public override void Destroy(BasisTwoBoneIKConstraintHandJob job) { }
    }
}
