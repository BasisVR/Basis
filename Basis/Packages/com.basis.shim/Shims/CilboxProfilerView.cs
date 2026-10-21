using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using Basis;
using Basis.BasisUI;
using Basis.Scripts.Settings;

namespace Cilbox
{
	[NoAutoStaticsCleanup]
	internal static class CilboxProfilerSettings
	{
		static CilboxProfilerSettings() => BasisSettingsBindingPostLoad.Register(typeof(CilboxProfilerSettings));
		public static BasisSettingsBinding<bool> Enabled = new BasisSettingsBinding<bool>("cilboxprofiler", new BasisPlatformDefault<bool>(false));
		internal static void EnsureLoaded() => _ = Enabled;
	}
	[AutoStaticsCleanup]
	internal static partial class CilboxProfilerView
	{
		internal const int Rows = 20, Columns = 6;
		internal const string SortFrame = "frame", SortPeak = "peak", SortTotal = "total";
		internal const string Key = "settings.developer.cilboxProfiler.";
		[NoAutoStaticsCleanup] static readonly string[] columnKeys = { "column.script", "column.frame", "column.peak", "column.calls", "column.total", "column.source" };
		static readonly List<CilboxProfileEntry> rows = new List<CilboxProfileEntry>();
		static readonly StringBuilder text = new StringBuilder(4096);
		static string[] headers = new string[Columns], cells = new string[Columns];
		static string[,] grid = new string[Rows, Columns];
		static readonly float[] widths = new float[Columns];
		static readonly float[] edges = new float[Columns];
		static string order = SortFrame;
		internal static string Order => order;
		internal static int SortVersion { get; private set; }
		[RuntimeInitializeOnLoadMethod]
		static void Register()
		{
			SettingsProvider.DeveloperSectionBuilders.Remove((Key + "title", BuildSection));
			SettingsProvider.DeveloperSectionBuilders.Add((Key + "title", BuildSection));
			SettingsProvider.DeveloperResetActions.Remove(ResetDefaults);
			SettingsProvider.DeveloperResetActions.Add(ResetDefaults);
			CilboxProfilerSettings.EnsureLoaded();
			CilboxProfilerSettings.Enabled.OnChanged -= OnSettingChanged;
			CilboxProfilerSettings.Enabled.OnChanged += OnSettingChanged;
			BasisSettingsDefaults.ShowDeveloperTab.OnChanged -= OnSettingChanged;
			BasisSettingsDefaults.ShowDeveloperTab.OnChanged += OnSettingChanged;
			BasisSettingsSystem.OnSettingsFinishedChanges -= Apply;
			BasisSettingsSystem.OnSettingsFinishedChanges += Apply;
			Apply();
		}
		static void OnSettingChanged(bool value) => Apply();
		static void Apply()
		{
			if (!Application.isPlaying) return;
			CilboxProfiler.SetActive(CilboxProfilerSettings.Enabled.RawValue && BasisSettingsDefaults.ShowDeveloperTab.RawValue);
		}
		static void ResetDefaults() => CilboxProfilerSettings.Enabled.ResetToDefault();
		internal static void BuildSection(RectTransform container)
		{
			PanelElementDescriptor group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
			group.SetBackgroundVisible(false);
			group.SetDescription(BasisLocalization.Get(Key + "description"));
			PanelToggle enable = PanelToggle.CreateNewEntry(group.ContentParent);
			enable.Descriptor.SetTitle(BasisLocalization.Get(Key + "enable"));
			enable.Descriptor.SetTooltip(BasisLocalization.Get(Key + "enable.tooltip"));
			enable.AssignBinding(CilboxProfilerSettings.Enabled);
			PanelDropdown sorting = PanelDropdown.CreateNewEntry(group.ContentParent);
			sorting.Descriptor.SetTitle(BasisLocalization.Get(Key + "sort"));
			sorting.Descriptor.SetTooltip(BasisLocalization.Get(Key + "sort.tooltip"));
			sorting.AssignLocalizedEntries(new List<string> { SortFrame, SortPeak, SortTotal }, new List<string> { Key + "sort.frame", Key + "sort.peak", Key + "sort.total" });
			sorting.SetValueWithoutNotify(order);
			sorting.OnValueChanged += value =>
			{
				order = value;
				SortVersion++;
			};
			PanelButton clear = PanelButton.CreateNew(group.ContentParent);
			clear.Descriptor.SetTitle(BasisLocalization.Get(Key + "clear"));
			clear.Descriptor.SetTooltip(BasisLocalization.Get(Key + "clear.tooltip"));
			clear.OnClicked += CilboxProfiler.Clear;
			PanelElementDescriptor graph = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
			graph.SetTitle(string.Empty);
			graph.SetDescription(string.Empty);
			graph.IsolateAsCanvas();
			PanelElementDescriptor output = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
			output.SetTitle(string.Empty);
			output.SetDescription(string.Empty);
			output.IsolateAsCanvas();
			CilboxProfilerPanel.Create(graph, output);
		}
		internal static string Caption(float scale, float seconds) => BasisLocalization.Get(Key + "graph", scale.ToString("0.##", CultureInfo.InvariantCulture), seconds);
		internal static void SortRows(List<CilboxProfileEntry> list, string by)
		{
			Comparison<CilboxProfileEntry> compare = by == SortPeak ? ByPeak : by == SortTotal ? ByTotal : ByFrame;
			list.Sort(compare);
		}
		internal static string RenderTable(string by, float width, Func<string, float> measure)
		{
			if (!CilboxProfiler.Active) return BasisLocalization.Get(Key + "off");
			CilboxProfiler.Collect(rows);
			if (rows.Count == 0) return BasisLocalization.Get(Key + "empty");
			SortRows(rows, by);
			int shown = Math.Min(Rows, rows.Count);
			double frameMs = 0d;
			for (int i = 0; i < rows.Count; i++) frameMs += rows[i].FrameMs;
			for (int c = 0; c < Columns; c++) headers[c] = BasisLocalization.Get(Key + columnKeys[c]);
			for (int i = 0; i < shown; i++)
			{
				CilboxProfileEntry e = rows[i];
				double total = CilboxProfiler.TicksToMs(e.TotalTicks);
				grid[i, 0] = e.Script;
				grid[i, 1] = Milliseconds(e.FrameMs);
				grid[i, 2] = Milliseconds(CilboxProfiler.TicksToMs(e.PeakTicks));
				grid[i, 3] = e.CallsPerFrame.ToString(e.CallsPerFrame >= 100d ? "0" : "0.##", CultureInfo.InvariantCulture);
				grid[i, 4] = total.ToString(total >= 10d ? "0" : "0.0", CultureInfo.InvariantCulture);
				grid[i, 5] = SourceOf(e);
			}
			float gap = measure("00");
			for (int c = 1; c < Columns; c++)
			{
				widths[c] = Width(headers[c], true, measure);
				for (int i = 0; i < shown; i++) widths[c] = Math.Max(widths[c], measure(grid[i, c]));
			}
			float rest = Math.Max(0f, width - widths[1] - widths[2] - widths[3] - widths[4] - gap * 5f);
			widths[5] = Math.Min(widths[5], rest * 0.4f);
			widths[0] = rest - widths[5];
			edges[0] = 0f;
			for (int c = 1; c < 5; c++) edges[c] = (c == 1 ? widths[0] : edges[c - 1]) + gap + widths[c];
			edges[5] = edges[4] + gap;
			text.Clear();
			text.Append(Fit(BasisLocalization.Get(Key + "summary", frameMs, rows.Count, CilboxProfiler.BoxCount), width, false, measure));
			for (int c = 0; c < Columns; c++) cells[c] = headers[c];
			AppendRow(null, true, measure);
			for (int i = 0; i < shown; i++)
			{
				for (int c = 0; c < Columns; c++) cells[c] = grid[i, c];
				AppendRow(rows[i].Slot >= 0 ? ColorUtility.ToHtmlStringRGB(BasisPerformanceBarView.CpuPalette[rows[i].Slot]) : null, false, measure);
			}
			if (rows.Count > shown) text.Append('\n').Append(Fit(BasisLocalization.Get(Key + "more", rows.Count - shown), width, false, measure));
			return text.ToString();
		}
		internal static string Fit(string value, float max, bool bold, Func<string, float> measure)
		{
			if (Width(value, bold, measure) <= max) return value;
			int low = 0, high = value.Length - 1;
			while (low < high)
			{
				int mid = (low + high + 1) / 2;
				if (Width(value.Substring(0, mid) + "…", bold, measure) <= max) low = mid;
				else high = mid - 1;
			}
			if (low > 0 && char.IsHighSurrogate(value[low - 1])) low--;
			return value.Substring(0, low).TrimEnd() + "…";
		}
		static void AppendRow(string color, bool bold, Func<string, float> measure)
		{
			text.Append('\n');
			if (bold) text.Append("<b>");
			string name = Fit(cells[0], widths[0], bold, measure);
			if (color != null) text.Append("<color=#").Append(color).Append('>').Append(name).Append("</color>");
			else text.Append(name);
			for (int c = 1; c < 5; c++) Position(edges[c] - Width(cells[c], bold, measure)).Append(cells[c]);
			Position(edges[5]).Append(Fit(cells[5], widths[5], bold, measure));
			if (bold) text.Append("</b>");
		}
		static StringBuilder Position(float x) => text.Append("<pos=").Append(Math.Max(0f, x).ToString("0.#", CultureInfo.InvariantCulture)).Append('>');
		static float Width(string value, bool bold, Func<string, float> measure) => measure(bold ? "<b>" + value + "</b>" : value);
		static string SourceOf(CilboxProfileEntry e)
		{
			string kind = BasisLocalization.Get(e.Kind == CilboxProfiler.KindAvatar ? Key + "source.avatar" : e.Kind == CilboxProfiler.KindProp ? Key + "source.prop" : Key + "source.world");
			return kind + ": " + e.Source;
		}
		static string Milliseconds(double ms) => ms.ToString(ms >= 100d ? "0" : "0.00", CultureInfo.InvariantCulture);
		static int ByFrame(CilboxProfileEntry a, CilboxProfileEntry b)
		{
			int compare = b.FrameMs.CompareTo(a.FrameMs);
			return compare != 0 ? compare : ByTotal(a, b);
		}
		static int ByPeak(CilboxProfileEntry a, CilboxProfileEntry b)
		{
			int compare = b.PeakTicks.CompareTo(a.PeakTicks);
			return compare != 0 ? compare : ByTotal(a, b);
		}
		static int ByTotal(CilboxProfileEntry a, CilboxProfileEntry b)
		{
			int compare = b.TotalTicks.CompareTo(a.TotalTicks);
			return compare != 0 ? compare : string.CompareOrdinal(a.Script, b.Script);
		}
	}
}
