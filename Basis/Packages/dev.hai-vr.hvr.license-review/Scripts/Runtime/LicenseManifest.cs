using System;
using System.Linq;
using UnityEngine;

namespace HVR.LicenseReview
{
    [CreateAssetMenu(fileName = "LicenseManifest", menuName = "HVR.Basis/License Manifest")]
    public class LicenseManifest : ScriptableObject
    {
        /// Packages that are part of this manifest.
        public LicenseTrackedPackage[] trackedPackages = Array.Empty<LicenseTrackedPackage>();

        // Packages that are excluded, usually because they are added to the project by users after the fact
        // (e.g. work tools that are not part of the project and never committed).
        public string[] excludedPackageNames = Array.Empty<string>();

        public LicenseBaked[] baked = Array.Empty<LicenseBaked>();
        public string urlPrefix;

        public bool IsTracked(string packageName, string subPath)
        {
            return trackedPackages.Any(package => package.packageName == packageName && package.licenses.Any(license => license.path == subPath));
        }

        public void Track(string packageName, string[] newPathsToTrack)
        {
            for (var index = 0; index < trackedPackages.Length; index++)
            {
                var trackedPackage = trackedPackages[index];

                if (trackedPackage.packageName == packageName)
                {
                    var existingPaths = trackedPackage.licenses.Select(license => license.path).ToHashSet();
                    var newLicenses = newPathsToTrack.Except(existingPaths)
                        .Select(path => new LicenseTrackedLicense
                        {
                            path = path
                        })
                        .ToList();

                    var newTrackedPackage = trackedPackage;
                    newTrackedPackage.licenses = newTrackedPackage.licenses.Concat(newLicenses).ToArray();
                    trackedPackages[index] = newTrackedPackage;
                    return;
                }
            }

            trackedPackages = trackedPackages
                .Append(new LicenseTrackedPackage
                {
                    packageName = packageName,
                    licenses = newPathsToTrack.Select(path => new LicenseTrackedLicense
                    {
                        path = path
                    }).ToArray()
                })
                .ToArray();
        }

        public void Untrack(string packageName, string[] toUntrack)
        {
            var toUntrackHashSet = toUntrack.ToHashSet();

            for (var index = 0; index < trackedPackages.Length; index++)
            {
                var trackedPackage = trackedPackages[index];

                if (trackedPackage.packageName == packageName)
                {
                    var newTrackedPackage = trackedPackage;
                    newTrackedPackage.licenses = newTrackedPackage.licenses.Where(license => !toUntrackHashSet.Contains(license.path)).ToArray();

                    if (newTrackedPackage.licenses.Length == 0)
                    {
                        trackedPackages = trackedPackages.Where((_, i) => i != index).ToArray();
                    }
                    else
                    {
                        trackedPackages[index] = newTrackedPackage;
                    }
                    return;
                }
            }
        }

        public bool IsExcluded(string packageName)
        {
            return excludedPackageNames.Contains(packageName);
        }

        public void Exclude(string packageName)
        {
            if (trackedPackages.Any(package => package.packageName == packageName)) return;
            if (excludedPackageNames.Contains(packageName)) return;

            excludedPackageNames = excludedPackageNames.Append(packageName).ToArray();
        }
    }

    [Serializable]
    public struct LicenseTrackedPackage
    {
        public string packageName;
        public LicenseTrackedLicense[] licenses;
    }

    [Serializable]
    public struct LicenseTrackedLicense
    {
        public string path;

        public string spdx;

        public string overrideProductName;
    }

    [Serializable]
    public struct LicenseBaked
    {
        public string productName;

        public string packageName;
        public string licensePath;

        public string spdx;
        public string fullLicenseName;
        public string fullLicenseText;

        public string licenseUrl;
    }
}
