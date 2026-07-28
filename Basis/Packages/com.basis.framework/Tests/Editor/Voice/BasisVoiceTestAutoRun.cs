using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

/// <summary>
/// Headless driver for the voice pipeline tests: when <c>Temp/voice_tests_run_request.txt</c>
/// exists after a domain reload, runs every test in this assembly via the TestRunnerApi and
/// writes per-test results to <c>Temp/voice_tests_results.txt</c>. Lets an external shell
/// session (or CI wrapper) execute the suite in an already-open editor by touching the
/// trigger file and forcing a refresh.
/// </summary>
static class BasisVoiceTestAutoRun
{
    const string TriggerPath = "Temp/voice_tests_run_request.txt";
    const string ResultPath = "Temp/voice_tests_results.txt";

    [InitializeOnLoadMethod]
    static void MaybeRun()
    {
        if (!File.Exists(TriggerPath)) return;
        File.Delete(TriggerPath);
        if (File.Exists(ResultPath)) File.Delete(ResultPath);
        EditorApplication.delayCall += () =>
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks());
            var filter = new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "Basis.Framework.Voice.Tests" },
            };
            api.Execute(new ExecutionSettings(filter));
        };
    }

    class Callbacks : ICallbacks
    {
        readonly StringBuilder _sb = new StringBuilder();

        public void RunStarted(ITestAdaptor testsToRun) { }

        public void RunFinished(ITestResultAdaptor result)
        {
            _sb.AppendLine($"RUN FINISHED: passed={result.PassCount} failed={result.FailCount} skipped={result.SkipCount} duration={result.Duration:F1}s");
            File.WriteAllText(ResultPath, _sb.ToString());
        }

        public void TestStarted(ITestAdaptor test) { }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (result.Test.IsSuite) return;
            _sb.AppendLine($"{result.TestStatus}: {result.Test.FullName} ({result.Duration:F2}s)");
            if (result.TestStatus == TestStatus.Failed)
            {
                _sb.AppendLine($"  MESSAGE: {result.Message}");
                _sb.AppendLine($"  STACK: {result.StackTrace}");
            }
        }
    }
}
