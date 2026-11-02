using System.Collections.Generic;
using System.Reflection;
using Basis.Cinematics;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    public class BasisCameraDollyTrackRedrawTests
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly FieldInfo PointsF = typeof(BasisCameraDollyTrack).GetField("points", NP);
        static readonly FieldInfo BufferF = typeof(BasisCameraDollyTrack).GetField("lineBuffer", NP);
        static readonly FieldInfo ColorsF = typeof(BasisCameraDollyTrack).GetField("lineColors", NP);
        static readonly FieldInfo LineIdF = typeof(BasisCameraDollyTrack).GetField("lineId", NP);
        static readonly MethodInfo UpdateLineM = typeof(BasisCameraDollyTrack).GetMethod("UpdateLine", NP);
        static readonly MethodInfo HideLineM = typeof(BasisCameraDollyTrack).GetMethod("HideLine", NP);

        BasisCameraDollyTrack _track;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(PointsF); Assert.IsNotNull(BufferF); Assert.IsNotNull(ColorsF); Assert.IsNotNull(UpdateLineM); Assert.IsNotNull(HideLineM);
            _track = new BasisCameraDollyTrack();
            var points = (List<Vector3>)PointsF.GetValue(_track);
            points.Add(new Vector3(0f, 1f, 0f));
            points.Add(new Vector3(2f, 1f, 1f));
            points.Add(new Vector3(4f, 1.5f, -1f));
        }

        [TearDown]
        public void TearDown()
        {
            int id = (int)LineIdF.GetValue(_track);
            if (id >= 0) BasisGizmoManager.DestroyGizmo(id);
        }

        void Draw(float scale = 1f) => UpdateLineM.Invoke(_track, new object[] { scale });
        Vector3[] Buffer => (Vector3[])BufferF.GetValue(_track);
        Color32[] Colors => (Color32[])ColorsF.GetValue(_track);

        [Test]
        public void AnUnchangedPathIsNotResampled_AMovedPointIs()
        {
            Draw();
            Vector3 sentinel = new Vector3(99f, 99f, 99f);
            Buffer[0] = sentinel;
            Draw();
            Assert.AreEqual(sentinel, Buffer[0], "nothing moved, so the path was not sampled again");

            var points = (List<Vector3>)PointsF.GetValue(_track);
            points[2] = new Vector3(5f, 2f, -2f);
            Draw();
            Assert.AreNotEqual(sentinel, Buffer[0], "a moved point resamples the path");
            Assert.LessOrEqual(Vector3.Distance(Buffer[Buffer.Length - 1], points[2]), 1e-4f, "the path ends on the moved point");
        }

        [Test]
        public void LoopingAndHidingRedrawThePath()
        {
            Draw();
            int open = Buffer.Length;
            _track.Looped = true;
            Draw();
            Assert.Greater(Buffer.Length, open, "a looped path closes back to the first point");

            Buffer[0] = new Vector3(99f, 99f, 99f);
            HideLineM.Invoke(_track, null);
            Draw();
            Assert.AreNotEqual(new Vector3(99f, 99f, 99f), Buffer[0], "a hidden line is redrawn when it comes back");
        }

        [Test]
        public void SpeedColoursFollowTheMoveButNotThePlayhead()
        {
            _track.ColorBySpeed = true;
            _track.MotionActive = true;
            BasisCameraDollySettings move = BasisCameraDollySettings.Default;
            move.mode = BasisCameraDollyMode.Play;
            move.speed = 0.5f;
            _track.Motion = move;
            Draw();
            Assert.IsTrue(_track.PaintsBySpeed);
            Color32 sentinel = new Color32(1, 2, 3, 4);

            Colors[Colors.Length / 2] = sentinel;
            move.position = 0.7f;
            _track.Motion = move;
            Draw();
            Assert.AreEqual(sentinel, Colors[Colors.Length / 2], "the playhead does not change the colouring");

            move.speed = 3f;
            _track.Motion = move;
            Draw();
            Assert.AreNotEqual(sentinel, Colors[Colors.Length / 2], "a speed change repaints the line");

            Colors[Colors.Length / 2] = sentinel;
            Draw(2f);
            Assert.AreNotEqual(sentinel, Colors[Colors.Length / 2], "a scale change repaints the line");
        }
    }
}
