using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public class BasisExternalRayTracerBuild : IPostprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPostprocessBuild(BuildReport report)
    {
        bool windows = report.summary.platform == BuildTarget.StandaloneWindows64;
        bool linux = report.summary.platform == BuildTarget.StandaloneLinux64;
        if (!windows && !linux) { return; }
        string source = Path.GetFullPath("Packages/com.basis.globalillumination/Native~/Tracer");
        string executable = windows ? "BasisRayTracer.exe" : "BasisRayTracer";
        if (!File.Exists(Path.Combine(source, executable)))
        {
            string script = windows ? "Native~/BasisRayTracer/build.ps1" : "Native~/BasisRayTracer/build.ps1 -Linux (or build.sh on Linux)";
            Debug.LogWarning($"[BasisGI] The external path tracer was not built for this platform, so Path Traced global illumination falls back in this player. Run {script} in com.basis.globalillumination.");
            return;
        }
        string output = report.summary.outputPath;
        string data = Path.Combine(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output) + "_Data");
        string target = Path.Combine(data, "Plugins", "x86_64", "BasisRayTracer");
        Directory.CreateDirectory(target);
        if (windows)
        {
            File.Copy(Path.Combine(source, executable), Path.Combine(target, executable), true);
            string d3d12 = Path.Combine(source, "D3D12");
            if (Directory.Exists(d3d12)) { Copy(d3d12, Path.Combine(target, "D3D12")); }
        }
        else
        {
            File.Copy(Path.Combine(source, executable), Path.Combine(target, executable), true);
        }
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) { File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true); }
        foreach (string directory in Directory.GetDirectories(source)) { Copy(directory, Path.Combine(target, Path.GetFileName(directory))); }
    }
}
