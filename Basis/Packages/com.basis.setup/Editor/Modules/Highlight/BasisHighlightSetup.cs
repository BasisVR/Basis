using System.IO;
using Basis.Scripts.BasisSdk.Highlight;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Basis.Setup.Modules
{
    public sealed class BasisHighlightSetup : BasisSetupModuleBase
    {
        private const string Folder = "Assets/Basis/Settings/Highlight";
        private const string DesktopPath = Folder + "/BasisHighlightSettingsDesktop.asset";
        private const string AndroidPath = Folder + "/BasisHighlightSettingsAndroid.asset";
        private const string DesktopGuid = "c301ac59f1dce524f8e770032f059e8f";
        private const string AndroidGuid = "3102546134695bd41b2d54c183638e5b";

        private const string MaskMaterialGuid = "3e981dc0e61e7de49b09dbf8a5676c08";
        private const string BlurMaterialGuid = "208ac887fc657b54e870bb2507e87031";
        private const string CompositeMaterialGuid = "44865722780e16b4bb2b7de1dfa10b68";

        public override string Key => "highlight.settings";
        public override string DisplayName => "Highlight Settings (Desktop / Android)";
        public override string Category => "Highlight";
        public override int Version => 1;

        protected override bool Exists()
        {
            return File.Exists(BasisSetupIO.ToFullPath(DesktopPath))
                && File.Exists(BasisSetupIO.ToFullPath(AndroidPath));
        }

        protected override bool Build(BasisSetupMode mode)
        {
            BasisSetupIO.EnsureAssetFolder(Folder);

            Material mask = LoadMaterial(MaskMaterialGuid);
            Material blur = LoadMaterial(BlurMaterialGuid);
            Material composite = LoadMaterial(CompositeMaterialGuid);

            BasisSetupIO.CreateOrUpdateAsset<BasisHighlightSettings>(DesktopPath, mode, s =>
            {
                s.defaultMaskMaterial = mask;
                s.blurMaterial = blur;
                s.compositeMaterial = composite;
                s.outlineColor = new Color(0f, 1f, 1f, 1f);
                s.outlineWidth = 4;
                s.maskDownsample = 2;
                s.maskMsaa = BasisHighlightSettings.MaskMsaa.X4;
                s.ringSoftness = 0.15f;
                s.ringSoftenRadius = 1f;
                s.innerSoftenRadius = 0.5f;
                s.glowIntensity = 0.6f;
                s.glowRadius = 1.25f;
                s.glowIterations = 2;
                s.interiorFill = 0f;
                s.injectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;
                s.debugMode = BasisHighlightSettings.DebugMode.Off;
            }, out bool desktopChanged);
            BasisSetupIO.ForceGuid(DesktopPath, DesktopGuid);

            BasisSetupIO.CreateOrUpdateAsset<BasisHighlightSettings>(AndroidPath, mode, s =>
            {
                s.defaultMaskMaterial = mask;
                s.blurMaterial = blur;
                s.compositeMaterial = composite;
                s.outlineColor = new Color(0.4117647f, 0.28235295f, 0.8509804f, 1f);
                s.outlineWidth = 1;
                s.maskDownsample = 3;
                s.maskMsaa = BasisHighlightSettings.MaskMsaa.X2;
                s.ringSoftness = 0.075f;
                s.ringSoftenRadius = 0.5f;
                s.innerSoftenRadius = 0.25f;
                s.glowIntensity = 0f;
                s.glowRadius = 1.25f;
                s.glowIterations = 0;
                s.interiorFill = 0f;
                s.injectionPoint = RenderPassEvent.AfterRenderingTransparents;
                s.debugMode = BasisHighlightSettings.DebugMode.Off;
            }, out bool androidChanged);
            BasisSetupIO.ForceGuid(AndroidPath, AndroidGuid);

            return desktopChanged || androidChanged;
        }

        private static Material LoadMaterial(string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Material>(path);
        }
    }
}
