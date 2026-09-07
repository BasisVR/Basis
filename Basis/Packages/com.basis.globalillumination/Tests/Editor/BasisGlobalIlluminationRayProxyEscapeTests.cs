using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Basis.Tests.GlobalIllumination
{
    /// <summary>
    /// Every ray fired at the acceleration structure has to step out of any avatar capsule it began inside.
    ///
    /// Avatars are traced as capsules on their bones while the depth buffer - the point every ray starts
    /// from - is the avatar's real surface, so a ray leaving a visible chest starts inside its own torso
    /// capsule. <c>BasisGIRtTraceEscapingProxies</c> is the answer to that and the gather and the reflection
    /// both went through it; the light visibility ray did not, and every lit avatar wore the shape of its
    /// own proxy as a hard edged black patch.
    ///
    /// This is asserted against the kernel SOURCE rather than against rendered light, and that is a
    /// deliberate choice rather than a shortcut. The defect was structural - one call site out of four not
    /// using a wrapper - and it is structural again the next time somebody adds a ray. Reproducing it
    /// through the renderer would need a humanoid Animator, a real avatar built at runtime and a ray
    /// tracing device, and would then only cover the one trace it happened to be written around. This
    /// covers all of them, including the ones not written yet.
    ///
    /// It cannot tell you the spots are gone. It can tell you nobody has quietly re-opened the door.
    /// </summary>
    public class BasisGlobalIlluminationRayProxyEscapeTests
    {
        private const string KernelPath =
            "Packages/com.basis.globalillumination/Shaders/BasisGlobalIlluminationRTKernel.hlsl";
        private const string EscapeFunction = "BasisGIRtTraceEscapingProxies";
        private const string LightingFunction = "BasisGIRtDirectLighting";

        /// <summary>
        /// The kernel with every comment replaced by a space.
        ///
        /// The comments in this file discuss the traces at length - one of them names the very any-hit call
        /// that caused the bug, while explaining why it is gone - so counting occurrences in the raw text
        /// would find the prose rather than the code.
        /// </summary>
        private static string ReadKernelWithoutComments()
        {
            string full = Path.GetFullPath(KernelPath);
            Assert.IsTrue(File.Exists(full),
                KernelPath + " is missing, so nothing here is guarding anything - fix the path.");

            string source = File.ReadAllText(full);
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            source = Regex.Replace(source, @"//[^\r\n]*", " ");
            return source;
        }

        /// <summary>The half-open character range of a function body, found by matching braces.</summary>
        private static void BodyOf(string source, string signature, out int start, out int end)
        {
            int declaration = source.IndexOf(signature, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(declaration, 0, "could not find " + signature + " in the kernel");

            start = source.IndexOf('{', declaration);
            Assert.GreaterOrEqual(start, 0, signature + " has no body");

            int depth = 0;
            for (int index = start; index < source.Length; index++)
            {
                if (source[index] == '{') { depth++; }
                else if (source[index] == '}')
                {
                    depth--;
                    if (depth == 0) { end = index; return; }
                }
            }

            Assert.Fail(signature + " has an unbalanced body, so this test cannot bound it");
            end = source.Length;
        }

        [Test]
        public void EveryTraceAgainstTheStructureStepsOutOfAvatarProxies()
        {
            string source = ReadKernelWithoutComments();
            BodyOf(source, "UnifiedRT::Hit " + EscapeFunction, out int start, out int end);

            // Closest-hit only. An any-hit is allowed outside the helper now, but only masked away from
            // proxies - AnyHitRaysNeverSeeAvatarProxies is what holds that half up.
            MatchCollection traces = Regex.Matches(source, @"UnifiedRT::TraceRayClosestHit\s*\(");
            Assert.Greater(traces.Count, 0,
                "no closest-hit traces found at all, so this test is reading the wrong file or the wrong syntax");

            foreach (Match trace in traces)
            {
                Assert.IsTrue(trace.Index > start && trace.Index < end,
                    "a closest-hit ray is fired at the acceleration structure outside " + EscapeFunction + ", at character " +
                    trace.Index + ". Avatars are in that structure as capsules that swallow their own visible " +
                    "surface, so a ray starting on an avatar hits its own proxy at nearly zero distance and " +
                    "reads as fully enclosed. Route it through " + EscapeFunction + " like every other ray here.");
            }
        }

        [Test]
        public void AnyHitRaysNeverSeeAvatarProxies()
        {
            // An any-hit answers whether SOMETHING was in the way and returns a bool - no instance to test
            // a proxy flag on, no distance to step past - so it cannot walk out of a capsule the ray began
            // inside. It is still the right primitive for visibility against the ROOM, which is nearly every
            // shadow ray and the reason routing them all through the closest-hit walk made reflections
            // crawl. Safe only while the mask keeps capsules out of it.
            string source = ReadKernelWithoutComments();

            MatchCollection anyHits = Regex.Matches(source, @"UnifiedRT::TraceRayAnyHit\s*\(\s*[^;]*?;");
            foreach (Match anyHit in anyHits)
            {
                Assert.IsTrue(anyHit.Value.Contains("solidMask"),
                    "an any-hit trace is not masked to solid geometry: " + anyHit.Value.Trim() + ". Given the whole " +
                    "trace mask it would meet avatar capsules, and an any-hit cannot step out of the one it " +
                    "started inside - which is a black patch on every lit avatar.");
            }
        }

        [Test]
        public void TheLightVisibilityRayStillEscapesProxies()
        {
            string source = ReadKernelWithoutComments();
            BodyOf(source, "float3 " + LightingFunction, out int start, out int end);

            string body = source.Substring(start, end - start);
            Assert.IsTrue(body.Contains(EscapeFunction + "("),
                LightingFunction + " no longer walks its shadow ray out of avatar capsules. This is the " +
                "exact call site that painted a hard edged black patch, shaped like the torso capsule, onto " +
                "every directly lit avatar in ray traced mode.");
        }

        [Test]
        public void TheEscapeAsksWhoseCapsuleItIsRatherThanOnlyWhetherItStartedInside()
        {
            string source = ReadKernelWithoutComments();
            BodyOf(source, "UnifiedRT::Hit " + EscapeFunction, out int start, out int end);

            string body = source.Substring(start, end - start);
            Assert.IsTrue(body.Contains(SelfReachName),
                "the escape decides on the back face alone again. A back face means \"I began inside this\", " +
                "which is only the same question as \"this is me\" while the capsule encloses the surface - and " +
                "the proxy fit is deliberately INSCRIBED, so a thigh capsule is about eight centimetres across " +
                "inside a fourteen centimetre thigh and the rendered skin sits OUTSIDE its own proxy. A grazing " +
                "ray then re-enters its own capsule FRONT face and reads as somebody else in the way. That is " +
                "the dark banding down the legs of almost every avatar, worst at the hips, where each thigh " +
                "capsule is half buried in the pelvis one and the skin is outside both of them.");
        }

        [Test]
        public void BothTracersAgreeOnHowNearACapsuleHasToBeToBeYourOwn()
        {
            float gi = ReachFrom(KernelPath, SelfReachName);
            float rtao = ReachFrom(RtaoKernelPath, RtaoSelfReachName);

            Assert.AreEqual(gi, rtao, 1e-6f,
                "global illumination and ambient occlusion disagree about which capsules belong to the surface " +
                "being shaded (" + gi + " against " + rtao + "). They stand on the SAME capsules, so a body part " +
                "that is your own to one tracer and a stranger to the other is lit one way and shaded another - " +
                "which reads as banding that changes with the ambient occlusion setting rather than as a bug in " +
                "either.");
        }

        private const string RtaoKernelPath = "Packages/com.basis.rtao/Shaders/BasisRTAOKernel.hlsl";
        private const string SelfReachName = "BASISGI_RT_PROXY_SELF_REACH";
        private const string RtaoSelfReachName = "BASIS_RTAO_PROXY_SELF_REACH";

        /// <summary>Reads a <c>#define NAME value</c> out of a kernel, so the two can be compared as numbers.</summary>
        private static float ReachFrom(string path, string name)
        {
            string full = Path.GetFullPath(path);
            Assert.IsTrue(File.Exists(full), path + " is missing, so this test is guarding nothing.");

            Match match = Regex.Match(File.ReadAllText(full),
                @"#define\s+" + Regex.Escape(name) + @"\s+([0-9.]+)");
            Assert.IsTrue(match.Success, path + " no longer defines " + name +
                ", so nothing decides which capsules belong to the body being shaded.");

            return float.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
