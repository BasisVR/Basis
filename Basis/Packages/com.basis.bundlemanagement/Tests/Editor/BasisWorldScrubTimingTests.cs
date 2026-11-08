using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
public class BasisWorldScrubTimingTests
{
    private readonly List<Scene> scenes = new List<Scene>();
    private readonly List<BasisWorldSceneGate.Ticket> tickets = new List<BasisWorldSceneGate.Ticket>();
    private readonly List<Object> owned = new List<Object>();
    private BundledContentHolder previousHolder;
    private UnityAction<Scene, LoadSceneMode> probe;
    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true;
        previousHolder = BundledContentHolder.Instance;
        GameObject holderObject = new GameObject("world-scrub-holder");
        holderObject.hideFlags = HideFlags.HideAndDontSave;
        owned.Add(holderObject);
        BundledContentHolder holder = holderObject.AddComponent<BundledContentHolder>();
        ContentPoliceSelector world = ScriptableObject.CreateInstance<ContentPoliceSelector>();
        owned.Add(world);
        holder.WorldScriptSelector = world;
        BundledContentHolder.Instance = holder;
    }
    [TearDown]
    public void TearDown()
    {
        if (probe != null) SceneManager.sceneLoaded -= probe;
        probe = null;
        foreach (BasisWorldSceneGate.Ticket ticket in tickets) BasisWorldSceneGate.Release(ticket);
        tickets.Clear();
        foreach (Scene scene in scenes) if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        scenes.Clear();
        foreach (Object item in owned) if (item != null) Object.DestroyImmediate(item);
        owned.Clear();
        BundledContentHolder.Instance = previousHolder;
    }
    private Scene WorldWithScrollRect(out ScrollRect scrollRect, out GameObject target)
    {
        Scene scene = EditorSceneManager.NewPreviewScene();
        scenes.Add(scene);
        GameObject canvas = new GameObject("world-root");
        SceneManager.MoveGameObjectToScene(canvas, scene);
        target = new GameObject("switched-off-by-listener");
        target.transform.SetParent(canvas.transform);
        GameObject view = new GameObject("scroll", typeof(RectTransform));
        view.transform.SetParent(canvas.transform);
        scrollRect = view.AddComponent<ScrollRect>();
        UnityEventTools.AddBoolPersistentListener(scrollRect.onValueChanged, target.SetActive, false);
        return scene;
    }
    private BasisWorldSceneGate.Ticket Expect(Scene scene)
    {
        BasisWorldSceneGate.Ticket ticket = BasisWorldSceneGate.Expect(scene.path, new ChecksRequired { UseContentRemoval = true, ScrubPersistentUnityEvents = true }, BundledContentHolder.Selector.World);
        tickets.Add(ticket);
        return ticket;
    }
    private void Dispatch(Scene scene)
    {
        System.Reflection.FieldInfo field = typeof(SceneManager).GetField("sceneLoaded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(field, "SceneManager.sceneLoaded backing field not found");
        UnityAction<Scene, LoadSceneMode> handlers = field.GetValue(null) as UnityAction<Scene, LoadSceneMode>;
        Assert.IsNotNull(handlers);
        UnityAction<Scene, LoadSceneMode> gate = BasisWorldSceneGate.OnSceneLoaded;
        foreach (System.Delegate handler in handlers.GetInvocationList())
        {
            if (handler.Equals(gate) || handler.Equals(probe)) ((UnityAction<Scene, LoadSceneMode>)handler)(scene, LoadSceneMode.Additive);
        }
    }
    [Test]
    public void ScanRunsInsideSceneLoaded_BeforeAnyFrame()
    {
        LogAssert.ignoreFailingMessages = true;
        Scene scene = WorldWithScrollRect(out ScrollRect scrollRect, out GameObject target);
        BasisWorldSceneGate.Ticket ticket = Expect(scene);
        BasisWorldSceneGate.OnSceneLoaded(scene, LoadSceneMode.Additive);
        Assert.IsTrue(ticket.Claimed);
        Assert.IsTrue(ticket.Scanned);
        Assert.IsTrue(scrollRect == null, "unapproved ScrollRect survived the sceneLoaded scan");
        Assert.IsTrue(target.activeSelf);
    }
    [Test]
    public void ScanRunsAheadOfHandlersSubscribedEarlier()
    {
        LogAssert.ignoreFailingMessages = true;
        Scene scene = WorldWithScrollRect(out ScrollRect scrollRect, out _);
        bool probeRan = false, probeSawScrollRect = true;
        probe = (loaded, mode) =>
        {
            if (loaded != scene) return;
            probeRan = true;
            probeSawScrollRect = scrollRect != null;
        };
        SceneManager.sceneLoaded += probe;
        Expect(scene);
        Assert.IsTrue(BasisWorldSceneGate.IsFirstSceneLoadedHandler());
        Dispatch(scene);
        Assert.IsTrue(probeRan);
        Assert.IsFalse(probeSawScrollRect, "an earlier sceneLoaded handler saw the world before Content Police");
    }
    [Test]
    public void ConcurrentCopiesOfOneWorld_AreEachScanned()
    {
        LogAssert.ignoreFailingMessages = true;
        Scene first = WorldWithScrollRect(out ScrollRect firstRect, out _);
        Scene second = WorldWithScrollRect(out ScrollRect secondRect, out _);
        BasisWorldSceneGate.Ticket firstTicket = Expect(first);
        BasisWorldSceneGate.Ticket secondTicket = Expect(second);
        BasisWorldSceneGate.OnSceneLoaded(first, LoadSceneMode.Additive);
        BasisWorldSceneGate.OnSceneLoaded(second, LoadSceneMode.Additive);
        Assert.IsTrue(firstTicket.Scene == first);
        Assert.IsTrue(secondTicket.Scene == second);
        Assert.IsTrue(firstTicket.Scanned && secondTicket.Scanned);
        Assert.IsTrue(firstRect == null && secondRect == null);
        Assert.IsTrue(BasisWorldSceneGate.IsClaimed(first) && BasisWorldSceneGate.IsClaimed(second));
    }
    [Test]
    public void MissingWorldSelector_FailsClosed()
    {
        LogAssert.ignoreFailingMessages = true;
        BundledContentHolder.Instance.WorldScriptSelector = null;
        Scene scene = WorldWithScrollRect(out _, out _);
        BasisWorldSceneGate.Ticket ticket = Expect(scene);
        BasisWorldSceneGate.OnSceneLoaded(scene, LoadSceneMode.Additive);
        Assert.IsTrue(ticket.Claimed);
        Assert.IsFalse(ticket.Scanned);
        foreach (GameObject root in scene.GetRootGameObjects()) Assert.IsFalse(root.activeSelf, root.name + " left active without a scan");
    }
    [Test]
    public void MissingContentHolder_FailsClosed()
    {
        LogAssert.ignoreFailingMessages = true;
        BundledContentHolder.Instance = null;
        Scene scene = WorldWithScrollRect(out _, out _);
        BasisWorldSceneGate.Ticket ticket = Expect(scene);
        BasisWorldSceneGate.OnSceneLoaded(scene, LoadSceneMode.Additive);
        Assert.IsFalse(ticket.Scanned);
        foreach (GameObject root in scene.GetRootGameObjects()) Assert.IsFalse(root.activeSelf);
    }
    [Test]
    public void ReleasingLastTicket_Unhooks()
    {
        LogAssert.ignoreFailingMessages = true;
        Scene scene = WorldWithScrollRect(out _, out _);
        BasisWorldSceneGate.Ticket ticket = Expect(scene);
        Assert.IsTrue(BasisWorldSceneGate.IsFirstSceneLoadedHandler());
        BasisWorldSceneGate.Release(ticket);
        tickets.Remove(ticket);
        Assert.AreEqual(0, BasisWorldSceneGate.PendingCount);
        Assert.IsFalse(BasisWorldSceneGate.IsFirstSceneLoadedHandler());
    }
}
