using System;
using Basis.Scripts.BasisSdk.Players;
using HVR.Vixxy;

namespace Basis.Shims
{
    /// <summary>
    /// Metadata bridge for HVR Vixxy menu items, for avatar, prop and world scripts. `HVRVixxyMenuItem`
    /// is pinned to GetValue/ApplyValue/ResolveTitle/ResolveDescription/Control/Presentation and
    /// `HVRVixxyControl` to its read members; this flattens the rest to primitives and finds the
    /// local player's items for props and worlds. Writes still go through HVRVixxyMenuItem.ApplyValue.
    ///
    /// Vixxy menu items are wearer-only by design — on a remote avatar the control is never resolved and
    /// the value is meaningless. Scripts should gate on BasisAvatarShim.IsOwnedLocally before using any
    /// of this; <see cref="HasControl"/> is the cheap readiness check.
    /// </summary>
    public static class BasisVixxyShim
    {
        public static HVRVixxyMenuItem[] GetLocalMenuItems()
        {
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            return player != null && player.BasisAvatar != null ? player.BasisAvatar.GetComponentsInChildren<HVRVixxyMenuItem>(true) : Array.Empty<HVRVixxyMenuItem>();
        }

        public static HVRVixxyMenuItem FindLocalMenuItem(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            HVRVixxyMenuItem[] items = GetLocalMenuItems();
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] != null && (items[i].name == title || items[i].ResolveTitle() == title)) return items[i];
            }
            return null;
        }

        public static bool HasControl(HVRVixxyMenuItem item)
        {
            return item != null && item.Control != null;
        }

        public static float DefaultValue(HVRVixxyMenuItem item)
        {
            return HasControl(item) ? item.Control.defaultValue : 0f;
        }

        public static float MinValue(HVRVixxyMenuItem item)
        {
            return AllChoicesValid(item) ? item.Control.Min() : 0f;
        }

        public static float MaxValue(HVRVixxyMenuItem item)
        {
            return AllChoicesValid(item) ? item.Control.Max() : 0f;
        }

        public static int ChoiceCount(HVRVixxyMenuItem item)
        {
            if (HasControl(item) == false || item.Control.choices == null)
            {
                return 0;
            }

            return item.Control.choices.Length;
        }

        public static float ChoiceValue(HVRVixxyMenuItem item, int index)
        {
            return IsValidChoice(item, index) ? item.Control.choices[index].value : 0f;
        }

        public static string ChoiceTitle(HVRVixxyMenuItem item, int index)
        {
            return IsValidChoice(item, index) ? item.Control.choices[index].title : string.Empty;
        }

        /// <summary>True for a plain two-state OFF/ON control, so a script can drive it with Min/Max.</summary>
        public static bool IsToggle(HVRVixxyMenuItem item)
        {
            return ChoiceCount(item) >= 2 && AllChoicesValid(item) && item.Control.IsRegularToggle;
        }

        public static bool IsSlider(HVRVixxyMenuItem item)
        {
            return item != null && item.Presentation == HVRVixxyControlPresentation.Slider;
        }

        public static string Title(HVRVixxyMenuItem item)
        {
            return item != null ? item.ResolveTitle() ?? string.Empty : string.Empty;
        }

        public static string Description(HVRVixxyMenuItem item)
        {
            return item != null ? item.ResolveDescription() ?? string.Empty : string.Empty;
        }

        private static bool IsValidChoice(HVRVixxyMenuItem item, int index)
        {
            return index >= 0 && index < ChoiceCount(item) && item.Control.choices[index] != null;
        }

        // Min()/Max()/IsRegularToggle dereference choice entries, so a half-authored control would throw
        // an NRE out of host code on a sandboxed call. Verify before handing them the array.
        private static bool AllChoicesValid(HVRVixxyMenuItem item)
        {
            int count = ChoiceCount(item);
            if (count == 0)
            {
                return false;
            }

            HVRVixxyChoiceControl[] choices = item.Control.choices;
            for (int i = 0; i < count; i++)
            {
                if (choices[i] == null)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
