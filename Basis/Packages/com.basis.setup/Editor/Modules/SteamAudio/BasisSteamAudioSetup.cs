using System.IO;
using SteamAudio;
using UnityEditor;
using UnityEngine;

namespace Basis.Setup.Modules
{
    public sealed class BasisSteamAudioSetup : BasisSetupModuleBase
    {
        private const string ResourcesFolder = "Assets/Basis/Settings/Resources";
        private const string MaterialsFolder = ResourcesFolder + "/Materials";
        private const string SettingsPath = ResourcesFolder + "/SteamAudioSettings.asset";
        private const string DefaultMaterialPath = MaterialsFolder + "/Default.asset";

        private struct MaterialSpec
        {
            public string Name;
            public string Guid;
            public float AbsLow;
            public float AbsMid;
            public float AbsHigh;
            public float Scattering;
            public float TransLow;
            public float TransMid;
            public float TransHigh;
        }

        private static readonly MaterialSpec[] Materials =
        {
            new MaterialSpec { Name = "Brick",    Guid = "ed65b244617a3154ba34ebdb722855a9", AbsLow = 0.03f, AbsMid = 0.04f, AbsHigh = 0.07f, Scattering = 0.05f, TransLow = 0.015f, TransMid = 0.015f, TransHigh = 0.015f },
            new MaterialSpec { Name = "Carpet",   Guid = "4ef433d5b2fa5ad4a9e19a415c6ab619", AbsLow = 0.24f, AbsMid = 0.69f, AbsHigh = 0.73f, Scattering = 0.05f, TransLow = 0.02f,  TransMid = 0.005f, TransHigh = 0.003f },
            new MaterialSpec { Name = "Ceramic",  Guid = "1ae39e85ad35335439c46ec8a6d27ea9", AbsLow = 0.01f, AbsMid = 0.01f, AbsHigh = 0.02f, Scattering = 0.05f, TransLow = 0.06f,  TransMid = 0.044f, TransHigh = 0.011f },
            new MaterialSpec { Name = "Concrete", Guid = "2a011bab5de11df4c81c68931509f73a", AbsLow = 0.05f, AbsMid = 0.07f, AbsHigh = 0.08f, Scattering = 0.05f, TransLow = 0.015f, TransMid = 0.002f, TransHigh = 0.001f },
            new MaterialSpec { Name = "Default",  Guid = "a086f686223eed942816c70be67841b0", AbsLow = 0.10f, AbsMid = 0.20f, AbsHigh = 0.30f, Scattering = 0.05f, TransLow = 0.10f,  TransMid = 0.05f,  TransHigh = 0.03f },
            new MaterialSpec { Name = "Glass",    Guid = "08bf73ad12cb44944875b02ce7f9d694", AbsLow = 0.06f, AbsMid = 0.03f, AbsHigh = 0.02f, Scattering = 0.05f, TransLow = 0.06f,  TransMid = 0.044f, TransHigh = 0.011f },
            new MaterialSpec { Name = "Gravel",   Guid = "b1c2b85c159e39042b3aa49340f34357", AbsLow = 0.60f, AbsMid = 0.70f, AbsHigh = 0.80f, Scattering = 0.05f, TransLow = 0.031f, TransMid = 0.012f, TransHigh = 0.008f },
            new MaterialSpec { Name = "Metal",    Guid = "d780f25df1d231d4aa9a31ab3ba935d0", AbsLow = 0.20f, AbsMid = 0.07f, AbsHigh = 0.06f, Scattering = 0.05f, TransLow = 0.20f,  TransMid = 0.025f, TransHigh = 0.01f },
            new MaterialSpec { Name = "Plaster",  Guid = "9e0a6d5705eaec64ab63e6ac7132e0a7", AbsLow = 0.12f, AbsMid = 0.06f, AbsHigh = 0.04f, Scattering = 0.05f, TransLow = 0.056f, TransMid = 0.056f, TransHigh = 0.004f },
            new MaterialSpec { Name = "Rock",     Guid = "5c0320d65bcecce4abf2efe02ff313aa", AbsLow = 0.13f, AbsMid = 0.20f, AbsHigh = 0.24f, Scattering = 0.05f, TransLow = 0.015f, TransMid = 0.002f, TransHigh = 0.001f },
            new MaterialSpec { Name = "Wood",     Guid = "5272c55b882edd34a9d80b11bd6f240c", AbsLow = 0.11f, AbsMid = 0.07f, AbsHigh = 0.06f, Scattering = 0.05f, TransLow = 0.07f,  TransMid = 0.014f, TransHigh = 0.005f },
        };

        public override string Key => "steamaudio.settings";
        public override string DisplayName => "Steam Audio Settings & Materials";
        public override string Category => "Steam Audio";
        public override int Version => 1;

        protected override bool Exists()
        {
            return File.Exists(BasisSetupIO.ToFullPath(SettingsPath));
        }

        protected override bool Build(BasisSetupMode mode)
        {
            BasisSetupIO.EnsureAssetFolder(MaterialsFolder);

            bool changed = false;
            for (int i = 0; i < Materials.Length; i++)
            {
                MaterialSpec spec = Materials[i];
                BasisSetupIO.CreateOrUpdateAsset<SteamAudioMaterial>(
                    MaterialsFolder + "/" + spec.Name + ".asset", mode, m =>
                    {
                        m.lowFreqAbsorption = spec.AbsLow;
                        m.midFreqAbsorption = spec.AbsMid;
                        m.highFreqAbsorption = spec.AbsHigh;
                        m.scattering = spec.Scattering;
                        m.lowFreqTransmission = spec.TransLow;
                        m.midFreqTransmission = spec.TransMid;
                        m.highFreqTransmission = spec.TransHigh;
                    }, out bool materialChanged);
                changed |= materialChanged;
                BasisSetupIO.ForceGuid(MaterialsFolder + "/" + spec.Name + ".asset", spec.Guid);
            }

            SteamAudioMaterial defaultMaterial = AssetDatabase.LoadAssetAtPath<SteamAudioMaterial>(DefaultMaterialPath);

            BasisSetupIO.CreateOrUpdateAsset<SteamAudioSettings>(SettingsPath, mode, s =>
            {
                s.defaultMaterial = defaultMaterial;
                s.hrtfVolumeGainDB = 1f;
                s.hrtfNormalizationType = HRTFNormType.RMS;
                s.layerMask = 459;
                s.maxOcclusionSamples = 64;
                s.realTimeMaxSources = 128;
                s.realTimeCPUCoresPercentage = 10;
                s.bakeParametric = true;
                s.deviceType = OpenCLDeviceType.Any;
            }, out bool settingsChanged);
            changed |= settingsChanged;

            return changed;
        }
    }
}
