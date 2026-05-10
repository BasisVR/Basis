using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor.PackageManager;

namespace HVR.LicenseReview.Editor
{
    public static class LicenseBaker
    {
        public static void BakeManifest(LicenseManifest manifest, Func<(string, string), string> urlFn)
        {
            var packagesInProject = PackageInfo.GetAllRegisteredPackages().ToDictionary(info => info.name, info => info);

            manifest.baked = manifest.trackedPackages
                .Where(package => packagesInProject.ContainsKey(package.packageName))
                .SelectMany(package =>
                {
                    var packageInProject = packagesInProject[package.packageName];

                    return package.licenses.Select(license => new LicenseBaked
                    {
                        productName = !string.IsNullOrWhiteSpace(license.overrideProductName) ? license.overrideProductName : packageInProject.displayName,
                        packageName = package.packageName,
                        licensePath = license.path,
                        spdx = license.spdx,
                        fullLicenseName = FromSpdxToFullLicenseName(license.spdx),
                        licenseUrl = urlFn.Invoke((package.packageName, license.path)),
                        fullLicenseText = File.ReadAllText(Path.Combine(packageInProject.resolvedPath, license.path), Encoding.UTF8)
                    });
                })
                .OrderBy(baked => baked.productName)
                .ThenBy(baked => baked.licensePath)
                .ToArray();
        }

        private static string FromSpdxToFullLicenseName(string licenseSpdx)
        {
            return licenseSpdx switch
            {
                "MIT" => "MIT License",
                "Apache-2.0" => "Apache License 2.0",
                "BSD-3-Clause" => "BSD 3-Clause License",
                "BSD-2-Clause" => "BSD 2-Clause License",
                "Unlicense" => "The Unlicense",
                _ => licenseSpdx
            };
        }
    }
}
