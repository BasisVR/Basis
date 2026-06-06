using Basis.Scripts.Device_Management;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Basis.Scripts.UI.NamePlate
{
    public static class BasisNamePlateAssets
    {
        public static Material TransparentMaterial;
        public static Material OpaqueMaterial;
        public static Material SelectedMaterial;
        public static TextMeshPro TextBaker;
        public static bool IsReady => TextBaker != null && SelectedMaterial != null;

        private const string TransparentMaterialAddress = "Packages/com.basis.sdk/Materials/TransParentNamePlateMaterial.mat";
        private const string OpaqueMaterialAddress = "Packages/com.basis.sdk/Materials/OpaqueNamePlateMaterial.mat";
        private const string FontAddress = "Packages/com.basis.sdk/Fonts/Poppins-Regular SDF NamePlate.asset";
        private const float DefaultBakeFontSize = 72f;

        private static bool initialized;
        private static bool unicodeFallbacksEnsured;
        private static HashSet<string> installedFontNamesLower;

        public static void Initialize()
        {
            if (initialized)
            {
                EnsureBakerParent();
                return;
            }

            if (TransparentMaterial == null)
            {
                Addressables.LoadAssetAsync<Material>(TransparentMaterialAddress).Completed += OnTransparentMaterialLoaded;
            }
            if (OpaqueMaterial == null)
            {
                Addressables.LoadAssetAsync<Material>(OpaqueMaterialAddress).Completed += OnOpaqueMaterialLoaded;
            }

            UpdateSelectedMaterial();

            if (TextBaker == null)
            {
                Addressables.LoadAssetAsync<TMP_FontAsset>(FontAddress).Completed += OnFontLoaded;
            }

            EnsureBakerParent();
            initialized = true;
        }

        private static void OnTransparentMaterialLoaded(AsyncOperationHandle<Material> handle)
        {
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                TransparentMaterial = handle.Result;
                UpdateSelectedMaterial();
            }
        }

        private static void OnOpaqueMaterialLoaded(AsyncOperationHandle<Material> handle)
        {
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                OpaqueMaterial = handle.Result;
                UpdateSelectedMaterial();
            }
        }

        private static void OnFontLoaded(AsyncOperationHandle<TMP_FontAsset> handle)
        {
            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null) return;
            if (TextBaker != null) return;

            GameObject bakingGO = new GameObject("BasisNameplateBaker");
            TextBaker = bakingGO.AddComponent<TextMeshPro>();
            TextBaker.font = handle.Result;
            TextBaker.fontSize = DefaultBakeFontSize;
            TextBaker.enableAutoSizing = false;
            TextBaker.alignment = TextAlignmentOptions.Center;
            TextBaker.color = Color.white;
            TextBaker.enableVertexGradient = false;
            TextBaker.textWrappingMode = TextWrappingModes.NoWrap;
            TextBaker.overflowMode = TextOverflowModes.Overflow;

            EnsureBakerParent();
            bakingGO.SetActive(false);
            EnsureUnicodeFallbacksOnNameplateFont();
        }

        private static void UpdateSelectedMaterial()
        {
            SelectedMaterial = BasisDeviceManagement.IsMobileHardware()
                ? OpaqueMaterial
                : TransparentMaterial;
        }

        private static void EnsureBakerParent()
        {
            if (TextBaker == null || BasisDeviceManagement.Instance == null) return;
            Transform bakerTransform = TextBaker.transform;
            Transform deviceTransform = BasisDeviceManagement.Instance.transform;
            if (bakerTransform.parent != deviceTransform)
            {
                bakerTransform.SetParent(deviceTransform, false);
            }
        }

        private static void EnsureUnicodeFallbacksOnNameplateFont()
        {
            if (unicodeFallbacksEnsured) return;
            unicodeFallbacksEnsured = true;

            if (TextBaker == null || TextBaker.font == null) return;

            TMP_FontAsset primary = TextBaker.font;
            if (primary.fallbackFontAssetTable == null)
            {
                primary.fallbackFontAssetTable = new List<TMP_FontAsset>();
            }

            HashSet<string> registered = new HashSet<string>();
            string[][] scriptCandidates =
            {
                new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo UI", "Meiryo", "MS Gothic", "Hiragino Sans", "Hiragino Kaku Gothic ProN", "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP", "TakaoGothic" },
                new[] { "Malgun Gothic", "Gulim", "Dotum", "Batang", "Apple SD Gothic Neo", "AppleGothic", "Noto Sans CJK KR", "Noto Sans KR", "NanumGothic" },
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "PingFang SC", "Hiragino Sans GB", "STHeiti", "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC", "WenQuanYi Micro Hei" },
                new[] { "Microsoft JhengHei UI", "Microsoft JhengHei", "PMingLiU", "MingLiU", "PingFang TC", "Heiti TC", "Noto Sans CJK TC", "Noto Sans TC" },
                new[] { "Tahoma", "Segoe UI", "Geeza Pro", "Damascus", "Noto Sans Arabic", "Noto Naskh Arabic", "DejaVu Sans" },
                new[] { "Leelawadee UI", "Leelawadee", "Thonburi", "Sukhumvit Set", "Noto Sans Thai", "Loma" },
                new[] { "David CLM", "Arial Hebrew", "Tahoma", "Segoe UI", "Lucida Grande", "Noto Sans Hebrew", "DejaVu Sans" },
                new[] { "Nirmala UI", "Mangal", "Devanagari MT", "Kohinoor Devanagari", "Noto Sans Devanagari", "Lohit Devanagari" },
            };

            foreach (string[] candidates in scriptCandidates)
            {
                AddFirstAvailableFallback(primary, candidates, registered);
            }
        }

        private static void AddFirstAvailableFallback(TMP_FontAsset primary, string[] candidates, HashSet<string> registered)
        {
            foreach (string family in candidates)
            {
                if (registered.Contains(family)) continue;
                if (!IsFontInstalled(family)) continue;

                TMP_FontAsset fallback = null;
                try
                {
                    fallback = TMP_FontAsset.CreateFontAsset(family, "Regular");
                }
                catch
                {
                    continue;
                }

                if (fallback == null) continue;

                fallback.name = "NamePlate Fallback (" + family + ")";
                primary.fallbackFontAssetTable.Add(fallback);
                registered.Add(family);
                return;
            }
        }

        private static bool IsFontInstalled(string family)
        {
            if (installedFontNamesLower == null)
            {
                HashSet<string> set = new HashSet<string>();
                try
                {
                    string[] names = Font.GetOSInstalledFontNames();
                    if (names != null)
                    {
                        foreach (string name in names)
                        {
                            if (!string.IsNullOrEmpty(name)) set.Add(name.ToLowerInvariant());
                        }
                    }
                }
                catch
                {
                }
                installedFontNamesLower = set;
            }

            if (installedFontNamesLower.Count == 0) return true;
            string normalized = family.ToLowerInvariant();
            if (installedFontNamesLower.Contains(normalized)) return true;

            foreach (string installedName in installedFontNamesLower)
            {
                if (installedName.StartsWith(normalized)) return true;
            }
            return false;
        }
    }
}
