using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Tests.GlobalIllumination
{
    public class BasisGlobalIlluminationItemStabilityTests
    {
        private const int Width = 640, Height = 360, Period = 24;
        private BasisGlobalIlluminationRenderHarness harness;
        private readonly List<GameObject> items = new List<GameObject>();
        private AmbientMode ambientMode;
        private Color ambientLight;
        private Light light;

        [SetUp]
        public void SetUp()
        {
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            ambientMode = RenderSettings.ambientMode;
            ambientLight = RenderSettings.ambientLight;
            harness = new BasisGlobalIlluminationRenderHarness();
            harness.Resize(Width, Height);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
        }

        [TearDown]
        public void TearDown()
        {
            harness?.Dispose();
            harness = null;
            items.Clear();
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientLight = ambientLight;
        }

        private void Item(PrimitiveType type, Vector3 position, Vector3 scale, Material material)
        {
            GameObject item = harness.Own(GameObject.CreatePrimitive(type));
            item.transform.position = position;
            item.transform.localScale = scale;
            item.GetComponent<MeshRenderer>().sharedMaterial = material;
            items.Add(item);
        }

        private void BuildRoom()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            Material grey = harness.CreateLitMaterial(new Color(0.75f, 0.75f, 0.75f), Color.black);
            Material red = harness.CreateLitMaterial(new Color(0.8f, 0.1f, 0.08f), Color.black);
            Material white = harness.CreateLitMaterial(new Color(0.85f, 0.85f, 0.85f), Color.black);
            Material blue = harness.CreateLitMaterial(new Color(0.15f, 0.3f, 0.8f), Color.black);
            Material metal = harness.CreateLitMaterial(new Color(0.9f, 0.85f, 0.8f), Color.black);
            metal.SetFloat("_Metallic", 1f);
            metal.SetFloat("_Smoothness", 0.92f);
            Material glossy = harness.CreateLitMaterial(new Color(0.7f, 0.7f, 0.7f), Color.black);
            glossy.SetFloat("_Smoothness", 0.8f);
            harness.AddBox(new Vector3(0f, -0.1f, 1f), new Vector3(6f, 0.2f, 6f), grey);
            harness.AddBox(new Vector3(0f, 3.1f, 1f), new Vector3(6f, 0.2f, 6f), grey);
            harness.AddBox(new Vector3(0f, 1.5f, 4.1f), new Vector3(6f, 3.2f, 0.2f), grey);
            harness.AddBox(new Vector3(0f, 1.5f, -2.1f), new Vector3(6f, 3.2f, 0.2f), grey);
            harness.AddBox(new Vector3(-3.1f, 1.5f, 1f), new Vector3(0.2f, 3.2f, 6f), grey);
            harness.AddBox(new Vector3(3.1f, 1.5f, 1f), new Vector3(0.2f, 3.2f, 6f), red);
            harness.AddBox(new Vector3(-2.98f, 1.3f, 1.4f), new Vector3(0.04f, 0.8f, 1.6f), harness.CreateLitMaterial(Color.black, new Color(6f, 3.2f, 1.2f)));
            GameObject lightHost = harness.Own(new GameObject("BasisGIItemLight"));
            lightHost.transform.position = new Vector3(0.9f, 2.6f, 0.2f);
            light = lightHost.AddComponent<Light>();
            light.type = LightType.Point;
            light.intensity = 6f;
            light.range = 9f;
            light.shadows = LightShadows.Soft;
            Item(PrimitiveType.Sphere, new Vector3(-1.1f, 0.04f, 0.4f), Vector3.one * 0.08f, white);
            Item(PrimitiveType.Sphere, new Vector3(-0.85f, 0.08f, 0.5f), Vector3.one * 0.16f, blue);
            Item(PrimitiveType.Sphere, new Vector3(-0.5f, 0.15f, 0.6f), Vector3.one * 0.3f, glossy);
            Item(PrimitiveType.Sphere, new Vector3(1.0f, 0.18f, 0.5f), Vector3.one * 0.36f, metal);
            Item(PrimitiveType.Cylinder, new Vector3(0.35f, 0.5f, 0.3f), new Vector3(0.03f, 0.5f, 0.03f), white);
            Item(PrimitiveType.Cylinder, new Vector3(0.5f, 0.5f, 0.45f), new Vector3(0.05f, 0.5f, 0.05f), blue);
            for (int bar = 0; bar < 6; bar++) { Item(PrimitiveType.Cube, new Vector3(-0.3f + bar * 0.07f, 0.35f, 1.1f), new Vector3(0.02f, 0.7f, 0.02f), white); }
            Item(PrimitiveType.Cube, new Vector3(0.1f, 0.7f, 1.1f), new Vector3(0.8f, 0.03f, 0.03f), white);
            Item(PrimitiveType.Cube, new Vector3(1.6f, 0.45f, 1.3f), new Vector3(0.9f, 0.04f, 0.6f), white);
            foreach (Vector2 leg in new[] { new Vector2(1.2f, 1.05f), new Vector2(2.0f, 1.05f), new Vector2(1.2f, 1.55f), new Vector2(2.0f, 1.55f) }) { Item(PrimitiveType.Cube, new Vector3(leg.x, 0.215f, leg.y), new Vector3(0.04f, 0.43f, 0.04f), white); }
            System.Random random = new System.Random(7);
            for (int index = 0; index < 10; index++) { Item(PrimitiveType.Cube, new Vector3(1.25f + (float)random.NextDouble() * 0.7f, 0.5f, 1.05f + (float)random.NextDouble() * 0.45f), Vector3.one * 0.06f, index % 2 == 0 ? red : blue); }
            harness.Camera.transform.position = new Vector3(0.1f, 1.35f, -1.6f);
            harness.Camera.transform.rotation = Quaternion.LookRotation(new Vector3(0.2f, 0.45f, 1.0f) - harness.Camera.transform.position, Vector3.up);
        }

        private static float Luma(Color colour) { return colour.r * 0.2126f + colour.g * 0.7152f + colour.b * 0.0722f; }

        private float Level()
        {
            Color[] colours = harness.CaptureColours();
            double total = 0.0;
            int count = 0;
            for (int y = Height / 4; y < Height * 3 / 4; y += 2)
            {
                for (int x = Width / 6; x < Width * 5 / 6; x += 2) { total += Luma(colours[y * Width + x]); count++; }
            }
            return (float)(total / count);
        }

        private bool[] ItemMask()
        {
            harness.Settings.enable = false;
            harness.Render();
            Color[] with = harness.CaptureColours();
            foreach (GameObject item in items) { item.SetActive(false); }
            harness.Render();
            Color[] without = harness.CaptureColours();
            foreach (GameObject item in items) { item.SetActive(true); }
            harness.Settings.enable = true;
            bool[] mask = new bool[with.Length];
            for (int index = 0; index < mask.Length; index++) { mask[index] = Mathf.Abs(Luma(with[index]) - Luma(without[index])) > 1e-3f; }
            return mask;
        }

        private void Sway(Vector3 position, Quaternion rotation, int frame)
        {
            float phase = frame * 6.2831853f / Period;
            harness.Camera.transform.SetPositionAndRotation(position + new Vector3(Mathf.Sin(phase) * 0.02f, Mathf.Sin(phase * 2f) * 0.005f, 0f), rotation * Quaternion.Euler(Mathf.Sin(phase * 2f) * 0.25f, Mathf.Cos(phase) * 0.6f, 0f));
        }

        [Test]
        public void SmallItemsDoNotSparkleWhileTheHeadSways()
        {
            BuildRoom();
            harness.Settings.quality = BasisGlobalIlluminationQuality.Medium;
            bool[] mask = ItemMask();
            Vector3 position = harness.Camera.transform.position;
            Quaternion rotation = harness.Camera.transform.rotation;
            const int warmup = 72, frames = 72;
            float[][] luminance = new float[frames][];
            for (int frame = 0; frame < warmup + frames; frame++)
            {
                Sway(position, rotation, frame);
                harness.Render();
                if (frame < warmup) { continue; }
                Color[] colours = harness.CaptureColours();
                float[] row = new float[colours.Length];
                for (int index = 0; index < colours.Length; index++) { row[index] = Luma(colours[index]); }
                luminance[frame - warmup] = row;
            }
            harness.Camera.transform.SetPositionAndRotation(position, rotation);
            double difference = 0.0, level = 0.0, sparkles = 0.0, samples = 0.0;
            for (int index = 0; index < mask.Length; index++)
            {
                if (!mask[index]) { continue; }
                for (int frame = Period; frame < frames; frame++)
                {
                    float now = luminance[frame][index], before = luminance[frame - Period][index], mean = 0.5f * (now + before);
                    if (mean <= 1e-4f) { continue; }
                    difference += Mathf.Abs(now - before);
                    level += mean;
                    if (Mathf.Abs(now - before) > 0.2f * mean + 0.004f) { sparkles += 1.0; }
                    samples += 1.0;
                }
            }
            float flicker = (float)(difference / System.Math.Max(level, 1e-9)), sparkle = (float)(sparkles / System.Math.Max(samples, 1.0));
            Debug.Log($"[BasisGI] items under sway: flicker {flicker:P2} sparkle {sparkle:P3} over {samples} samples");
            Assert.Greater(samples, 10000.0, "the items barely cover the frame, so this test proves nothing");
            Assert.Less(sparkle, 0.0005f, $"item pixels jumped by more than a fifth between two frames at the same pose: {sparkle:P3}");
            Assert.Less(flicker, 0.01f, $"item pixels moved too far between two frames at the same pose: {flicker:P2}");
        }

        [Test]
        public void TheBounceFollowsALightSwitchWithinAFewFrames()
        {
            BuildRoom();
            harness.Settings.quality = BasisGlobalIlluminationQuality.Medium;
            harness.SetDebugView(BasisGlobalIlluminationDebugView.IndirectOnly);
            foreach (bool on in new[] { true, false })
            {
                light.intensity = on ? 0f : 6f;
                for (int frame = 0; frame < 90; frame++) { harness.Render(); }
                float start = Level();
                light.intensity = on ? 6f : 0f;
                const int frames = 90;
                float[] levels = new float[frames];
                for (int frame = 0; frame < frames; frame++)
                {
                    harness.Render();
                    levels[frame] = Level();
                }
                float settled = 0f;
                for (int frame = frames - 16; frame < frames; frame++) { settled += levels[frame]; }
                settled /= 16f;
                int half = frames;
                for (int frame = frames - 1; frame >= 0; frame--) { if ((levels[frame] - start) / (settled - start) < 0.5f) { half = frame + 1; break; } }
                Debug.Log($"[BasisGI] light {(on ? "on" : "off")}: bounce {start:F4} -> {settled:F4}, halfway after {half} frames");
                Assert.Greater(Mathf.Abs(settled - start), 0.004f, "the light barely changes the bounce, so this test proves nothing");
                Assert.LessOrEqual(half, on ? 10 : 12, $"the bounce took {half} frames to cover half the change after the light switched {(on ? "on" : "off")}");
            }
        }
    }
}
