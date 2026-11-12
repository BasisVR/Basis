using Basis.BasisUI;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
	public static class BasisSettingFavouritesShim
	{
		public const int Toggle = 0, Slider = 1, Choice = 2, Button = 3;
		public static int GetCount(object self) => CilboxSettingFavourites.Allowed(self) ? BasisSettingFavourites.Count : 0;
		public static int GetVersion(object self) => CilboxSettingFavourites.Allowed(self) ? BasisSettingFavourites.Version : 0;
		public static string GetKey(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) ? entry.Key : string.Empty;
		public static string GetTitle(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) ? BasisSettingFavourites.DisplayTitle(entry) : string.Empty;
		public static int GetKind(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) ? (int)entry.Kind : -1;

		public static bool IsOn(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.TryGetValue(entry.Key, out bool on) && on;
		public static bool SetOn(object self, int index, bool on) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.SetValue(entry.Key, on);
		public static bool Press(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.Press(entry.Key);

		public static float GetValue(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.TryGetSlider(entry.Key, out float value) ? value : 0f;
		public static float GetMin(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) ? entry.Slider.SliderMin : 0f;
		public static float GetMax(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) ? entry.Slider.SliderMax : 0f;
		public static bool IsWholeNumbers(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && entry.Slider.UseWholeNumbers;
		public static float GetNormalized(object self, int index)
		{
			if (!CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) || !BasisSettingFavourites.TryGetSlider(entry.Key, out float value)) return 0f;
			return Mathf.InverseLerp(entry.Slider.SliderMin, entry.Slider.SliderMax, value);
		}
		public static string GetValueText(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.TryGetSlider(entry.Key, out float value) ? BasisSettingFavourites.FormatSlider(entry, value) : string.Empty;
		public static string FormatValue(object self, int index, float value) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && entry.Kind == BasisSettingFavourites.Kind.Slider ? BasisSettingFavourites.FormatSlider(entry, BasisSettingFavourites.Conform(entry, value)) : string.Empty;
		public static bool SetValue(object self, int index, float value) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.SetSlider(entry.Key, value);
		public static bool SetNormalized(object self, int index, float normalized) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.SetSlider(entry.Key, Mathf.Lerp(entry.Slider.SliderMin, entry.Slider.SliderMax, Mathf.Clamp01(normalized)));

		public static int GetOptionCount(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) ? entry.OptionCount : 0;
		public static string GetOptionLabel(object self, int index, int option) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) ? BasisSettingFavourites.OptionLabel(entry, option) : string.Empty;
		public static int GetSelected(object self, int index) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.TryGetChoice(entry.Key, out int option) ? option : -1;
		public static bool Select(object self, int index, int option) => CilboxSettingFavourites.Get(self, index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.SetChoice(entry.Key, option);
	}
}

namespace Cilbox
{
	internal static class CilboxSettingFavourites
	{
		internal static bool Allowed(object self)
		{
			CilboxProxy proxy = self as CilboxProxy;
			CilboxBasisCommon box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			return proxy != null && !proxy.disabled && box != null && !box.disabled && box.interpreterAccountingDepth != 0 && CilboxBasisConsent.ForLocalPlayer(box, proxy.gameObject);
		}
		internal static bool Get(object self, int index, out BasisSettingFavourites.Entry entry)
		{
			entry = default;
			return Allowed(self) && BasisSettingFavourites.TryGet(index, out entry);
		}
	}
}
