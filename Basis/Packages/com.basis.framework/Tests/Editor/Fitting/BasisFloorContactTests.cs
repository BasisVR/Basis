using Basis.Scripts.BasisCharacterController;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.Fitting
{
    public class BasisFloorContactTests
    {
        static float SettleRootHeight(float centerY, float height, float radius, float skin)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var body = new GameObject("FloorContactBody");
            try
            {
                floor.transform.position = new Vector3(0f, -0.5f, 0f);
                floor.transform.localScale = new Vector3(20f, 1f, 20f);
                body.transform.position = new Vector3(0f, 0.3f, 0f);
                var controller = body.AddComponent<CharacterController>();
                controller.height = height;
                controller.radius = radius;
                controller.skinWidth = skin;
                controller.minMoveDistance = 0f;
                controller.center = new Vector3(0f, centerY, 0f);
                Physics.SyncTransforms();
                for (int i = 0; i < 400; i++)
                {
                    controller.Move(Vector3.down * 0.05f);
                }
                return body.transform.position.y;
            }
            finally
            {
                Object.DestroyImmediate(body);
                Object.DestroyImmediate(floor);
            }
        }
        [Test]
        public void TheCharacterRootRestsOnTheFloor([Values(0.01f, 0.02f)] float skin)
        {
            float height = 1.6f;
            float rest = SettleRootHeight(BasisLocalCharacterDriver.ControllerCenterY(height * 0.5f, skin), height, 0.15f, skin);
            Assert.That(rest, Is.EqualTo(0f).Within(0.002f), "the root (and the avatar's soles) must sit on the floor, not skinWidth above it");
            float old = SettleRootHeight(height * 0.5f - skin, height, 0.15f, skin);
            Assert.That(old, Is.GreaterThan(rest + skin * 1.5f), "the old compensation doubled the hover");
        }
        [Test]
        public void PlantedAnklesSitExactlyAtTheirRestHeight()
        {
            float ankleRest = 0.07f, toeJoint = 0.015f, leg = 0.85f, hipJoint = leg + ankleRest;
            BasisLocalFootDriver.DeriveFootHeights(ankleRest - toeJoint, ankleRest, hipJoint - ankleRest, leg, 0.2f, out float planted, out float hang);
            Assert.That(planted, Is.EqualTo(ankleRest), "a planted foot rests its sole on the floor, never in it");
            Assert.That(hang, Is.LessThan(planted));
        }
    }
}
