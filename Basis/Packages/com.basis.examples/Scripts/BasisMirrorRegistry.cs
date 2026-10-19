using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;

[AutoStaticsCleanup]
public static partial class BasisMirrorRegistry
{
    public const string SpawnUrl = "Personal Mirror";

    private static readonly List<BasisSDKMirror> mirrors = new List<BasisSDKMirror>();

    public static IReadOnlyList<BasisSDKMirror> Mirrors => mirrors;
    public static int Count => mirrors.Count;

    public static event Action OnChanged;

    public static void Add(BasisSDKMirror mirror)
    {
        if (mirror == null) return;
        if (mirrors.Contains(mirror)) return;
        mirrors.Add(mirror);
        OnChanged?.Invoke();
    }

    public static void Remove(BasisSDKMirror mirror)
    {
        if (mirror == null) return;
        if (mirrors.Remove(mirror)) OnChanged?.Invoke();
    }
}
