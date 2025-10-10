using System.Collections.Generic;

namespace HVR.Osushi
{
    internal class OsushiNode
    {
        public string DESCRIPTION;
        public string FULL_PATH;
        public int ACCESS;
        public string TYPE;
        public List<object> VALUE;
        public Dictionary<string, OsushiNode> CONTENTS;
    }

    internal static class OsushiUtil
    {
        private static string[] paths = {
            "BrowDownLeft", "BrowDownRight", "BrowInnerUp", "BrowInnerUpLeft", "BrowInnerUpRight", "BrowLowererLeft",
            "BrowLowererRight", "BrowOuterUpLeft", "BrowOuterUpRight", "BrowPinchLeft", "BrowPinchRight",
            "CheekPuffSuck", "CheekPuffSuckLeft", "CheekPuffSuckRight", "CheekSquintLeft", "CheekSquintRight",
            "EyeLeftX", "EyeLidLeft", "EyeLidRight", "EyeRightX", "EyeSquintLeft", "EyeSquintRight", "EyeY",
            "JawClench", "JawMandibleRaise", "JawOpen", "JawX", "JawZ", "LipFunnel", "LipFunnelLowerLeft",
            "LipFunnelLowerRight", "LipFunnelUpperLeft", "LipFunnelUpperRight", "LipPucker", "LipPuckerLowerLeft",
            "LipPuckerLowerRight", "LipPuckerUpperLeft", "LipPuckerUpperRight", "LipSuckCornerLeft",
            "LipSuckCornerRight", "LipSuckLower", "LipSuckLowerLeft", "LipSuckLowerRight", "LipSuckUpper",
            "LipSuckUpperLeft", "LipSuckUpperRight", "MouthClosed", "MouthCornerPullLeft", "MouthCornerPullRight",
            "MouthCornerSlantLeft", "MouthCornerSlantRight", "MouthDimpleLeft", "MouthDimpleRight", "MouthFrownLeft",
            "MouthFrownRight", "MouthLowerDownLeft", "MouthLowerDownRight", "MouthLowerX", "MouthPressLeft",
            "MouthPressRight", "MouthRaiserLower", "MouthRaiserUpper", "MouthSmileLeft", "MouthSmileRight",
            "MouthStretchLeft", "MouthStretchRight", "MouthTightenerLeft", "MouthTightenerRight",
            "MouthUpperDeepenLeft", "MouthUpperDeepenRight", "MouthUpperUpLeft", "MouthUpperUpRight", "MouthUpperX",
            "NasalConstrictLeft", "NasalConstrictRight", "NasalDilationLeft", "NasalDilationRight", "NeckFlexLeft",
            "NeckFlexRight", "NoseSneerLeft", "NoseSneerRight", "SoftPalateClose", "ThroatSwallow", "TongueArchY",
            "TongueOut", "TongueRoll", "TongueShape", "TongueTwistLeft", "TongueTwistRight", "TongueX", "TongueY",
        };

        internal static OsushiNode CreateFaceTrackingNodes()
        {
            return new OsushiNode
            {
                DESCRIPTION = "root node",
                FULL_PATH = "/",
                ACCESS = 0,
                CONTENTS = new Dictionary<string, OsushiNode>
                {
                    ["avatar"] = new()
                    {
                        FULL_PATH = "/avatar",
                        ACCESS = 0,
                        CONTENTS = new Dictionary<string, OsushiNode>
                        {
                            ["change"] = new()
                            {
                                DESCRIPTION = "",
                                FULL_PATH = "/avatar/change",
                                ACCESS = 3,
                                TYPE = "s",
                                VALUE = new List<object>
                                {
                                    "avtr_00000000-89b1-4313-aa2d-000000000000"
                                }
                            },
                            ["parameters"] = new()
                            {
                                FULL_PATH = "/avatar/parameters",
                                ACCESS = 0,
                                CONTENTS = new Dictionary<string, OsushiNode>
                                {
                                    ["FT"] = new()
                                    {
                                        FULL_PATH = "/avatar/parameters/FT",
                                        ACCESS = 0,
                                        CONTENTS = new Dictionary<string, OsushiNode>
                                        {
                                            ["v2"] = new()
                                            {
                                                FULL_PATH = "/avatar/parameters/FT/v2",
                                                ACCESS = 0,
                                                CONTENTS = MakeParams()
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }

        private static Dictionary<string, OsushiNode> MakeParams()
        {
            var dict = new Dictionary<string, OsushiNode>();
            foreach (var path in paths)
            {
                P(path, dict);
            }

            return dict;
        }

        private static void P(string path, Dictionary<string, OsushiNode> results)
        {
            results[path] = new OsushiNode
            {
                FULL_PATH = $"/avatar/parameters/FT/v2/{path}",
                ACCESS = 3,
                TYPE = "f",
                VALUE = new List<object> { 0.0 }
            };
        }
    }
}
