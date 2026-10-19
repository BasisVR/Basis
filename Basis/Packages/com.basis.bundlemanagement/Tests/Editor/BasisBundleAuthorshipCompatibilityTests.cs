using System;
using System.Text;
using NUnit.Framework;
using UnityEngine;

public class BasisBundleAuthorshipCompatibilityTests
{
    [Serializable]
    public class LegacyDescription
    {
        public string AssetBundleName;
        public string AssetBundleDescription;
        public Texture2D AssetBundleIcon;
        public string[] Tags = new string[0];
        public string ContentGroupId;
    }

    [Serializable]
    public class LegacyConnector
    {
        public string UniqueVersion;
        public LegacyDescription BasisBundleDescription;
        public string ImageBase64;
        public string DateOfCreation;
    }

    [Serializable]
    public class LegacyWrapper
    {
        public LegacyConnector Value;
    }

    private static BasisBundleAuthorship ExampleAuthorship()
    {
        return new BasisBundleAuthorship
        {
            Author = "exampleAuthor and Second Author",
            Contact = "https://example.com/author",
            License = "CC BY 4.0",
            Credits = "Base model by Example Studio\nTextures by <b>Example</b> Artist",
        };
    }

    private static BasisBundleConnector NewConnector()
    {
        return new BasisBundleConnector
        {
            UniqueVersion = "0123456789abcdef0123456789abcdef",
            DateOfCreation = "2026-01-01T00:00:00.0000000Z",
            BasisBundleGenerated = new BasisBundleGenerated[0],
            BasisBundleDescription = new BasisBundleDescription
            {
                AssetBundleName = "exampleName",
                AssetBundleDescription = "Example description",
                Tags = new[] { "Example" },
                ContentGroupId = "fedcba9876543210fedcba9876543210",
                Authorship = ExampleAuthorship(),
            },
        };
    }

    private static LegacyConnector LegacyShapedConnector()
    {
        return new LegacyConnector
        {
            UniqueVersion = "0123456789abcdef0123456789abcdef",
            DateOfCreation = "2026-01-01T00:00:00.0000000Z",
            BasisBundleDescription = new LegacyDescription
            {
                AssetBundleName = "exampleName",
                AssetBundleDescription = "Example description",
                Tags = new[] { "Example" },
                ContentGroupId = "fedcba9876543210fedcba9876543210",
            },
        };
    }

    [Test]
    public void AuthorshipRoundTripsExactlyAsWritten()
    {
        BasisBundleConnector read = BasisSerialization.DeserializeValue<BasisBundleConnector>(BasisSerialization.SerializeValue(NewConnector()));
        BasisBundleAuthorship expected = ExampleAuthorship();
        BasisBundleAuthorship actual = read.BasisBundleDescription.Authorship;

        Assert.That(actual.Author, Is.EqualTo(expected.Author));
        Assert.That(actual.Contact, Is.EqualTo(expected.Contact));
        Assert.That(actual.License, Is.EqualTo(expected.License));
        Assert.That(actual.Credits, Is.EqualTo(expected.Credits));
        Assert.That(BasisBundleConnector.HasAuthorship(actual), Is.True);
        Assert.That(read.BasisBundleDescription.AssetBundleName, Is.EqualTo("exampleName"));
    }

    [Test]
    public void AuthorshipIsItsOwnSectionOfTheDescription()
    {
        string json = Encoding.UTF8.GetString(BasisSerialization.SerializeValue(NewConnector()));

        StringAssert.Contains("\"Authorship\":{\"Author\":\"exampleAuthor and Second Author\",\"Contact\":", json);
        Assert.That(json.IndexOf("\"BasisBundleDescription\":{", StringComparison.Ordinal), Is.LessThan(json.IndexOf("\"Authorship\":{", StringComparison.Ordinal)));
    }

    [Test]
    public void ConnectorBuiltBeforeAuthorshipLoadsWithoutIt()
    {
        byte[] legacyBytes = BasisSerialization.SerializeValue(LegacyShapedConnector());
        StringAssert.DoesNotContain("Authorship", Encoding.UTF8.GetString(legacyBytes));

        BasisBundleConnector read = BasisSerialization.DeserializeValue<BasisBundleConnector>(legacyBytes);

        Assert.That(read, Is.Not.Null);
        Assert.That(read.BasisBundleDescription.AssetBundleName, Is.EqualTo("exampleName"));
        Assert.That(read.BasisBundleDescription.AssetBundleDescription, Is.EqualTo("Example description"));
        Assert.That(read.BasisBundleDescription.ContentGroupId, Is.EqualTo("fedcba9876543210fedcba9876543210"));
        Assert.That(BasisBundleConnector.HasAuthorship(read.BasisBundleDescription.Authorship), Is.False);
        Assert.That(read.BasisBundleDescription.Authorship.Author, Is.Null.Or.Empty);
    }

    [Test]
    public void ReaderThatPredatesAuthorshipStillLoadsNewConnector()
    {
        LegacyConnector read = BasisSerialization.DeserializeValue<LegacyConnector>(BasisSerialization.SerializeValue(NewConnector()));

        Assert.That(read, Is.Not.Null);
        Assert.That(read.UniqueVersion, Is.EqualTo("0123456789abcdef0123456789abcdef"));
        Assert.That(read.BasisBundleDescription.AssetBundleName, Is.EqualTo("exampleName"));
        Assert.That(read.BasisBundleDescription.AssetBundleDescription, Is.EqualTo("Example description"));
        Assert.That(read.BasisBundleDescription.Tags, Is.EqualTo(new[] { "Example" }));
        Assert.That(read.BasisBundleDescription.ContentGroupId, Is.EqualTo("fedcba9876543210fedcba9876543210"));
    }

    [Test]
    public void JsonUtilityReaderThatPredatesAuthorshipStillLoadsNewConnector()
    {
        string json = Encoding.UTF8.GetString(BasisSerialization.SerializeValue(NewConnector()));

        LegacyWrapper read = JsonUtility.FromJson<LegacyWrapper>(json);

        Assert.That(read?.Value, Is.Not.Null);
        Assert.That(read.Value.UniqueVersion, Is.EqualTo("0123456789abcdef0123456789abcdef"));
        Assert.That(read.Value.BasisBundleDescription.AssetBundleName, Is.EqualTo("exampleName"));
        Assert.That(read.Value.BasisBundleDescription.ContentGroupId, Is.EqualTo("fedcba9876543210fedcba9876543210"));
    }

    [Test]
    public void HasAuthorshipNeedsOneFieldWithText()
    {
        Assert.That(BasisBundleConnector.HasAuthorship(default), Is.False);
        Assert.That(BasisBundleConnector.HasAuthorship(new BasisBundleAuthorship { Author = " ", Contact = "", License = "\t", Credits = "\n" }), Is.False);
        Assert.That(BasisBundleConnector.HasAuthorship(new BasisBundleAuthorship { Author = "a" }), Is.True);
        Assert.That(BasisBundleConnector.HasAuthorship(new BasisBundleAuthorship { Contact = "a" }), Is.True);
        Assert.That(BasisBundleConnector.HasAuthorship(new BasisBundleAuthorship { License = "a" }), Is.True);
        Assert.That(BasisBundleConnector.HasAuthorship(new BasisBundleAuthorship { Credits = "a" }), Is.True);
    }

    [Test]
    public void EmptyAuthorTakesTheFallbackOnACopy()
    {
        const string did = "did:key:z6MkSyntheticTestKeyNotARealIdentity0000000000";
        BasisBundleDescription authored = new BasisBundleDescription { AssetBundleName = "exampleName", Authorship = new BasisBundleAuthorship { Author = " ", License = "CC BY 4.0" } };

        BasisBundleDescription built = authored.WithAuthorFallback(did);

        Assert.That(built, Is.Not.SameAs(authored));
        Assert.That(built.Authorship.Author, Is.EqualTo(did));
        Assert.That(built.Authorship.License, Is.EqualTo("CC BY 4.0"));
        Assert.That(built.AssetBundleName, Is.EqualTo("exampleName"));
        Assert.That(authored.Authorship.Author, Is.EqualTo(" "));
    }

    [Test]
    public void WrittenAuthorOrMissingFallbackLeavesTheDescriptionAlone()
    {
        BasisBundleDescription written = new BasisBundleDescription { Authorship = new BasisBundleAuthorship { Author = "exampleAuthor" } };
        BasisBundleDescription empty = new BasisBundleDescription();

        Assert.That(written.WithAuthorFallback("did:key:z6MkSyntheticTestKeyNotARealIdentity0000000000"), Is.SameAs(written));
        Assert.That(empty.WithAuthorFallback(string.Empty), Is.SameAs(empty));
        Assert.That(empty.WithAuthorFallback(null), Is.SameAs(empty));
    }

    [Test]
    public void OnlyADidKeyCountsAsALocalAuthor()
    {
        Assert.That(BasisContentDefaults.IsDidKey("did:key:z6MkSyntheticTestKeyNotARealIdentity0000000000"), Is.True);
        Assert.That(BasisContentDefaults.IsDidKey(null), Is.False);
        Assert.That(BasisContentDefaults.IsDidKey(string.Empty), Is.False);
        Assert.That(BasisContentDefaults.IsDidKey("did:key:z"), Is.False);
        Assert.That(BasisContentDefaults.IsDidKey("did:web:example.com"), Is.False);
        Assert.That(BasisContentDefaults.IsDidKey("did:key:z6Mk with space"), Is.False);
        Assert.That(BasisContentDefaults.IsDidKey("did:key:z6Mk<b>bold</b>"), Is.False);
        Assert.That(BasisContentDefaults.IsDidKey("did:key:z" + new string('a', 200)), Is.False);
    }
}
