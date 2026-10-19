using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;
using UnityCamera = UnityEngine.Camera;

namespace Basis.Tests.Camera
{
    public class BasisCameraPassesRenderTests
    {
        private const int Width = 160, Height = 120;
        private static readonly Vector3 SubjectCentre = new Vector3(0.6f, 0f, 3f);
        private static readonly Vector3 WallCentre = new Vector3(0f, 0f, 6f);
        private readonly List<Object> _owned = new List<Object>();
        private UnityCamera _camera;
        private RenderTexture _target;
        private RenderTexture[] _targets;
        private Material _material, _maskMaterial;
        private BasisCameraPassesPass _pass;
        private BasisCameraPassSet _wanted, _ready;
        private int _subjectLayer;

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("This run has no graphics device.");
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)) Assert.Ignore("The active render pipeline is not URP.");
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) Assert.Ignore("The URP lit shader is not available.");
            _subjectLayer = LayerMask.NameToLayer("RemotePlayerAvatar");
            if (_subjectLayer < 0) Assert.Ignore("The project has no RemotePlayerAvatar layer.");

            Shader passes = Resources.Load<Shader>(BasisCameraPasses.ShaderResource);
            Shader mask = Resources.Load<Shader>(BasisCameraPasses.MaskShaderResource);
            Assert.That(passes, Is.Not.Null, "The render pass shader did not load from Resources.");
            Assert.That(mask, Is.Not.Null, "The mask shader did not load from Resources.");
            _material = Own(new Material(passes));
            _maskMaterial = Own(new Material(mask));

            _target = Own(new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "BasisPassesTestTarget" });
            _target.Create();
            _targets = new RenderTexture[BasisCameraPasses.Count];
            for (int Index = 0; Index < _targets.Length; Index++)
            {
                _targets[Index] = Own(new RenderTexture(Width, Height, 0, BasisCameraPasses.TargetFormat(true), RenderTextureReadWrite.Linear) { name = "BasisPassesTest" + BasisCameraPasses.Suffixes[Index] });
                _targets[Index].Create();
            }

            GameObject host = Own(new GameObject("BasisPassesTestCamera"));
            _camera = host.AddComponent<UnityCamera>();
            _camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 50f;
            _camera.fieldOfView = 60f;
            _camera.allowMSAA = false;
            _camera.targetTexture = _target;
            UniversalAdditionalCameraData data = _camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;

            Material surface = Own(new Material(lit));
            AddBox("BasisPassesTestWall", WallCentre, new Vector3(20f, 20f, 0.2f), surface, 0);
            AddBox("BasisPassesTestSubject", SubjectCentre, Vector3.one, surface, _subjectLayer);
            AddBox("BasisPassesTestOccluder", new Vector3(0.21f, 0f, 1.5f), new Vector3(0.3f, 3f, 0.1f), surface, 0);

            _pass = new BasisCameraPassesPass();
            RenderPipelineManager.beginCameraRendering += Enqueue;
        }

        [TearDown]
        public void TearDown()
        {
            RenderPipelineManager.beginCameraRendering -= Enqueue;
            if (_camera != null) _camera.targetTexture = null;
            _pass?.Release();
            _pass = null;
            foreach (Object owned in _owned)
            {
                if (owned is RenderTexture texture) texture.Release();
                if (owned != null) Object.DestroyImmediate(owned);
            }
            _owned.Clear();
        }

        private T Own<T>(T owned) where T : Object
        {
            _owned.Add(owned);
            return owned;
        }

        private void AddBox(string name, Vector3 position, Vector3 scale, Material material, int layer)
        {
            GameObject box = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
            box.name = name;
            box.layer = layer;
            box.transform.position = position;
            box.transform.localScale = scale;
            box.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private void Enqueue(ScriptableRenderContext context, UnityCamera rendering)
        {
            if (!ReferenceEquals(rendering, _camera)) return;
            _ready = _pass.Setup(_material, _maskMaterial, _target, _targets, _wanted, 1 << _subjectLayer, 10f, true, _camera.transform.rotation);
            if (_ready != BasisCameraPassSet.None) _camera.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(_pass);
        }

        private void Render(BasisCameraPassSet wanted)
        {
            _wanted = wanted;
            _camera.Render();
            Assert.That(_ready, Is.EqualTo(wanted), "Every requested pass should have had a target and a material.");
        }

        private Color Read(BasisCameraPass pass, Vector3 worldPoint)
        {
            Vector3 screen = _camera.WorldToScreenPoint(worldPoint);
            int x = Mathf.Clamp(Mathf.RoundToInt(screen.x), 0, Width - 1), y = Mathf.Clamp(Mathf.RoundToInt(screen.y), 0, Height - 1);
            Texture2D readback = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = _targets[(int)pass];
                readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                readback.Apply(false);
                RenderTexture.active = previous;
                return readback.GetPixel(x, y);
            }
            finally
            {
                Object.DestroyImmediate(readback);
            }
        }

        private static Vector3 FrontOf(Vector3 centre, float halfDepth) => new Vector3(centre.x, centre.y, centre.z - halfDepth);

        [Test]
        public void TheDepthPassHoldsTheDistanceInMetres()
        {
            Render(BasisCameraPassSet.Depth);
            Color subject = Read(BasisCameraPass.Depth, FrontOf(SubjectCentre, 0.5f) + new Vector3(0.2f, 0f, 0f));
            Color wall = Read(BasisCameraPass.Depth, FrontOf(WallCentre, 0.1f) + new Vector3(2f, 2f, 0f));
            Assert.That(subject.r, Is.EqualTo(2.5f).Within(0.05f), "The front face of the subject is 2.5 m from the lens.");
            Assert.That(wall.r, Is.EqualTo(5.9f).Within(0.08f), "The wall is 5.9 m from the lens.");
        }

        [Test]
        public void TheNormalsPassIsSeenFromTheCamera()
        {
            Render(BasisCameraPassSet.Normals);
            Color facing = Read(BasisCameraPass.Normals, FrontOf(SubjectCentre, 0.5f) + new Vector3(0.2f, 0f, 0f));
            Assert.That(facing.r, Is.EqualTo(0f).Within(0.05f), $"A face pointing at the lens has no sideways part, got {facing}.");
            Assert.That(facing.g, Is.EqualTo(0f).Within(0.05f), $"A face pointing at the lens has no upward part, got {facing}.");
            Assert.That(facing.b, Is.EqualTo(1f).Within(0.05f), $"A face pointing at the lens points along +Z in camera space, got {facing}.");
        }

        [Test]
        public void TheMaskCoversThePlayerAndNotTheWorldInFrontOrBehind()
        {
            Render(BasisCameraPassSet.Mask);
            Color subject = Read(BasisCameraPass.Mask, FrontOf(SubjectCentre, 0.5f) + new Vector3(0.25f, 0.2f, 0f));
            Color wall = Read(BasisCameraPass.Mask, FrontOf(WallCentre, 0.1f) + new Vector3(2.5f, 2f, 0f));
            Color hidden = Read(BasisCameraPass.Mask, FrontOf(SubjectCentre, 0.5f) + new Vector3(-0.25f, 0.2f, 0f));
            Assert.That(subject.r, Is.EqualTo(1f).Within(0.02f), "The player layer is white in the mask.");
            Assert.That(wall.r, Is.EqualTo(0f).Within(0.02f), "The world behind the player stays black.");
            Assert.That(hidden.r, Is.EqualTo(0f).Within(0.02f), "Where the world stands in front of the player the mask is cut away.");
        }

        [Test]
        public void AllPassesRenderTogetherInOneFrame()
        {
            Render(BasisCameraPasses.QuadSet | BasisCameraPassSet.Motion);
            Assert.That(Read(BasisCameraPass.Depth, FrontOf(SubjectCentre, 0.5f) + new Vector3(0.2f, 0f, 0f)).r, Is.EqualTo(2.5f).Within(0.05f));
            Assert.That(Read(BasisCameraPass.Normals, FrontOf(SubjectCentre, 0.5f) + new Vector3(0.2f, 0f, 0f)).b, Is.EqualTo(1f).Within(0.05f));
            Assert.That(Read(BasisCameraPass.Mask, FrontOf(SubjectCentre, 0.5f) + new Vector3(0.25f, 0.2f, 0f)).r, Is.EqualTo(1f).Within(0.02f));
        }
    }
}
