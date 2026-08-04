using Basis.Scripts.BasisSdk;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using OpenLipSync.Inference;
using OpenLipSync.Inference.OVRCompat;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Measures how far behind the audio the lip-sync actually lands, in the two places the delay
/// is created: the temporal smoother inside the model front-end, and the
/// Simulate -> background inference -> Apply hand-off in <see cref="BasisOpenLipSyncContext"/>.
///
/// Both are measured rather than asserted-by-eye — the numbers are written to
/// <c>Temp/viseme_latency.txt</c> so a change here can be compared against a previous run.
/// </summary>
public class BasisVisemeLatencyTests
{
    private const int VisemeCount = BasisVisemeDriveConfig.VisemeCount;
    private const int TagViseme = 10;
    private const float FrameSeconds = 1f / 90f;
    private const int OutputSampleRate = 48000;

    private readonly List<GameObject> _spawned = new List<GameObject>();
    private static readonly StringBuilder Report = new StringBuilder();

    // ────────────────────────────────────────────────────────────────
    //  Fixture
    // ────────────────────────────────────────────────────────────────

    [TearDown]
    public void TearDown()
    {
        DrainBatchPipeline();
        BasisOpenLipSyncDriver.ProcessFrameOverride = null;

        for (int Index = 0; Index < _spawned.Count; Index++)
        {
            if (_spawned[Index] != null)
            {
                UnityEngine.Object.DestroyImmediate(_spawned[Index]);
            }
        }
        _spawned.Clear();
    }

    [OneTimeTearDown]
    public void WriteReport()
    {
        if (Report.Length == 0) return;
        try
        {
            // Batchmode does not run from the project root, so anchor on the project itself.
            string temp = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Temp");
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(temp, "viseme_latency.txt"), Report.ToString());
        }
        catch (IOException)
        {
            // Reporting is a convenience; never fail a run over it.
        }
    }

    private static void Record(string line)
    {
        Report.AppendLine(line);
        Debug.Log("[VisemeLatency] " + line);
    }

    /// <summary>
    /// Lets any in-flight batch task finish and empties the shared pending list, so static
    /// state cannot leak from one test into the next.
    /// </summary>
    private static void DrainBatchPipeline()
    {
        BasisOpenLipSyncDriver.ProcessFrameOverride = (handle, audio, count, frame) => Result.Success;

        for (int attempt = 0; attempt < 200; attempt++)
        {
            BasisOpenLipSyncContext.ProcessAllPending();
            if (!BasisOpenLipSyncContext.DebugBatchRunning && BasisOpenLipSyncContext.DebugPendingCount == 0)
            {
                return;
            }
            Thread.Sleep(2);
        }
    }

    private static void WaitForBatchIdle()
    {
        for (int attempt = 0; attempt < 2500 && BasisOpenLipSyncContext.DebugBatchRunning; attempt++)
        {
            Thread.Sleep(2);
        }
        Assert.IsFalse(BasisOpenLipSyncContext.DebugBatchRunning, "Batch inference did not complete within 5s.");
    }

    private BasisAvatar BuildAvatar()
    {
        GameObject root = new GameObject("VisemeLatencyAvatar");
        _spawned.Add(root);

        Mesh mesh = new Mesh();
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

    /// <summary>
    /// One audio callback's worth of stereo PCM, every sample stamped with the same tag so the
    /// inference stub can report which frame's audio it was handed.
    /// </summary>
    private static void FeedTaggedAudio(BasisOpenLipSyncContext context, float tag, int monoSamples)
    {
        float[] data = new float[monoSamples * 2];
        for (int Index = 0; Index < data.Length; Index++)
        {
            data[Index] = tag;
        }
        context.ProcessAudioSamples(data, 2, data.Length);
    }

    // ────────────────────────────────────────────────────────────────
    //  Pipeline latency: audio in -> blendshape weight out
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stands in for the ONNX session. Blocks until the test grants a permit, so the test
    /// decides exactly where in the frame inference "finishes" — the interesting case being
    /// "after Apply has already run", which is what happens on a loaded machine.
    /// </summary>
    private sealed class GatedInference : IDisposable
    {
        private readonly SemaphoreSlim _permits = new SemaphoreSlim(0);

        /// <summary>Newest tag present in each chunk handed to inference, in call order.</summary>
        public readonly List<float> ChunkNewestTag = new List<float>();

        /// <summary>Every distinct tag seen across all chunks, in the order first seen.</summary>
        public readonly List<float> TagsSeen = new List<float>();

        public int Calls { get; private set; }

        public Result Run(uint handle, float[] audio, int count, Frame frame)
        {
            if (!_permits.Wait(5000)) return Result.Unknown;

            float newest = count > 0 ? audio[count - 1] : 0f;
            lock (ChunkNewestTag)
            {
                Calls++;
                ChunkNewestTag.Add(newest);
                float previous = float.NaN;
                for (int Index = 0; Index < count; Index++)
                {
                    float value = audio[Index];
                    if (value == previous) continue;
                    previous = value;
                    if (!TagsSeen.Contains(value)) TagsSeen.Add(value);
                }
            }

            Array.Clear(frame.Visemes, 0, frame.Visemes.Length);
            frame.Visemes[TagViseme] = newest;
            return Result.Success;
        }

        public void Release(int permits = 1) => _permits.Release(permits);

        public void Dispose() => _permits.Dispose();
    }

    /// <summary>
    /// Runs a frame loop where inference always completes AFTER that frame's Apply — the
    /// realistic case once a few people are talking — and reports, per frame, which audio tag
    /// the mesh is showing.
    /// </summary>
    private float[] RunFrameLoop(BasisOpenLipSyncContext context, BasisAvatar avatar, GatedInference stub, int frames)
    {
        int samplesPerFrame = Mathf.RoundToInt(OutputSampleRate * FrameSeconds);
        float[] shownTagPerFrame = new float[frames];

        for (int frame = 0; frame < frames; frame++)
        {
            // Whatever inference was dispatched last frame is allowed to land now, before this
            // frame's Simulate — i.e. it took about one frame, and missed its own Apply.
            if (frame > 0)
            {
                stub.Release();
                WaitForBatchIdle();
            }

            FeedTaggedAudio(context, TagFor(frame), samplesPerFrame);
            context.Simulate(FrameSeconds);
            BasisOpenLipSyncContext.ProcessAllPending();
            context.Apply(FrameSeconds);

            shownTagPerFrame[frame] = avatar.FaceVisemeMesh.GetBlendShapeWeight(TagViseme) / 100f;
        }

        stub.Release(4);
        WaitForBatchIdle();
        return shownTagPerFrame;
    }

    // Tags stay well inside [0,1] so they survive the probability clamp on the way to a weight,
    // and are spaced far enough apart to be told apart through the 0.25 write epsilon.
    private static float TagFor(int frame) => 0.05f + 0.01f * frame;

    [Test]
    public void EveryAudioFrameReachesInferenceInsteadOfEveryOther()
    {
        BasisAvatar avatar = BuildAvatar();
        BasisOpenLipSyncContext context = new BasisOpenLipSyncContext();
        context.Initialize(avatar, 1);

        using GatedInference stub = new GatedInference();
        BasisOpenLipSyncDriver.ProcessFrameOverride = stub.Run;

        const int Frames = 12;
        RunFrameLoop(context, avatar, stub, Frames);

        lock (stub.ChunkNewestTag)
        {
            Record($"inference calls for {Frames} audio frames: {stub.Calls}");
            Record($"distinct audio tags that reached inference: {stub.TagsSeen.Count}/{Frames}");

            // The old hand-off refused to queue new audio until Apply had consumed the previous
            // result, so inference ran on every SECOND frame and every second chunk carried two
            // frames of audio merged together. One call per frame is the whole point.
            Assert.GreaterOrEqual(stub.Calls, Frames - 1,
                $"Inference ran {stub.Calls} times for {Frames} audio frames — the pipeline is still skipping frames.");

            Assert.AreEqual(Frames, stub.TagsSeen.Count,
                "Some frames of audio never reached the model at all.");
        }

        context.Dispose();
    }

    [Test]
    public void VisemesAdvanceOnEveryFrameRatherThanEveryOther()
    {
        BasisAvatar avatar = BuildAvatar();
        BasisOpenLipSyncContext context = new BasisOpenLipSyncContext();
        context.Initialize(avatar, 2);

        using GatedInference stub = new GatedInference();
        BasisOpenLipSyncDriver.ProcessFrameOverride = stub.Run;

        const int Frames = 12;
        float[] shown = RunFrameLoop(context, avatar, stub, Frames);

        // Count frames where the mouth changed at all. Steady state should be "every frame".
        int changes = 0;
        for (int frame = 1; frame < Frames; frame++)
        {
            if (Mathf.Abs(shown[frame] - shown[frame - 1]) > 1e-4f) changes++;
        }

        Record($"frames where the mouth advanced: {changes}/{Frames - 1}");
        Assert.GreaterOrEqual(changes, Frames - 3,
            $"The mouth only advanced on {changes} of {Frames - 1} frames — it is running at a fraction of the frame rate.");

        context.Dispose();
    }

    [Test]
    public void PipelineLatencyIsOneFrameWhenInferenceMissesItsOwnApply()
    {
        BasisAvatar avatar = BuildAvatar();
        BasisOpenLipSyncContext context = new BasisOpenLipSyncContext();
        context.Initialize(avatar, 3);

        using GatedInference stub = new GatedInference();
        BasisOpenLipSyncDriver.ProcessFrameOverride = stub.Run;

        const int Frames = 12;
        float[] shown = RunFrameLoop(context, avatar, stub, Frames);

        // Steady state: how many frames after audio tagged N was fed does tag N appear?
        int worst = 0;
        int measured = 0;
        for (int frame = 4; frame < Frames; frame++)
        {
            for (int source = frame; source >= 0; source--)
            {
                if (Mathf.Abs(shown[frame] - TagFor(source)) < 1e-4f)
                {
                    int lag = frame - source;
                    if (lag > worst) worst = lag;
                    measured++;
                    break;
                }
            }
        }

        Record($"pipeline lag (frames), worst of {measured} samples: {worst}  =  {worst * FrameSeconds * 1000f:F1} ms at 90 fps");
        Assert.Greater(measured, 0, "No fed tag was ever recognised on the mesh.");
        Assert.LessOrEqual(worst, 2,
            $"Audio takes {worst} frames to reach the face; the hand-off is holding results longer than it needs to.");

        context.Dispose();
    }

    [Test]
    public void FastInferenceReachesTheFaceInTheSameFrame()
    {
        // The other pipeline tests model a loaded machine, where inference finishes after its
        // own Apply. With one or two people talking an ONNX step is a fraction of a millisecond
        // and lands inside the Simulate..Apply window — which has to mean zero frames of lag,
        // not one, or the hand-off is holding results it already has.
        BasisAvatar avatar = BuildAvatar();
        BasisOpenLipSyncContext context = new BasisOpenLipSyncContext();
        context.Initialize(avatar, 5);

        using GatedInference stub = new GatedInference();
        BasisOpenLipSyncDriver.ProcessFrameOverride = stub.Run;

        int samplesPerFrame = Mathf.RoundToInt(OutputSampleRate * FrameSeconds);
        const int Frames = 8;
        int sameFrame = 0;

        for (int frame = 0; frame < Frames; frame++)
        {
            FeedTaggedAudio(context, TagFor(frame), samplesPerFrame);
            context.Simulate(FrameSeconds);
            BasisOpenLipSyncContext.ProcessAllPending();

            // Inference completes before Apply, as it does when the machine is not loaded.
            stub.Release();
            WaitForBatchIdle();

            context.Apply(FrameSeconds);

            float shown = avatar.FaceVisemeMesh.GetBlendShapeWeight(TagViseme) / 100f;
            if (Mathf.Abs(shown - TagFor(frame)) < 1e-4f) sameFrame++;
        }

        Record($"zero-lag frames when inference beats Apply: {sameFrame}/{Frames}");
        Assert.GreaterOrEqual(sameFrame, Frames - 1,
            $"Only {sameFrame} of {Frames} frames showed their own audio; results are being held a frame.");

        context.Dispose();
    }

    [Test]
    public void LongMainThreadStallDoesNotReorderBufferedAudio()
    {
        BasisAvatar avatar = BuildAvatar();
        BasisOpenLipSyncContext context = new BasisOpenLipSyncContext();
        context.Initialize(avatar, 4);

        List<float> received = new List<float>();
        BasisOpenLipSyncDriver.ProcessFrameOverride = (handle, audio, count, frame) =>
        {
            for (int Index = 0; Index < count; Index++) received.Add(audio[Index]);
            return Result.Success;
        };

        // 1.5 s of audio with no Simulate in between — more than the 1 s ingest buffer holds.
        // The buffer used to wrap to index 0 while Simulate still read it linearly from 0, so
        // the model was handed a second of scrambled, out-of-order samples.
        const int Chunks = 75;
        int samplesPerChunk = OutputSampleRate / 50; // 20 ms
        for (int chunk = 0; chunk < Chunks; chunk++)
        {
            FeedTaggedAudio(context, 0.01f * chunk, samplesPerChunk);
        }

        context.Simulate(FrameSeconds);
        BasisOpenLipSyncContext.ProcessAllPending();
        WaitForBatchIdle();

        Assert.Greater(received.Count, 0, "Nothing was handed to inference after the stall.");

        int firstRegression = -1;
        for (int Index = 1; Index < received.Count; Index++)
        {
            if (received[Index] + 1e-6f < received[Index - 1])
            {
                firstRegression = Index;
                break;
            }
        }

        Assert.AreEqual(-1, firstRegression,
            firstRegression < 0
                ? string.Empty
                : $"Buffered audio came out of order at sample {firstRegression}: " +
                  $"{received[firstRegression - 1]} then {received[firstRegression]}.");
        Record($"post-stall ingest: {received.Count} samples, monotonic (no wrap reorder)");

        context.Dispose();
    }

    // ────────────────────────────────────────────────────────────────
    //  Smoother group delay
    // ────────────────────────────────────────────────────────────────

    private static float[] OnePoleStepResponse(float alpha, int hops)
    {
        float[] output = new float[hops];
        float state = 0f;
        for (int hop = 0; hop < hops; hop++)
        {
            state = state * alpha + 1f * (1f - alpha);
            output[hop] = state;
        }
        return output;
    }

    private static float[] SmootherStepResponse(float alpha, int hops)
    {
        VisemeSmoother smoother = new VisemeSmoother(VisemeCount, alpha);
        float[] probabilities = new float[VisemeCount];
        probabilities[TagViseme] = 1f;

        float[] output = new float[hops];
        for (int hop = 0; hop < hops; hop++)
        {
            smoother.Step(probabilities);
            output[hop] = smoother.Output[TagViseme];
        }
        return output;
    }

    /// <summary>
    /// Group delay, measured the way it is defined: how far a steady ramp is horizontally
    /// displaced once the filter has settled. A step-response threshold crossing cannot resolve
    /// this — the answer is a couple of hops and the quantisation swallows it.
    /// </summary>
    private static float RampLagHops(Func<float, float> filterStep, float slope, int hops)
    {
        float output = 0f;
        float lag = 0f;
        for (int hop = 0; hop < hops; hop++)
        {
            float input = slope * hop;
            output = filterStep(input);
            lag = (input - output) / slope;
        }
        return lag;
    }

    [Test]
    public void SmootherCancelsTheOnePoleGroupDelay()
    {
        const float Alpha = 0.7f; // BasisVisemeDriveConfig.DefaultBackendSmoothing / 100
        const float HopMs = 10f;  // the model's fixed 100 Hz mel hop
        const float Slope = 1e-4f;
        const int Hops = 4000;

        float state = 0f;
        float onePoleHops = RampLagHops(input =>
        {
            state = state * Alpha + input * (1f - Alpha);
            return state;
        }, Slope, Hops);

        VisemeSmoother smoother = new VisemeSmoother(VisemeCount, Alpha);
        float[] probabilities = new float[VisemeCount];
        float compensatedHops = RampLagHops(input =>
        {
            probabilities[TagViseme] = input;
            smoother.Step(probabilities);
            return smoother.Output[TagViseme];
        }, Slope, Hops);

        float saved = (onePoleHops - compensatedHops) * HopMs;
        Record($"smoother group delay at alpha={Alpha}: one-pole {onePoleHops:F2} hops ({onePoleHops * HopMs:F1} ms) " +
               $"-> compensated {compensatedHops:F2} hops ({compensatedHops * HopMs:F1} ms), saves {saved:F1} ms");

        // a/(1-a) = 2.333 hops for the pole this replaces; the cascade cancels it to zero.
        Assert.AreEqual(Alpha / (1f - Alpha), onePoleHops, 0.05f, "Reference one-pole delay is not what theory says.");
        Assert.Less(Mathf.Abs(compensatedHops), 0.1f,
            $"Compensated smoother still lags by {compensatedHops:F2} hops.");
    }

    [Test]
    public void SmootherStepResponseStillRisesSooner()
    {
        // The ramp measurement above is the rigorous one, but confirm the practical case too:
        // a hard onset has to reach the target sooner than the pole it replaces.
        const float Alpha = 0.7f;
        float[] onePole = OnePoleStepResponse(Alpha, 200);
        float[] compensated = SmootherStepResponse(Alpha, 200);

        for (int hop = 0; hop < 8; hop++)
        {
            Assert.GreaterOrEqual(compensated[hop], onePole[hop] - 1e-5f,
                $"Compensated smoother is behind the plain one-pole at hop {hop}.");
        }
        Assert.Greater(compensated[1], onePole[1] + 0.1f,
            "No meaningful head start on a hard onset.");
    }

    [Test]
    public void SmootherSettlesOnTheSameValueAsTheOnePole()
    {
        // Cancelling lag must not introduce a DC error: a viseme held at 0.6 has to read 0.6.
        VisemeSmoother smoother = new VisemeSmoother(VisemeCount, 0.7f);
        float[] probabilities = new float[VisemeCount];
        probabilities[TagViseme] = 0.6f;

        for (int hop = 0; hop < 500; hop++) smoother.Step(probabilities);

        Assert.AreEqual(0.6f, smoother.Output[TagViseme], 1e-3f);
    }

    [Test]
    public void SmootherOutputStaysInProbabilitySpace()
    {
        // The lead term deliberately overshoots on a hard onset. These values become blendshape
        // weights directly, so the overshoot has to be clamped rather than merely small.
        VisemeSmoother smoother = new VisemeSmoother(VisemeCount, 0.9f);
        float[] probabilities = new float[VisemeCount];

        System.Random random = new System.Random(20260804);
        for (int hop = 0; hop < 2000; hop++)
        {
            for (int viseme = 0; viseme < VisemeCount; viseme++)
            {
                // Alternating hard slams plus noise — the worst case for an extrapolator.
                probabilities[viseme] = (hop % 7 < 3) ? 1f : 0f;
                probabilities[viseme] += (float)(random.NextDouble() - 0.5) * 0.2f;
                probabilities[viseme] = Mathf.Clamp01(probabilities[viseme]);
            }

            smoother.Step(probabilities);

            for (int viseme = 0; viseme < VisemeCount; viseme++)
            {
                float value = smoother.Output[viseme];
                Assert.IsFalse(float.IsNaN(value), $"NaN at hop {hop}, viseme {viseme}.");
                Assert.GreaterOrEqual(value, 0f, $"Negative weight at hop {hop}, viseme {viseme}.");
                Assert.LessOrEqual(value, 1f, $"Weight above 1 at hop {hop}, viseme {viseme}.");
            }
        }
    }

    [Test]
    public void ZeroSmoothingIsStillAPassthrough()
    {
        // An avatar that asks for no smoothing must get the model's output untouched, not a
        // one-frame extrapolation of it.
        VisemeSmoother smoother = new VisemeSmoother(VisemeCount, 0f);
        float[] probabilities = new float[VisemeCount];

        for (int hop = 0; hop < 32; hop++)
        {
            probabilities[TagViseme] = (hop % 2 == 0) ? 0.8f : 0.1f;
            smoother.Step(probabilities);
            Assert.AreEqual(probabilities[TagViseme], smoother.Output[TagViseme], 1e-5f);
        }
    }

    [Test]
    public void SmootherStartsSilentAndResetsSilent()
    {
        VisemeSmoother smoother = new VisemeSmoother(VisemeCount, 0.7f);
        Assert.AreEqual(1f, smoother.Output[0], 1e-6f, "Should start on 'sil'.");

        float[] probabilities = new float[VisemeCount];
        probabilities[TagViseme] = 1f;
        for (int hop = 0; hop < 50; hop++) smoother.Step(probabilities);
        Assert.Greater(smoother.Output[TagViseme], 0.5f);

        smoother.Reset();
        Assert.AreEqual(1f, smoother.Output[0], 1e-6f);
        Assert.AreEqual(0f, smoother.Output[TagViseme], 1e-6f);
    }

    [Test]
    public void SmootherRejectsNoiseBetterThanNoSmoothingAtAll()
    {
        // The lead term costs some noise rejection. Confirm what is left is still worth having,
        // otherwise the smoothing knob is doing nothing for its lag.
        const float Alpha = 0.7f;
        const int Hops = 4000;

        VisemeSmoother smoother = new VisemeSmoother(VisemeCount, Alpha);
        float[] probabilities = new float[VisemeCount];
        System.Random random = new System.Random(4242);

        double rawSum = 0, rawSq = 0, outSum = 0, outSq = 0;
        for (int hop = 0; hop < Hops; hop++)
        {
            float value = 0.5f + (float)(random.NextDouble() - 0.5) * 0.4f;
            probabilities[TagViseme] = value;
            smoother.Step(probabilities);

            if (hop < 100) continue; // let it settle
            float output = smoother.Output[TagViseme];
            rawSum += value; rawSq += (double)value * value;
            outSum += output; outSq += (double)output * output;
        }

        int n = Hops - 100;
        double rawStd = Math.Sqrt(rawSq / n - (rawSum / n) * (rawSum / n));
        double outStd = Math.Sqrt(outSq / n - (outSum / n) * (outSum / n));

        Record($"noise rejection at alpha={Alpha}: raw std {rawStd:F4} -> smoothed {outStd:F4} ({outStd / rawStd:F2}x)");
        Assert.Less(outStd, rawStd * 0.85,
            "The compensated smoother is barely smoothing; the lead term has eaten the whole benefit.");
    }
}
