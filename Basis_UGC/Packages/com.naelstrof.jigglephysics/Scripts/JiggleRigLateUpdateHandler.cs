using UnityEngine;

namespace JigglePhysics
{

    internal class JiggleRigLateUpdateHandler : JiggleRigHandler<JiggleRigLateUpdateHandler>
    {
        public Vector3 GlobalGravity;
        public void Start()
        {
            GlobalGravity = Physics.gravity;
        }
        public void LateUpdate()
        {

            //  CachedSphereCollider.DisableSphereCollider();
            var deltaTime = Time.deltaTime;
            var timeAsDouble = Time.timeAsDouble;
            var timeAsDoubleOneStepBack = timeAsDouble - JiggleRigBuilder.VERLET_TIME_STEP;
            //  if (!CachedSphereCollider.TryGet(out SphereCollider sphereCollider))
            // {
            //     throw new UnityException("Failed to create a sphere collider, this should never happen! Is a scene not loaded but a jiggle rig is?");
            // }
            for (int Index = 0; Index < JiggleRigCount; Index++)
            {
                try
                {
                    jiggleRigsArray[Index].Advance(deltaTime, GlobalGravity, timeAsDouble, timeAsDoubleOneStepBack);
                }
                catch
                {
                    Debug.LogError("Unable to continue for Jiggle Rig in JiggleRigLateUpdateHandler " + Index);
                }
            }
            //   CachedSphereCollider.DisableSphereCollider();
        }
    }
}
