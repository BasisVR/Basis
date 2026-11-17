using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace Basis.Capture
{
    public static class BasisCapturePack
    {
        public const int Magic = 0x4B504342;
        public static void Pack(string folder, string packPath)
        {
            List<string> files = new List<string>();
            foreach (string f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) files.Add(f);
            using FileStream fs = new FileStream(packPath, FileMode.Create, FileAccess.Write);
            using BinaryWriter w = new BinaryWriter(fs, Encoding.UTF8);
            w.Write(Magic);
            w.Write(files.Count);
            foreach (string f in files) { w.Write(Path.GetRelativePath(folder, f).Replace('\\', '/')); w.Write(new FileInfo(f).Length); }
            byte[] copy = new byte[1 << 20];
            foreach (string f in files) using (FileStream src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { int n; while ((n = src.Read(copy, 0, copy.Length)) > 0) w.Write(copy, 0, n); }
        }
        public static void Unpack(string packPath, string folder)
        {
            using FileStream fs = new FileStream(packPath, FileMode.Open, FileAccess.Read);
            using BinaryReader r = new BinaryReader(fs, Encoding.UTF8);
            if (r.ReadInt32() != Magic) throw new InvalidDataException("not a capture pack");
            int count = r.ReadInt32();
            if (count < 0 || count > 10000) throw new InvalidDataException("bad file count");
            string root = Path.GetFullPath(folder);
            List<(string path, long length)> entries = new List<(string, long)>(count);
            for (int i = 0; i < count; i++)
            {
                string rel = r.ReadString();
                long length = r.ReadInt64();
                string full = Path.GetFullPath(Path.Combine(root, rel));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || length < 0) throw new InvalidDataException("bad entry " + rel);
                entries.Add((full, length));
            }
            byte[] copy = new byte[1 << 20];
            foreach ((string path, long length) in entries)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using FileStream dst = new FileStream(path, FileMode.Create, FileAccess.Write);
                long left = length;
                while (left > 0) { int n = r.Read(copy, 0, (int)Math.Min(copy.Length, left)); if (n <= 0) throw new EndOfStreamException(); dst.Write(copy, 0, n); left -= n; }
            }
        }
    }
}
