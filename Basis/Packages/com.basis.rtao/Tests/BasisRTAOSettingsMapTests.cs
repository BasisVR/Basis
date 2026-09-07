using NUnit.Framework;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOSettingsMapTests
    {
        [Test]
        public void ModeReadsTheDropdownEntries()
        {
            Assert.AreEqual(BasisRTAOTracingMode.ScreenSpace, BasisRTAOSettingsMap.ReadMode("Auto"),
                "Auto was removed, but a settings file written before that still says it. It has to land on the new default rather than on the expensive backend it used to pick.");
            Assert.AreEqual(BasisRTAOTracingMode.RayTracedOnly, BasisRTAOSettingsMap.ReadMode("Ray Traced"));
            Assert.AreEqual(BasisRTAOTracingMode.ScreenSpace, BasisRTAOSettingsMap.ReadMode("Screen Space"));
        }

        [Test]
        public void ModeSurvivesTheLowercasingTheSettingsSystemApplies()
        {
            Assert.AreEqual(BasisRTAOTracingMode.RayTracedOnly, BasisRTAOSettingsMap.ReadMode("ray traced"),
                "Values reach the modules already lowercased, so the parser has to accept that form.");
            Assert.AreEqual(BasisRTAOTracingMode.ScreenSpace, BasisRTAOSettingsMap.ReadMode("screen space"));
            Assert.AreEqual(BasisRTAOTracingMode.ScreenSpace, BasisRTAOSettingsMap.ReadMode("  SCREEN SPACE  "));
        }

        [Test]
        public void UnknownModeFallsBackToAuto()
        {
            Assert.AreEqual(BasisRTAOTracingMode.ScreenSpace, BasisRTAOSettingsMap.ReadMode(null));
            Assert.AreEqual(BasisRTAOTracingMode.ScreenSpace, BasisRTAOSettingsMap.ReadMode(string.Empty));
            Assert.AreEqual(BasisRTAOTracingMode.ScreenSpace, BasisRTAOSettingsMap.ReadMode("nonsense"),
                "A stale settings file must not turn the effect off, it must land on the safe default.");
        }

        [Test]
        public void QualityReadsEveryTier()
        {
            Assert.AreEqual(BasisRTAOQuality.Low, BasisRTAOSettingsMap.ReadQuality("Low"));
            Assert.AreEqual(BasisRTAOQuality.Medium, BasisRTAOSettingsMap.ReadQuality("Medium"));
            Assert.AreEqual(BasisRTAOQuality.High, BasisRTAOSettingsMap.ReadQuality("high"));
            Assert.AreEqual(BasisRTAOQuality.Ultra, BasisRTAOSettingsMap.ReadQuality("ULTRA"));
            Assert.AreEqual(BasisRTAOQuality.Medium, BasisRTAOSettingsMap.ReadQuality("who knows"));
        }

        [Test]
        public void SkinnedModeReadsEveryEntry()
        {
            Assert.AreEqual(BasisRTAOSkinnedMode.Off, BasisRTAOSettingsMap.ReadSkinnedMode("Off"));
            Assert.AreEqual(BasisRTAOSkinnedMode.Proxy, BasisRTAOSettingsMap.ReadSkinnedMode("Proxy"));
        }

        /// <summary>
        /// The mask on the ray is what keeps two effects honest about wanting different halves of one
        /// shared structure, so every option has to name a non-empty set, and the three have to nest the
        /// same way the layer sets do.
        /// </summary>
        [Test]
        public void TraceCategoriesFollowTheLayerOption()
        {
            BasisRTAOSceneSettings settings = BasisRTAOSceneSettings.Default;
            if (BasisRTAOSceneSettings.AvatarLayers.value == ~0) { Assert.Ignore("The avatar layers are not present in this project."); }

            settings.layerMask = BasisRTAOSettingsMap.ReadLayers("Avatars");
            Assert.AreEqual(BasisTracedCategory.Avatar, settings.TraceCategories);

            settings.layerMask = BasisRTAOSettingsMap.ReadLayers("World");
            Assert.AreEqual(BasisTracedCategory.World, settings.TraceCategories);

            settings.layerMask = BasisRTAOSettingsMap.ReadLayers("World And Avatars");
            Assert.AreEqual(BasisTracedCategory.All, settings.TraceCategories,
                "The widest option has to ask for both halves, or borrowing a shared structure would silently drop one of them.");
        }

        [Test]
        public void AnUnrecognisedLayerSetStillTracesSomething()
        {
            BasisRTAOSceneSettings settings = BasisRTAOSceneSettings.Default;
            settings.layerMask = 0;
            Assert.AreEqual(BasisTracedCategory.All, settings.TraceCategories,
                "A mask of zero would be a ray that can hit nothing at all, which reads as the effect being broken rather than as a narrow setting.");
        }

        [Test]
        public void CategoriesFollowTheLayerAnObjectIsOn()
        {
            const int avatarMask = (1 << 6) | (1 << 7);
            // AvatarMesh rather than the combined Avatar: For classifies renderers, and a renderer is never
            // a proxy capsule. Returning the combined value would let real geometry into the proxy-only
            // trace, where a back face is treated as a hull to step out of rather than as occlusion.
            Assert.AreEqual(BasisTracedCategory.AvatarMesh, BasisTracedCategory.For(6, avatarMask));
            Assert.AreEqual(BasisTracedCategory.AvatarMesh, BasisTracedCategory.For(7, avatarMask));
            Assert.AreEqual(BasisTracedCategory.World, BasisTracedCategory.For(0, avatarMask),
                "Default is the room, not a person.");
            Assert.AreNotEqual(0, BasisTracedCategory.Avatar & BasisTracedCategory.All);
            Assert.AreNotEqual(0, BasisTracedCategory.World & BasisTracedCategory.All);

            // The three the ambient occlusion kernel leans on. It splits one trace into real geometry and
            // body proxies so it can step out of a capsule it started inside without stepping through the
            // back of a wall, and that split is only sound while these hold.
            Assert.AreEqual(0, BasisTracedCategory.For(6, avatarMask) & BasisTracedCategory.AvatarProxy,
                "A renderer classified as a proxy would enter the proxy-only trace, where a back face is a hull to step out of rather than occlusion.");
            Assert.AreEqual(0, BasisTracedCategory.For(0, avatarMask) & BasisTracedCategory.AvatarProxy,
                "Room geometry classified as a proxy would let rays walk straight through the back of a wall.");
            Assert.AreNotEqual(0, BasisTracedCategory.Avatar & BasisTracedCategory.AvatarProxy,
                "Asking for avatars has to reach their capsules, or people stop occluding entirely - the capsules ARE the bodies.");
            Assert.AreNotEqual(0, BasisTracedCategory.All & BasisTracedCategory.AvatarProxy,
                "The widest set has to include the capsules too.");
        }

        [Test]
        public void LayerOptionsRoundTripThroughTheDropdownString()
        {
            foreach (string option in new[] { "Avatars", "World", "World And Avatars" })
                Assert.AreEqual(option, BasisRTAOSettingsMap.WriteLayers(BasisRTAOSettingsMap.ReadLayers(option)));
        }

        /// <summary>
        /// Avatars and World must be disjoint and must together be exactly World + Avatars. Without that
        /// the three rows overlap, and a player who picks World still pays for the avatars they thought
        /// they had just excluded.
        /// </summary>
        [Test]
        public void LayerSetsPartitionCleanly()
        {
            int avatars = BasisRTAOSettingsMap.ReadLayers("Avatars").value;
            int world = BasisRTAOSettingsMap.ReadLayers("World").value;
            int both = BasisRTAOSettingsMap.ReadLayers("World And Avatars").value;

            // AvatarLayers answers ~0 when neither named layer exists, which makes World empty and the
            // partition meaningless. LayersMatchTheirNames is the test that owns that failure.
            if (avatars == ~0) { Assert.Ignore("The avatar layers are not present in this project."); }
            Assert.AreNotEqual(0, avatars, "The avatar layers have to resolve, or every option is the same set.");
            Assert.AreEqual(0, avatars & world, "World must not carry the avatar layers.");
            Assert.AreEqual(both, avatars | world, "The two halves have to add up to the combined set.");
        }

        [Test]
        public void RetiredLayerNamesStillReadAsTheWidestSet()
        {
            int both = BasisRTAOSettingsMap.ReadLayers("World And Avatars").value;
            Assert.AreEqual(both, BasisRTAOSettingsMap.ReadLayers("Avatars And World").value,
                "The same intent under the name earlier builds wrote.");
            Assert.AreEqual(both, BasisRTAOSettingsMap.ReadLayers("Everything").value,
                "Everything was already interface filtered, so it was this set under another name.");
        }

        [Test]
        public void UnknownLayerValueStaysOnAvatars()
        {
            Assert.AreEqual(BasisRTAOSceneSettings.AvatarLayers.value, BasisRTAOSettingsMap.ReadLayers("garbage").value,
                "Tracing the whole world is the expensive answer, so an unreadable value must not opt the player into it.");
            Assert.AreEqual(BasisRTAOSceneSettings.AvatarLayers.value, BasisRTAOSettingsMap.ReadLayers(null).value);
        }

        [Test]
        public void RetiredModesStillReadAsProxy()
        {
            Assert.AreEqual(BasisRTAOSkinnedMode.Proxy, BasisRTAOSettingsMap.ReadSkinnedMode("static"),
                "A settings file written before Static was removed still says Static, and it meant avatars are in the structure.");
            Assert.AreEqual(BasisRTAOSkinnedMode.Proxy, BasisRTAOSettingsMap.ReadSkinnedMode("Dynamic"),
                "Same for Dynamic: answering Off here would silently take avatar occlusion away from anyone who had it on.");
        }

        [Test]
        public void UnknownSkinnedModeStaysOff()
        {
            Assert.AreEqual(BasisRTAOSkinnedMode.Off, BasisRTAOSettingsMap.ReadSkinnedMode("everything"),
                "Baking skinned meshes costs CPU every frame, so an unreadable value must not opt the player in.");
            Assert.AreEqual(BasisRTAOSkinnedMode.Off, BasisRTAOSettingsMap.ReadSkinnedMode(null));
        }

        [Test]
        public void EveryModeRoundTripsThroughTheDropdownString()
        {
            foreach (BasisRTAOTracingMode mode in new[] { BasisRTAOTracingMode.RayTracedOnly, BasisRTAOTracingMode.ScreenSpace })
                Assert.AreEqual(mode, BasisRTAOSettingsMap.ReadMode(BasisRTAOSettingsMap.WriteMode(mode)));
        }

        [Test]
        public void EveryQualityRoundTripsThroughTheDropdownString()
        {
            foreach (BasisRTAOQuality quality in System.Enum.GetValues(typeof(BasisRTAOQuality)))
                Assert.AreEqual(quality, BasisRTAOSettingsMap.ReadQuality(BasisRTAOSettingsMap.WriteQuality(quality)));
        }

        [Test]
        public void EverySkinnedModeRoundTripsThroughTheDropdownString()
        {
            foreach (BasisRTAOSkinnedMode mode in System.Enum.GetValues(typeof(BasisRTAOSkinnedMode)))
                Assert.AreEqual(mode, BasisRTAOSettingsMap.ReadSkinnedMode(BasisRTAOSettingsMap.WriteSkinnedMode(mode)));
        }

        [Test]
        public void WrittenStringsMatchTheDropdownEntriesTheUiRegisters()
        {
            Assert.AreEqual("Screen Space", BasisRTAOSettingsMap.WriteMode(BasisRTAOTracingMode.ScreenSpace));
            Assert.AreEqual("Ray Traced", BasisRTAOSettingsMap.WriteMode(BasisRTAOTracingMode.RayTracedOnly));
            Assert.AreEqual("Screen Space", BasisRTAOSettingsMap.WriteMode(BasisRTAOTracingMode.ScreenSpace));
            Assert.AreEqual("Off", BasisRTAOSettingsMap.WriteSkinnedMode(BasisRTAOSkinnedMode.Off));
            Assert.AreEqual("Proxy", BasisRTAOSettingsMap.WriteSkinnedMode(BasisRTAOSkinnedMode.Proxy));
        }

        [Test]
        public void DenoiseDropdownMapsToPassCounts()
        {
            Assert.AreEqual(0, BasisRTAOSettingsMap.ReadDenoisePasses("Off"));
            Assert.AreEqual(1, BasisRTAOSettingsMap.ReadDenoisePasses("Standard"));
            Assert.AreEqual(2, BasisRTAOSettingsMap.ReadDenoisePasses("High"));
            Assert.AreEqual(3, BasisRTAOSettingsMap.ReadDenoisePasses("Maximum"));
        }

        [Test]
        public void DenoiseDropdownSurvivesLowercasingAndNonsense()
        {
            Assert.AreEqual(0, BasisRTAOSettingsMap.ReadDenoisePasses("off"));
            Assert.AreEqual(3, BasisRTAOSettingsMap.ReadDenoisePasses("  MAXIMUM "));
            Assert.AreEqual(2, BasisRTAOSettingsMap.ReadDenoisePasses("who knows"),
                "An unreadable value must land on the shipping default, not on Off.");
        }

        [Test]
        public void EveryDenoiseLevelRoundTrips()
        {
            for (int passes = 0; passes <= 3; passes++)
                Assert.AreEqual(passes, BasisRTAOSettingsMap.ReadDenoisePasses(BasisRTAOSettingsMap.WriteDenoisePasses(passes)));
        }

        [Test]
        public void ApplyModeMapsToTheDropdown()
        {
            Assert.AreEqual(BasisRTAOApplyMode.Lighting, BasisRTAOSettingsMap.ReadApplyMode("Lighting"));
            Assert.AreEqual(BasisRTAOApplyMode.AfterOpaque, BasisRTAOSettingsMap.ReadApplyMode("Final Image"));
            Assert.AreEqual(BasisRTAOApplyMode.AfterOpaque, BasisRTAOSettingsMap.ReadApplyMode("after opaque"),
                "URP calls this After Opaque, so a settings file written against that name must still read.");
            Assert.AreEqual(BasisRTAOApplyMode.Lighting, BasisRTAOSettingsMap.ReadApplyMode("nonsense"));
        }

        [Test]
        public void EveryApplyModeRoundTrips()
        {
            foreach (BasisRTAOApplyMode mode in System.Enum.GetValues(typeof(BasisRTAOApplyMode)))
                Assert.AreEqual(mode, BasisRTAOSettingsMap.ReadApplyMode(BasisRTAOSettingsMap.WriteApplyMode(mode)));
        }

        [Test]
        public void ScreenSpaceSettingResolvesToTheFallbackOnARayTracingGpu()
        {
            BasisRTAOTracingMode mode = BasisRTAOSettingsMap.ReadMode("Screen Space");
            Assert.AreEqual(BasisRTAOBackend.ScreenSpace, BasisRTAOTracing.Resolve(mode, true, true),
                "Choosing Screen Space in the settings must be honoured even when the GPU could trace.");
        }

        [Test]
        public void AutoSettingResolvesToTheFallbackWithoutRayTracing()
        {
            BasisRTAOTracingMode mode = BasisRTAOSettingsMap.ReadMode("Screen Space");
            Assert.AreEqual(BasisRTAOBackend.ScreenSpace, BasisRTAOTracing.Resolve(mode, false, true));
        }
    }
}
