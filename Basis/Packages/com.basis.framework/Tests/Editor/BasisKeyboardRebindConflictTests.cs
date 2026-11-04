using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Basis.Framework.Tests
{
    public class BasisKeyboardRebindConflictTests
    {
        private InputActionAsset asset;
        private InputAction jump;
        private InputAction move;
        private InputAction mic;
        private InputAction primary;

        [SetUp]
        public void SetUp()
        {
            asset = ScriptableObject.CreateInstance<InputActionAsset>();
            InputActionMap map = asset.AddActionMap("Player");
            move = map.AddAction("Move");
            move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            jump = map.AddAction("Jump", binding: "<Keyboard>/space");
            mic = map.AddAction("ToggleMicMute", binding: "<Keyboard>/v");
            primary = map.AddAction("Primary Controller A", binding: "<Keyboard>/v");
        }

        [TearDown]
        public void TearDown()
        {
            if (asset != null)
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void AKeyAnotherActionUsesIsRefused()
        {
            Assert.That(SettingsProviderKeyboardBindings.IsBoundElsewhere(asset, jump, 0, "<Keyboard>/w"), Is.True);
            Assert.That(SettingsProviderKeyboardBindings.IsBoundElsewhere(asset, jump, 0, "<Keyboard>/W"), Is.True);
            Assert.That(SettingsProviderKeyboardBindings.IsBoundElsewhere(asset, jump, 0, "<Keyboard>/j"), Is.False);
        }

        [Test]
        public void AnotherPartOfTheSameCompositeCountsAsTaken()
        {
            int up = PartIndex(move, "Up");

            Assert.That(SettingsProviderKeyboardBindings.IsBoundElsewhere(asset, move, up, "<Keyboard>/s"), Is.True);
            Assert.That(SettingsProviderKeyboardBindings.IsBoundElsewhere(asset, move, up, "<Keyboard>/w"), Is.False, "its own key is not a conflict");
        }

        [Test]
        public void AKeyFreedByAnEarlierRebindCanBeTakenAgain()
        {
            jump.ApplyBindingOverride(0, "<Keyboard>/j");

            Assert.That(SettingsProviderKeyboardBindings.IsBoundElsewhere(asset, move, PartIndex(move, "Up"), "<Keyboard>/space"), Is.False);
        }

        [Test]
        public void GoingBackToADefaultThatShipsSharedIsAllowed()
        {
            mic.ApplyBindingOverride(0, "<Keyboard>/m");

            Assert.That(SettingsProviderKeyboardBindings.IsBoundElsewhere(asset, mic, 0, "<Keyboard>/v"), Is.False, "V was shared with Primary Controller A out of the box");
            Assert.That(SettingsProviderKeyboardBindings.IsBoundElsewhere(asset, jump, 0, "<Keyboard>/v"), Is.True, "only the binding that shipped on V may share it");
        }

        [Test]
        public void GoingBackToADefaultSomethingElseWasMovedOntoIsRefused()
        {
            jump.ApplyBindingOverride(0, "<Keyboard>/j");
            move.ApplyBindingOverride(PartIndex(move, "Up"), "<Keyboard>/space");

            Assert.That(SettingsProviderKeyboardBindings.IsBoundElsewhere(asset, jump, 0, "<Keyboard>/space"), Is.True);
            Assert.That(primary.bindings[0].effectivePath, Is.EqualTo("<Keyboard>/v"));
        }

        private static int PartIndex(InputAction action, string part)
        {
            for (int Index = 0; Index < action.bindings.Count; Index++)
            {
                if (action.bindings[Index].isPartOfComposite && action.bindings[Index].name == part)
                {
                    return Index;
                }
            }
            Assert.Fail($"{action.name} has no {part} part");
            return -1;
        }
    }
}
