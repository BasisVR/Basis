using Unity.Scripting.LifecycleManagement;
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.DocEngine
{
    /// <summary>
    /// The API Reference's promise is that the snippet compiles as written. These pin the parts of
    /// that promise a reader would notice breaking: declared locals instead of comment placeholders,
    /// a captured chain the null guard can actually test, and a cilboxed subscription whose teardown
    /// can still reach what it subscribed to.
    /// </summary>
    // Stand-ins for a Basis singleton component and its neighbours, so the tests do not depend on
    // any real API keeping its shape. Declared at namespace scope because a nested type is written
    // through its outer type, which would put the fixture's own name in every expectation.
    [NoAutoStaticsCleanup]
    internal class Subject : MonoBehaviour
    {
        public static Subject Instance;

        public float Speed { get; set; }
        public float ReadOnlyValue { get; private set; }
        public const int Limit = 7;

        public event Action<int, string> Changed;

        public void Move(Vector3 direction, float scale = 1f) { _ = Changed; }
        public bool TryFind(string name, out Transform found) { found = null; return false; }
        public static int Count(string group) => group.Length;
    }

    internal class Nested
    {
        public class Inner { }
    }

    internal enum Sample
    {
        First,
        Second,
    }

    [TestFixture]
    public class BasisDocSnippetTests
    {
        private static BasisDocSnippetContext Context() => new BasisDocSnippetContext
        {
            HostType = typeof(Subject),
            AccessExpr = "Subject.Instance",
            AccessorHint = "Singleton",
            MayBeNull = true,
        };

        private static MemberInfo Member(string name) =>
            typeof(Subject).GetMember(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)[0];

        [Test]
        public void MethodParametersBecomeDeclaredLocals()
        {
            string code = BasisDocSnippet.Build(Member(nameof(Subject.Move)), Context()).Code;

            Assert.That(code, Does.Contain("Vector3 direction = Vector3.zero;"));
            Assert.That(code, Does.Contain("float scale = 0f;"));
            Assert.That(code, Does.Contain("subject.Move(direction, scale);"));
            Assert.That(code, Does.Not.Contain("/*"), "placeholders have to be real values, not comments");
        }

        [Test]
        public void OptionalParametersSayWhatTheyDefaultTo()
        {
            string code = BasisDocSnippet.Build(Member(nameof(Subject.Move)), Context()).Code;

            Assert.That(code, Does.Contain("optional, defaults to 1f"));
        }

        [Test]
        public void ChainIsCapturedAndGuarded()
        {
            string code = BasisDocSnippet.Build(Member(nameof(Subject.Move)), Context()).Code;

            Assert.That(code, Does.Contain("Subject subject = Subject.Instance;"));
            Assert.That(code, Does.Contain("if (subject == null) return;"));
        }

        [Test]
        public void TryPatternBecomesAnIfWithAnInlineOut()
        {
            string code = BasisDocSnippet.Build(Member(nameof(Subject.TryFind)), Context()).Code;

            Assert.That(code, Does.Contain("if (subject.TryFind(name, out Transform found))"));
        }

        [Test]
        public void StaticMembersUseTheTypeAndNeedNoGuard()
        {
            string code = BasisDocSnippet.Build(Member(nameof(Subject.Count)), Context()).Code;

            Assert.That(code, Does.Contain("Subject.Count(group)"));
            Assert.That(code, Does.Not.Contain("== null"));
        }

        [Test]
        public void ReadOnlyPropertyOffersNoWrite()
        {
            string code = BasisDocSnippet.Build(Member(nameof(Subject.ReadOnlyValue)), Context()).Code;

            Assert.That(code, Does.Contain("subject.ReadOnlyValue;"));
            Assert.That(code, Does.Not.Contain("subject.ReadOnlyValue ="));
        }

        [Test]
        public void SettablePropertyOffersBoth()
        {
            string code = BasisDocSnippet.Build(Member(nameof(Subject.Speed)), Context()).Code;

            Assert.That(code, Does.Contain("float speed = subject.Speed;"));
            Assert.That(code, Does.Contain("subject.Speed = 0f;"));
        }

        [Test]
        public void ConstFieldSaysItIsFolded()
        {
            string code = BasisDocSnippet.Build(Member(nameof(Subject.Limit)), Context()).Code;

            Assert.That(code, Does.Contain("const"));
            Assert.That(code, Does.Not.Contain("Subject.Limit ="));
        }

        [Test]
        public void EventHandlerMatchesTheDelegateSignature()
        {
            string code = BasisDocSnippet.Build(Member(nameof(Subject.Changed)), Context()).Code;

            Assert.That(code, Does.Contain("subject.Changed += HandleChanged;"));
            Assert.That(code, Does.Contain("subject.Changed -= HandleChanged;"));
            Assert.That(code, Does.Match(@"void HandleChanged\(int \w+, string \w+\)"));
        }

        [Test]
        public void CilboxSubscriptionCachesTheSourceInAField()
        {
            string code = BasisDocSnippet.BuildCilbox(Member(nameof(Subject.Changed)), Context()).Code;

            Assert.That(code, Does.Contain("[Cilboxable]"));
            Assert.That(code, Does.Contain("Subject subject;"), "OnDestroy has to reach the same object Start subscribed to");
            Assert.That(code, Does.Contain("void Start()"));
            Assert.That(code, Does.Contain("void OnDestroy()"));
            Assert.That(code.IndexOf("+= HandleChanged", StringComparison.Ordinal),
                Is.LessThan(code.IndexOf("-= HandleChanged", StringComparison.Ordinal)));
        }

        [Test]
        public void CilboxWrapperPutsSetupInStartNotOnEnable()
        {
            string code = BasisDocSnippet.BuildCilbox(Member(nameof(Subject.Move)), Context()).Code;

            Assert.That(code, Does.Contain("void Start()"));
            Assert.That(code, Does.Not.Contain("void OnEnable()"), "the proxy drops the first OnEnable");
            Assert.That(code, Does.Contain("using Cilbox;"));
        }

        [Test]
        public void UsingsCoverTheTypesWrittenAndSkipKeywordAliases()
        {
            var result = BasisDocSnippet.Build(Member(nameof(Subject.Move)), Context());

            Assert.That(result.Usings, Does.Contain("UnityEngine"));
            Assert.That(result.Usings, Does.Not.Contain("System"), "float is written as a keyword, so System is not needed");
        }

        [Test]
        public void NiceTypeWritesNestedAndGenericTypesAsSource()
        {
            Assert.That(BasisDocSnippet.NiceType(typeof(int)), Is.EqualTo("int"));
            Assert.That(BasisDocSnippet.NiceType(typeof(Vector3[])), Is.EqualTo("Vector3[]"));
            Assert.That(BasisDocSnippet.NiceType(typeof(System.Collections.Generic.List<string>)), Is.EqualTo("List<string>"));
            Assert.That(BasisDocSnippet.NiceType(typeof(Nested.Inner)), Is.EqualTo("Nested.Inner"));
        }

        [Test]
        public void PlaceholdersCompileForTheShapesTheReferenceMeets()
        {
            Assert.That(BasisDocSnippet.PlaceholderFor(typeof(string)), Is.EqualTo("\"\""));
            Assert.That(BasisDocSnippet.PlaceholderFor(typeof(Quaternion)), Is.EqualTo("Quaternion.identity"));
            Assert.That(BasisDocSnippet.PlaceholderFor(typeof(Transform)), Is.EqualTo("null"));
            Assert.That(BasisDocSnippet.PlaceholderFor(typeof(int[])), Is.EqualTo("new int[0]"));
            Assert.That(BasisDocSnippet.PlaceholderFor(typeof(Sample)), Is.EqualTo("Sample.First"));
            Assert.That(BasisDocSnippet.PlaceholderFor(typeof(Action<int>)), Is.EqualTo("obj => { }"));
        }
    }
}
