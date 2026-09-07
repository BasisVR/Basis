using Basis.Scripts.Drivers;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderFrameBottleneck
    {
        private const int RefreshIntervalTicks = 15;
        private const int TicksBeforeFreeze = 2;
        private const int RefreshesBeforeVerdictChange = 4;
        private const BasisFrameBottleneckKind UnsetKind = (BasisFrameBottleneckKind)(-1);

        private static readonly BasisFrameBottleneckKind[] AllKinds =
        {
            BasisFrameBottleneckKind.Measuring,
            BasisFrameBottleneckKind.Cpu,
            BasisFrameBottleneckKind.Gpu,
            BasisFrameBottleneckKind.Balanced,
            BasisFrameBottleneckKind.FrameCap,
            BasisFrameBottleneckKind.NoGpuTimer
        };

        private static PanelSectionToggle _toggle;
        private static PanelElementDescriptor _group;
        private static PanelElementDescriptor _cpuField;
        private static PanelElementDescriptor _gpuField;
        private static BasisPanelTint.Handle _tint;

        private static int _tickCounter;
        private static int _refreshCount;
        private static bool _subscribed;
        private static bool _layoutFrozen;
        private static BasisFrameBottleneckKind _shownKind = UnsetKind;
        private static BasisFrameBottleneckKind _pendingKind = UnsetKind;
        private static int _pendingHolds;

        public static BasisFrameBottleneckKind Verdict =>
            _shownKind == UnsetKind ? BasisFrameBottleneckKind.Measuring : _shownKind;

        public static void BuildFrameBottleneckGroup(RectTransform container, PanelElementDescriptor descriptor)
        {
            // Collapsible so the page can stay short; the toggle's own header carries the live
            // GPU/CPU-limited verdict, so the headline is visible even while collapsed.
            PanelSectionToggle toggle = PanelSectionToggle.CreateNewEntry(container);
            toggle.SetTitle(BasisLocalization.Get("settings.graphics.bottleneck.title"));

            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, container);
            group.SetTitle(string.Empty);
            group.SetDescription(BasisLocalization.Get("settings.graphics.bottleneck.measuring"));
            group.SetTooltip(BasisLocalization.Get("settings.graphics.bottleneck.tooltip"));
            toggle.RegisterContentContainer(group);

            PanelElementDescriptor cpuField = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
            cpuField.SetTitle(BasisLocalization.Get("settings.graphics.bottleneck.cpu"));
            cpuField.SetDescription("...");

            PanelElementDescriptor gpuField = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
            gpuField.SetTitle(BasisLocalization.Get("settings.graphics.bottleneck.gpu"));
            gpuField.SetDescription("...");

            // Per-pass GPU/CPU timing rides Unity's Sampler/Recorder/ProfilerMarker APIs, which are
            // stripped/disabled outside the Editor and Development Builds — a release build would
            // export an all-zero snapshot. Debug.isDebugBuild covers exactly the builds this works in.
            if (Debug.isDebugBuild)
            {
                PanelButton captureButton = PanelButton.CreateNew(group.ContentParent);
                captureButton.Descriptor.SetTitle(BasisLocalization.Get("settings.graphics.bottleneck.renderPassCapture"));
                captureButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.graphics.bottleneck.renderPassCapture.tooltip"));
                captureButton.OnClicked += () => BasisRenderProfileHistory.CaptureToDisk("settings-panel");
            }

            group.IsolateAsCanvas();

            Attach(toggle, group, cpuField, gpuField);
            group.OnInstanceReleased += () => Detach(group);

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(toggle, group, false,
                _ => descriptor.ForceRebuild());
        }

        private static void Attach(
            PanelSectionToggle toggle,
            PanelElementDescriptor group,
            PanelElementDescriptor cpuField,
            PanelElementDescriptor gpuField)
        {
            _toggle = toggle;
            _group = group;
            _cpuField = cpuField;
            _gpuField = gpuField;

            _cpuField.DisableRichText();
            _gpuField.DisableRichText();

            _tint = BasisPanelTint.Capture(group);
            _tickCounter = 0;
            _refreshCount = 0;
            _layoutFrozen = false;
            _shownKind = UnsetKind;
            _pendingKind = UnsetKind;
            _pendingHolds = 0;

            BasisFrameBottleneck.Reset();

            if (!_subscribed)
            {
                BasisFrameClock.OnTick += OnTick;
                BasisFrameClock.AddRequest();
                _subscribed = true;
            }

            Refresh();
        }

        private static void Detach(PanelElementDescriptor group)
        {
            if (!ReferenceEquals(_group, group))
            {
                return;
            }

            Unsubscribe();

            _toggle = null;
            _group = null;
            _cpuField = null;
            _gpuField = null;
            _tint = null;
        }

        private static void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }

            BasisFrameClock.OnTick -= OnTick;
            BasisFrameClock.RemoveRequest();
            _subscribed = false;
        }

        private static void OnTick()
        {
            if (_group == null)
            {
                Unsubscribe();
                return;
            }

            if (!_group.gameObject.activeInHierarchy)
            {
                return;
            }

            BasisFrameBottleneck.Sample();

            if (++_tickCounter < RefreshIntervalTicks)
            {
                return;
            }
            _tickCounter = 0;
            Refresh();
        }

        private static void Refresh()
        {
            if (_group == null)
            {
                return;
            }

            BasisFrameBottleneckReading reading = BasisFrameBottleneck.Read(_shownKind);

            if (ShouldCommitVerdict(reading.Kind))
            {
                _shownKind = reading.Kind;
                _toggle.SetTitle(TitleFor(reading.Kind));
                _group.SetDescription(AdviceFor(reading.Kind));
                ApplyTint(reading.Kind);
                SettingsProviderBottleneckHints.Show(reading.Kind);
            }

            if (_shownKind == BasisFrameBottleneckKind.Measuring)
            {
                string pending = BasisLocalization.Get("settings.graphics.bottleneck.pending");
                _cpuField.SetDescription(pending);
                _gpuField.SetDescription(pending);
                return;
            }

            _cpuField.SetDescription(BasisLocalization.Get("settings.graphics.bottleneck.cpu.value",
                reading.CpuBusyMs, reading.MainThreadMs, reading.RenderThreadMs, reading.PresentWaitMs));

            _gpuField.SetDescription(reading.HasGpuTiming
                ? BasisLocalization.Get("settings.graphics.bottleneck.gpu.value", reading.GpuMs)
                : BasisLocalization.Get("settings.graphics.bottleneck.gpu.unavailable"));

            if (_layoutFrozen)
            {
                return;
            }

            if (++_refreshCount < TicksBeforeFreeze)
            {
                return;
            }

            _layoutFrozen = true;
            _cpuField.FreezeLayoutSize(150f);
            _gpuField.FreezeLayoutSize(110f);
            FreezeGroupLayout();
        }

        private static bool ShouldCommitVerdict(BasisFrameBottleneckKind kind)
        {
            if (kind == _shownKind)
            {
                _pendingKind = UnsetKind;
                _pendingHolds = 0;
                return false;
            }

            if (_shownKind == UnsetKind || _shownKind == BasisFrameBottleneckKind.Measuring)
            {
                _pendingKind = UnsetKind;
                _pendingHolds = 0;
                return true;
            }

            if (_pendingKind != kind)
            {
                _pendingKind = kind;
                _pendingHolds = 1;
                return false;
            }

            if (++_pendingHolds < RefreshesBeforeVerdictChange)
            {
                return false;
            }

            _pendingKind = UnsetKind;
            _pendingHolds = 0;
            return true;
        }

        // Title now lives on the always-visible toggle header, not this content group, so only the
        // description (the multi-line advice text) needs a tallest-candidate freeze to stop the
        // group resizing/jumping as the verdict changes.
        private static void FreezeGroupLayout()
        {
            string description = _group.Description;

            _group.SetDescription(TallestAdvice(_group.HasDescription ? _group.DescriptionLabel : null));

            _group.FreezeLayoutSize();

            _group.SetDescription(description);
        }

        private static string TallestAdvice(TextMeshProUGUI label)
        {
            string tallest = AdviceFor(AllKinds[0]);
            float width = label != null ? label.rectTransform.rect.width : 0f;
            float best = width > 0f ? label.GetPreferredValues(tallest, width, 0f).y : tallest.Length;

            for (int index = 1; index < AllKinds.Length; index++)
            {
                string candidate = AdviceFor(AllKinds[index]);
                float height = width > 0f ? label.GetPreferredValues(candidate, width, 0f).y : candidate.Length;
                if (height > best)
                {
                    best = height;
                    tallest = candidate;
                }
            }

            return tallest;
        }

        private static string TitleFor(BasisFrameBottleneckKind kind)
        {
            string baseTitle = BasisLocalization.Get("settings.graphics.bottleneck.title");
            switch (kind)
            {
                case BasisFrameBottleneckKind.Cpu:
                case BasisFrameBottleneckKind.Gpu:
                case BasisFrameBottleneckKind.Balanced:
                case BasisFrameBottleneckKind.FrameCap:
                case BasisFrameBottleneckKind.NoGpuTimer:
                    return BasisLocalization.Get("settings.graphics.bottleneck.title.verdict",
                        baseTitle, BasisLocalization.Get(VerdictKeyFor(kind)));
                default:
                    return baseTitle;
            }
        }

        private static string VerdictKeyFor(BasisFrameBottleneckKind kind)
        {
            switch (kind)
            {
                case BasisFrameBottleneckKind.Cpu: return "settings.graphics.bottleneck.verdict.cpu";
                case BasisFrameBottleneckKind.Gpu: return "settings.graphics.bottleneck.verdict.gpu";
                case BasisFrameBottleneckKind.Balanced: return "settings.graphics.bottleneck.verdict.balanced";
                case BasisFrameBottleneckKind.FrameCap: return "settings.graphics.bottleneck.verdict.frameCap";
                case BasisFrameBottleneckKind.NoGpuTimer: return "settings.graphics.bottleneck.verdict.noGpuTimer";
                default: return "settings.graphics.bottleneck.verdict.measuring";
            }
        }

        private static string AdviceFor(BasisFrameBottleneckKind kind)
        {
            string advice = BasisLocalization.Get(AdviceKeyFor(kind));
            if (SettingsProviderBottleneckHints.SideFor(kind) == BasisFrameCostSide.None)
            {
                return advice;
            }

            return advice + " " + BasisLocalization.Get("settings.graphics.bottleneck.advice.highlight");
        }

        private static string AdviceKeyFor(BasisFrameBottleneckKind kind)
        {
            switch (kind)
            {
                case BasisFrameBottleneckKind.Cpu: return "settings.graphics.bottleneck.advice.cpu";
                case BasisFrameBottleneckKind.Gpu: return "settings.graphics.bottleneck.advice.gpu";
                case BasisFrameBottleneckKind.Balanced: return "settings.graphics.bottleneck.advice.balanced";
                case BasisFrameBottleneckKind.FrameCap: return "settings.graphics.bottleneck.advice.frameCap";
                case BasisFrameBottleneckKind.NoGpuTimer: return "settings.graphics.bottleneck.advice.noGpuTimer";
                default: return "settings.graphics.bottleneck.measuring";
            }
        }

        private static void ApplyTint(BasisFrameBottleneckKind kind)
        {
            BasisPanelTint.Apply(_tint, SeverityFor(kind));
        }

        private static BasisPanelSeverity SeverityFor(BasisFrameBottleneckKind kind)
        {
            switch (kind)
            {
                case BasisFrameBottleneckKind.FrameCap: return BasisPanelSeverity.Calm;
                case BasisFrameBottleneckKind.Balanced: return BasisPanelSeverity.Caution;
                case BasisFrameBottleneckKind.Cpu:
                case BasisFrameBottleneckKind.Gpu: return BasisPanelSeverity.Hot;
                default: return BasisPanelSeverity.None;
            }
        }
    }
}
