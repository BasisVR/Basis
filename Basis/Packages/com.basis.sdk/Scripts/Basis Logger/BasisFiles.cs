using Unity.Scripting.LifecycleManagement;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

[NoAutoStaticsCleanup]
public static class BasisFiles
{
    public static Task<string> ReadAllTextAsync(string path, CancellationToken token = default)
    {
        if (BasisTasks.ThreadsAvailable) return File.ReadAllTextAsync(path, token);
        if (token.IsCancellationRequested) return Task.FromCanceled<string>(token);
        try
        {
            return Task.FromResult(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return Task.FromException<string>(ex);
        }
    }

    public static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default)
    {
        if (BasisTasks.ThreadsAvailable) return File.ReadAllBytesAsync(path, token);
        if (token.IsCancellationRequested) return Task.FromCanceled<byte[]>(token);
        try
        {
            return Task.FromResult(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            return Task.FromException<byte[]>(ex);
        }
    }

    public static Task WriteAllTextAsync(string path, string contents, CancellationToken token = default)
    {
        if (BasisTasks.ThreadsAvailable) return File.WriteAllTextAsync(path, contents, token);
        if (token.IsCancellationRequested) return Task.FromCanceled(token);
        try
        {
            File.WriteAllText(path, contents);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken token = default)
    {
        if (BasisTasks.ThreadsAvailable) return File.WriteAllBytesAsync(path, bytes, token);
        if (token.IsCancellationRequested) return Task.FromCanceled(token);
        try
        {
            File.WriteAllBytes(path, bytes);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }
}
