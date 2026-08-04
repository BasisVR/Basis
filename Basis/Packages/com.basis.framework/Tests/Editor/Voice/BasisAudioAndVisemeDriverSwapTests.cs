using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Regression for visemes dying after an avatar swap: the driver subscribes TryShutdown to the
/// face renderer's DestroyCalled, and the OUTGOING avatar's BasisMeshRendererCheck stays alive
/// until its end-of-frame destroy — which lands AFTER the incoming avatar has re-initialized the
/// shared per-player driver. The stale notification cleared WasSuccessful, and ProcessAudioSamples
/// gates on it, so the new avatar's mouth never moved again until the next avatar change.
/// </summary>
public class BasisAudioAndVisemeDriverSwapTests
{
    private const int VisemeCount = BasisVisemeDriveConfig.VisemeCount;

    private readonly List<GameObject> _spawned = new List<GameObject>();
    private readonly List<Mesh> _meshes = new List<Mesh>();

    [TearDown]
    public void TearDown()
    {
        for (int Index = 0; Index < _spawned.Count; Index++)
        {
            if (_spawned[Index] != null)
            {
                Object.DestroyImmediate(_spawned[Index]);
            }
        }
        for (int Index = 0; Index < _meshes.Count; Index++)
        {
            if (_meshes[Index] != null)
            {
                Object.DestroyImmediate(_meshes[Index]);
            }
        }
        _spawned.Clear();
        _meshes.Clear();
    }

    private BasisAvatar BuildAvatar(string name)
    {
        GameObject root = new GameObject(name);
        _spawned.Add(root);

        Mesh mesh = new Mesh();
        _meshes.Add(mesh);
        mesh.vertices = new Vector3[] { Vector3.zero, Vector3.right, Vector3.up };
        mesh.triangles = new int[] { 0, 1, 2 };
        Vector3[] delta = new Vector3[] { Vector3.up, Vector3.up, Vector3.up };
        for (int Index = 0; Index < VisemeCount; Index++)
        {
            mesh.AddBlendShapeFrame($"viseme{Index}", 100f, delta, null, null);
        }

        SkinnedMeshRenderer renderer = root.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;

        BasisAvatar avatar = root.AddComponent<BasisAvatar>();
        avatar.FaceVisemeMesh = renderer;
        avatar.FaceVisemeMovement = new int[VisemeCount];
        for (int Index = 0; Index < VisemeCount; Index++)
        {
            avatar.FaceVisemeMovement[Index] = Index;
        }
        return avatar;
    }

    private BasisRemotePlayer BuildPlayer(BasisAvatar avatar)
    {
        GameObject mouth = new GameObject("Mouth");
        _spawned.Add(mouth);

        return new BasisRemotePlayer
        {
            DisplayName = "viseme-swap-test",
            UUID = System.Guid.NewGuid().ToString("N"),
            MouthTransform = mouth.transform,
            BasisAvatar = avatar,
            FaceIsVisible = true,
            FaceRenderer = avatar.gameObject.AddComponent<BasisMeshRendererCheck>(),
        };
    }

    [Test]
    public void OutgoingRendererDestroy_DoesNotShutDownTheIncomingAvatar()
    {
        BasisAvatar first = BuildAvatar("VisemeAvatarA");
        BasisRemotePlayer remote = BuildPlayer(first);
        BasisMeshRendererCheck outgoing = remote.FaceRenderer;

        BasisAudioAndVisemeDriver driver = new BasisAudioAndVisemeDriver();
        Assert.IsTrue(driver.TryInitialize(remote), "the first avatar wires up");

        BasisAvatar second = BuildAvatar("VisemeAvatarB");
        remote.BasisAvatar = second;
        remote.FaceRenderer = second.gameObject.AddComponent<BasisMeshRendererCheck>();
        Assert.IsTrue(driver.TryInitialize(remote), "the swap re-wires onto the incoming avatar");

        Assert.IsNull(outgoing.DestroyCalled, "the outgoing renderer must be unsubscribed at swap time");

        outgoing.DestroyCalled?.Invoke();
        outgoing.Check?.Invoke(false);

        Assert.IsTrue(driver.WasSuccessful, "the outgoing avatar's destroy must not shut down the new avatar's visemes");
        Assert.IsTrue(driver.FaceVisible, "the outgoing avatar's final OnBecameInvisible must not mute the new avatar");
    }

    [Test]
    public void CurrentRendererDestroy_StillShutsDown()
    {
        BasisAvatar avatar = BuildAvatar("VisemeAvatarOnly");
        BasisRemotePlayer remote = BuildPlayer(avatar);

        BasisAudioAndVisemeDriver driver = new BasisAudioAndVisemeDriver();
        Assert.IsTrue(driver.TryInitialize(remote));

        remote.FaceRenderer.DestroyCalled?.Invoke();

        Assert.IsFalse(driver.WasSuccessful, "destroying the renderer actually in use must still shut the driver down");
    }

    [Test]
    public void ReInitializeOnTheSameRenderer_DoesNotStackSubscriptions()
    {
        BasisAvatar avatar = BuildAvatar("VisemeAvatarRepeat");
        BasisRemotePlayer remote = BuildPlayer(avatar);

        BasisAudioAndVisemeDriver driver = new BasisAudioAndVisemeDriver();
        driver.TryInitialize(remote);
        driver.TryInitialize(remote);
        driver.TryInitialize(remote);

        Assert.AreEqual(1, remote.FaceRenderer.Check.GetInvocationList().Length);
        Assert.AreEqual(1, remote.FaceRenderer.DestroyCalled.GetInvocationList().Length);

        driver.OnDeInitialize();
        Assert.IsNull(remote.FaceRenderer.Check, "teardown unsubscribes the tracked check");
        Assert.IsNull(remote.FaceRenderer.DestroyCalled, "teardown unsubscribes the tracked destroy hook");
    }
}
