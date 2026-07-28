using System.Collections.Generic;

namespace Klak.Syphon {

public static class SyphonServerDirectory
{
    public static IEnumerable<string> ServerNames => EnumerateServerNames();

    public static IEnumerable<string> EnumerateServerNames()
    {
        using var plugin = Interop.ServerList.Create();
        var list = new List<string>();
        for (var i = 0; i < (plugin?.Count ?? 0); i++)
        {
            var (app, name) = (plugin.GetAppName(i), plugin.GetName(i));
            list.Add(string.IsNullOrEmpty(name) ? app : $"{app}/{name}");
        }
        return list;
    }
}

} // namespace Klak.Syphon
