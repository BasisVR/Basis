// Blob layout, unified shape order and FT/v2 formulas ported from VRCFaceTracking
// (https://github.com/benaclejames/VRCFaceTracking), Apache License 2.0, Copyright benaclejames and contributors.
using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using HVR.Basis.Comms.OSC.Lyuma;
using UnityEngine;

namespace HVR.Basis.Comms.OSC
{
    [NoAutoStaticsCleanup]
    internal sealed class BasisVrcftNativeFaceDecoder
    {
        public const string Address = "/tracking/face/v1";
        private const string ParameterPrefix = "/avatar/parameters/FT/v2/";
        private const int EyeByteCount = 14;
        private const float ByteToUnit = 1f / 255f;

        private static readonly string[] ShapeNames =
        {
            "EyeSquintRight", "EyeSquintLeft", "EyeWideRight", "EyeWideLeft",
            "BrowPinchRight", "BrowPinchLeft", "BrowLowererRight", "BrowLowererLeft", "BrowInnerUpRight", "BrowInnerUpLeft", "BrowOuterUpRight", "BrowOuterUpLeft",
            "NasalDilationRight", "NasalDilationLeft", "NasalConstrictRight", "NasalConstrictLeft",
            "CheekSquintRight", "CheekSquintLeft", "CheekPuffRight", "CheekPuffLeft", "CheekSuckRight", "CheekSuckLeft",
            "JawOpen", "JawRight", "JawLeft", "JawForward", "JawBackward", "JawClench", "JawMandibleRaise", "MouthClosed",
            "LipSuckUpperRight", "LipSuckUpperLeft", "LipSuckLowerRight", "LipSuckLowerLeft", "LipSuckCornerRight", "LipSuckCornerLeft",
            "LipFunnelUpperRight", "LipFunnelUpperLeft", "LipFunnelLowerRight", "LipFunnelLowerLeft",
            "LipPuckerUpperRight", "LipPuckerUpperLeft", "LipPuckerLowerRight", "LipPuckerLowerLeft",
            "MouthUpperUpRight", "MouthUpperUpLeft", "MouthUpperDeepenRight", "MouthUpperDeepenLeft", "NoseSneerRight", "NoseSneerLeft",
            "MouthLowerDownRight", "MouthLowerDownLeft", "MouthUpperRight", "MouthUpperLeft", "MouthLowerRight", "MouthLowerLeft",
            "MouthCornerPullRight", "MouthCornerPullLeft", "MouthCornerSlantRight", "MouthCornerSlantLeft", "MouthFrownRight", "MouthFrownLeft",
            "MouthStretchRight", "MouthStretchLeft", "MouthDimpleRight", "MouthDimpleLeft", "MouthRaiserUpper", "MouthRaiserLower",
            "MouthPressRight", "MouthPressLeft", "MouthTightenerRight", "MouthTightenerLeft",
            "TongueOut", "TongueUp", "TongueDown", "TongueRight", "TongueLeft", "TongueRoll", "TongueBendDown", "TongueCurlUp", "TongueSquish", "TongueFlat",
            "TongueTwistRight", "TongueTwistLeft", "SoftPalateClose", "ThroatSwallow", "NeckFlexRight", "NeckFlexLeft",
        };

        private sealed class Frame
        {
            public readonly float[] S = new float[ShapeNames.Length];
            public float LeftX, LeftY, RightX, RightY, LeftOpen = 1f, RightOpen = 1f, LeftPupilMm, RightPupilMm;
        }

        private readonly Frame _frame = new Frame();
        private readonly string[] _paths;
        private readonly Func<Frame, float>[] _evaluators;
        private readonly float[] _lastSent;
        private readonly bool[] _hasSent;

        public bool HasReceived { get; private set; }

        public BasisVrcftNativeFaceDecoder(IReadOnlyList<string> parameterNames)
        {
            var paths = new List<string>(parameterNames.Count);
            var evaluators = new List<Func<Frame, float>>(parameterNames.Count);
            for (int i = 0; i < parameterNames.Count; i++)
            {
                Func<Frame, float> evaluator = Resolve(parameterNames[i]);
                if (evaluator == null) continue;
                paths.Add(ParameterPrefix + parameterNames[i]);
                evaluators.Add(evaluator);
            }
            _paths = paths.ToArray();
            _evaluators = evaluators.ToArray();
            _lastSent = new float[_paths.Length];
            _hasSent = new bool[_paths.Length];
        }

        public void Reset()
        {
            HasReceived = false;
            Array.Clear(_hasSent, 0, _hasSent.Length);
        }

        public static bool TryGetBlob(in SimpleOSC.OSCMessage message, out byte[] blob)
        {
            blob = null;
            if (!string.Equals(message.path, Address, StringComparison.Ordinal) || message.arguments == null || message.arguments.Length == 0) return false;
            blob = message.arguments[0] as byte[];
            return blob != null && blob.Length >= EyeByteCount;
        }

        public void Decode(byte[] blob, List<SimpleOSC.OSCMessage> output)
        {
            Frame f = _frame;
            f.RightX = (blob[0] - blob[1]) * ByteToUnit;
            f.RightY = (blob[2] - blob[3]) * ByteToUnit;
            f.LeftX = (blob[5] - blob[4]) * ByteToUnit;
            f.LeftY = (blob[6] - blob[7]) * ByteToUnit;
            f.RightOpen = 1f - blob[8] * ByteToUnit;
            f.LeftOpen = 1f - blob[9] * ByteToUnit;
            f.RightPupilMm = PupilMm(blob[10], blob[12]);
            f.LeftPupilMm = PupilMm(blob[11], blob[13]);

            int shapeCount = Mathf.Min(f.S.Length, blob.Length - EyeByteCount);
            for (int i = 0; i < shapeCount; i++) f.S[i] = blob[EyeByteCount + i] * ByteToUnit;
            for (int i = shapeCount; i < f.S.Length; i++) f.S[i] = 0f;

            HasReceived = true;
            for (int i = 0; i < _paths.Length; i++)
            {
                float value = _evaluators[i](f);
                if (_hasSent[i] && _lastSent[i] == value) continue;
                _hasSent[i] = true;
                _lastSent[i] = value;
                output.Add(new SimpleOSC.OSCMessage { path = _paths[i], typeTag = "f", arguments = new object[] { value } });
            }
        }

        private static float PupilMm(byte dilate, byte constrict)
        {
            if (dilate == 0 && constrict == 0) return 0f;
            return ((dilate - constrict) * ByteToUnit + 1f) * 5f;
        }

        private static int Shape(string name) => Array.IndexOf(ShapeNames, name);

        private static Func<Frame, float> Resolve(string name)
        {
            int direct = Shape(name);
            if (direct >= 0) return f => f.S[direct];

            switch (name)
            {
                case "EyeLeftX": return f => f.LeftX;
                case "EyeRightX": return f => f.RightX;
                case "EyeLeftY": return f => f.LeftY;
                case "EyeRightY": return f => f.RightY;
                case "EyeX": return f => (f.LeftX + f.RightX) * .5f;
                case "EyeY": return f => (f.LeftY + f.RightY) * .5f;
                case "EyeOpenLeft": return f => f.LeftOpen;
                case "EyeOpenRight": return f => f.RightOpen;
                case "EyeOpen": return f => (f.LeftOpen + f.RightOpen) * .5f;
                case "EyeClosedLeft": return f => 1f - f.LeftOpen;
                case "EyeClosedRight": return f => 1f - f.RightOpen;
                case "EyeClosed": return f => 1f - (f.LeftOpen + f.RightOpen) * .5f;
                case "PupilDiameterLeft": return f => f.LeftPupilMm * .1f;
                case "PupilDiameterRight": return f => f.RightPupilMm * .1f;
                case "PupilDiameter": return f => (f.LeftPupilMm + f.RightPupilMm) * .05f;
            }

            int ewl = Shape("EyeWideLeft"), ewr = Shape("EyeWideRight"), esl = Shape("EyeSquintLeft"), esr = Shape("EyeSquintRight");
            int biul = Shape("BrowInnerUpLeft"), biur = Shape("BrowInnerUpRight"), boul = Shape("BrowOuterUpLeft"), bour = Shape("BrowOuterUpRight");
            int bll = Shape("BrowLowererLeft"), blr = Shape("BrowLowererRight"), bpl = Shape("BrowPinchLeft"), bpr = Shape("BrowPinchRight");
            int cpl = Shape("CheekPuffLeft"), cpr = Shape("CheekPuffRight"), csl = Shape("CheekSuckLeft"), csr = Shape("CheekSuckRight");
            int cql = Shape("CheekSquintLeft"), cqr = Shape("CheekSquintRight");
            int jr = Shape("JawRight"), jl = Shape("JawLeft"), jf = Shape("JawForward"), jb = Shape("JawBackward");
            int mur = Shape("MouthUpperRight"), mul = Shape("MouthUpperLeft"), mlr = Shape("MouthLowerRight"), mll = Shape("MouthLowerLeft");
            int lsur = Shape("LipSuckUpperRight"), lsul = Shape("LipSuckUpperLeft"), lslr = Shape("LipSuckLowerRight"), lsll = Shape("LipSuckLowerLeft");
            int lfur = Shape("LipFunnelUpperRight"), lful = Shape("LipFunnelUpperLeft"), lflr = Shape("LipFunnelLowerRight"), lfll = Shape("LipFunnelLowerLeft");
            int lpur = Shape("LipPuckerUpperRight"), lpul = Shape("LipPuckerUpperLeft"), lplr = Shape("LipPuckerLowerRight"), lpll = Shape("LipPuckerLowerLeft");
            int muur = Shape("MouthUpperUpRight"), muul = Shape("MouthUpperUpLeft"), mldr = Shape("MouthLowerDownRight"), mldl = Shape("MouthLowerDownLeft");
            int mcpr = Shape("MouthCornerPullRight"), mcpl = Shape("MouthCornerPullLeft"), mcsr = Shape("MouthCornerSlantRight"), mcsl = Shape("MouthCornerSlantLeft");
            int mfr = Shape("MouthFrownRight"), mfl = Shape("MouthFrownLeft"), msr = Shape("MouthStretchRight"), msl = Shape("MouthStretchLeft");
            int mtr = Shape("MouthTightenerRight"), mtl = Shape("MouthTightenerLeft"), mpr = Shape("MouthPressRight"), mpl = Shape("MouthPressLeft");
            int mdr = Shape("MouthDimpleRight"), mdl = Shape("MouthDimpleLeft"), nsr = Shape("NoseSneerRight"), nsl = Shape("NoseSneerLeft");
            int tr = Shape("TongueRight"), tl = Shape("TongueLeft"), tu = Shape("TongueUp"), td = Shape("TongueDown");
            int tcu = Shape("TongueCurlUp"), tbd = Shape("TongueBendDown"), tfl = Shape("TongueFlat"), tsq = Shape("TongueSquish");

            switch (name)
            {
                case "EyeLidLeft": return f => f.LeftOpen * .75f + f.S[ewl] * .25f;
                case "EyeLidRight": return f => f.RightOpen * .75f + f.S[ewr] * .25f;
                case "EyeLid": return f => (f.LeftOpen + f.RightOpen) * .5f * .75f + (f.S[ewl] + f.S[ewr]) * .5f * .25f;
                case "EyeWide": return f => Mathf.Max(f.S[ewl], f.S[ewr]);
                case "EyeSquint":
                case "EyesSquint": return f => Mathf.Max(f.S[esl], f.S[esr]);

                case "BrowUpRight": return f => f.S[bour] * .6f + f.S[biur] * .4f;
                case "BrowUpLeft": return f => f.S[boul] * .6f + f.S[biul] * .4f;
                case "BrowDownRight": return f => f.S[blr] * .75f + f.S[bpr] * .25f;
                case "BrowDownLeft": return f => f.S[bll] * .75f + f.S[bpl] * .25f;
                case "BrowUp": return f => (f.S[bour] * .6f + f.S[biur] * .4f + f.S[boul] * .6f + f.S[biul] * .4f) * .5f;
                case "BrowDown": return f => (f.S[blr] * .75f + f.S[bpr] * .25f + f.S[bll] * .75f + f.S[bpl] * .25f) * .5f;
                case "BrowInnerUp": return f => (f.S[biul] + f.S[biur]) * .5f;
                case "BrowOuterUp": return f => (f.S[boul] + f.S[bour]) * .5f;
                case "BrowExpressionRight": return f => Mathf.Min(1f, f.S[biur] * .5f + f.S[bour] * .5f) - (f.S[blr] * .75f + f.S[bpr] * .25f);
                case "BrowExpressionLeft": return f => Mathf.Min(1f, f.S[biul] * .5f + f.S[boul] * .5f) - (f.S[bll] * .75f + f.S[bpl] * .25f);

                case "JawX": return f => f.S[jr] - f.S[jl];
                case "JawZ": return f => f.S[jf] - f.S[jb];

                case "CheekSquint": return f => (f.S[cql] + f.S[cqr]) * .5f;
                case "CheekPuffSuckLeft": return f => f.S[cpl] - f.S[csl];
                case "CheekPuffSuckRight": return f => f.S[cpr] - f.S[csr];
                case "CheekPuffSuck": return f => (f.S[cpr] + f.S[cpl]) * .5f - (f.S[csr] + f.S[csl]) * .5f;
                case "CheekSuck": return f => (f.S[csl] + f.S[csr]) * .5f;

                case "MouthUpperX": return f => f.S[mur] - f.S[mul];
                case "MouthLowerX": return f => f.S[mlr] - f.S[mll];
                case "MouthX": return f => (f.S[mur] + f.S[mlr]) * .5f - (f.S[mul] + f.S[mll]) * .5f;

                case "LipSuckUpper": return f => (f.S[lsur] + f.S[lsul]) * .5f;
                case "LipSuckLower": return f => (f.S[lslr] + f.S[lsll]) * .5f;
                case "LipSuck": return f => (f.S[lsur] + f.S[lsul] + f.S[lslr] + f.S[lsll]) * .25f;
                case "LipFunnelUpper": return f => (f.S[lfur] + f.S[lful]) * .5f;
                case "LipFunnelLower": return f => (f.S[lflr] + f.S[lfll]) * .5f;
                case "LipFunnel": return f => (f.S[lfur] + f.S[lful] + f.S[lflr] + f.S[lfll]) * .25f;
                case "LipPuckerUpper": return f => (f.S[lpur] + f.S[lpul]) * .5f;
                case "LipPuckerLower": return f => (f.S[lplr] + f.S[lpll]) * .5f;
                case "LipPuckerRight": return f => (f.S[lpur] + f.S[lplr]) * .5f;
                case "LipPuckerLeft": return f => (f.S[lpul] + f.S[lpll]) * .5f;
                case "LipPucker": return f => (f.S[lpur] + f.S[lpul] + f.S[lplr] + f.S[lpll]) * .25f;

                case "MouthUpperUp": return f => (f.S[muur] + f.S[muul]) * .5f;
                case "MouthLowerDown": return f => (f.S[mldr] + f.S[mldl]) * .5f;
                case "MouthOpen": return f => (f.S[muur] + f.S[muul] + f.S[mldr] + f.S[mldl]) * .25f;
                case "MouthStretch": return f => (f.S[msr] + f.S[msl]) * .5f;
                case "MouthTightener": return f => (f.S[mtr] + f.S[mtl]) * .5f;
                case "MouthPress": return f => (f.S[mpr] + f.S[mpl]) * .5f;
                case "MouthDimple": return f => (f.S[mdr] + f.S[mdl]) * .5f;
                case "NoseSneer": return f => (f.S[nsr] + f.S[nsl]) * .5f;

                case "MouthSmileRight": return f => f.S[mcpr] * .8f + f.S[mcsr] * .2f;
                case "MouthSmileLeft": return f => f.S[mcpl] * .8f + f.S[mcsl] * .2f;
                case "MouthSadRight": return f => Mathf.Max(f.S[mfr], f.S[msr]);
                case "MouthSadLeft": return f => Mathf.Max(f.S[mfl], f.S[msl]);
                case "SmileFrownRight": return f => f.S[mcpr] * .8f + f.S[mcsr] * .2f - f.S[mfr];
                case "SmileFrownLeft": return f => f.S[mcpl] * .8f + f.S[mcsl] * .2f - f.S[mfl];

                case "TongueX": return f => f.S[tr] - f.S[tl];
                case "TongueY": return f => f.S[tu] - f.S[td];
                case "TongueArchY": return f => f.S[tcu] - f.S[tbd];
                case "TongueShape": return f => f.S[tfl] - f.S[tsq];
            }

            return null;
        }
    }
}
