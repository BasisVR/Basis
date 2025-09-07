using Unity.Collections;

namespace UnityEngine.Animations.Rigging
{
    /// <summary>
    /// The ChainIK constraint job.
    /// </summary>
    [Unity.Burst.BurstCompile]
    public struct BasisChainIKConstraintJob : IWeightedAnimationJob
    {
        /// <summary>An array of Transform handles that represents the Transform chain.</summary>
        public NativeArray<ReadWriteTransformHandle> chain;
        /// <summary>The Transform handle for the target Transform.</summary>
        public Vector3Property TargetPosition;
        /// <summary>The Transform handle for the target Transform.</summary>
        public Vector3Property TargetRotation;

        /// <summary>An array of length in between Transforms in the chain.</summary>
        public NativeArray<float> linkLengths;

        /// <summary>An array of positions for Transforms in the chain.</summary>
        public NativeArray<Vector3> linkPositions;

        /// <summary>CacheIndex to ChainIK tolerance value.</summary>
        /// <seealso cref="AnimationJobCache"/>
        public CacheIndex toleranceIdx;
        /// <summary>CacheIndex to ChainIK maxIterations value.</summary>
        /// <seealso cref="AnimationJobCache"/>
        public CacheIndex maxIterationsIdx;
        /// <summary>Cache for static properties in the job.</summary>
        public AnimationJobCache cache;

        /// <summary>The maximum distance the Transform chain can reach.</summary>
        public float maxReach;

        /// <inheritdoc />
        public FloatProperty jobWeight { get; set; }

        /// <summary>
        /// Defines what to do when processing the root motion.
        /// </summary>
        /// <param name="stream">The animation stream to work on.</param>
        public void ProcessRootMotion(AnimationStream stream) { }

        /// <summary>
        /// Defines what to do when processing the animation.
        /// </summary>
        /// <param name="stream">The animation stream to work on.</param>
        public void ProcessAnimation(AnimationStream stream)
        {
            float w = jobWeight.Get(stream);
            if (w > 0f)
            {
              //  Debug.Log($"chain {chain.Length}");
                for (int i = 0; i < chain.Length; ++i)
                {
                    var handle = chain[i];
                    linkPositions[i] = handle.GetPosition(stream);
                    chain[i] = handle;
                }

                int tipIndex = chain.Length - 1;
                if (AnimationRuntimeUtils.SolveFABRIK(ref linkPositions, ref linkLengths, TargetPosition.Get(stream),//targetOffset.translation
                    cache.GetRaw(toleranceIdx), maxReach, (int)cache.GetRaw(maxIterationsIdx)))
                {
                    var chainRWeight =  w;
                    for (int i = 0; i < tipIndex; ++i)
                    {
                        var prevDir = chain[i + 1].GetPosition(stream) - chain[i].GetPosition(stream);
                        var newDir = linkPositions[i + 1] - linkPositions[i];
                        var rot = chain[i].GetRotation(stream);
                        chain[i].SetRotation(stream, Quaternion.Lerp(rot, QuaternionExt.FromToRotation(prevDir, newDir) * rot, chainRWeight));
                    }
                }

                chain[tipIndex].SetRotation(
                    stream,
                    Quaternion.Lerp(
                        chain[tipIndex].GetRotation(stream),
                        Quaternion.Euler(TargetRotation.Get(stream)),// * targetOffset.rotation
                         w
                        )
                    );
            }
            else
            {
                for (int i = 0; i < chain.Length; ++i)
                    AnimationRuntimeUtils.PassThrough(stream, chain[i]);
            }
        }
    }

    /// <summary>
    /// This interface defines the data mapping for the ChainIK constraint.
    /// </summary>
    public interface BasisIChainIKConstraintData
    {
        /// <summary>The root Transform of the ChainIK hierarchy.</summary>
        Transform root { get; }
        /// <summary>The tip Transform of the ChainIK hierarchy. The tip needs to be a descendant/child of the root Transform.</summary>
        Transform tip { get; }

        /// <summary>The path to the chain rotation weight property in the constraint component.</summary>
        string targetRotationProperty { get; }
        /// <summary>The path to the tip rotation weight property in the constraint component.</summary>
        string targetPositionProperty { get; }
    }

    /// <summary>
    /// The ChainIK constraint job binder.
    /// </summary>
    /// <typeparam name="T">The constraint data type</typeparam>
    public class BasisChainIKConstraintJobBinder<T> : AnimationJobBinder<BasisChainIKConstraintJob, T>
        where T : struct, IAnimationJobData, BasisIChainIKConstraintData
    {
        /// <inheritdoc />
        public override BasisChainIKConstraintJob Create(Animator animator, ref T data, Component component)
        {
            Transform[] chain = ConstraintsUtils.ExtractChain(data.root, data.tip);

            var job = new BasisChainIKConstraintJob();
        //    Debug.Log($"Length was {chain.Length}");
            job.chain = new NativeArray<ReadWriteTransformHandle>(chain.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            job.linkLengths = new NativeArray<float>(chain.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            job.linkPositions = new NativeArray<Vector3>(chain.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            job.maxReach = 0f;

            int tipIndex = chain.Length - 1;
            for (int i = 0; i < chain.Length; ++i)
            {
                job.chain[i] = ReadWriteTransformHandle.Bind(animator, chain[i]);
                job.linkLengths[i] = (i != tipIndex) ? Vector3.Distance(chain[i].position, chain[i + 1].position) : 0f;
                job.maxReach += job.linkLengths[i];
            }

            job.TargetPosition = Vector3Property.Bind(animator, component, data.targetPositionProperty);
            job.TargetRotation = Vector3Property.Bind(animator,component, data.targetRotationProperty);

            var cacheBuilder = new AnimationJobCacheBuilder();
            job.maxIterationsIdx = cacheBuilder.Add(16);
            job.toleranceIdx = cacheBuilder.Add(0.001f);
            job.cache = cacheBuilder.Build();

            return job;
        }

        /// <inheritdoc />
        public override void Destroy(BasisChainIKConstraintJob job)
        {
            job.chain.Dispose();
            job.linkLengths.Dispose();
            job.linkPositions.Dispose();
            job.cache.Dispose();
        }

        /// <inheritdoc />
        public override void Update(BasisChainIKConstraintJob job, ref T data)
        {
            job.cache.SetRaw(16, job.maxIterationsIdx);
            job.cache.SetRaw(0.001f, job.toleranceIdx);
        }
    }

    /// <summary>
    /// The ChainIK constraint data.
    /// </summary>
    [System.Serializable]
    public struct BasisChainIKConstraintData : IAnimationJobData, BasisIChainIKConstraintData
    {

        [SerializeField] Transform m_Root;
        [SerializeField] Transform m_Tip;

        [SyncSceneToStream, SerializeField] public Vector3 headTargetPosition;
        [SyncSceneToStream, SerializeField] public Vector3 headTargetRotationEuler; // degrees

        /// <inheritdoc />
        public Transform root { get => m_Root; set => m_Root = value; }
        /// <inheritdoc />
        public Transform tip { get => m_Tip; set => m_Tip = value; }

        public string targetRotationProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(headTargetPosition));

        public string targetPositionProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(headTargetRotationEuler));
        /// <inheritdoc />
        bool IAnimationJobData.IsValid()
        {
            if (m_Root == null || m_Tip == null)
                return false;

            int count = 1;
            Transform tmp = m_Tip;
            while (tmp != null && tmp != m_Root)
            {
                tmp = tmp.parent;
                ++count;
            }

            return (tmp == m_Root && count > 2);
        }

        /// <inheritdoc />
        void IAnimationJobData.SetDefaultValues()
        {
            m_Root = null;
            m_Tip = null;

        }
    }
    [DisallowMultipleComponent]
    [AddComponentMenu("BasisChainIKConstraint")]
    [HelpURL("https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/manual/index.html")]
    public class BasisChainIKConstraint
        : RigConstraint<BasisChainIKConstraintJob,
                        BasisChainIKConstraintData,
                        BasisChainIKConstraintJobBinder<BasisChainIKConstraintData>>
    {
        protected override void OnValidate()
        {

        }
    }
}
