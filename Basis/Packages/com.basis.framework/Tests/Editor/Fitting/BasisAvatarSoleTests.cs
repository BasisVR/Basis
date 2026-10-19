using System.Collections.Generic;
using Basis.Scripts.Avatar;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.Fitting
{
    public class BasisAvatarSoleTests
    {
        GameObject root;
        Transform shin, foot;
        readonly List<Mesh> meshes = new List<Mesh>();
        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
            }
            foreach (Mesh mesh in meshes)
            {
                Object.DestroyImmediate(mesh);
            }
            meshes.Clear();
        }
        Mesh NewMesh()
        {
            var mesh = new Mesh();
            meshes.Add(mesh);
            return mesh;
        }
        SkinnedMeshRenderer Build(float soleY, float rootScale = 1f, float shinVertexY = 0.3f, float heelShapeDrop = 0f)
        {
            root = new GameObject("SoleRoot");
            root.transform.localScale = Vector3.one * rootScale;
            shin = new GameObject("Shin").transform;
            shin.SetParent(root.transform, false);
            shin.localPosition = new Vector3(0f, 0.5f, 0f);
            foot = new GameObject("Foot").transform;
            foot.SetParent(shin, false);
            foot.localPosition = new Vector3(0f, -0.42f, 0f);
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            var renderer = body.AddComponent<SkinnedMeshRenderer>();
            Mesh mesh = NewMesh();
            mesh.vertices = new[] { new Vector3(-0.04f, soleY, -0.05f), new Vector3(0.04f, soleY, 0.12f), new Vector3(0f, soleY + 0.06f, 0f), new Vector3(0f, shinVertexY, 0f) };
            mesh.boneWeights = new[] { new BoneWeight { boneIndex0 = 1, weight0 = 1f }, new BoneWeight { boneIndex0 = 1, weight0 = 0.6f, boneIndex1 = 0, weight1 = 0.4f }, new BoneWeight { boneIndex0 = 1, weight0 = 1f }, new BoneWeight { boneIndex0 = 0, weight0 = 1f } };
            mesh.triangles = new[] { 0, 1, 2, 1, 2, 3 };
            mesh.bindposes = new[] { shin.worldToLocalMatrix * body.transform.localToWorldMatrix, foot.worldToLocalMatrix * body.transform.localToWorldMatrix };
            if (heelShapeDrop != 0f)
            {
                var delta = new Vector3[4];
                delta[0] = Vector3.down * heelShapeDrop / rootScale;
                mesh.AddBlendShapeFrame("Heel", 100f, delta, null, null);
            }
            renderer.bones = new[] { shin, foot };
            renderer.rootBone = shin;
            renderer.sharedMesh = mesh;
            if (heelShapeDrop != 0f)
            {
                renderer.SetBlendShapeWeight(0, 100f);
            }
            return renderer;
        }
        [Test]
        public void FindsTheSolesWhereverTheCreatorPutThem([Values(-0.004f, 0f, 0.02f)] float soleY)
        {
            Build(soleY);
            Assert.That(BasisAvatarSole.TryMeasure(root.transform, foot, null, 1f, 1.6f, out float sole), Is.True);
            Assert.That(sole, Is.EqualTo(soleY).Within(1e-5f));
        }
        [Test]
        public void ReportsTheSoleInTheUnscaledAvatarFrame()
        {
            Build(-0.01f, 0.5f);
            Assert.That(BasisAvatarSole.TryMeasure(root.transform, foot, null, 0.5f, 1.6f, out float sole), Is.True);
            Assert.That(sole, Is.EqualTo(-0.01f).Within(1e-5f));
        }
        [Test]
        public void TheSoleFollowsTheFootBoneAsTheTposeMovesIt()
        {
            Build(0f);
            foot.position += new Vector3(0f, -0.004f, 0f);
            foot.rotation = Quaternion.Euler(10f, 0f, 0f) * foot.rotation;
            Assert.That(BasisAvatarSole.TryMeasure(root.transform, foot, null, 1f, 1.6f, out float sole), Is.True);
            Vector3 footBind = new Vector3(0f, 0.08f, 0f);
            float Rigid(Vector3 vertex) => (foot.position + foot.rotation * (vertex - footBind)).y;
            float expected = Mathf.Min(Rigid(new Vector3(-0.04f, 0f, -0.05f)), 0.6f * Rigid(new Vector3(0.04f, 0f, 0.12f)));
            Assert.That(sole, Is.LessThan(-0.004f), "rotating the foot toes-down after binding has to carry its sole with it");
            Assert.That(sole, Is.EqualTo(expected).Within(1e-4f));
        }
        [Test]
        public void IgnoresGeometryThatDoesNotFollowTheFeet()
        {
            Build(0.01f, 1f, -0.3f);
            Assert.That(BasisAvatarSole.TryMeasure(root.transform, foot, null, 1f, 1.6f, out float sole), Is.True);
            Assert.That(sole, Is.EqualTo(0.01f).Within(1e-5f));
        }
        [Test]
        public void CountsHeelShapeKeysThatAreSwitchedOn([Values(1f, 0.5f)] float rootScale)
        {
            Build(0.01f, rootScale, 0.3f, 0.02f * rootScale);
            Assert.That(BasisAvatarSole.TryMeasure(root.transform, foot, null, rootScale, 1.6f, out float sole), Is.True);
            Assert.That(sole, Is.EqualTo(-0.01f).Within(1e-4f), "a 2 cm heel shape key lowers the sole by 2 cm");
        }
        [Test]
        public void CountsRigidShoesParentedToTheFoot()
        {
            Build(0.01f);
            var shoe = new GameObject("Shoe");
            shoe.transform.SetParent(foot, false);
            shoe.transform.position = new Vector3(0f, 0.03f, 0f);
            Mesh mesh = NewMesh();
            mesh.vertices = new[] { new Vector3(-0.05f, -0.025f, -0.05f), new Vector3(0.05f, -0.025f, 0.15f), new Vector3(0f, 0.025f, 0f) };
            mesh.triangles = new[] { 0, 1, 2 };
            shoe.AddComponent<MeshFilter>().sharedMesh = mesh;
            shoe.AddComponent<MeshRenderer>();
            Assert.That(BasisAvatarSole.TryMeasure(root.transform, foot, null, 1f, 1.6f, out float sole), Is.True);
            Assert.That(sole, Is.EqualTo(0.005f).Within(1e-5f));
        }
        [Test]
        public void RejectsSolesThatCannotBeRight([Values(-0.3f, 0.12f)] float soleY)
        {
            Build(soleY);
            Assert.That(BasisAvatarSole.TryMeasure(root.transform, foot, null, 1f, 1.6f, out float sole), Is.False);
            Assert.That(sole, Is.EqualTo(0f));
        }
        [Test]
        public void FallsBackWhenTheMeshIsNotReadable()
        {
            SkinnedMeshRenderer renderer = Build(-0.004f);
            renderer.sharedMesh.UploadMeshData(true);
            Assert.That(BasisAvatarSole.TryMeasure(root.transform, foot, null, 1f, 1.6f, out _), Is.False);
        }
    }
}
