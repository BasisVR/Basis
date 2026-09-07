using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A remote's OpenLipSync context is pooled: it is disposed once they go quiet or leave viseme
/// range, and a brand new instance is allocated when they speak again. Anything reading
/// openLipSyncContext has to re-read it every tick — the HVR comms bridge cached it and its
/// LastApplied array instead, which froze remote Voice Gain and visemes at 0 after the speaker's
/// first pause. These lock the release side of that contract; HVRBuiltInAddressPublisherTests
/// locks the reader side.
/// </summary>
public class BasisVisemeContextLifecycleTests
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
            DisplayName = "viseme-lifecycle-test",
            UUID = System.Guid.NewGuid().ToString("N"),
            MouthTransform = mouth.transform,
            BasisAvatar = avatar,
            FaceIsVisible = true,
        };
    }

    private AudioSource MakeAudioSource()
    {
        GameObject go = new GameObject("SpatialSource");
        _spawned.Add(go);
        return go.AddComponent<AudioSource>();
    }

    /// The pool is only reachable through BasisOpenLipSyncDriver, which needs the ONNX backend, so
    /// the context is planted directly. Only its lifetime is under test here, not inference.
    private BasisAudioAndVisemeDriver DriverHoldingAContext(out BasisOpenLipSyncContext planted)
    {
        BasisAvatar avatar = BuildAvatar("VisemeLifecycleAvatar");
        BasisRemotePlayer remote = BuildPlayer(avatar);

        BasisAudioAndVisemeDriver driver = new BasisAudioAndVisemeDriver();
        Assert.IsTrue(driver.TryInitialize(remote), "the fixture avatar has to wire up before the lifetime is meaningful");

        planted = new BasisOpenLipSyncContext();
        driver.openLipSyncContext = planted;
        driver.UseOpenLipSync = true;
        driver.InVisemeRange = true;
        driver.FaceVisible = true;
        return driver;
    }

    [Test]
    public void IdleSpatialSourceReleasesTheContextTheCommsBridgeReads()
    {
        BasisAudioAndVisemeDriver driver = DriverHoldingAContext(out BasisOpenLipSyncContext planted);
        driver.TrackedAudioSource = MakeAudioSource();
        driver.AudioSourceInactive = true;

        driver.Simulate(0.016f);

        Assert.IsNull(driver.openLipSyncContext, "three seconds of silence disables the source and hands the slot back");
        Assert.IsFalse(driver.UseOpenLipSync);
        Assert.IsNotNull(planted, "the released instance survives as a managed object, which is what made caching it look safe");
    }

    [Test]
    public void LeavingVisemeRangeReleasesTheContext()
    {
        BasisAudioAndVisemeDriver driver = DriverHoldingAContext(out _);
        driver.InVisemeRange = false;

        driver.Simulate(0.016f);

        Assert.IsNull(driver.openLipSyncContext, "a distant player's slot goes to somebody closer");
    }

    [Test]
    public void AnnouncingPlayerKeepsTheContextWhileTheSpatialSourceIsIdle()
    {
        BasisAudioAndVisemeDriver driver = DriverHoldingAContext(out BasisOpenLipSyncContext planted);
        driver.TrackedAudioSource = MakeAudioSource();
        driver.AudioSourceInactive = true;
        driver.AnnounceActive = true;

        driver.Simulate(0.016f);

        Assert.AreSame(planted, driver.openLipSyncContext, "an announcer's spatial source is idle for their whole sentence");
    }

    [Test]
    public void ActiveSpeakerKeepsTheContextAcrossTicks()
    {
        BasisAudioAndVisemeDriver driver = DriverHoldingAContext(out BasisOpenLipSyncContext planted);
        driver.TrackedAudioSource = MakeAudioSource();
        driver.AudioSourceInactive = false;

        driver.Simulate(0.016f);
        driver.Simulate(0.016f);

        Assert.AreSame(planted, driver.openLipSyncContext, "the context must be stable while somebody is actually talking");
    }

    [Test]
    public void TeardownReleasesTheContext()
    {
        BasisAudioAndVisemeDriver driver = DriverHoldingAContext(out _);

        driver.OnDestroy();

        Assert.IsNull(driver.openLipSyncContext);
    }

    [Test]
    public void ReleasedContextIsNotReusedWhenTheSpeakerReturns()
    {
        BasisAudioAndVisemeDriver driver = DriverHoldingAContext(out BasisOpenLipSyncContext first);
        driver.TrackedAudioSource = MakeAudioSource();
        driver.AudioSourceInactive = true;
        driver.Simulate(0.016f);
        Assert.IsNull(driver.openLipSyncContext);

        BasisOpenLipSyncContext second = new BasisOpenLipSyncContext();
        driver.openLipSyncContext = second;
        driver.UseOpenLipSync = true;

        Assert.AreNotSame(first, second, "the reacquired context is a different instance with a different LastApplied array");
        Assert.AreNotSame(first.LastApplied, driver.openLipSyncContext.LastApplied);
    }
}
