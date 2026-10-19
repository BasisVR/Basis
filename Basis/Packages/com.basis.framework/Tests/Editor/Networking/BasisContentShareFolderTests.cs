using System.Collections.Generic;
using Basis.Scripts.UI.UI_Panels;
using NUnit.Framework;
using static SerializableBasis;

public class BasisContentShareFolderTests
{
    private const string FolderId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    [SetUp]
    public void SetUp()
    {
        BasisContentShareFolder.Register();
    }

    private static BasisDataStoreItemKeys.ItemKey Key(BundledContentHolder.Mode mode, string url, string pass = "0123456789abcdef0123456789abcdef") =>
        new BasisDataStoreItemKeys.ItemKey { Mode = mode, Url = url, Pass = pass };

    private static BasisDataStoreItemKeys.ItemKey EmbeddedKey(string url) =>
        new BasisDataStoreItemKeys.ItemKey { Mode = BundledContentHolder.Mode.Prop, Url = url, Pass = "x", EmbeddedSettings = BasisDataStoreItemKeys.EmbeddedSettings.Addressable };

    [Test]
    public void FolderIsRegisteredAsAnInlinePayload()
    {
        Assert.IsTrue(BasisContentSharePayloadRegistry.TryGet(ContentShareType.Folder, out BasisContentSharePayloadKind kind));
        Assert.AreEqual(BasisShareableKind.Folder, kind.ShareableKind);
        Assert.IsTrue(ContentSharePayload.IsPayloadType(ContentShareType.Folder));
        Assert.IsTrue(BasisContentSharePayloadRegistry.IsHandled(ContentShareType.Folder));
    }

    [Test]
    public void PayloadKeepsOnlyWhatOtherPlayersCanLoad()
    {
        List<BasisDataStoreItemKeys.ItemKey> items = new List<BasisDataStoreItemKeys.ItemKey>
        {
            Key(BundledContentHolder.Mode.Avatar, "https://example.com/a.BEE"),
            Key(BundledContentHolder.Mode.Prop, "https://example.com/p.BEE"),
            Key(BundledContentHolder.Mode.Prop, "https://example.com/p.BEE"),
            Key(BundledContentHolder.Mode.World, "file:///C:/Worlds/local.BEE"),
            Key(BundledContentHolder.Mode.Prop, "https://example.com/no-password.BEE", string.Empty),
            Key(BundledContentHolder.Mode.Legacy, "https://example.com/legacy.BEE"),
            EmbeddedKey("Packages/com.basis.sdk/Prefabs/Thing.prefab"),
        };

        BasisContentShareFolder.BuildResult result = BasisContentShareFolder.BuildPayload(FolderId, "Kit", items, item => "Name of " + item.Mode, out string payload, out int shared, out int skipped);

        Assert.AreEqual(BasisContentShareFolder.BuildResult.Ok, result);
        Assert.AreEqual(2, shared);
        Assert.AreEqual(4, skipped);
        Assert.IsTrue(ContentShareFolder.TryRead(payload, out ContentShareFolder.Contents folder));
        Assert.AreEqual(FolderId, folder.Id);
        Assert.AreEqual("Kit", folder.Name);
        Assert.AreEqual(2, folder.Items.Count);
        Assert.AreEqual(ContentShareType.Avatar, folder.Items[0].Type);
        Assert.AreEqual(ContentShareType.Prop, folder.Items[1].Type);
        Assert.AreEqual("Name of Prop", folder.Items[1].Name);
    }

    [Test]
    public void AFolderWithNothingShareableIsReportedAsSuch()
    {
        List<BasisDataStoreItemKeys.ItemKey> items = new List<BasisDataStoreItemKeys.ItemKey>
        {
            Key(BundledContentHolder.Mode.World, "file:///C:/Worlds/local.BEE"),
            EmbeddedKey("Packages/com.basis.sdk/Prefabs/Thing.prefab"),
        };
        Assert.AreEqual(BasisContentShareFolder.BuildResult.Nothing, BasisContentShareFolder.BuildPayload(FolderId, "Kit", items, null, out string payload, out int shared, out int skipped));
        Assert.IsNull(payload);
        Assert.AreEqual(0, shared);
        Assert.AreEqual(2, skipped);
        Assert.AreEqual(BasisContentShareFolder.BuildResult.Nothing, BasisContentShareFolder.BuildPayload(FolderId, "Kit", new List<BasisDataStoreItemKeys.ItemKey>(), null, out _, out _, out _));
    }

    [Test]
    public void NamesAreDroppedBeforeABigFolderIsRefused()
    {
        List<BasisDataStoreItemKeys.ItemKey> items = new List<BasisDataStoreItemKeys.ItemKey>();
        for (int i = 0; i < 100; i++) items.Add(Key(BundledContentHolder.Mode.Prop, $"https://cdn.example.com/content/{i:D4}/{new string('p', 40)}.BEE"));
        string longName = new string('n', ContentShareFolder.MaxItemNameLength);

        Assert.AreEqual(BasisContentShareFolder.BuildResult.Ok, BasisContentShareFolder.BuildPayload(FolderId, "Kit", items, _ => longName, out string payload, out int shared, out _));
        Assert.AreEqual(100, shared);
        Assert.LessOrEqual(payload.Length, ContentSharePayload.MaxLength);
        Assert.IsTrue(ContentShareFolder.TryRead(payload, out ContentShareFolder.Contents folder));
        Assert.AreEqual(string.Empty, folder.Items[0].Name);
        Assert.AreEqual(100, folder.Items.Count);
    }

    [Test]
    public void AFolderTooBigForOneShareIsRefused()
    {
        List<BasisDataStoreItemKeys.ItemKey> items = new List<BasisDataStoreItemKeys.ItemKey>();
        for (int i = 0; i <= ContentShareFolder.MaxItems; i++) items.Add(Key(BundledContentHolder.Mode.Prop, $"https://e.io/{i}"));
        Assert.AreEqual(BasisContentShareFolder.BuildResult.TooLarge, BasisContentShareFolder.BuildPayload(FolderId, "Kit", items, null, out string payload, out _, out _));
        Assert.IsNull(payload);

        List<BasisDataStoreItemKeys.ItemKey> huge = new List<BasisDataStoreItemKeys.ItemKey>();
        for (int i = 0; i < 60; i++) huge.Add(Key(BundledContentHolder.Mode.Prop, $"https://e.io/{i}/{new string('u', 400)}"));
        Assert.AreEqual(BasisContentShareFolder.BuildResult.TooLarge, BasisContentShareFolder.BuildPayload(FolderId, "Kit", huge, null, out _, out _, out _));
    }

    [Test]
    public void AReceivedFolderBecomesLibraryEntries()
    {
        ContentShareFolder.Item[] items =
        {
            new ContentShareFolder.Item { Type = ContentShareType.World, Url = "https://example.com/w.BEE", Password = "w-pass", Name = "World" },
            new ContentShareFolder.Item { Type = ContentShareType.Avatar, Url = "https://example.com/a.BEE", Password = "a-pass", Name = "Avatar" },
            new ContentShareFolder.Item { Type = ContentShareType.Prop, Url = "https://example.com/p.BEE", Password = "p-pass", Name = "Prop" },
        };
        Assert.IsTrue(ContentShareFolder.TryRead(ContentShareFolder.Write(FolderId, "Kit", items), out ContentShareFolder.Contents folder));

        List<BasisDataStoreItemKeys.ItemKey> keys = BasisContentShareFolder.ToItemKeys(folder);

        Assert.AreEqual(3, keys.Count);
        Assert.AreEqual(BundledContentHolder.Mode.World, keys[0].Mode);
        Assert.AreEqual(BundledContentHolder.Mode.Avatar, keys[1].Mode);
        Assert.AreEqual(BundledContentHolder.Mode.Prop, keys[2].Mode);
        for (int i = 0; i < keys.Count; i++)
        {
            Assert.AreEqual(items[i].Url, keys[i].Url);
            Assert.AreEqual(items[i].Password, keys[i].Pass);
            Assert.AreEqual(BundledContentHolder.PlacementType.SpawnAtRaycast, keys[i].PlacementType);
            Assert.IsFalse(keys[i].EmbeddedSettings.IsEmbedded);
        }
    }

    [Test]
    public void AFolderCodePastedFromAnotherAppStillReads()
    {
        string payload = ContentShareFolder.Write(FolderId, "Kit", new[]
        {
            new ContentShareFolder.Item { Type = ContentShareType.Prop, Url = "https://example.com/p.BEE", Password = "p\tass", Name = "Prop" },
        });
        string pasted = BasisContentShareText.Normalize("  " + payload.Replace("\n", "\r\n") + "\r\n  ");

        Assert.IsTrue(ContentShareFolder.IsFolderText(pasted));
        Assert.IsTrue(ContentShareFolder.TryRead(pasted, out ContentShareFolder.Contents folder));
        Assert.AreEqual("p\tass", folder.Items[0].Password);
        Assert.IsFalse(ContentShareFolder.IsFolderText(BasisContentShareText.Normalize("look: " + payload)));
    }

    [Test]
    public void OrbLabelIsShortAndCountsTheItems()
    {
        ContentShareFolder.Item[] items =
        {
            new ContentShareFolder.Item { Type = ContentShareType.Prop, Url = "https://example.com/p.BEE", Password = "p" },
            new ContentShareFolder.Item { Type = ContentShareType.Prop, Url = "https://example.com/q.BEE", Password = "q" },
        };
        string payload = ContentShareFolder.Write(FolderId, "A very long folder name that keeps going", items);
        string label = BasisContentSharePayloadRegistry.Describe(ContentShareType.Folder, payload);
        StringAssert.EndsWith("2", label);
        Assert.LessOrEqual(label.Length, BasisContentSphere.MaxTitleNameLength);
        Assert.IsNull(BasisContentSharePayloadRegistry.Describe(ContentShareType.Folder, "not a folder"));
    }

    [Test]
    public void FolderEntriesResolveAgainstTheLibraryByAddressAndPassword()
    {
        BasisDataStoreItemKeys.ItemKey first = Key(BundledContentHolder.Mode.Prop, "https://example.com/a.BEE", "one");
        BasisDataStoreItemKeys.ItemKey second = Key(BundledContentHolder.Mode.Prop, "https://example.com/a.BEE", "two");
        BasisDataStoreItemKeys.ItemKey other = Key(BundledContentHolder.Mode.Avatar, "https://example.com/b.BEE", "one");
        BasisDataStoreItemKeys.ItemKey embedded = EmbeddedKey("Packages/com.basis.sdk/Prefabs/Thing.prefab");
        BasisDataStoreItemFolders.ItemFolder folder = new BasisDataStoreItemFolders.ItemFolder
        {
            Id = FolderId,
            Name = "Kit",
            Items = new[]
            {
                new BasisDataStoreItemFolders.FolderItem { Url = "https://example.com/a.BEE", Pass = "two" },
                new BasisDataStoreItemFolders.FolderItem { Url = "Packages/com.basis.sdk/Prefabs/Thing.prefab", Pass = "anything" },
                new BasisDataStoreItemFolders.FolderItem { Url = "https://example.com/removed.BEE", Pass = "one" },
            },
        };

        List<BasisDataStoreItemKeys.ItemKey> resolved = BasisDataStoreItemFolders.Resolve(folder, new List<BasisDataStoreItemKeys.ItemKey> { first, second, other, embedded });

        CollectionAssert.AreEqual(new[] { second, embedded }, resolved);
        Assert.IsTrue(BasisDataStoreItemFolders.Contains(folder, second));
        Assert.IsFalse(BasisDataStoreItemFolders.Contains(folder, first));
        Assert.IsFalse(BasisDataStoreItemFolders.Contains(folder, other));
    }

    [Test]
    public void FolderNamesAreCleanedTheSameWayTheShareCleansThem()
    {
        Assert.AreEqual("Event Kit", BasisDataStoreItemFolders.CleanName("  Event\n\tKit  "));
        Assert.AreEqual(ContentShareFolder.MaxNameLength, BasisDataStoreItemFolders.CleanName(new string('x', 500)).Length);
        Assert.AreEqual(string.Empty, BasisDataStoreItemFolders.CleanName(null));
    }
}
