using Basis.BasisUI;
using Cilbox;

namespace Basis.Shims
{
	public static class BasisSettingFavouritesShim
	{
		public static int GetCount(object self) => CilboxSettingFavourites.Allowed(self) ? BasisSettingFavourites.Count : 0;
		public static int GetVersion(object self) => CilboxSettingFavourites.Allowed(self) ? BasisSettingFavourites.Version : 0;
		public static string GetKey(object self, int index) => CilboxSettingFavourites.Allowed(self) && BasisSettingFavourites.TryGet(index, out BasisSettingFavourites.Entry entry) ? entry.Key : string.Empty;
		public static string GetTitle(object self, int index) => CilboxSettingFavourites.Allowed(self) && BasisSettingFavourites.TryGet(index, out BasisSettingFavourites.Entry entry) ? BasisSettingFavourites.DisplayTitle(entry) : string.Empty;
		public static bool IsOn(object self, int index) => CilboxSettingFavourites.Allowed(self) && BasisSettingFavourites.TryGet(index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.TryGetValue(entry.Key, out bool on) && on;
		public static bool SetOn(object self, int index, bool on) => CilboxSettingFavourites.Allowed(self) && BasisSettingFavourites.TryGet(index, out BasisSettingFavourites.Entry entry) && BasisSettingFavourites.SetValue(entry.Key, on);
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
	}
}
