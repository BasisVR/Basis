using Basis.BasisUI;
using NUnit.Framework;

namespace Basis.Tests.UI
{
    /// <summary>
    /// Covers the content type a dropped or pasted BEE is filed under. A world's component census
    /// is summed over every root in the scene, so it lists whatever props and avatars are standing
    /// in the world next to its BasisScene; reading that census last-one-wins filed a world holding
    /// a single prop under Props. The scene AssetMode on the bundle's sections is the signal that
    /// settles it, and only the SDK's scene build path ever writes it.
    ///
    /// The census has the same blind spot on prefab bundles, and there a build hook can create it:
    /// NDMF's, run over a prop, added a BasisAvatar to the build clone and every prop built with it
    /// installed then filed under Avatars. The ContentKind stamp names what the SDK was asked to
    /// build and outranks both signals; bundles built before it keep resolving off the census.
    /// </summary>
    public class BasisLibraryContentModeTests
    {
        private static BasisBundleConnector Connector(string assetMode, params string[] componentNames)
        {
            BasisBundleConnector connector = new BasisBundleConnector
            {
                BasisBundleGenerated = assetMode == null
                    ? null
                    : new[] { new BasisBundleGenerated { AssetMode = assetMode, Platform = "StandaloneWindows64" } },
            };

            if (componentNames != null)
            {
                BasisBundleConnector.BasisComponentName[] census = new BasisBundleConnector.BasisComponentName[componentNames.Length];
                for (int Index = 0; Index < componentNames.Length; Index++)
                {
                    census[Index] = new BasisBundleConnector.BasisComponentName { Name = componentNames[Index], count = 1 };
                }
                connector.MetaData.ComponentNames = census;
            }
            return connector;
        }

        [Test]
        public void SceneBundleHoldingAProp_IsAWorld()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.SceneAssetMode, "BasisScene", "BasisProp");

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.World),
                "a prop standing in a world does not make the world a prop.");
        }

        [Test]
        public void SceneBundleWithNoCensus_IsStillAWorld()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.SceneAssetMode);

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.World),
                "a world built before the component census must not fall through to the legacy prompt.");
        }

        [Test]
        public void SceneBundleForAnotherPlatform_IsStillAWorld()
        {
            BasisBundleConnector connector = new BasisBundleConnector
            {
                BasisBundleGenerated = new[]
                {
                    new BasisBundleGenerated { AssetMode = BasisBundleConnector.SceneAssetMode, Platform = "Android" },
                },
            };

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.World),
                "every section is read, not just the one this platform would load.");
        }

        [Test]
        public void SceneAssetModeIsMatchedCaseInsensitively()
        {
            BasisBundleConnector connector = Connector("scene");

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.World));
        }

        [TestCase("BasisProp", "BasisScene")]
        [TestCase("BasisScene", "BasisProp")]
        public void CensusOrderDoesNotDecideTheType(string first, string second)
        {
            BasisBundleConnector connector = Connector(null, first, second);

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.World),
                "the census lists what the bundle contains in hierarchy walk order, which is not a ranking.");
        }

        [Test]
        public void AvatarBeatsPropInTheCensus()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode, "BasisProp", "BasisAvatar");

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Avatar),
                "matches the SDK's own build-time rule: a BasisAvatar present makes it an avatar.");
        }

        [Test]
        public void PropBundle_IsAProp()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode, "BasisProp", "MeshRenderer");

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Prop));
        }

        [Test]
        public void AvatarBundle_IsAnAvatar()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode, "BasisAvatar", "SkinnedMeshRenderer");

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Avatar));
        }

        [Test]
        public void PrefabBundleWithNothingRecognisable_IsLegacy()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode, "MeshRenderer", "BoxCollider");

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Legacy),
                "nothing is guessed: the add dialog asks instead.");
        }

        [Test]
        public void EmptyCensusOnAPrefabBundle_IsLegacy()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode);

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Legacy));
        }

        [Test]
        public void NullCensusOnAPrefabBundle_IsLegacy()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode, (string[])null);

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Legacy));
        }

        [Test]
        public void NullConnector_IsLegacy()
        {
            Assert.That(LibraryProvider.ResolveModeFromConnector(null), Is.EqualTo(BundledContentHolder.Mode.Legacy),
                "an unreachable or unopenable bee must not be filed on a guess.");
        }

        [Test]
        public void NullSectionEntryIsSkipped()
        {
            BasisBundleConnector connector = new BasisBundleConnector
            {
                BasisBundleGenerated = new BasisBundleGenerated[] { null },
            };
            connector.MetaData.ComponentNames = new[]
            {
                new BasisBundleConnector.BasisComponentName { Name = "BasisProp", count = 1 },
            };

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Prop));
        }

        [Test]
        public void NullComponentNameIsSkipped()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode, null, "BasisProp");

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Prop));
        }

        [Test]
        public void ContentKindOutranksAnAvatarWeldedIntoAProp()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode, "BasisProp", "BasisAvatar", "NDMFAvatarRoot");
            connector.MetaData.ContentKind = BasisBundleConnector.PropContentKind;

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Prop),
                "a build hook adding a BasisAvatar to the build clone does not turn a prop into an avatar.");
        }

        [TestCase("Avatar", BundledContentHolder.Mode.Avatar)]
        [TestCase("Prop", BundledContentHolder.Mode.Prop)]
        [TestCase("Scene", BundledContentHolder.Mode.World)]
        [TestCase("prop", BundledContentHolder.Mode.Prop)]
        public void ContentKindStampDecidesTheType(string contentKind, BundledContentHolder.Mode expected)
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode);
            connector.MetaData.ContentKind = contentKind;

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(expected),
                "the stamp is written from the component the SDK built and is matched case-insensitively.");
        }

        [Test]
        public void SceneContentKindWinsOverAnAvatarStandingInTheWorld()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.SceneAssetMode, "BasisScene", "BasisAvatar");
            connector.MetaData.ContentKind = BasisBundleConnector.SceneContentKind;

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.World));
        }

        [Test]
        public void UnstampedBundleStillFallsBackToTheCensus()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode, "BasisProp");
            connector.MetaData.ContentKind = null;

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Prop),
                "bundles built before the stamp existed must keep resolving exactly as they did.");
        }

        [Test]
        public void UnrecognisedContentKindFallsBackToTheCensus()
        {
            BasisBundleConnector connector = Connector(BasisBundleConnector.GameObjectAssetMode, "BasisAvatar");
            connector.MetaData.ContentKind = "SomethingElse";

            Assert.That(LibraryProvider.ResolveModeFromConnector(connector), Is.EqualTo(BundledContentHolder.Mode.Avatar),
                "a stamp this client does not know is not a reason to refuse to file the bundle.");
        }
    }
}
