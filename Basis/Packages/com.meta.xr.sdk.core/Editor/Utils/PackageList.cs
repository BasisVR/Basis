/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.PackageManager.UI;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Meta.XR.Editor.Utils
{
    [InitializeOnLoad]
    internal static class PackageList
    {
        private static ListRequest _packageManagerListRequest;
        private static Dictionary<string, PackageInfo> _packagesDictionary;

        static PackageList()
        {
            _packageManagerListRequest = Client.List(offlineMode: false, includeIndirectDependencies: true);
            Events.registeringPackages += RegisteringPackagesEventHandler;
        }

        private static void RegisteringPackagesEventHandler(PackageRegistrationEventArgs args)
        {
            if (!PackageManagerListAvailable || _packageManagerListRequest.Result == null)
            {
                return;
            }
            _packagesDictionary ??= _packageManagerListRequest.Result.ToDictionary(package => package.name);

            foreach (PackageInfo addedPackage in args.added)
            {
                _packagesDictionary.Add(addedPackage.name, addedPackage);
            }

            foreach (PackageInfo removedPackage in args.removed)
            {
                _packagesDictionary.Remove(removedPackage.name);
            }

            foreach (PackageInfo changedTo in args.changedTo)
            {
                _packagesDictionary[changedTo.name] = changedTo;
            }
        }

        public static bool PackageManagerListAvailable => _packageManagerListRequest is { Status: StatusCode.Success };

        public static PackageInfo GetPackage(string packageId)
        {
            if (!PackageManagerListAvailable || _packageManagerListRequest.Result == null)
            {
                return null;
            }

            _packagesDictionary ??= _packageManagerListRequest.Result.ToDictionary(package => package.name);

            var (name, _, _) = ParsePackageId(packageId);
            return _packagesDictionary.GetValueOrDefault(name);
        }

        internal static (string name, string version, string sampleName) ParsePackageId(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                throw new ArgumentNullException();
            }

            var containsSampleName = packageId.Contains(":");
            var containsVersion = packageId.Contains("@");

            return (containsSampleName, containsVersion) switch
            {
                (true, true) => throw new ArgumentException("Setting both sample name and version in the packageId is not supported."),
                (true, false) => ParseWithSampleName(packageId),
                (false, true) => ParseWithVersion(packageId),
                (false, false) => (packageId, null, null)
            };

            (string, string, string) ParseWithSampleName(string s)
            {
                var (packageName, sampleName) = SplitStringBySeparator(s, ":");

                if (string.IsNullOrEmpty(sampleName))
                {
                    throw new ArgumentException($"{nameof(sampleName)} cannot be null or empty");
                }

                return (packageName, null, sampleName);
            }

            (string, string, string) ParseWithVersion(string s)
            {
                var (packageName, packageVersion) = SplitStringBySeparator(s, "@");

                if (string.IsNullOrEmpty(packageVersion))
                {
                    throw new ArgumentException($"{nameof(packageVersion)} cannot be null or empty");
                }

                return (packageName, packageVersion, null);
            }

            (string, string) SplitStringBySeparator(string s, string separator)
            {
                var parts = s.Split(separator);
                var p1 = parts[0];
                var p2 = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : null;
                return (p1, p2);
            }
        }

        public static bool IsPackageInstalled(string packageId) => GetPackage(packageId) != null;

        public static bool IsPackageInstalledWithValidVersion(string packageId)
        {
            var (_, expectedPackageVersion, sampleName) = ParsePackageId(packageId);
            var installedPacked = GetPackage(packageId);

            if (installedPacked == null)
            {
                return false;
            }

            return ValidatePackageVersion() && ValidateSample();

            bool ValidatePackageVersion()
            {
                return string.IsNullOrEmpty(expectedPackageVersion) || IsVersionValid(expectedPackageVersion, installedPacked.version);
            }

            bool ValidateSample()
            {
                if (string.IsNullOrEmpty(sampleName))
                {
                    return true;
                }

                return Sample.FindByPackage(installedPacked.name, installedPacked.version)
                    .Any(sample => sample.displayName == sampleName && sample.isImported);
            }
        }

        internal static bool IsValidPackageName(string packageName)
        {
            const string pattern = @"^([a-z0-9]+(-[a-z0-9]+)*\.)+[a-z]{2,}(@([0-9]+\.){2}[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?)?$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            return regex.IsMatch(packageName);
        }

        internal static bool IsValidPackageId(string packageId)
        {
            try
            {
                var (packageName, packageVersion, sampleName) = ParsePackageId(packageId);

                if (!IsValidPackageName(packageName))
                {
                    return false;
                }

                if (packageVersion != null)
                {
                    return IsValidSemanticVersion(packageVersion);
                }

                return sampleName == null || sampleName.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool IsValidSemanticVersion(string version)
        {
            return !string.IsNullOrEmpty(version) && Version.TryParse(NormalizeVersion(version), out _);
        }

        internal static bool IsVersionValid(string expectedVersion, string actualVersion)
        {
            actualVersion = NormalizeInternalPackageVersion(actualVersion);

            if (!IsValidSemanticVersion(expectedVersion) || !IsValidSemanticVersion(actualVersion))
                return false;

            var normalizedExpectedVersion = NormalizeVersion(expectedVersion);
            var normalizedActualVersion = NormalizeVersion(actualVersion);

            if (!Version.TryParse(normalizedExpectedVersion, out var expected) ||
                !Version.TryParse(normalizedActualVersion, out var actual))
            {
                return false;
            }

            if (expectedVersion.StartsWith("~"))
            {
                return expected.Major == actual.Major && expected.Minor == actual.Minor &&
                       actual.Build >= expected.Build;
            }

            if (expectedVersion.StartsWith("^"))
            {
                return expected.Major == actual.Major && actual.Minor >= expected.Minor &&
                       (expected.Minor != actual.Minor || actual.Build >= expected.Build);
            }

            if (expectedVersion.StartsWith(">="))
            {
                return actual >= expected;
            }

            return expected.Equals(actual);
        }

        private static string NormalizeVersion(string version)
        {
            var prefixes = new[] { "^", "~", ">=" };

            foreach (var prefix in prefixes)
            {
                if (version.StartsWith(prefix))
                {
                    return version[prefix.Length..];
                }
            }

            return version;
        }

        private static string NormalizeInternalPackageVersion(string version)
        {
            if (version == null)
            {
                return null;
            }

            var dashIndex = version.IndexOf('-');
            return dashIndex >= 0 ? version[..dashIndex] : version;
        }
    }
}
