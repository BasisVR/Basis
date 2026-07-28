using Basis.Scripts.Networking.Sync;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using static Basis.Tests.Sync.BasisSyncTestSupport;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// The far side's final step: <see cref="ApplySyncTransformsJob"/> writes the interpolated pool values onto the
    /// bound transforms. Verifies the pos+rot / pos-only / rot-only / scale paths, world vs local space, the -1
    /// "unbound component untouched" rule, the Active==0 skip, and the composed channels (partial position axes
    /// holding live values, euler and quat-4-float rotation, uniform and partial scale) — driven through the real
    /// Burst job over a real TransformAccessArray.
    /// </summary>
    public class BasisSyncApplyTransformTests
    {
        [Test]
        public void ApplyTransforms_WritesEachBoundChannel_AndSkipsInactive()
        {
            var parent = new GameObject("apply-parent");
            parent.transform.SetPositionAndRotation(new Vector3(100f, 200f, 300f), Quaternion.Euler(0f, 40f, 0f));

            GameObject[] gos = null;
            var contOut = new NativeArray<float>(12, Allocator.TempJob);
            var rotOut = new NativeArray<quaternion>(3, Allocator.TempJob);
            var active = new NativeArray<byte>(5, Allocator.TempJob);
            var bindings = new NativeArray<BasisSyncApplyBinding>(5, Allocator.TempJob);
            TransformAccessArray taa = default;

            try
            {
                gos = new GameObject[5];
                for (int i = 0; i < 5; i++)
                {
                    gos[i] = new GameObject("apply-" + i);
                    gos[i].transform.SetParent(parent.transform, false);
                }
                // Pre-seed the inactive object so we can prove the job leaves it alone.
                gos[4].transform.localPosition = new Vector3(9f, 9f, 9f);

                // binding 0: pos+rot, local;  1: pos only, world;  2: rot only, local;  3: scale;  4: pos+rot, INACTIVE.
                contOut[0] = 1f; contOut[1] = 2f; contOut[2] = 3f;     // b0 pos
                contOut[3] = 5f; contOut[4] = 6f; contOut[5] = 7f;     // b1 pos
                contOut[6] = 2f; contOut[7] = 3f; contOut[8] = 4f;     // b3 scale
                contOut[9] = 0f; contOut[10] = 0f; contOut[11] = 0f;   // b4 pos (skipped)
                rotOut[0] = Quat(10f, 20f, 30f);
                rotOut[1] = Quat(45f, 0f, 0f);
                rotOut[2] = quaternion.identity;

                var b0 = BasisSyncApplyBinding.Empty;
                b0.Slot = 0; b0.PosX = 0; b0.PosY = 1; b0.PosZ = 2; b0.RotQuat = 0;
                var b1 = BasisSyncApplyBinding.Empty;
                b1.Slot = 1; b1.PosX = 3; b1.PosY = 4; b1.PosZ = 5; b1.World = 1;
                var b2 = BasisSyncApplyBinding.Empty;
                b2.Slot = 2; b2.RotQuat = 1;
                var b3 = BasisSyncApplyBinding.Empty;
                b3.Slot = 3; b3.ScaleX = 6; b3.ScaleY = 7; b3.ScaleZ = 8;
                var b4 = BasisSyncApplyBinding.Empty;
                b4.Slot = 4; b4.PosX = 9; b4.PosY = 10; b4.PosZ = 11; b4.RotQuat = 2;
                bindings[0] = b0; bindings[1] = b1; bindings[2] = b2; bindings[3] = b3; bindings[4] = b4;

                for (int i = 0; i < 5; i++) active[i] = 1;
                active[4] = 0;

                taa = new TransformAccessArray(5);
                for (int i = 0; i < 5; i++) taa.Add(gos[i].transform);

                new ApplySyncTransformsJob
                {
                    ContOut = contOut,
                    RotOut = rotOut,
                    Active = active,
                    Bindings = bindings,
                }.Schedule(taa).Complete();

                // b0: pos+rot in local space
                AssertVec(new Vector3(1f, 2f, 3f), gos[0].transform.localPosition, "b0 local position");
                Assert.LessOrEqual(Quaternion.Angle(gos[0].transform.localRotation, ToUnity(Quat(10f, 20f, 30f))), 0.01f, "b0 local rotation");

                // b1: world position only
                AssertVec(new Vector3(5f, 6f, 7f), gos[1].transform.position, "b1 world position");

                // b2: local rotation only
                Assert.LessOrEqual(Quaternion.Angle(gos[2].transform.localRotation, ToUnity(Quat(45f, 0f, 0f))), 0.01f, "b2 local rotation");

                // b3: scale only
                AssertVec(new Vector3(2f, 3f, 4f), gos[3].transform.localScale, "b3 scale");

                // b4: inactive — must be left exactly as pre-seeded
                AssertVec(new Vector3(9f, 9f, 9f), gos[4].transform.localPosition, "b4 inactive position untouched");
            }
            finally
            {
                if (taa.isCreated) taa.Dispose();
                contOut.Dispose(); rotOut.Dispose();
                active.Dispose(); bindings.Dispose();
                if (gos != null) foreach (GameObject go in gos) if (go != null) Object.DestroyImmediate(go);
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void ApplyTransforms_ComposesPartialAndAlternateChannels()
        {
            GameObject[] gos = null;
            var contOut = new NativeArray<float>(8, Allocator.TempJob);
            var rotOut = new NativeArray<quaternion>(1, Allocator.TempJob);
            var active = new NativeArray<byte>(5, Allocator.TempJob);
            var bindings = new NativeArray<BasisSyncApplyBinding>(5, Allocator.TempJob);
            TransformAccessArray taa = default;

            try
            {
                gos = new GameObject[5];
                for (int i = 0; i < 5; i++) gos[i] = new GameObject("compose-" + i);

                // binding 0: partial position (Y only) — X/Z hold the live local values, rotation untouched.
                gos[0].transform.localPosition = new Vector3(1f, 2f, 3f);
                gos[0].transform.localRotation = Quaternion.Euler(0f, 30f, 0f);
                contOut[0] = 5f;
                var b0 = BasisSyncApplyBinding.Empty;
                b0.Slot = 0; b0.PosY = 0;

                // binding 1: euler rotation (Y synced) composed against the held baseline.
                contOut[1] = 90f;
                var b1 = BasisSyncApplyBinding.Empty;
                b1.Slot = 1; b1.EulerY = 1; b1.HeldEuler = new float3(10f, 0f, 70f);

                // binding 2: quaternion streamed as 4 floats, unnormalized on purpose.
                Quaternion q = Quaternion.Euler(0f, 45f, 0f);
                contOut[2] = q.x * 2f; contOut[3] = q.y * 2f; contOut[4] = q.z * 2f; contOut[5] = q.w * 2f;
                var b2 = BasisSyncApplyBinding.Empty;
                b2.Slot = 2; b2.RotCont = 2;

                // binding 3: uniform scale from one float.
                contOut[6] = 2.5f;
                var b3 = BasisSyncApplyBinding.Empty;
                b3.Slot = 3; b3.ScaleUniform = 6;

                // binding 4: partial scale (Y only) — X/Z hold the live values.
                gos[4].transform.localScale = new Vector3(1f, 2f, 3f);
                contOut[7] = 4f;
                var b4 = BasisSyncApplyBinding.Empty;
                b4.Slot = 4; b4.ScaleY = 7;

                bindings[0] = b0; bindings[1] = b1; bindings[2] = b2; bindings[3] = b3; bindings[4] = b4;
                for (int i = 0; i < 5; i++) active[i] = 1;

                taa = new TransformAccessArray(5);
                for (int i = 0; i < 5; i++) taa.Add(gos[i].transform);

                new ApplySyncTransformsJob
                {
                    ContOut = contOut,
                    RotOut = rotOut,
                    Active = active,
                    Bindings = bindings,
                }.Schedule(taa).Complete();

                AssertVec(new Vector3(1f, 5f, 3f), gos[0].transform.localPosition, "b0 partial position holds live X/Z");
                Assert.LessOrEqual(Quaternion.Angle(gos[0].transform.localRotation, Quaternion.Euler(0f, 30f, 0f)), 0.01f, "b0 rotation untouched");

                Assert.LessOrEqual(Quaternion.Angle(gos[1].transform.localRotation, Quaternion.Euler(10f, 90f, 70f)), 0.01f, "b1 euler composition");

                Assert.LessOrEqual(Quaternion.Angle(gos[2].transform.localRotation, Quaternion.Euler(0f, 45f, 0f)), 0.01f, "b2 quat-4-float normalized");

                AssertVec(new Vector3(2.5f, 2.5f, 2.5f), gos[3].transform.localScale, "b3 uniform scale");

                AssertVec(new Vector3(1f, 4f, 3f), gos[4].transform.localScale, "b4 partial scale holds live X/Z");
            }
            finally
            {
                if (taa.isCreated) taa.Dispose();
                contOut.Dispose(); rotOut.Dispose();
                active.Dispose(); bindings.Dispose();
                if (gos != null) foreach (GameObject go in gos) if (go != null) Object.DestroyImmediate(go);
            }
        }

        static void AssertVec(Vector3 expected, Vector3 actual, string what)
        {
            Assert.LessOrEqual(Vector3.Distance(expected, actual), 1e-3f, $"{what}: expected {expected} got {actual}");
        }
    }
}
