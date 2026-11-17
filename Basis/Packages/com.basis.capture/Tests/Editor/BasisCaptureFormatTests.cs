using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
namespace Basis.Capture.Tests
{
    public class BasisCaptureFormatTests
    {
        private string dir;
        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "basis_capture_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }
        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(dir, true); } catch { }
        }
        private static int CountBits(byte[] data, int at, int count)
        {
            int n = 0;
            for (int i = 0; i < count; i++) if ((data[at + (i >> 3)] & (1 << (i & 7))) != 0) n++;
            return n;
        }
        [Test]
        public void WritePoseOnlyWritesChangedBonesAndShapes()
        {
            float[] current = new float[3 * BasisCaptureFormat.BoneFloats], previous = new float[current.Length], shapes = { 10f, 20f }, previousShapes = new float[2];
            for (int i = 0; i < current.Length; i++) current[i] = i;
            bool have = false;
            BasisCaptureBuffer buffer = new BasisCaptureBuffer();
            BasisCaptureActor.WritePose(buffer, 4, true, Matrix4x4.identity, current, previous, shapes, previousShapes, new[] { true, false }, ref have);
            int maskAt = 9 + 1 + 64 + 4;
            Assert.AreEqual(3, CountBits(buffer.Data, maskAt, 3));
            Assert.IsTrue(have);
            buffer = new BasisCaptureBuffer();
            current[BasisCaptureFormat.BoneFloats + 2] += 1f;
            BasisCaptureActor.WritePose(buffer, 4, true, Matrix4x4.identity, current, previous, shapes, previousShapes, new[] { true, false }, ref have);
            Assert.AreEqual(1, CountBits(buffer.Data, maskAt, 3));
            int shapeCountAt = maskAt + 1 + BasisCaptureFormat.BoneFloats * 4;
            Assert.AreEqual(2, BinaryPrimitives.ReadInt32LittleEndian(buffer.Data.AsSpan(shapeCountAt)));
            Assert.AreEqual(0, CountBits(buffer.Data, shapeCountAt + 4, 2));
            Assert.AreEqual(4, BinaryPrimitives.ReadInt32LittleEndian(buffer.Data.AsSpan(1)));
            Assert.AreEqual(buffer.Length - 9, BinaryPrimitives.ReadInt32LittleEndian(buffer.Data.AsSpan(5)));
        }
        [Test]
        public void FrameWriterChunksInflateBackToFrames()
        {
            string path = Path.Combine(dir, BasisCaptureFormat.FramesFile);
            using (BasisCaptureFrameWriter writer = new BasisCaptureFrameWriter(path, 3))
            {
                for (int f = 0; f < 10; f++)
                {
                    byte[] data = writer.Rent(8);
                    BinaryPrimitives.WriteInt32LittleEndian(data, f);
                    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), f * 7);
                    writer.Submit(data, 8);
                }
            }
            using BinaryReader r = new BinaryReader(File.OpenRead(path));
            Assert.AreEqual(BasisCaptureFormat.FramesMagic, r.ReadUInt32());
            Assert.AreEqual(BasisCaptureFormat.Version, r.ReadInt32());
            List<int> frames = new List<int>();
            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                int first = r.ReadInt32(), count = r.ReadInt32(), raw = r.ReadInt32(), comp = r.ReadInt32();
                Assert.AreEqual(frames.Count, first);
                using DeflateStream inflate = new DeflateStream(new MemoryStream(r.ReadBytes(comp)), CompressionMode.Decompress);
                BinaryReader body = new BinaryReader(inflate);
                for (int i = 0; i < count; i++) { int f = body.ReadInt32(); Assert.AreEqual(f * 7, body.ReadInt32()); frames.Add(f); }
                Assert.AreEqual(count * 8, raw);
            }
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, frames);
        }
        [Test]
        public void CompactKeepsOnlyTheRendererSubmeshes()
        {
            BasisCaptureMeshData m = new BasisCaptureMeshData { vertexCount = 6, positions = new Vector3[6] };
            for (int i = 0; i < 6; i++) m.positions[i] = new Vector3(i, 0, 0);
            m.submeshes.Add((MeshTopology.Triangles, new[] { 0, 1, 2 }));
            m.submeshes.Add((MeshTopology.Triangles, new[] { 5, 3, 4 }));
            m.shapes.Add(new BasisCaptureMeshData.Shape { name = "s", weights = new[] { 100f }, frames = new[] { new List<(int, Vector3, Vector3, Vector3)> { (1, Vector3.up, Vector3.zero, Vector3.zero), (4, Vector3.right, Vector3.zero, Vector3.zero) } } });
            m.Compact(1, 1);
            Assert.AreEqual(3, m.vertexCount);
            Assert.AreEqual(1, m.submeshes.Count);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, m.submeshes[0].indices);
            Assert.AreEqual(5f, m.positions[0].x);
            Assert.AreEqual(1, m.shapes[0].frames[0].Count);
            Assert.AreEqual(2, m.shapes[0].frames[0][0].vertex);
        }
        [Test]
        public void JsonEscapesAndUsesInvariantNumbers()
        {
            BasisCaptureJson j = new BasisCaptureJson();
            j.BeginObject().Prop("name", "a\"b\\c\n").Prop("f", 1.5f).Prop("n", 3).Name("list").Ints(new[] { 1, 2 }).BeginObject("o").EndObject().EndObject();
            Assert.AreEqual("{\"name\":\"a\\\"b\\\\c\\n\",\"f\":1.5,\"n\":3,\"list\":[1,2],\"o\":{}}", j.ToString());
        }
        [Test]
        public void PackRoundTripsAndRejectsEscapingEntries()
        {
            string src = Path.Combine(dir, "take");
            Directory.CreateDirectory(Path.Combine(src, "audio"));
            File.WriteAllBytes(Path.Combine(src, "frames.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(src, "audio", "x.wav"), "voice");
            string pack = Path.Combine(dir, "take.pack"), dst = Path.Combine(dir, "out");
            BasisCapturePack.Pack(src, pack);
            BasisCapturePack.Unpack(pack, dst);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(dst, "frames.bin")));
            Assert.AreEqual("voice", File.ReadAllText(Path.Combine(dst, "audio", "x.wav")));
            string evil = Path.Combine(dir, "evil.pack");
            using (BinaryWriter w = new BinaryWriter(File.Create(evil))) { w.Write(BasisCapturePack.Magic); w.Write(1); w.Write("../escaped.txt"); w.Write(1L); w.Write((byte)0); }
            Assert.Throws<InvalidDataException>(() => BasisCapturePack.Unpack(evil, Path.Combine(dir, "evil")));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "escaped.txt")));
        }
    }
}
