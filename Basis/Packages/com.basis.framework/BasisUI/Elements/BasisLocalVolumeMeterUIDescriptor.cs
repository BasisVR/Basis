using System;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    [RequireComponent(typeof(BasisLocalVolumeMeterUI))]
    public class BasisLocalVolumeMeterUIDescriptor : AddressableUIInstanceBase
    {
        public static class ElementStyles
        {
            // Adjust the paths to match however you structure your prefabs
            public static string Horizontal =>
                "Packages/com.basis.sdk/Prefabs/Panel Elements/Volume Meter Horizontal.prefab";
        }

        public static BasisLocalVolumeMeterUIDescriptor CreateNew(string style, Component parent) =>
            CreateNew<BasisLocalVolumeMeterUIDescriptor>(style, parent);

        [Header("References")]
        [field: SerializeField] public BasisLocalVolumeMeterUI Meter { get; private set; }
        [field: SerializeField] public Image FillImage { get; private set; }
        [field: SerializeField] public Image PeakTickImage { get; private set; }

        public bool HasMeter => Meter;
        public bool HasFill => FillImage;
        public bool HasPeakTick => PeakTickImage;

        [Header("Defaults – Visuals")]
        [Tooltip("Gradient assigned to the meter on Awake/Validate.")]
        [field: SerializeField] public Gradient DefaultColorByLevel { get; private set; }

        [Header("Defaults – Meter mapping")]
        [field: SerializeField] public bool DefaultUseDecibels { get; private set; } = true;
        [field: SerializeField] public float DefaultMinDb { get; private set; } = -60f;
        [field: SerializeField] public float DefaultMaxDb { get; private set; } = 0f;
        [field: SerializeField] public float DefaultGainDb { get; private set; } = 0f;

        [Header("Defaults – Dynamics")]
        [field: SerializeField] public float DefaultAttack { get; private set; } = 0.06f;
        [field: SerializeField] public float DefaultRelease { get; private set; } = 0.20f;
        [field: SerializeField] public float DefaultPeakHoldTime { get; private set; } = 0.6f;
        [field: SerializeField] public float DefaultPeakFallPerSecond { get; private set; } = 1.5f;

        [Header("Behaviour")]
        [SerializeField]
        [Tooltip("If true, meter is reset to zero on Awake before applying defaults.")]
        private bool _resetOnAwake = true;

        protected override void Awake()
        {
            base.Awake();

            if (!Meter) Meter = GetComponent<BasisLocalVolumeMeterUI>();

            if (!Meter) return;

            if (_resetOnAwake)
            {
                if (Meter.fill)
                {
                    Meter.fill.fillAmount = 0f;
                }
            }

            ApplyDefaultsToMeter();
        }

        private void ApplyDefaultsToMeter()
        {
            if (!Meter) return;

            // Wire references
            if (HasFill) Meter.fill = FillImage;
            if (HasPeakTick) Meter.peakTick = PeakTickImage;
            if (DefaultColorByLevel != null) Meter.colorByLevel = DefaultColorByLevel;

            // Meter mapping
            Meter.useDecibels = DefaultUseDecibels;
            Meter.minDb = DefaultMinDb;
            Meter.maxDb = DefaultMaxDb;
            Meter.gainDb = DefaultGainDb;

            // Dynamics
            Meter.attack = DefaultAttack;
            Meter.release = DefaultRelease;
            Meter.peakHoldTime = DefaultPeakHoldTime;
            Meter.peakFallPerSecond = DefaultPeakFallPerSecond;
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            if (Application.isPlaying) return;

            if (!Meter) Meter = GetComponent<BasisLocalVolumeMeterUI>();
            if (!Meter) return;

            Undo.RecordObject(Meter, "Apply BasisLocalVolumeMeterUIDescriptor defaults");

            // Keep references + defaults in sync in edit mode
            if (HasFill) Meter.fill = FillImage;
            if (HasPeakTick) Meter.peakTick = PeakTickImage;
            if (DefaultColorByLevel != null) Meter.colorByLevel = DefaultColorByLevel;

            Meter.useDecibels = DefaultUseDecibels;
            Meter.minDb = DefaultMinDb;
            Meter.maxDb = DefaultMaxDb;
            Meter.gainDb = DefaultGainDb;

            Meter.attack = DefaultAttack;
            Meter.release = DefaultRelease;
            Meter.peakHoldTime = DefaultPeakHoldTime;
            Meter.peakFallPerSecond = DefaultPeakFallPerSecond;
        }
#endif
    }
}
