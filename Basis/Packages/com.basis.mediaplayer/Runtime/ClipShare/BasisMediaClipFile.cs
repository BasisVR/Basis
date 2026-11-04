using System;
using System.IO;
using System.IO.MemoryMappedFiles;

public sealed class BasisMediaClipFile : IDisposable
{
    public readonly string Path;
    public readonly long Length;
    private FileStream stream;
    private MemoryMappedFile map;
    private MemoryMappedViewAccessor view;

    private BasisMediaClipFile(string path, long length)
    {
        Path = path;
        Length = length;
    }

    public static BasisMediaClipFile CreateForWrite(string path, long length)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        var file = new BasisMediaClipFile(path, length);
        file.stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1, FileOptions.None);
        file.stream.SetLength(length);
        file.TryMap(MemoryMappedFileAccess.ReadWrite);
        return file;
    }

    public static BasisMediaClipFile OpenForRead(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        var file = new BasisMediaClipFile(path, stream.Length) { stream = stream };
        file.TryMap(MemoryMappedFileAccess.Read);
        return file;
    }

    private void TryMap(MemoryMappedFileAccess access)
    {
        if (Length <= 0) return;
        try
        {
            map = MemoryMappedFile.CreateFromFile(stream, null, Length, access, HandleInheritability.None, true);
            view = map.CreateViewAccessor(0, Length, access);
        }
        catch (Exception)
        {
            view?.Dispose();
            map?.Dispose();
            view = null;
            map = null;
        }
    }

    public void Write(long offset, byte[] source, int sourceOffset, int count)
    {
        if (view != null)
        {
            view.WriteArray(offset, source, sourceOffset, count);
            return;
        }
        stream.Position = offset;
        stream.Write(source, sourceOffset, count);
    }

    public void Read(long offset, byte[] destination, int destinationOffset, int count)
    {
        if (view != null)
        {
            view.ReadArray(offset, destination, destinationOffset, count);
            return;
        }
        stream.Position = offset;
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(destination, destinationOffset + read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public void Dispose()
    {
        view?.Dispose();
        map?.Dispose();
        stream?.Dispose();
        view = null;
        map = null;
        stream = null;
    }
}
