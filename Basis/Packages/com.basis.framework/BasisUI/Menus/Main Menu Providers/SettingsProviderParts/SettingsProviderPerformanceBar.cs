using System.Collections.Generic;
using System.Text;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderPerformanceBar
    {
        private const int RefreshIntervalTicks = 10;

        private static readonly string[] GpuLabels =
        {
            "settings.graphics.performanceBar.segment.shadows",
            "settings.graphics.performanceBar.segment.opaque",
            "settings.graphics.performanceBar.segment.gi",
            "settings.graphics.performanceBar.segment.reflections",
            "settings.graphics.performanceBar.segment.rtao",
            "settings.graphics.performanceBar.segment.transparent",
            "settings.graphics.performanceBar.segment.other",
        };
        // Order must match BasisPerformanceCpuSegment exactly - it indexes both this array and
        // BasisPerformanceBarView.CpuPalette.
        private static readonly string[] CpuLabels =
        {
            "settings.graphics.performanceBar.segment.eventDriver",
            "settings.graphics.performanceBar.segment.ik",
            "settings.graphics.performanceBar.segment.movement",
            "settings.graphics.performanceBar.segment.avatarLoad",
            "settings.graphics.performanceBar.segment.networking",
            "settings.graphics.performanceBar.segment.jiggle",
            "settings.graphics.performanceBar.segment.voice",
            "settings.graphics.performanceBar.segment.renderDispatch",
            "settings.graphics.performanceBar.segment.other",
        };

        private static PanelElementDescriptor _group;
        private static PanelElementDescriptor _gpuField;
        private static PanelElementDescriptor _cpuField;
        private static int _tickCounter;
        private static bool _subscribed;

        public static void BuildPerformanceBarGroup(RectTransform container, PanelElementDescriptor descriptor)
        {
            PanelSectionToggle toggle = PanelSectionToggle.CreateNewEntry(container);
            toggle.SetTitle(BasisLocalization.Get("settings.graphics.performanceBar.title"));

            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, container);
            group.SetTitle(string.Empty);
            toggle.RegisterContentContainer(group);

            PanelToggle toggleShow = PanelToggle.CreateNewEntry(group.ContentParent);
            toggleShow.AssignBinding(BasisSettingsDefaults.ShowPerformanceBar);
            toggleShow.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.performanceBar.enable"));
            toggleShow.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.performanceBar.enable.tooltip"));

            // CPU only: GPU's 6 named segments are already the full granularity this build tracks,
            // but the CPU side collapses every BasisDriver/BasisEerie profiler marker into 9 coarse
            // buckets, so there is real detail underneath worth exposing on demand.
            PanelToggle toggleDetailed = PanelToggle.CreateNewEntry(group.ContentParent);
            toggleDetailed.AssignBinding(BasisSettingsDefaults.PerformanceBarDetailed);
            toggleDetailed.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.performanceBar.detailed"));
            toggleDetailed.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.performanceBar.detailed.tooltip"));

            PanelElementDescriptor gpuField = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
            gpuField.SetTitle(BasisLocalization.Get("settings.graphics.performanceBar.gpu"));
            gpuField.SetDescription(BasisLocalization.Get("settings.graphics.performanceBar.measuring"));
            BasisPerformanceBarView.Create(gpuField.ContentParent, true);

            PanelElementDescriptor cpuField = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
            cpuField.SetTitle(BasisLocalization.Get("settings.graphics.performanceBar.cpu"));
            cpuField.SetDescription(BasisLocalization.Get("settings.graphics.performanceBar.measuring"));
            BasisPerformanceBarView.Create(cpuField.ContentParent, false);

            ApplyVisible(gpuField, cpuField, toggleShow.Value);
            toggleShow.OnValueChanged += value =>
            {
                ApplyVisible(gpuField, cpuField, value);
                group.ForceRebuild();
            };

            group.IsolateAsCanvas();
            Attach(group, gpuField, cpuField);
            group.OnInstanceReleased += () => Detach(group);

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(toggle, group, false,
                _ => descriptor.ForceRebuild());
        }

        private static void ApplyVisible(PanelElementDescriptor gpuField, PanelElementDescriptor cpuField, bool visible)
        {
            gpuField.SetActive(visible);
            cpuField.SetActive(visible);
        }

        private static void Attach(PanelElementDescriptor group, PanelElementDescriptor gpuField, PanelElementDescriptor cpuField)
        {
            _group = group;
            _gpuField = gpuField;
            _cpuField = cpuField;
            _tickCounter = 0;

            if (!_subscribed)
            {
                BasisFrameClock.OnTick += OnTick;
                BasisFrameClock.AddRequest();
                _subscribed = true;
            }
        }

        private static void Detach(PanelElementDescriptor group)
        {
            if (!ReferenceEquals(_group, group)) return;
            Unsubscribe();
            _group = null;
            _gpuField = null;
            _cpuField = null;
        }

        private static void Unsubscribe()
        {
            if (!_subscribed) return;
            BasisFrameClock.OnTick -= OnTick;
            BasisFrameClock.RemoveRequest();
            _subscribed = false;
        }

        private static void OnTick()
        {
            if (_group == null) { Unsubscribe(); return; }
            if (!_gpuField.gameObject.activeInHierarchy) return;
            if (++_tickCounter < RefreshIntervalTicks) return;
            _tickCounter = 0;

            _gpuField.SetDescription(FormatLegend(BasisPerformanceBarData.GpuMs, GpuLabels, BasisPerformanceBarView.GpuPalette));
            _cpuField.SetDescription(BasisSettingsDefaults.PerformanceBarDetailed.RawValue
                ? FormatDetailedCpuLegend()
                : FormatLegend(BasisPerformanceBarData.CpuMs, CpuLabels, BasisPerformanceBarView.CpuPalette));
        }

        // Colors each segment's text with the same swatch its bar uses, so the legend and the bar
        // read as one thing rather than needing a separate color key.
        private static string FormatLegend(float[] ms, string[] labels, Color[] palette)
        {
            StringBuilder sb = new StringBuilder(160);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i] < 0.05f) continue;
                if (sb.Length > 0) sb.Append("   ");
                Color color = i < palette.Length ? palette[i] : Color.gray;
                sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(color)).Append('>');
                sb.Append(BasisLocalization.Get(labels[i])).Append(' ').Append(ms[i].ToString("F2")).Append("ms");
                sb.Append("</color>");
            }
            return sb.Length > 0 ? sb.ToString() : BasisLocalization.Get("settings.graphics.performanceBar.measuring");
        }

        // Reused across ticks so the sort has nothing to allocate every refresh.
        private static readonly List<BasisPerformanceBarData.MarkerRow> _detailScratch = new List<BasisPerformanceBarData.MarkerRow>(64);

        // One line per underlying profiler marker instead of one per coarse bucket — every marker
        // Basis has classified, including ones reading 0.00ms right now, not just whichever
        // currently cost something; sorted worst-first so the heaviest is still easy to spot. Raw
        // marker names on purpose — this is the "show me exactly what" view, not the at-a-glance one.
        private static string FormatDetailedCpuLegend()
        {
            IReadOnlyList<BasisPerformanceBarData.MarkerRow> rows = BasisPerformanceBarData.CpuRows;
            if (rows.Count == 0)
            {
                return BasisLocalization.Get("settings.graphics.performanceBar.measuring");
            }

            _detailScratch.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                _detailScratch.Add(rows[i]);
            }
            _detailScratch.Sort((a, b) => b.SmoothedMs.CompareTo(a.SmoothedMs));

            Color[] palette = BasisPerformanceBarView.CpuPalette;
            StringBuilder sb = new StringBuilder(512);
            for (int i = 0; i < _detailScratch.Count; i++)
            {
                BasisPerformanceBarData.MarkerRow row = _detailScratch[i];
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                int segment = (int)row.Segment;
                Color color = segment < palette.Length ? palette[segment] : Color.gray;
                sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(color)).Append('>');
                sb.Append(row.Name).Append(' ').Append(row.SmoothedMs.ToString("F2")).Append("ms");
                sb.Append("</color>");
            }
            return sb.ToString();
        }
    }
}
