using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace HVR.LicenseReview.Editor
{
    [CustomEditor(typeof(LicenseManifest))]
    public class LicenseManifestEditor : UnityEditor.Editor
    {
        private const string ChangeLicenseTrackingStatusLabel = "Change license tracking status";
        private const string UnknownLicense = "Unknown";
        private string _rootFullPath;
        private List<PPackage> _allPackages;
        private LicenseManifest my;
        private HashSet<string> _allPackageNames;

        private static bool editLicenses;
        private static bool displayLicenses;

        private void OnEnable()
        {
            my = (LicenseManifest)target;
            _rootFullPath = Path.GetFullPath(".").Replace("\\", "/") + "/";
            _allPackages = FindPackageInformation();
            _allPackageNames = _allPackages.Select(package => package.package.name).ToHashSet();
        }

        private List<PPackage> FindPackageInformation()
        {
            var results = new List<PPackage>();

            var packageList = PackageInfo.GetAllRegisteredPackages();
            foreach (PackageInfo package in packageList)
            {
                if (package.resolvedPath != null && Directory.Exists(package.resolvedPath))
                {
                    var allFiles = Directory.GetFiles(package.resolvedPath, "*", SearchOption.AllDirectories);

                    var foundLicenseFiles = allFiles
                        .Where(file =>
                        {
                            var filename = Path.GetFileName(file).ToLowerInvariant();
                            return filename.Equals("license")
                                || filename.Equals("license.txt")
                                || filename.Equals("license.md")
                                || filename.Equals("copying")
                                || filename.Equals("copying.txt")
                                || filename.Equals("copying.md")
                                || (filename.Contains("_license_") && !filename.EndsWith(".meta"))
                                   ;
                        })
                        .Select(s => s.Replace("\\", "/"))
                        .ToList();
                    var foundTrademarkFiles = allFiles
                        .Where(file =>
                        {
                            var filename = Path.GetFileName(file).ToLowerInvariant();
                            return filename.Contains("trademark") && !filename.EndsWith(".meta");
                        })
                        .Select(s => s.Replace("\\", "/"))
                        .ToList();

                    results.Add(new PPackage
                    {
                        package = package,
                        resolvedPath = package.resolvedPath.Replace("\\", "/"),
                        foundLicenseFiles = foundLicenseFiles.Select(licensePath =>
                        {
                            return Guess(package, licensePath);
                        }).ToList(),
                        foundTrademarkFiles = foundTrademarkFiles.Select(trademarkPath => new PTrademark
                        {
                            trademarkPath = trademarkPath,
                            trademarkContents = File.ReadAllText(trademarkPath, Encoding.UTF8)
                        }).ToList()
                    });
                }
            }

            return results
                .OrderBy(package => package.package.name.StartsWith("com.basis.") ? 0 : 1)
                .ThenBy(package => package.package.name.StartsWith("org.basisvr.") ? 0 : 1)
                .ThenBy(package => package.package.displayName)
                .ToList();
        }

        private PLicense Guess(PackageInfo package, string licensePath)
        {
            var my = (LicenseManifest)target;

            var subPath = licensePath.Substring(package.resolvedPath.Length + 1);

            var contents = File.ReadAllText(licensePath, Encoding.UTF8);
            return new PLicense
            {
                licensePath = licensePath,
                licenseContents = contents,
                licenseGuess = GuessLicense(contents),

                licenseSubPath = subPath,
                isTrackedInManifest = my.IsTracked(package.name, subPath)
            };
        }

        public override void OnInspectorGUI()
        {
            my = (LicenseManifest)target;

            if (GUILayout.Button("Bake"))
            {
                Undo.RecordObject(my, "Bake licenses");
                LicenseBaker.BakeManifest(my, tuple => $"{my.urlPrefix}{tuple.Item1}/{tuple.Item2}");
                return;
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(LicenseManifest.urlPrefix)));
            EditorGUILayout.Separator();

            editLicenses = EditorGUILayout.ToggleLeft("Edit Licenses", editLicenses);
            displayLicenses = EditorGUILayout.ToggleLeft("Display Full License Text", displayLicenses);

            var packagesNotFound = my.trackedPackages.Where(package => !_allPackageNames.Contains(package.packageName)).ToList();

            if (packagesNotFound.Count > 0)
            {
                EditorGUILayout.HelpBox("The manifest contains references to packages that don't exist in this project.", MessageType.Error);
                foreach (var package in packagesNotFound)
                {
                    EditorGUILayout.LabelField($"- {package.packageName} not found in project");
                }
                EditorGUILayout.Separator();
            }

            var partitonByIsNotUnity = Partition(_allPackages, package => !package.package.name.StartsWith("com.unity."));

            var partitionByHasAnyLicense = Partition(partitonByIsNotUnity.matches, package => package.foundLicenseFiles.Count > 0);

            EditorGUILayout.LabelField($"Found license files across {partitionByHasAnyLicense.matches.Count} packages.");
            EditorGUILayout.LabelField($"Found {partitionByHasAnyLicense.rest.Count} packages with no license file.");
            EditorGUILayout.Separator();

            var guesses = partitionByHasAnyLicense.matches.GroupBy(package =>
                {
                    if (package.foundLicenseFiles.Count == 0) return "Missing";
                    if (package.foundLicenseFiles.Count == 1) return package.foundLicenseFiles[0].licenseGuess;
                    if (package.foundLicenseFiles.Select(license => license.licenseGuess).Distinct().Count() > 1) return "Multiple license types found";
                    return package.foundLicenseFiles[0].licenseGuess;
                })
                .OrderBy(packages => packages.Key != "Missing" ? 0 : 1)
                .ThenBy(packages => packages.Key != UnknownLicense ? 0 : 1)
                .ThenBy(packages => packages.Key != "Multiple license types found" ? 0 : 1);
            foreach (var groups in guesses)
            {
                EditorGUILayout.BeginVertical("GroupBox");
                EditorGUILayout.LabelField(groups.Key + "?", EditorStyles.boldLabel);

                EditorGUILayout.Separator();
                foreach (var package in groups)
                {
                    var hasMoreThanOneLicenseInIt = package.foundLicenseFiles.Count > 1;

                    LayoutPackageName(package);

                    var isExcluded = my.IsExcluded(package.package.name);
                    if (isExcluded) EditorGUI.BeginDisabledGroup(true);

                    if (!isExcluded && (hasMoreThanOneLicenseInIt || package.foundLicenseFiles[0].isTrackedInManifest && editLicenses))
                    {
                        foreach (var licenseFile in package.foundLicenseFiles)
                        {
                            EditorGUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("", GUILayout.Width(20));
                            var isTracked = licenseFile.isTrackedInManifest;
                            var newTracked = EditorGUILayout.Toggle(isTracked, GUILayout.Width(20));
                            if (newTracked != isTracked)
                            {
                                Undo.RecordObject(my, ChangeLicenseTrackingStatusLabel);
                                if (newTracked)
                                {
                                    my.Track(package.package.name, new [] { licenseFile.licenseSubPath });
                                }
                                else
                                {
                                    my.Untrack(package.package.name, new [] { licenseFile.licenseSubPath });
                                }
                                licenseFile.isTrackedInManifest = newTracked;
                                serializedObject.Update();
                            }
                            EditorGUILayout.LabelField(licenseFile.licenseGuess + "?", GUILayout.Width(100));
                            EditorGUILayout.LabelField(licenseFile.licenseSubPath);
                            EditorGUILayout.EndHorizontal();

                            if (isTracked && editLicenses)
                            {
                                var trackedPackagesSp = serializedObject.FindProperty(nameof(LicenseManifest.trackedPackages));
                                for (var i = 0; i < trackedPackagesSp.arraySize; i++)
                                {
                                    var packageElt = trackedPackagesSp.GetArrayElementAtIndex(i);
                                    var packageNameSp = packageElt.FindPropertyRelative(nameof(LicenseTrackedPackage.packageName));
                                    if (packageNameSp.stringValue == package.package.name)
                                    {
                                        var licensesProperty = packageElt.FindPropertyRelative(nameof(LicenseTrackedPackage.licenses));
                                        for (var j = 0; j < licensesProperty.arraySize; j++)
                                        {
                                            var licenseSp = licensesProperty.GetArrayElementAtIndex(j);
                                            var pathSo = licenseSp.FindPropertyRelative(nameof(LicenseTrackedLicense.path));
                                            if (pathSo.stringValue == licenseFile.licenseSubPath)
                                            {
                                                EditorGUILayout.BeginVertical("GroupBox");
                                                EditorGUILayout.LabelField($"{package.package.displayName} ({package.package.name})", EditorStyles.boldLabel);
                                                EditorGUILayout.BeginHorizontal();
                                                EditorGUILayout.LabelField(licenseFile.licensePath);
                                                if (GUILayout.Button("Open package.json", GUILayout.Width(150)))
                                                {
                                                    Application.OpenURL($"file://{package.package.resolvedPath}/package.json");
                                                }
                                                if (GUILayout.Button("Open license", GUILayout.Width(100)))
                                                {
                                                    Application.OpenURL($"file://{licenseFile.licensePath}");
                                                }
                                                EditorGUILayout.EndHorizontal();
                                                EditorGUILayout.Separator();

                                                var spdxSp = licenseSp.FindPropertyRelative(nameof(LicenseTrackedLicense.spdx));
                                                EditorGUILayout.PropertyField(spdxSp, new GUIContent("SPDX"));
                                                if (licenseFile.licenseGuess != UnknownLicense && spdxSp.stringValue != licenseFile.licenseGuess)
                                                {
                                                    EditorGUILayout.HelpBox($"The SPDX identifier does not match our guess: {licenseFile.licenseGuess}. Please review.", MessageType.Warning);
                                                }
                                                else if (licenseFile.licenseGuess == UnknownLicense)
                                                {
                                                    if (string.IsNullOrWhiteSpace(spdxSp.stringValue))
                                                    {
                                                        EditorGUILayout.HelpBox($"We were unable to guess the license for this. Please find and specify the license.", MessageType.Error);
                                                    }
                                                    else
                                                    {
                                                        EditorGUILayout.HelpBox($"We were unable to guess the license for this. Make sure the license is correct.", MessageType.Info);
                                                    }
                                                }

                                                EditorGUILayout.Separator();
                                                EditorGUILayout.LabelField($"Overrides", EditorStyles.boldLabel);
                                                EditorGUILayout.PropertyField(licenseSp.FindPropertyRelative(nameof(LicenseTrackedLicense.overrideProductName)));

                                                if (displayLicenses)
                                                {
                                                    EditorGUILayout.Separator();
                                                    EditorGUILayout.TextArea(licenseFile.licenseContents, GUILayout.Height(100));
                                                }
                                                EditorGUILayout.EndVertical();
                                                break;
                                            }
                                        }
                                        break;
                                    }
                                }
                            }

                            // EditorGUILayout.TextArea(licenseFile.licenseContents, GUILayout.Height(100));
                        }
                    }

                    if (!isExcluded)
                    {
                        LayoutTrademarks(package);
                    }
                    if (isExcluded) EditorGUI.EndDisabledGroup();
                }
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Separator();
            EditorGUILayout.LabelField("No license found", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical("GroupBox");
            foreach (var package in partitionByHasAnyLicense.rest)
            {
                LayoutPackageName(package);
                LayoutTrademarks(package);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Separator();
            EditorGUILayout.LabelField("Unity packages that have a LICENSE in them", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("GroupBox");
            foreach (var package in partitonByIsNotUnity.rest.Where(package => package.foundLicenseFiles.Count > 0))
            {
                LayoutPackageName(package, true);
            }
            EditorGUILayout.EndVertical();

            serializedObject.ApplyModifiedProperties();
        }

        private void LayoutPackageName(PPackage package, bool forceDoNotShowLicenses = false)
        {
            EditorGUILayout.BeginHorizontal();

            var isAlreadyTracked = package.foundLicenseFiles.Any(license => license.isTrackedInManifest);
            var isExcluded = my.IsExcluded(package.package.name);

            if (isExcluded) EditorGUI.BeginDisabledGroup(true);

            if (!forceDoNotShowLicenses && package.foundLicenseFiles.Count > 0)
            {
                var isTracked = package.foundLicenseFiles.All(license => license.isTrackedInManifest);
                var newTracked = EditorGUILayout.Toggle(isTracked, GUILayout.Width(20));
                if (isTracked != newTracked)
                {
                    Undo.RecordObject(my, ChangeLicenseTrackingStatusLabel);
                    if (newTracked)
                    {
                        my.Track(package.package.name, package.foundLicenseFiles.Select(license => license.licenseSubPath).ToArray());
                    }
                    else
                    {
                        my.Untrack(package.package.name, package.foundLicenseFiles.Select(license => license.licenseSubPath).ToArray());
                    }
                    foreach (var license in package.foundLicenseFiles)
                    {
                        license.isTrackedInManifest = newTracked;
                    }
                    serializedObject.Update();
                }
            }

            EditorGUILayout.LabelField(package.package.displayName, EditorStyles.boldLabel, GUILayout.Width(300));
            EditorGUILayout.LabelField($"→ {package.package.name} ({SimplifyPath(package.resolvedPath)})", EditorStyles.boldLabel);
            if (!isAlreadyTracked && !isExcluded && !forceDoNotShowLicenses)
            {
                if (GUILayout.Button("Exclude"))
                {
                    my.Exclude(package.package.name);
                }
            }

            if (isExcluded)
            {
                EditorGUILayout.LabelField("Excluded", EditorStyles.boldLabel, GUILayout.Width(100));
            }

            if (isExcluded) EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
        }

        private void LayoutTrademarks(PPackage package)
        {
            if (package.foundTrademarkFiles.Count > 0)
            {
                EditorGUILayout.BeginVertical("GroupBox");
                EditorGUILayout.LabelField("Trademarks", EditorStyles.boldLabel);
                foreach (var trademarkFile in package.foundTrademarkFiles)
                {
                    EditorGUILayout.LabelField(SimplifyPath(trademarkFile.trademarkPath));
                    EditorGUILayout.TextArea(trademarkFile.trademarkContents, GUILayout.Height(100));
                }
                EditorGUILayout.EndVertical();
            }
        }

        private string SimplifyPath(string path)
        {
            return path.StartsWith(_rootFullPath) ? path.Substring(_rootFullPath.Length) : path;
        }

        private static (List<T> matches, List<T> rest) Partition<T>(IEnumerable<T> source, Func<T, bool> predicate)
        {
            var matches = new List<T>();
            var rest = new List<T>();
            foreach (var item in source)
            {
                if (predicate(item)) matches.Add(item);
                else rest.Add(item);
            }
            return (matches, rest);
        }

        private string GuessLicense(string licenseContents)
        {
            // This function only provides a guess, it may be incorrect. Only display this string for aid purposes.
            // The developer using the tool should be responsible for checking
            // before writing or serializing the result into a data table.

            if (string.IsNullOrWhiteSpace(licenseContents))
                return UnknownLicense;

            var normalizedContents = licenseContents.ToLowerInvariant().Replace("\r\n", "\n");

            if (normalizedContents.Contains("unity companion license"))
                return "Unity Companion License";

            if (normalizedContents.Contains("permission is hereby granted, free of charge") &&
                normalizedContents.Contains("mit license"))
                return "MIT";

            if (normalizedContents.Contains("permission is hereby granted, free of charge") &&
                normalizedContents.Contains("without restriction") &&
                normalizedContents.Contains("use, copy, modify, merge, publish, distribute, sublicense"))
                return "MIT";

            if (normalizedContents.Contains("apache license") && normalizedContents.Contains("version 2.0"))
                return "Apache-2.0";

            if (normalizedContents.Contains("redistribution and use in source and binary forms"))
            {
                if (normalizedContents.Contains("3-clause"))
                    return "BSD-3-Clause";
                if (normalizedContents.Contains("2-clause"))
                    return "BSD-2-Clause";

                var clauseCount = 0;
                if (normalizedContents.Contains("redistributions of source code must retain"))
                    clauseCount++;
                if (normalizedContents.Contains("redistributions in binary form must reproduce"))
                    clauseCount++;
                if (normalizedContents.Contains("neither the name of"))
                    clauseCount++;

                if (clauseCount == 3)
                    return "BSD-3-Clause";
                if (clauseCount == 2)
                    return "BSD-2-Clause";

                return "BSD";
            }

            if (normalizedContents.Contains("this is free and unencumbered software released into the public domain"))
                return "Unlicense";

            if (normalizedContents.Contains("creative commons") && normalizedContents.Contains("cc0"))
                return "CC0-1.0";

            return UnknownLicense;
        }

        private class PPackage
        {
            public PackageInfo package;
            public string resolvedPath;
            public List<PLicense> foundLicenseFiles;
            public List<PTrademark> foundTrademarkFiles;
        }

        private class PLicense
        {
            public string licensePath;
            public string licenseContents;
            public string licenseGuess;
            public string licenseSubPath;
            public bool isTrackedInManifest;
        }

        private class PTrademark
        {
            public string trademarkPath;
            public string trademarkContents;
        }
    }
}
