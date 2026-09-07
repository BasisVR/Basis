using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Performance
{
    /// <summary>
    /// The level table behind Performance Mode: which crowd size arms which level, and how a level
    /// survives the round trip through the settings file it is stored in.
    ///
    /// These are the parts that decide, without asking, that a player's instance is now heavy enough
    /// to start cutting quality. An off-by-one at a threshold means either a 251-player instance
    /// that never engages the mode, or a quiet instance that quietly turns the player's settings
    /// down; a broken id round trip means the level silently resets to Off on the next launch.
    /// </summary>
    public class BasisPerformanceModeLevelTests
    {
        [Test]
        public void AnEmptyOrQuietInstanceLeavesTheModeOff()
        {
            Assert.That(BasisPerformanceMode.LevelForPopulation(0), Is.EqualTo(BasisPerformanceLevel.Off));
            Assert.That(BasisPerformanceMode.LevelForPopulation(1), Is.EqualTo(BasisPerformanceLevel.Off));
            Assert.That(BasisPerformanceMode.LevelForPopulation(249), Is.EqualTo(BasisPerformanceLevel.Off));
        }

        [Test]
        public void ThresholdsAreExclusive()
        {
            // "Over 250" means 251. Sitting exactly on the number must not arm the level, or a
            // lobby that hovers at the cap flips the player's settings back and forth.
            int[] thresholds = BasisPerformanceMode.PopulationThresholds;
            for (int index = 0; index < thresholds.Length; index++)
            {
                Assert.That(BasisPerformanceMode.LevelForPopulation(thresholds[index]),
                    Is.EqualTo((BasisPerformanceLevel)index),
                    $"exactly {thresholds[index]} occupants stays on the level below");
                Assert.That(BasisPerformanceMode.LevelForPopulation(thresholds[index] + 1),
                    Is.EqualTo((BasisPerformanceLevel)(index + 1)),
                    $"{thresholds[index] + 1} occupants arms the next level");
            }
        }

        [Test]
        public void EachTierArmsItsOwnLevel()
        {
            Assert.That(BasisPerformanceMode.LevelForPopulation(300), Is.EqualTo(BasisPerformanceLevel.Light));
            Assert.That(BasisPerformanceMode.LevelForPopulation(600), Is.EqualTo(BasisPerformanceLevel.Balanced));
            Assert.That(BasisPerformanceMode.LevelForPopulation(1200), Is.EqualTo(BasisPerformanceLevel.Aggressive));
        }

        [Test]
        public void TheHeaviestLevelHasNoCeiling()
        {
            Assert.That(BasisPerformanceMode.LevelForPopulation(int.MaxValue),
                Is.EqualTo(BasisPerformanceLevel.Aggressive));
        }

        [Test]
        public void ANegativeCountIsTreatedAsEmpty()
        {
            // The occupant count comes from a live player list that can be read mid-teardown.
            Assert.That(BasisPerformanceMode.LevelForPopulation(-1), Is.EqualTo(BasisPerformanceLevel.Off));
        }

        [Test]
        public void PopulationLevelsNeverGoBackwards()
        {
            BasisPerformanceLevel previous = BasisPerformanceLevel.Off;
            for (int occupants = 0; occupants <= 1500; occupants++)
            {
                BasisPerformanceLevel level = BasisPerformanceMode.LevelForPopulation(occupants);
                Assert.That((int)level, Is.GreaterThanOrEqualTo((int)previous),
                    $"a busier instance must never ask for less trimming ({occupants} occupants)");
                previous = level;
            }
        }

        [Test]
        public void ThresholdsAreAscending()
        {
            int[] thresholds = BasisPerformanceMode.PopulationThresholds;
            for (int index = 1; index < thresholds.Length; index++)
            {
                Assert.That(thresholds[index], Is.GreaterThan(thresholds[index - 1]));
            }
            Assert.That(thresholds.Length, Is.EqualTo(3), "one threshold per non-Off level");
        }

        [Test]
        public void EveryLevelRoundTripsThroughItsStoredId()
        {
            foreach (BasisPerformanceLevel level in System.Enum.GetValues(typeof(BasisPerformanceLevel)))
            {
                string id = BasisPerformanceMode.LevelToId(level);
                Assert.That(BasisPerformanceMode.IdToLevel(id), Is.EqualTo(level), $"{level} did not survive the round trip");
            }
        }

        [Test]
        public void StoredIdsAreCaseInsensitive()
        {
            Assert.That(BasisPerformanceMode.IdToLevel("balanced"), Is.EqualTo(BasisPerformanceLevel.Balanced));
            Assert.That(BasisPerformanceMode.IdToLevel("BALANCED"), Is.EqualTo(BasisPerformanceLevel.Balanced));
            Assert.That(BasisPerformanceMode.IdToLevel("BaLaNcEd"), Is.EqualTo(BasisPerformanceLevel.Balanced));
        }

        [Test]
        public void AnUnreadableStoredValueFallsBackToOff()
        {
            // Never to a level that cuts quality: a corrupt settings file must not leave the
            // player wondering why their graphics look wrong.
            Assert.That(BasisPerformanceMode.IdToLevel(null), Is.EqualTo(BasisPerformanceLevel.Off));
            Assert.That(BasisPerformanceMode.IdToLevel(string.Empty), Is.EqualTo(BasisPerformanceLevel.Off));
            Assert.That(BasisPerformanceMode.IdToLevel("Extreme"), Is.EqualTo(BasisPerformanceLevel.Off));
        }

        [Test]
        public void EveryLevelReportsTheCountThatArmsIt()
        {
            Assert.That(BasisPerformanceMode.ThresholdFor(BasisPerformanceLevel.Off), Is.Zero);
            Assert.That(BasisPerformanceMode.ThresholdFor(BasisPerformanceLevel.Light),
                Is.EqualTo(BasisPerformanceMode.PopulationThresholds[0]));
            Assert.That(BasisPerformanceMode.ThresholdFor(BasisPerformanceLevel.Balanced),
                Is.EqualTo(BasisPerformanceMode.PopulationThresholds[1]));
            Assert.That(BasisPerformanceMode.ThresholdFor(BasisPerformanceLevel.Aggressive),
                Is.EqualTo(BasisPerformanceMode.PopulationThresholds[2]));
        }

        [Test]
        public void TheThresholdShownMatchesTheOneThatActuallyArms()
        {
            // The prompt copy and the auto-follow logic have to agree, or the prompt offers
            // Balanced "at 500" while the mode engages somewhere else.
            foreach (BasisPerformanceLevel level in System.Enum.GetValues(typeof(BasisPerformanceLevel)))
            {
                if (level == BasisPerformanceLevel.Off) continue;
                int threshold = BasisPerformanceMode.ThresholdFor(level);
                Assert.That((int)BasisPerformanceMode.LevelForPopulation(threshold + 1),
                    Is.GreaterThanOrEqualTo((int)level), $"{level} claims to arm just past {threshold}");
            }
        }

        [Test]
        public void EveryLevelHasItsOwnLocalizationKey()
        {
            System.Collections.Generic.HashSet<string> keys = new System.Collections.Generic.HashSet<string>();
            foreach (BasisPerformanceLevel level in System.Enum.GetValues(typeof(BasisPerformanceLevel)))
            {
                string key = BasisPerformanceMode.LocalizationKeyFor(level);
                Assert.That(string.IsNullOrEmpty(key), Is.False, $"{level} has no key");
                Assert.That(keys.Add(key), Is.True, $"{level} reuses the key {key}");
            }
        }

        [Test]
        public void TheAccentGetsWarmerAsTheLevelGetsHeavier()
        {
            Color off = BasisPerformanceMode.AccentColor(BasisPerformanceLevel.Off);
            Color light = BasisPerformanceMode.AccentColor(BasisPerformanceLevel.Light);
            Color balanced = BasisPerformanceMode.AccentColor(BasisPerformanceLevel.Balanced);
            Color aggressive = BasisPerformanceMode.AccentColor(BasisPerformanceLevel.Aggressive);

            Assert.That(off, Is.EqualTo(Color.white), "off keeps the section's normal styling.");
            Assert.That(light, Is.Not.EqualTo(balanced));
            Assert.That(balanced, Is.Not.EqualTo(aggressive));
            Assert.That(light.g, Is.GreaterThan(aggressive.g), "green through to red as the trimming gets harder.");
        }

        [Test]
        public void IsActiveMatchesTheLevelBeingSomethingOtherThanOff()
        {
            Assert.That(BasisPerformanceMode.IsActive,
                Is.EqualTo(BasisPerformanceMode.ActiveLevel != BasisPerformanceLevel.Off));
        }
    }
}
