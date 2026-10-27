using System.Collections.Generic;
using Basis.Editor.Identity;
using NUnit.Framework;

namespace Basis.Editor.Tests
{
    public class BasisIdentitySettingsTests
    {
        static IEnumerable<BasisIdentityEntry> Entries => BasisIdentitySettings.Entries;

        [Test]
        public void EntryIds_AreUnique()
        {
            HashSet<string> ids = new HashSet<string>();
            foreach (BasisIdentityEntry entry in Entries) Assert.That(ids.Add(entry.Id), Is.True, entry.Id);
        }

        [Test]
        public void LinkedEntries_Exist()
        {
            foreach (BasisIdentityEntry entry in Entries)
            {
                if (entry.LinkedTo != null) Assert.That(BasisIdentitySettings.Find(entry.LinkedTo), Is.Not.Null, entry.Id);
            }
        }

        [TestCaseSource(nameof(Entries))]
        public void EveryLocation_ResolvesOnceAndAgrees(BasisIdentityEntry entry)
        {
            if (entry.IsSetting) return;
            string value = null;
            foreach (BasisIdentityRead read in BasisIdentitySettings.ReadAll(entry))
            {
                if (read.Missing)
                {
                    Assert.That(read.Target.Optional, Is.True, $"{entry.Id}: {read.Target.Display} is missing");
                    continue;
                }
                Assert.That(read.Matches, Is.EqualTo(1), $"{entry.Id}: {read.Target.Display}");
                if (value == null) value = read.Value;
                else Assert.That(read.Value, Is.EqualTo(value), $"{entry.Id}: {read.Target.Display} disagrees");
            }
            Assert.That(value, Is.Not.Null, entry.Id);
        }

        [TestCaseSource(nameof(Entries))]
        public void CurrentValue_IsValid(BasisIdentityEntry entry)
        {
            string value = BasisIdentitySettings.Current(entry);
            Assert.That(BasisIdentitySettings.Validate(entry, value), Is.Null, $"{entry.Id} = {value}");
        }

        [TestCase("plain")]
        [TestCase("quote \" and \\ slash")]
        public void CSharpEscape_RoundTrips(string value)
        {
            Assert.That(BasisIdentitySettings.UnescapeCSharp(BasisIdentitySettings.EscapeCSharp(value)), Is.EqualTo(value));
        }

        [Test]
        public void Validate_RejectsBadValues()
        {
            Assert.That(BasisIdentitySettings.Validate(BasisIdentitySettings.Find("beeProgId"), "Bad Id"), Is.Not.Null);
            Assert.That(BasisIdentitySettings.Validate(BasisIdentitySettings.Find("beeMimeType"), "nope"), Is.Not.Null);
            Assert.That(BasisIdentitySettings.Validate(BasisIdentitySettings.Find("urlScheme"), "Has Space"), Is.Not.Null);
            Assert.That(BasisIdentitySettings.Validate(BasisIdentitySettings.Find("serverPort"), "70000"), Is.Not.Null);
            Assert.That(BasisIdentitySettings.Validate(BasisIdentitySettings.Find("websiteUrl"), "ftp://x"), Is.Not.Null);
            Assert.That(BasisIdentitySettings.Validate(BasisIdentitySettings.Find("productName"), "a/b"), Is.Not.Null);
            Assert.That(BasisIdentitySettings.Validate(BasisIdentitySettings.Find("beeProgId"), "MyLabs.BEE"), Is.Null);
        }
    }
}
