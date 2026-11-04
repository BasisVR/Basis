using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using OpenLipSync.Inference;
using OpenLipSync.Inference.OVRCompat;

[NoAutoStaticsCleanup]
public class BasisOpenLipSyncReleaseTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly FieldInfo BackendF = typeof(BasisOpenLipSyncDriver).GetField("_backend", PrivateStatic);
    private static readonly FieldInfo InitializedF = typeof(BasisOpenLipSyncDriver).GetField("_initialized", PrivateStatic);
    private static readonly FieldInfo PendingF = typeof(BasisOpenLipSyncDriver).GetField("_releasePending", PrivateStatic);
    private static readonly FieldInfo InFlightF = typeof(BasisOpenLipSyncDriver).GetField("_inferenceInFlight", PrivateStatic);
    private static readonly FieldInfo PoolF = typeof(BasisOpenLipSyncDriver).GetField("_contextPool", PrivateStatic);
    private static readonly FieldInfo DisposedF = typeof(OpenLipSyncBackend).GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance);
    private bool useSlotLimit;
    private int maxSlots;

    [SetUp]
    public void SetUp()
    {
        Assert.IsNotNull(BackendF, "BasisOpenLipSyncDriver._backend moved");
        Assert.IsNotNull(InitializedF, "BasisOpenLipSyncDriver._initialized moved");
        Assert.IsNotNull(PendingF, "BasisOpenLipSyncDriver._releasePending moved");
        Assert.IsNotNull(InFlightF, "BasisOpenLipSyncDriver._inferenceInFlight moved");
        Assert.IsNotNull(PoolF, "BasisOpenLipSyncDriver._contextPool moved");
        Assert.IsNotNull(DisposedF, "OpenLipSyncBackend._disposed moved");
        if (BackendF.GetValue(null) != null) Assert.Ignore("A live lip sync backend is loaded; these checks only run against an idle driver.");
        BasisOpenLipSyncContext.StopWorker();
        BasisOpenLipSyncDriver.ProcessFrameOverride = null;
        Assert.AreEqual(0, (int)InFlightF.GetValue(null), "inference is still running from another test");
        useSlotLimit = BasisOpenLipSyncDriver.UseSlotLimit;
        maxSlots = BasisOpenLipSyncDriver.MaxSlots;
    }

    [TearDown]
    public void TearDown()
    {
        BasisOpenLipSyncDriver.Shutdown();
        ((Stack<uint>)PoolF.GetValue(null)).Clear();
        BasisOpenLipSyncDriver.UseSlotLimit = useSlotLimit;
        BasisOpenLipSyncDriver.MaxSlots = maxSlots;
    }

    private static OpenLipSyncBackend Install()
    {
        OpenLipSyncBackend backend = new OpenLipSyncBackend();
        BackendF.SetValue(null, backend);
        InitializedF.SetValue(null, true);
        return backend;
    }

    private static bool Disposed(OpenLipSyncBackend backend) => (bool)DisposedF.GetValue(backend);

    private static bool Pending() => (bool)PendingF.GetValue(null);

    [Test]
    public void ShutdownDuringABatchFreesTheBackendWhenTheBatchEnds()
    {
        OpenLipSyncBackend backend = Install();
        BasisOpenLipSyncDriver.EnterInference();
        try
        {
            BasisOpenLipSyncDriver.Shutdown();
            Assert.IsFalse(BasisOpenLipSyncDriver.IsInitialized);
            Assert.IsFalse(Disposed(backend), "the backend was freed under a running batch");
            Assert.IsTrue(Pending());
        }
        finally
        {
            BasisOpenLipSyncDriver.ExitInference();
        }
        Assert.IsTrue(Disposed(backend), "the backend is freed as soon as the batch ends");
        Assert.IsFalse(Pending());
    }

    [Test]
    public void ShutdownWhileIdleFreesAtOnce()
    {
        OpenLipSyncBackend backend = Install();
        BasisOpenLipSyncDriver.Shutdown();
        Assert.IsTrue(Disposed(backend));
        Assert.IsFalse(Pending());
    }

    [Test]
    public void NoInferenceStartsWhileATrimIsPending()
    {
        Install();
        ((Stack<uint>)PoolF.GetValue(null)).Push(7);
        BasisOpenLipSyncDriver.UseSlotLimit = true;
        BasisOpenLipSyncDriver.MaxSlots = 0;
        BasisOpenLipSyncDriver.EnterInference();
        try
        {
            BasisOpenLipSyncDriver.EnforceSlotLimit();
            Assert.IsTrue(Pending(), "the trim waits for the running batch");
            Assert.AreEqual(0, ((Stack<uint>)PoolF.GetValue(null)).Count, "the trimmed context can no longer be handed out");
            Assert.AreEqual(Result.Unknown, BasisOpenLipSyncDriver.ProcessFrame(7, new float[160], 160, new Frame()));
            Assert.AreEqual(string.Empty, BasisOpenLipSyncDriver.DebugBackendError, "inference reached the backend while a free was pending");
        }
        finally
        {
            BasisOpenLipSyncDriver.ExitInference();
        }
        Assert.IsFalse(Pending(), "the trim lands as soon as the batch ends");
    }
}
