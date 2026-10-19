using UnityEngine;
namespace Basis.Tests.Fitting
{
    public struct BasisFitBodySpec
    {
        public string Name;
        public float EyeY, EyeForward, HeadY, NeckY, UpperChestY, ChestY, SpineY, HipsY, HipJointY, HipJointX, KneeY, AnkleY, ToeY, ToeForward;
        public float ShoulderRootY, ShoulderRootX, ShoulderJointY, ShoulderJointX, UpperArmLength, ForearmLength, ArmDropDeg;
        public bool HasUpperChest, HasNeck, ShouldersOnChest;
        public float WristSpan => 2f * ShoulderJointX + 2f * (UpperArmLength + ForearmLength) * Mathf.Cos(ArmDropDeg * Mathf.Deg2Rad);
        public float SegmentSpan => 2f * ShoulderJointX + 2f * (UpperArmLength + ForearmLength);
        public static BasisFitBodySpec Human(string name, float height, float armScale = 1f, float legShift = 0f, float shoulderWidth = 0.20f)
        {
            BasisFitBodySpec s = default;
            s.Name = name;
            s.EyeY = 0.936f * height;
            s.EyeForward = 0.045f * height;
            s.HeadY = 0.87f * height;
            s.NeckY = 0.83f * height;
            s.UpperChestY = 0.76f * height;
            s.ChestY = (0.69f + legShift * 0.3f) * height;
            s.SpineY = (0.62f + legShift * 0.6f) * height;
            s.HipsY = (0.555f + legShift) * height;
            s.HipJointY = (0.52f + legShift) * height;
            s.HipJointX = 0.05f * height;
            s.AnkleY = 0.04f * height;
            s.KneeY = s.AnkleY + (s.HipJointY - s.AnkleY) * 0.505f;
            s.ToeY = 0.012f * height;
            s.ToeForward = 0.08f * height;
            s.ShoulderRootY = 0.815f * height;
            s.ShoulderRootX = 0.015f * height;
            s.ShoulderJointY = 0.815f * height;
            s.ShoulderJointX = shoulderWidth * 0.5f * height;
            s.UpperArmLength = 0.17f * height * armScale;
            s.ForearmLength = 0.143f * height * armScale;
            s.HasUpperChest = true;
            s.HasNeck = true;
            return s;
        }
        public static BasisFitBodySpec Child(string name, float height)
        {
            BasisFitBodySpec s = Human(name, height, 0.95f, -0.02f, 0.21f);
            s.EyeY = 0.915f * height;
            s.EyeForward = 0.05f * height;
            s.HeadY = 0.835f * height;
            s.NeckY = 0.80f * height;
            s.UpperChestY = 0.735f * height;
            s.ShoulderRootY = 0.785f * height;
            s.ShoulderJointY = 0.785f * height;
            return s;
        }
        public static BasisFitBodySpec Anime(string name)
        {
            float height = 1.55f;
            BasisFitBodySpec s = Human(name, height, 0.97f, 0.035f, 0.17f);
            s.EyeY = 0.91f * height;
            s.EyeForward = 0.05f * height;
            s.HeadY = 0.835f * height;
            s.NeckY = 0.80f * height;
            s.UpperChestY = 0.745f * height;
            s.ShoulderRootY = 0.785f * height;
            s.ShoulderJointY = 0.785f * height;
            return s;
        }
        public static BasisFitBodySpec Chibi(string name)
        {
            float height = 1.0f;
            BasisFitBodySpec s = default;
            s.Name = name;
            s.EyeY = 0.70f * height;
            s.EyeForward = 0.12f * height;
            s.HeadY = 0.55f * height;
            s.NeckY = 0.52f * height;
            s.UpperChestY = 0.47f * height;
            s.ChestY = 0.42f * height;
            s.SpineY = 0.36f * height;
            s.HipsY = 0.31f * height;
            s.HipJointY = 0.28f * height;
            s.HipJointX = 0.06f * height;
            s.AnkleY = 0.03f * height;
            s.KneeY = 0.15f * height;
            s.ToeY = 0.01f * height;
            s.ToeForward = 0.07f * height;
            s.ShoulderRootY = 0.50f * height;
            s.ShoulderRootX = 0.02f * height;
            s.ShoulderJointY = 0.50f * height;
            s.ShoulderJointX = 0.11f * height;
            s.UpperArmLength = 0.11f * height;
            s.ForearmLength = 0.10f * height;
            s.HasUpperChest = true;
            s.HasNeck = true;
            return s;
        }
        public BasisFitBodySpec Scaled(float factor, string name)
        {
            BasisFitBodySpec s = this;
            s.Name = name;
            s.EyeY *= factor; s.EyeForward *= factor; s.HeadY *= factor; s.NeckY *= factor; s.UpperChestY *= factor; s.ChestY *= factor; s.SpineY *= factor; s.HipsY *= factor;
            s.HipJointY *= factor; s.HipJointX *= factor; s.KneeY *= factor; s.AnkleY *= factor; s.ToeY *= factor; s.ToeForward *= factor;
            s.ShoulderRootY *= factor; s.ShoulderRootX *= factor; s.ShoulderJointY *= factor; s.ShoulderJointX *= factor; s.UpperArmLength *= factor; s.ForearmLength *= factor;
            return s;
        }
        public BasisFitBodySpec With(string name, System.Func<BasisFitBodySpec, BasisFitBodySpec> edit)
        {
            BasisFitBodySpec s = edit(this);
            s.Name = name;
            return s;
        }
        public override string ToString() => Name;
    }
    public static class BasisFitBodies
    {
        public static BasisFitBodySpec[] Players => new[]
        {
            BasisFitBodySpec.Human("PlayerAverage", 1.75f),
            BasisFitBodySpec.Human("PlayerShort", 1.52f, 1f, 0f, 0.19f),
            BasisFitBodySpec.Human("PlayerTall", 1.95f),
            BasisFitBodySpec.Human("PlayerLongArms", 1.75f, 1.07f),
            BasisFitBodySpec.Human("PlayerShortArms", 1.75f, 0.93f),
            BasisFitBodySpec.Human("PlayerLongLegs", 1.75f, 1f, 0.025f),
            BasisFitBodySpec.Human("PlayerShortLegs", 1.75f, 1f, -0.025f),
            BasisFitBodySpec.Child("PlayerChild", 1.25f),
            BasisFitBodySpec.Human("PlayerLikeReport", 1.65f, 1.06f),
        };
        public static BasisFitBodySpec Realistic => BasisFitBodySpec.Human("AvatarRealistic", 1.75f);
        public static BasisFitBodySpec[] Avatars => new[]
        {
            Realistic,
            BasisFitBodySpec.Human("AvatarRealisticFemale", 1.62f, 1f, 0.005f, 0.18f),
            BasisFitBodySpec.Anime("AvatarAnime"),
            BasisFitBodySpec.Chibi("AvatarChibi"),
            BasisFitBodySpec.Human("AvatarLongArms", 1.75f, 1.35f),
            BasisFitBodySpec.Human("AvatarShortArms", 1.75f, 0.75f),
            Realistic.Scaled(3f, "AvatarGiant"),
            Realistic.Scaled(0.35f, "AvatarTiny"),
            Realistic.With("AvatarAPose", s => { s.ArmDropDeg = 40f; return s; }),
            Realistic.With("AvatarHighHipsBone", s => { s.HipsY = 0.62f * 1.75f; s.SpineY = 0.66f * 1.75f; return s; }),
            Realistic.With("AvatarLongNeck", s => { s.NeckY = 0.80f * 1.75f; s.HeadY = 0.90f * 1.75f; s.EyeY = 0.955f * 1.75f; return s; }),
            Realistic.With("AvatarNoUpperChest", s => { s.HasUpperChest = false; return s; }),
            Realistic.With("AvatarNoNeck", s => { s.HasNeck = false; return s; }),
            Realistic.With("AvatarShouldersOnChest", s => { s.ShouldersOnChest = true; return s; }),
            BasisFitBodySpec.Human("AvatarLikeReport", 1.714f, 0.9f, 0.01f, 0.16f),
        };
    }
}
