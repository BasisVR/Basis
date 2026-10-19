using System.Collections.Generic;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using UnityEngine;

public class BasisAvatarShadowCloneTests
{
	private GameObject root;
	private Transform hips;
	private Transform head;
	private Transform hair;
	private readonly List<Mesh> meshes = new();

	[SetUp]
	public void SetUp()
	{
		root = new GameObject("Avatar");
		hips = new GameObject("Hips").transform;
		hips.SetParent(root.transform);
		head = new GameObject("Head").transform;
		head.SetParent(hips);
		hair = new GameObject("Hair").transform;
		hair.SetParent(head);
	}

	[TearDown]
	public void TearDown()
	{
		BasisAvatarDriver.RemoveOldShadowClones();
		Object.DestroyImmediate(root);
		for (int i = 0; i < meshes.Count; i++)
		{
			Object.DestroyImmediate(meshes[i]);
		}
		meshes.Clear();
	}

	[Test]
	public void RendererWeightedToHeadIsSelected()
	{
		SkinnedMeshRenderer renderer = CreateRenderer(
			"Combined",
			new[] { hips, head },
			BoneWeightFor(1)
		);

		Assert.IsTrue(BasisAvatarDriver.IsRendererInfluencedByHead(renderer, head));
	}

	[Test]
	public void RendererWeightedToHeadDescendantIsSelected()
	{
		SkinnedMeshRenderer renderer = CreateRenderer(
			"HairMesh",
			new[] { hips, hair },
			BoneWeightFor(1)
		);

		Assert.IsTrue(BasisAvatarDriver.IsRendererInfluencedByHead(renderer, head));
	}

	[Test]
	public void FullArmatureRendererWithoutHeadWeightsIsNotSelected()
	{
		SkinnedMeshRenderer renderer = CreateRenderer(
			"BodyOnly",
			new[] { hips, head, hair },
			BoneWeightFor(0)
		);

		Assert.IsFalse(BasisAvatarDriver.IsRendererInfluencedByHead(renderer, head));
	}

	[Test]
	public void RendererParentedBelowHeadIsSelectedWithoutSkinWeights()
	{
		GameObject meshObject = new GameObject("RigidHeadAttachment");
		meshObject.transform.SetParent(head);
		SkinnedMeshRenderer renderer = meshObject.AddComponent<SkinnedMeshRenderer>();

		Assert.IsTrue(BasisAvatarDriver.IsRendererInfluencedByHead(renderer, head));
	}

	private SkinnedMeshRenderer CreateRenderer(
		string name,
		Transform[] bones,
		BoneWeight weight
	)
	{
		GameObject meshObject = new GameObject(name);
		meshObject.transform.SetParent(root.transform);
		SkinnedMeshRenderer renderer = meshObject.AddComponent<SkinnedMeshRenderer>();
		Mesh mesh = new Mesh { name = name + "Mesh" };
		meshes.Add(mesh);
		mesh.vertices = new[] { Vector3.zero };
		mesh.boneWeights = new[] { weight };
		mesh.bindposes = new Matrix4x4[bones.Length];
		renderer.sharedMesh = mesh;
		renderer.bones = bones;
		renderer.rootBone = hips;
		return renderer;
	}

	private static BoneWeight BoneWeightFor(int boneIndex)
	{
		return new BoneWeight { boneIndex0 = boneIndex, weight0 = 1f };
	}
}
