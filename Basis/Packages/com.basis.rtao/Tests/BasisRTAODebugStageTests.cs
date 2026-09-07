using NUnit.Framework;
using UnityEngine.Rendering.RenderGraphModule;

namespace Basis.Rendering.RTAO.Tests
{
    /// <summary>
    /// The debug view exists to say which stage of the pipeline introduced an artifact, which it can only do
    /// if asking for a stage actually shows that stage.
    ///
    /// A TextureHandle is only made valid by a recording render graph, so these split the decision in two:
    /// MapStage is the part that says how a stage should be read and can be checked directly, and SelectStage
    /// is checked for the one thing that is observable without a graph - that a stage which did not run falls
    /// back rather than showing an unreadable buffer.
    /// </summary>
    public sealed class BasisRTAODebugStageTests
    {
        private const int TraceScale = 2;

        [Test]
        public void EveryStageNameSurvivesTheRoundTrip()
        {
            foreach (BasisRTAODebugStage stage in System.Enum.GetValues(typeof(BasisRTAODebugStage)))
            {
                string written = BasisRTAOSettingsMap.WriteDebugStage(stage);
                Assert.AreEqual(stage, BasisRTAOSettingsMap.ReadDebugStage(written),
                    $"The dropdown stores {written} for {stage} and must read it back as the same stage.");
            }
        }

        [Test]
        public void TheDropdownEntriesMatchTheEnum()
        {
            Assert.AreEqual(System.Enum.GetValues(typeof(BasisRTAODebugStage)).Length,
                BasisRTAOSettingsMap.DebugStageNames.Length,
                "A stage with no name in the map would silently read back as Final.");
        }

        [Test]
        public void AnUnknownNameFallsBackToFinal()
        {
            Assert.AreEqual(BasisRTAODebugStage.Final, BasisRTAOSettingsMap.ReadDebugStage("nonsense"));
            Assert.AreEqual(BasisRTAODebugStage.Final, BasisRTAOSettingsMap.ReadDebugStage(string.Empty));
            Assert.AreEqual(BasisRTAODebugStage.Final, BasisRTAOSettingsMap.ReadDebugStage(null));
        }

        [Test]
        public void PositionAndNormalCarryTheirOwnInterpretation()
        {
            BasisRTAODebugPass.MapStage(BasisRTAODebugStage.Position, TraceScale,
                out BasisRTAODebugPass.Interpretation positionAs, out _);
            Assert.AreEqual(BasisRTAODebugPass.Interpretation.Position, positionAs,
                "Drawing the position buffer as a grey visibility would show nothing useful.");

            BasisRTAODebugPass.MapStage(BasisRTAODebugStage.Normal, TraceScale,
                out BasisRTAODebugPass.Interpretation normalAs, out _);
            Assert.AreEqual(BasisRTAODebugPass.Interpretation.Normal, normalAs);

            foreach (BasisRTAODebugStage stage in new[]
                     { BasisRTAODebugStage.Final, BasisRTAODebugStage.Raw,
                       BasisRTAODebugStage.Temporal, BasisRTAODebugStage.Denoised })
            {
                BasisRTAODebugPass.MapStage(stage, TraceScale,
                    out BasisRTAODebugPass.Interpretation readAs, out _);
                Assert.AreEqual(BasisRTAODebugPass.Interpretation.Visibility, readAs, $"{stage} holds a visibility.");
            }
        }

        [Test]
        public void OnlyTheFinalBufferIsAtFullResolutionAndOutsideTheStageArray()
        {
            bool finalIsArray = BasisRTAODebugPass.MapStage(BasisRTAODebugStage.Final, TraceScale, out _, out int finalScale);
            Assert.IsFalse(finalIsArray,
                "The composited result is a TEXTURE2D_X, not a stage array; reading it as an array returns nothing.");
            Assert.AreEqual(1, finalScale,
                "It is full resolution, so stepping its coordinate down would zoom into a corner.");

            foreach (BasisRTAODebugStage stage in new[]
                     { BasisRTAODebugStage.Raw, BasisRTAODebugStage.Temporal, BasisRTAODebugStage.Denoised,
                       BasisRTAODebugStage.Position, BasisRTAODebugStage.Normal })
            {
                bool isArray = BasisRTAODebugPass.MapStage(stage, TraceScale, out _, out int scale);
                Assert.IsTrue(isArray, $"{stage} lives in the trace resolution array.");
                Assert.AreEqual(TraceScale, scale,
                    $"{stage} is at trace resolution and the view has to step the coordinate down to match.");
            }
        }

        [Test]
        public void EveryStageIsMappedRatherThanFallingThroughToTheDefault()
        {
            // A stage added to the enum and forgotten here would land in the default case and silently show
            // Final while the dropdown still said its name.
            foreach (BasisRTAODebugStage stage in System.Enum.GetValues(typeof(BasisRTAODebugStage)))
            {
                bool isArray = BasisRTAODebugPass.MapStage(stage, TraceScale, out _, out _);
                Assert.AreEqual(stage != BasisRTAODebugStage.Final, isArray,
                    $"{stage} is mapped to the wrong kind of buffer.");
            }
        }

        [Test]
        public void AStageThatDidNotRunFallsBackToTheFinalBuffer()
        {
            // Nothing ran: every handle is null. With no denoise passes there is no denoised buffer, and
            // asking for it should show the composited result rather than sampling an unreadable texture.
            BasisRTAOResolvedTexture textures = new BasisRTAOResolvedTexture { scale = TraceScale };

            foreach (BasisRTAODebugStage stage in System.Enum.GetValues(typeof(BasisRTAODebugStage)))
            {
                BasisRTAODebugStage shown = BasisRTAODebugPass.SelectStage(stage, textures,
                    out _, out BasisRTAODebugPass.Interpretation interpretation, out int scale, out bool fromStageArray);

                Assert.AreEqual(BasisRTAODebugStage.Final, shown, $"{stage} did not run, so it cannot be shown.");
                Assert.AreEqual(BasisRTAODebugPass.Interpretation.Visibility, interpretation);
                Assert.AreEqual(1, scale, "The fallback is the full resolution buffer and must carry its scale.");
                Assert.IsFalse(fromStageArray, "The fallback is not in the stage array.");
            }
        }

        [Test]
        public void ResetClearsEveryStageNotJustTheResult()
        {
            // ContextItem instances are pooled across frames. A stage left behind would be read next frame
            // against a graph that never wrote it.
            BasisRTAOResolvedTexture textures = new BasisRTAOResolvedTexture { scale = TraceScale };
            textures.Reset();

            Assert.IsFalse(textures.handle.IsValid());
            Assert.IsFalse(textures.raw.IsValid());
            Assert.IsFalse(textures.temporal.IsValid());
            Assert.IsFalse(textures.denoised.IsValid());
            Assert.IsFalse(textures.position.IsValid());
            Assert.IsFalse(textures.normal.IsValid());
            Assert.AreEqual(1, textures.scale);
        }
    }
}
