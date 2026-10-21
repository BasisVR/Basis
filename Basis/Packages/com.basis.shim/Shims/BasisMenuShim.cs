using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Shims;
using Cilbox;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Shims
{
	public static class BasisMenuShim
	{
		public const int MaxSettingsTabs = 4, MaxPanelTabs = 8, MaxItems = 64, MaxOptions = 32, MaxTitleLength = 48, MaxTextLength = 512;
		public static BasisMenuPage AddSettingsTab(object self, string title) => CilboxMenus.AddSettingsTab(self, title);
		public static BasisMenuPage AddPanel(object self, string title) => CilboxMenus.AddPanel(self, title);
	}

	public sealed class BasisMenuPage
	{
		internal BasisMenuPage() { }
		public bool IsValid => CilboxMenus.Find(this) != null;
		public bool IsOpen => CilboxMenus.IsOpen(this);
		public BasisMenuPage AddTab(string title) => CilboxMenus.AddTab(this, title);
		public BasisMenuItem AddHeader(string text) => CilboxMenus.Add(this, CilboxMenus.Header, text, 0f, 0f, 0f, false, null, null);
		public BasisMenuItem AddLabel(string text) => CilboxMenus.Add(this, CilboxMenus.Label, text, 0f, 0f, 0f, false, null, null);
		public BasisMenuItem AddButton(string title, Action onClick) => CilboxMenus.Add(this, CilboxMenus.Button, title, 0f, 0f, 0f, false, null, onClick);
		public BasisMenuItem AddToggle(string title, bool value, Action<bool> onChanged) => CilboxMenus.Add(this, CilboxMenus.Toggle, title, value ? 1f : 0f, 0f, 1f, true, null, onChanged);
		public BasisMenuItem AddSlider(string title, float min, float max, float value, Action<float> onChanged) => CilboxMenus.Add(this, CilboxMenus.Slider, title, value, min, max, false, null, onChanged);
		public BasisMenuItem AddSlider(string title, float min, float max, float value, bool wholeNumbers, Action<float> onChanged) => CilboxMenus.Add(this, CilboxMenus.Slider, title, value, min, max, wholeNumbers, null, onChanged);
		public BasisMenuItem AddDropdown(string title, string[] options, int selected, Action<int> onChanged) => CilboxMenus.Add(this, CilboxMenus.Dropdown, title, selected, 0f, 0f, true, options, onChanged);
		public void SetTitle(string title) => CilboxMenus.SetTitle(this, title);
		public void SetVisible(bool visible) => CilboxMenus.SetVisible(this, visible);
		public void Clear() => CilboxMenus.Clear(this);
		public bool Open() => CilboxMenus.Open(this);
		public void Remove() => CilboxMenus.Remove(this);
	}

	public sealed class BasisMenuItem
	{
		internal BasisMenuItem() { }
		public bool IsValid => CilboxMenus.Find(this) != null;
		public float Value => CilboxMenus.ValueOf(this);
		public void SetText(string text) => CilboxMenus.SetText(this, text);
		public void SetTooltip(string text) => CilboxMenus.SetTooltip(this, text);
		public void SetValue(float value) => CilboxMenus.SetValue(this, value);
		public void SetOptions(string[] options) => CilboxMenus.SetOptions(this, options);
		public void SetVisible(bool visible) => CilboxMenus.SetVisible(this, visible);
		public void SetInteractable(bool interactable) => CilboxMenus.SetInteractable(this, interactable);
		public void Remove() => CilboxMenus.Remove(this);
	}
}

namespace Cilbox
{
	internal sealed class CilboxMenuPanel
	{
		internal CilboxMenuProvider Provider;
		internal readonly List<CilboxMenuPage> Pages = new List<CilboxMenuPage>();
		internal string Title;
		internal PanelTabGroup Group;
		internal CilboxMenuPage Pending;
	}

	internal sealed class CilboxMenuPage
	{
		internal BasisMenuPage Handle;
		internal CilboxBasisCommon Box;
		internal CilboxProxy Owner;
		internal BasisContentBase Content;
		internal CilboxMenuPanel Panel;
		internal string Key, Title, Source;
		internal int Kind, Tab = -1;
		internal bool Visible = true, Removed, Dirty, Layout, World;
		internal double NextBuild;
		internal PanelTabPage Page;
		internal readonly List<CilboxMenuItem> Items = new List<CilboxMenuItem>();
		internal readonly List<PanelElementDescriptor> Built = new List<PanelElementDescriptor>();
	}

	internal sealed class CilboxMenuItem
	{
		internal BasisMenuItem Handle;
		internal CilboxMenuPage Page;
		internal int Type;
		internal string Text, Tooltip;
		internal float Value, Initial, Min, Max;
		internal bool Whole, Visible = true, Interactable = true, Layout;
		internal string[] Options;
		internal Delegate Callback;
		internal PanelElementDescriptor Element;
		internal PanelComponent Control;
	}

	[AutoStaticsCleanup]
	internal static partial class CilboxMenus
	{
		internal const int Header = 0, Label = 1, Button = 2, Toggle = 3, Slider = 4, Dropdown = 5;
		internal const int KindWorld = 0, KindProp = 1, KindAvatar = 2;
		internal const int MaxContentSettingsTabs = 8, MaxPanels = 4, MaxSourceLength = 40, MaxLines = 8, MaxAddsPerWindow = 32;
		internal const float ActivationSeconds = 5f, PressGap = 0.01f;
		internal const double RebuildSeconds = 0.25d, AddWindowSeconds = 10d;
		internal const char Joiner = (char)0x2060;
		internal const string KeyPrefix = "content:";
		static readonly List<CilboxMenuPage> pages = new List<CilboxMenuPage>();
		static readonly List<CilboxMenuPanel> panels = new List<CilboxMenuPanel>();
		static readonly Dictionary<BasisMenuPage, CilboxMenuPage> byPage = new Dictionary<BasisMenuPage, CilboxMenuPage>();
		static readonly Dictionary<BasisMenuItem, CilboxMenuItem> byItem = new Dictionary<BasisMenuItem, CilboxMenuItem>();
		static readonly List<(BasisContentBase Content, float At)> opens = new List<(BasisContentBase Content, float At)>();
		static readonly StringBuilder text = new StringBuilder(64);
		static bool ticking;

		internal static int PageCount => pages.Count;
		internal static int PanelCount => panels.Count;

		internal static bool Blocks(Type type, string name)
		{
			if (type == typeof(BasisMenuShim)) return name != nameof(BasisMenuShim.AddSettingsTab) && name != nameof(BasisMenuShim.AddPanel);
			if (type == typeof(BasisMenuPage))
			{
				switch (name)
				{
					case "get_" + nameof(BasisMenuPage.IsValid):
					case "get_" + nameof(BasisMenuPage.IsOpen):
					case nameof(BasisMenuPage.AddTab):
					case nameof(BasisMenuPage.AddHeader):
					case nameof(BasisMenuPage.AddLabel):
					case nameof(BasisMenuPage.AddButton):
					case nameof(BasisMenuPage.AddToggle):
					case nameof(BasisMenuPage.AddSlider):
					case nameof(BasisMenuPage.AddDropdown):
					case nameof(BasisMenuPage.SetTitle):
					case nameof(BasisMenuPage.SetVisible):
					case nameof(BasisMenuPage.Clear):
					case nameof(BasisMenuPage.Open):
					case nameof(BasisMenuPage.Remove):
						return false;
				}
				return true;
			}
			if (type == typeof(BasisMenuItem))
			{
				switch (name)
				{
					case "get_" + nameof(BasisMenuItem.IsValid):
					case "get_" + nameof(BasisMenuItem.Value):
					case nameof(BasisMenuItem.SetText):
					case nameof(BasisMenuItem.SetTooltip):
					case nameof(BasisMenuItem.SetValue):
					case nameof(BasisMenuItem.SetOptions):
					case nameof(BasisMenuItem.SetVisible):
					case nameof(BasisMenuItem.SetInteractable):
					case nameof(BasisMenuItem.Remove):
						return false;
				}
				return true;
			}
			return false;
		}

		internal static BasisMenuPage AddSettingsTab(object self, string title)
		{
			CilboxBasisCommon box = Running(self, out CilboxProxy proxy);
			return box != null && Allowed(box, proxy, out BasisContentBase content) ? SettingsTab(box, proxy, content, title) : null;
		}

		internal static BasisMenuPage AddPanel(object self, string title)
		{
			CilboxBasisCommon box = Running(self, out CilboxProxy proxy);
			if (box == null || !Allowed(box, proxy, out BasisContentBase content)) return null;
			string name = Clean(title, BasisMenuShim.MaxTitleLength, false);
			if (name.Length == 0) return null;
			Purge();
			if (panels.Count >= MaxPanels || HasPanel(WorldOwned(box, content), content) || PanelTitleTaken(name) || !Spend(box)) return null;
			CilboxMenuPanel panel = new CilboxMenuPanel { Title = name };
			CilboxMenuPage page = NewPage(box, proxy, content, name);
			page.Panel = panel;
			panel.Pages.Add(page);
			panels.Add(panel);
			panel.Provider = new CilboxMenuProvider(panel);
			BasisMenuBase<BasisMainMenu>.AddProvider(panel.Provider);
			return page.Handle;
		}

		internal static BasisMenuPage AddTab(BasisMenuPage handle, string title)
		{
			CilboxMenuPage sibling = Mine(handle);
			if (sibling == null) return null;
			if (sibling.Panel == null) return SettingsTab(sibling.Box, sibling.Owner, sibling.Content, title);
			CilboxMenuPanel panel = sibling.Panel;
			string name = Clean(title, BasisMenuShim.MaxTitleLength, false);
			if (name.Length == 0) return null;
			Purge();
			if (panel.Pages.Count >= BasisMenuShim.MaxPanelTabs || !Spend(sibling.Box)) return null;
			bool shown = AnyVisible(panel);
			CilboxMenuPage page = NewPage(sibling.Box, sibling.Owner, sibling.Content, name);
			page.Panel = panel;
			panel.Pages.Add(page);
			CilboxMenuView.TabAdded(page, shown);
			return page.Handle;
		}

		internal static BasisMenuItem Add(BasisMenuPage handle, int type, string title, float value, float min, float max, bool whole, string[] options, Delegate callback)
		{
			CilboxMenuPage page = Mine(handle);
			if (page == null || page.Items.Count >= BasisMenuShim.MaxItems || !Accepts(page, callback)) return null;
			CilboxMenuItem item = new CilboxMenuItem { Handle = new BasisMenuItem(), Page = page, Type = type, Callback = callback, Text = Clean(title, type == Label ? BasisMenuShim.MaxTextLength : BasisMenuShim.MaxTitleLength, type == Label) };
			if (type == Slider)
			{
				if (!Finite(min) || !Finite(max) || !(max > min) || (whole && Mathf.Floor(max) < Mathf.Ceil(min))) return null;
				item.Min = min;
				item.Max = max;
				item.Whole = whole;
				item.Value = Clamp(item, value);
			}
			else if (type == Dropdown)
			{
				item.Options = Options(options);
				if (item.Options == null) return null;
				item.Value = Index(item, value);
			}
			else if (type == Toggle) item.Value = value != 0f ? 1f : 0f;
			item.Initial = item.Value;
			page.Items.Add(item);
			byItem[item.Handle] = item;
			page.Dirty = true;
			return item.Handle;
		}

		internal static CilboxMenuPage Find(BasisMenuPage handle) => handle != null && byPage.TryGetValue(handle, out CilboxMenuPage page) && !page.Removed ? page : null;

		internal static CilboxMenuItem Find(BasisMenuItem handle) => handle != null && byItem.TryGetValue(handle, out CilboxMenuItem item) && !item.Page.Removed ? item : null;

		internal static bool IsOpen(BasisMenuPage handle)
		{
			CilboxMenuPage page = Find(handle);
			return page != null && page.Visible && Shown(page);
		}

		static bool Shown(CilboxMenuPage page) => page.Panel == null ? SettingsProvider.IsContentTabShown(page.Key) : CilboxMenuView.IsShown(page);

		internal static float ValueOf(BasisMenuItem handle)
		{
			CilboxMenuItem item = Find(handle);
			return item != null ? item.Value : 0f;
		}

		internal static void SetTitle(BasisMenuPage handle, string title)
		{
			CilboxMenuPage page = Mine(handle);
			if (page == null) return;
			string name = Clean(title, BasisMenuShim.MaxTitleLength, false);
			if (name.Length == 0 || name == page.Title) return;
			if (page.Panel == null)
			{
				Purge();
				if (SettingsTitleTaken(name, page)) return;
				page.Title = name;
				SettingsProvider.RenameContentTab(page.Key, name);
			}
			else page.Title = name;
			CilboxMenuView.Retitle(page);
		}

		internal static void SetVisible(BasisMenuPage handle, bool visible)
		{
			CilboxMenuPage page = Mine(handle);
			if (page == null || page.Visible == visible) return;
			if (page.Panel == null)
			{
				page.Visible = visible;
				SettingsProvider.SetContentTabVisible(page.Key, visible);
				return;
			}
			bool shown = AnyVisible(page.Panel);
			page.Visible = visible;
			CilboxMenuView.Present(page, shown);
		}

		internal static void Clear(BasisMenuPage handle)
		{
			CilboxMenuPage page = Mine(handle);
			if (page == null || page.Items.Count == 0) return;
			for (int i = 0; i < page.Items.Count; i++) byItem.Remove(page.Items[i].Handle);
			page.Items.Clear();
			page.Dirty = true;
		}

		internal static bool Open(BasisMenuPage handle)
		{
			CilboxMenuPage page = Mine(handle);
			if (page == null || !page.Visible || (page.Panel != null && !AnyVisible(page.Panel))) return false;
			if (Shown(page)) return true;
			if (!Pressed(page.Content)) return false;
			if (page.Panel == null) SettingsProvider.OpenToTab(page.Key);
			else CilboxMenuView.Show(page);
			return true;
		}

		internal static void Remove(BasisMenuPage handle)
		{
			CilboxMenuPage page = Mine(handle);
			if (page != null) Remove(page);
		}

		internal static void SetText(BasisMenuItem handle, string value)
		{
			CilboxMenuItem item = Mine(handle);
			if (item == null) return;
			string clean = Clean(value, item.Type == Label ? BasisMenuShim.MaxTextLength : BasisMenuShim.MaxTitleLength, item.Type == Label);
			if (clean == item.Text) return;
			item.Text = clean;
			CilboxMenuView.ShowText(item);
		}

		internal static void SetTooltip(BasisMenuItem handle, string value)
		{
			CilboxMenuItem item = Mine(handle);
			if (item == null) return;
			item.Tooltip = Clean(value, BasisMenuShim.MaxTextLength, false);
			CilboxMenuView.ShowTooltip(item);
		}

		internal static void SetValue(BasisMenuItem handle, float value)
		{
			CilboxMenuItem item = Mine(handle);
			if (item == null) return;
			if (item.Type == Toggle) item.Value = value != 0f && !float.IsNaN(value) ? 1f : 0f;
			else if (item.Type == Slider) item.Value = Clamp(item, value);
			else if (item.Type == Dropdown) item.Value = Index(item, value);
			else return;
			CilboxMenuView.ShowValue(item);
		}

		internal static void SetOptions(BasisMenuItem handle, string[] options)
		{
			CilboxMenuItem item = Mine(handle);
			if (item == null || item.Type != Dropdown) return;
			string[] clean = Options(options);
			if (clean == null) return;
			item.Options = clean;
			item.Value = Index(item, item.Value);
			CilboxMenuView.ShowOptions(item);
		}

		internal static void SetVisible(BasisMenuItem handle, bool visible)
		{
			CilboxMenuItem item = Mine(handle);
			if (item == null || item.Visible == visible) return;
			item.Visible = visible;
			CilboxMenuView.ShowVisible(item);
		}

		internal static void SetInteractable(BasisMenuItem handle, bool interactable)
		{
			CilboxMenuItem item = Mine(handle);
			if (item == null || item.Interactable == interactable) return;
			item.Interactable = interactable;
			CilboxMenuView.ShowInteractable(item);
		}

		internal static void Remove(BasisMenuItem handle)
		{
			CilboxMenuItem item = Mine(handle);
			if (item == null) return;
			item.Page.Items.Remove(item);
			byItem.Remove(handle);
			item.Page.Dirty = true;
		}

		internal static void Clicked(CilboxMenuItem item)
		{
			if (Find(item.Handle) == item) Run(item);
		}

		internal static void Changed(CilboxMenuItem item, float value)
		{
			if (Find(item.Handle) != item) return;
			item.Value = item.Type == Slider ? Clamp(item, value) : item.Type == Dropdown ? Index(item, value) : value != 0f ? 1f : 0f;
			Run(item);
		}

		internal static bool AnyVisible(CilboxMenuPanel panel)
		{
			for (int i = 0; i < panel.Pages.Count; i++)
			{
				if (panel.Pages[i].Visible && !panel.Pages[i].Removed) return true;
			}
			return false;
		}

		internal static bool Alive(CilboxMenuPage page) => page.Owner != null && !page.Owner.disabled && page.Box != null && !page.Box.disabled && (ReferenceEquals(page.Content, null) || page.Content != null) && Current(page.Box, page.Content);

		internal static string Clean(string value, int max, bool multiline)
		{
			if (string.IsNullOrEmpty(value)) return string.Empty;
			text.Clear();
			int lines = 0;
			for (int i = 0; i < value.Length && text.Length < max; i++)
			{
				char c = value[i];
				if (c == '<') c = '\u2039';
				else if (c == '>') c = '\u203A';
				else if (c == '\n' && multiline && lines < MaxLines) lines++;
				else if (char.IsControl(c)) c = ' ';
				else if (c != '\u200D' && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
				else if (c == '\\')
				{
					if (text.Length + 2 > max) break;
					text.Append(c).Append(Joiner);
					continue;
				}
				text.Append(c);
			}
			if (text.Length > 0 && char.IsHighSurrogate(text[text.Length - 1])) text.Length--;
			return text.ToString().Trim();
		}

		internal static void Tick()
		{
			double now = Time.realtimeSinceStartupAsDouble;
			for (int i = pages.Count - 1; i >= 0; i--)
			{
				if (i >= pages.Count) continue;
				CilboxMenuPage page = pages[i];
				try
				{
					if (!Alive(page)) Remove(page);
					else if (page.Dirty && now >= page.NextBuild) CilboxMenuView.Rebuild(page, now);
					else if (page.Layout && now >= page.NextBuild) CilboxMenuView.Relayout(page, now);
				}
				catch (Exception e)
				{
					page.Dirty = false;
					Debug.LogException(e);
				}
			}
		}

		internal static void Remove(CilboxMenuPage page)
		{
			if (page.Removed) return;
			bool shown = page.Panel != null && AnyVisible(page.Panel);
			page.Removed = true;
			pages.Remove(page);
			byPage.Remove(page.Handle);
			for (int i = 0; i < page.Items.Count; i++) byItem.Remove(page.Items[i].Handle);
			if (page.Panel == null) SettingsProvider.RemoveContentTab(page.Key);
			else
			{
				CilboxMenuPanel panel = page.Panel;
				panel.Pages.Remove(page);
				if (panel.Pages.Count > 0) CilboxMenuView.Present(page, shown);
				else
				{
					panels.Remove(panel);
					CilboxMenuView.PanelRemoved(panel);
					BasisMenuBase<BasisMainMenu>.RemoveProvider(panel.Provider);
				}
			}
			CilboxMenuView.Forget(page);
			if (pages.Count == 0 && ticking)
			{
				ticking = false;
				BasisFrameClock.OnTick -= Tick;
				BasisFrameClock.RemoveRequest();
			}
		}

		internal static void Reset()
		{
			for (int i = pages.Count - 1; i >= 0; i--)
			{
				if (i < pages.Count) Remove(pages[i]);
			}
			opens.Clear();
		}

		static void Purge()
		{
			for (int i = pages.Count - 1; i >= 0; i--)
			{
				if (i >= pages.Count || Alive(pages[i])) continue;
				try
				{
					Remove(pages[i]);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
		}

		static void Run(CilboxMenuItem item)
		{
			CilboxMenuPage page = item.Page;
			if (item.Callback == null || page.Removed || !Alive(page)) return;
			try
			{
				switch (item.Type)
				{
					case Button: ((Action)item.Callback).Invoke(); break;
					case Toggle: ((Action<bool>)item.Callback).Invoke(item.Value != 0f); break;
					case Slider: ((Action<float>)item.Callback).Invoke(item.Value); break;
					case Dropdown: ((Action<int>)item.Callback).Invoke((int)item.Value); break;
				}
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}
		}

		static CilboxBasisCommon Running(object self, out CilboxProxy proxy)
		{
			proxy = self as CilboxProxy;
			if (proxy == null || proxy.disabled || !(proxy.box is CilboxBasisCommon box) || box == null || box.disabled || box.interpreterAccountingDepth == 0) return null;
			return box;
		}

		static bool Allowed(CilboxBasisCommon box, CilboxProxy proxy, out BasisContentBase content)
		{
			if (!CilboxBasisConsent.TryOwner(box, proxy.gameObject, out content)) content = null;
			return CilboxBasisConsent.ForLocalPlayer(box, proxy.gameObject) && Current(box, content);
		}

		static bool Current(CilboxBasisCommon box, BasisContentBase content)
		{
			if (!(box is CilboxAvatarBasis)) return true;
			BasisLocalPlayer local = BasisLocalPlayer.Instance;
			return content is BasisAvatar avatar && (ReferenceEquals(local, null) || ReferenceEquals(local.BasisAvatar, avatar));
		}

		static bool WorldOwned(CilboxBasisCommon box, BasisContentBase content) => KindOf(box) == KindWorld || content == null || content is BasisScene;

		static bool Shares(CilboxMenuPage page, bool world, BasisContentBase content) => world ? page.World : !page.World && ReferenceEquals(page.Content, content);

		static bool Spend(CilboxBasisCommon box)
		{
			double now = Time.realtimeSinceStartupAsDouble;
			if (now - box.menuWindowStart >= AddWindowSeconds || now < box.menuWindowStart)
			{
				box.menuWindowStart = now;
				box.menuWindowAdds = 0;
			}
			if (box.menuWindowAdds >= MaxAddsPerWindow) return false;
			box.menuWindowAdds++;
			return true;
		}

		static bool Pressed(BasisContentBase content)
		{
			if (content == null) return false;
			float now = Time.realtimeSinceStartup, window = ActivationSeconds;
			for (int i = opens.Count - 1; i >= 0; i--)
			{
				float at = opens[i].At;
				if (now < at || now - at > ActivationSeconds) opens.RemoveAt(i);
				else if (ReferenceEquals(opens[i].Content, content)) window = Mathf.Min(window, now - at - PressGap);
			}
			if (window <= 0f || !BasisUserActivation.Recent(content, window)) return false;
			opens.Add((content, now));
			return true;
		}

		static CilboxMenuPage Mine(BasisMenuPage handle)
		{
			CilboxMenuPage page = Find(handle);
			return page != null && Alive(page) && page.Box.interpreterAccountingDepth > 0 ? page : null;
		}

		static CilboxMenuItem Mine(BasisMenuItem handle)
		{
			CilboxMenuItem item = Find(handle);
			return item != null && Mine(item.Page.Handle) != null ? item : null;
		}

		static bool Accepts(CilboxMenuPage page, Delegate callback)
		{
			if (callback is null) return true;
			foreach (Delegate part in callback.GetInvocationList())
			{
				if (!(part.Target is CilboxPlatform.DelegateRepackage repackage) || repackage.meth == null || repackage.meth.parentClass == null || !ReferenceEquals(repackage.meth.parentClass.box, page.Box)) return false;
				if (!ReferenceEquals(repackage.o, null) && !ReferenceEquals(repackage.o.box, page.Box)) return false;
			}
			return true;
		}

		static BasisMenuPage SettingsTab(CilboxBasisCommon box, CilboxProxy owner, BasisContentBase content, string title)
		{
			string name = Clean(title, BasisMenuShim.MaxTitleLength, false);
			if (name.Length == 0) return null;
			Purge();
			if (!TabRoom(WorldOwned(box, content), content) || SettingsTitleTaken(name, null) || !Spend(box)) return null;
			CilboxMenuPage page = NewPage(box, owner, content, name);
			string key = KeyPrefix + page.Kind.ToString(CultureInfo.InvariantCulture) + ":" + name.ToLowerInvariant();
			for (int n = 2; SettingsProvider.HasContentTab(key); n++) key = KeyPrefix + page.Kind.ToString(CultureInfo.InvariantCulture) + ":" + name.ToLowerInvariant() + ":" + n.ToString(CultureInfo.InvariantCulture);
			page.Key = key;
			if (SettingsProvider.AddContentTab(key, name, group => CilboxMenuView.BuildTab(page, group))) return page.Handle;
			Remove(page);
			return null;
		}

		static CilboxMenuPage NewPage(CilboxBasisCommon box, CilboxProxy owner, BasisContentBase content, string title)
		{
			CilboxMenuPage page = new CilboxMenuPage { Handle = new BasisMenuPage(), Box = box, Owner = owner, Content = content, Title = title, Kind = KindOf(box), World = WorldOwned(box, content) };
			page.Source = SourceOf(box, content);
			pages.Add(page);
			byPage[page.Handle] = page;
			if (!ticking)
			{
				ticking = true;
				BasisFrameClock.OnTick += Tick;
				BasisFrameClock.AddRequest();
			}
			return page;
		}

		static int KindOf(CilboxBasisCommon box) => box is CilboxAvatarBasis ? KindAvatar : box is CilboxPropBasis ? KindProp : KindWorld;

		static string SourceOf(CilboxBasisCommon box, BasisContentBase content)
		{
			string name = content != null ? content.BasisBundleDescription?.AssetBundleName : null;
			if (string.IsNullOrEmpty(name)) name = box.gameObject.scene.name;
			if (string.IsNullOrEmpty(name)) name = box.name;
			name = Clean(name, MaxSourceLength, false);
			return name.Length > 0 ? name : "?";
		}

		static bool HasPanel(bool world, BasisContentBase content)
		{
			for (int i = 0; i < panels.Count; i++)
			{
				if (panels[i].Pages.Count > 0 && Shares(panels[i].Pages[0], world, content)) return true;
			}
			return false;
		}

		static bool PanelTitleTaken(string name)
		{
			List<BasisMenuActionProvider<BasisMainMenu>> providers = BasisMenuBase<BasisMainMenu>.Providers;
			for (int i = 0; i < providers.Count; i++)
			{
				if (providers[i] != null && string.Equals(providers[i].Title, name, StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		static bool TabRoom(bool world, BasisContentBase content)
		{
			int mine = 0, all = 0;
			for (int i = 0; i < pages.Count; i++)
			{
				if (pages[i].Panel != null) continue;
				all++;
				if (Shares(pages[i], world, content)) mine++;
			}
			return mine < BasisMenuShim.MaxSettingsTabs && all < MaxContentSettingsTabs;
		}

		static bool SettingsTitleTaken(string name, CilboxMenuPage except)
		{
			for (int i = 0; i < pages.Count; i++)
			{
				if (pages[i].Panel == null && pages[i] != except && string.Equals(pages[i].Title, name, StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

		static float Clamp(CilboxMenuItem item, float value)
		{
			if (!Finite(value)) value = item.Min;
			value = Mathf.Clamp(value, item.Min, item.Max);
			return item.Whole ? Mathf.Clamp(Mathf.Round(value), Mathf.Ceil(item.Min), Mathf.Floor(item.Max)) : value;
		}

		static float Index(CilboxMenuItem item, float value)
		{
			int last = item.Options.Length - 1;
			return Finite(value) ? Mathf.Clamp(Mathf.RoundToInt(value), 0, last) : 0f;
		}

		static string[] Options(string[] options)
		{
			if (options == null || options.Length == 0) return null;
			string[] clean = new string[Math.Min(options.Length, BasisMenuShim.MaxOptions)];
			for (int i = 0; i < clean.Length; i++) clean[i] = Clean(options[i], BasisMenuShim.MaxTitleLength, false);
			return clean;
		}
	}
}
