using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Drivers;
using HVR.Basis.Comms;
using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>
    /// Routes MediaPipe ARKit-52 face blendshapes and derived eye gaze into Basis through
    /// HVR.Basis.Comms, the same way OSC face tracking does: submit FT/v2/* values to the
    /// scene AcquisitionService and notify the avatar's activity relay. The avatar's
    /// AutomaticFaceTracking applies them to the mesh/eye bones and networks them to remotes.
    /// </summary>
    [NoAutoStaticsCleanup]
    public sealed class MediaPipeFaceConverter
    {
        public float EyeGainX = 1f;
        public float EyeGainY = 1f;
        public float GazeStrength = 1f;
        public bool InvertEyeX = false;
        public bool InvertEyeY = false;
        public bool EyeLidIsOpenness = true;
        public float TongueGain = 1f;
        /// <summary>Head rotation the user rests at; mesh-measured cues are only trusted near it.</summary>
        public Quaternion Frontal = Quaternion.identity;

        private const float IrisGainX = 1.5f, IrisGainY = 2f, HoldSeconds = 0.5f, RelaxHz = 3f, TongueCutoff = 0.5f;
        private const int LeftGazeX = 0, LeftGazeY = 1, RightGazeX = 2, RightGazeY = 3;
        private MediaPipeScalarFilter[] _shapes;
        private readonly MediaPipeScalarFilter[] _gaze = new MediaPipeScalarFilter[4];
        private MediaPipeScalarFilter _tongue;
        private float[] _smoothed;
        private readonly float[] _input = new float[(int)MediaPipeArkitBlendshape.Count];
        private readonly MediaPipeExpressionRange[] _ranges = new MediaPipeExpressionRange[(int)MediaPipeArkitBlendshape.Count];
        private MediaPipeExpressionRange _cheekWidth, _noseLeft, _noseRight;
        private float _tongueValue, _lostFor;
        private bool _started, _irisSeen;
        private Vector2 _gazeCenterLeft, _gazeCenterRight;
        private readonly Dictionary<int, float> _lastSubmitted = new Dictionary<int, float>();
        private const float SubmitEpsilon = 1f / 255f;

        private static readonly (string addr, MediaPipeArkitBlendshape src)[] Direct =
        {
            ("FT/v2/BrowDownLeft", MediaPipeArkitBlendshape.BrowDownLeft),
            ("FT/v2/BrowDownRight", MediaPipeArkitBlendshape.BrowDownRight),
            ("FT/v2/BrowInnerUp", MediaPipeArkitBlendshape.BrowInnerUp),
            ("FT/v2/BrowInnerUpLeft", MediaPipeArkitBlendshape.BrowInnerUp),
            ("FT/v2/BrowInnerUpRight", MediaPipeArkitBlendshape.BrowInnerUp),
            ("FT/v2/BrowOuterUpLeft", MediaPipeArkitBlendshape.BrowOuterUpLeft),
            ("FT/v2/BrowOuterUpRight", MediaPipeArkitBlendshape.BrowOuterUpRight),
            ("FT/v2/CheekSquintLeft", MediaPipeArkitBlendshape.CheekSquintLeft),
            ("FT/v2/CheekSquintRight", MediaPipeArkitBlendshape.CheekSquintRight),
            ("FT/v2/EyeSquintLeft", MediaPipeArkitBlendshape.EyeSquintLeft),
            ("FT/v2/EyeSquintRight", MediaPipeArkitBlendshape.EyeSquintRight),
            ("FT/v2/JawOpen", MediaPipeArkitBlendshape.JawOpen),
            ("FT/v2/MouthClosed", MediaPipeArkitBlendshape.MouthClose),
            ("FT/v2/LipSuckUpper", MediaPipeArkitBlendshape.MouthRollUpper),
            ("FT/v2/LipSuckLower", MediaPipeArkitBlendshape.MouthRollLower),
            ("FT/v2/LipFunnel", MediaPipeArkitBlendshape.MouthFunnel),
            ("FT/v2/LipPucker", MediaPipeArkitBlendshape.MouthPucker),
            ("FT/v2/LipSuckUpperLeft", MediaPipeArkitBlendshape.MouthRollUpper),
            ("FT/v2/LipSuckUpperRight", MediaPipeArkitBlendshape.MouthRollUpper),
            ("FT/v2/LipSuckLowerLeft", MediaPipeArkitBlendshape.MouthRollLower),
            ("FT/v2/LipSuckLowerRight", MediaPipeArkitBlendshape.MouthRollLower),
            ("FT/v2/LipFunnelUpperLeft", MediaPipeArkitBlendshape.MouthFunnel),
            ("FT/v2/LipFunnelUpperRight", MediaPipeArkitBlendshape.MouthFunnel),
            ("FT/v2/LipFunnelLowerLeft", MediaPipeArkitBlendshape.MouthFunnel),
            ("FT/v2/LipFunnelLowerRight", MediaPipeArkitBlendshape.MouthFunnel),
            ("FT/v2/LipPuckerUpperLeft", MediaPipeArkitBlendshape.MouthPucker),
            ("FT/v2/LipPuckerUpperRight", MediaPipeArkitBlendshape.MouthPucker),
            ("FT/v2/LipPuckerLowerLeft", MediaPipeArkitBlendshape.MouthPucker),
            ("FT/v2/LipPuckerLowerRight", MediaPipeArkitBlendshape.MouthPucker),
            ("FT/v2/MouthUpperUpLeft", MediaPipeArkitBlendshape.MouthUpperUpLeft),
            ("FT/v2/MouthUpperUpRight", MediaPipeArkitBlendshape.MouthUpperUpRight),
            ("FT/v2/MouthLowerDownLeft", MediaPipeArkitBlendshape.MouthLowerDownLeft),
            ("FT/v2/MouthLowerDownRight", MediaPipeArkitBlendshape.MouthLowerDownRight),
            ("FT/v2/MouthSmileLeft", MediaPipeArkitBlendshape.MouthSmileLeft),
            ("FT/v2/MouthSmileRight", MediaPipeArkitBlendshape.MouthSmileRight),
            ("FT/v2/MouthFrownLeft", MediaPipeArkitBlendshape.MouthFrownLeft),
            ("FT/v2/MouthFrownRight", MediaPipeArkitBlendshape.MouthFrownRight),
            ("FT/v2/MouthStretchLeft", MediaPipeArkitBlendshape.MouthStretchLeft),
            ("FT/v2/MouthStretchRight", MediaPipeArkitBlendshape.MouthStretchRight),
            ("FT/v2/MouthDimpleLeft", MediaPipeArkitBlendshape.MouthDimpleLeft),
            ("FT/v2/MouthDimpleRight", MediaPipeArkitBlendshape.MouthDimpleRight),
            ("FT/v2/MouthPressLeft", MediaPipeArkitBlendshape.MouthPressLeft),
            ("FT/v2/MouthPressRight", MediaPipeArkitBlendshape.MouthPressRight),
            ("FT/v2/MouthRaiserUpper", MediaPipeArkitBlendshape.MouthShrugUpper),
            ("FT/v2/MouthRaiserLower", MediaPipeArkitBlendshape.MouthShrugLower),
            ("FT/v2/NoseSneerLeft", MediaPipeArkitBlendshape.NoseSneerLeft),
            ("FT/v2/NoseSneerRight", MediaPipeArkitBlendshape.NoseSneerRight),
        };

        // Starting rest and full-strength scores; each person's range is then learned from there. MediaPipe
        // under-reports these channels, every other channel starts at 0..1.
        private static readonly (MediaPipeArkitBlendshape shape, float neutral, float full)[] InputRanges =
        {
            (MediaPipeArkitBlendshape.CheekPuff, 0.02f, 0.35f),
            (MediaPipeArkitBlendshape.CheekSquintLeft, 0f, 0.6f),
            (MediaPipeArkitBlendshape.CheekSquintRight, 0f, 0.6f),
            (MediaPipeArkitBlendshape.NoseSneerLeft, 0f, 0.5f),
            (MediaPipeArkitBlendshape.NoseSneerRight, 0f, 0.5f),
            (MediaPipeArkitBlendshape.MouthLeft, 0.02f, 0.4f),
            (MediaPipeArkitBlendshape.MouthRight, 0.02f, 0.4f),
            (MediaPipeArkitBlendshape.JawForward, 0.02f, 0.4f),
            (MediaPipeArkitBlendshape.EyeWideLeft, 0.15f, 0.6f),
            (MediaPipeArkitBlendshape.EyeWideRight, 0.15f, 0.6f),
        };
        private const float MinSpanFraction = 0.5f, CheekWidthSpan = 0.03f, NoseLiftSpan = 0.06f;
        private const float LearnTiltDeg = 12f, GeometryTiltStart = 10f, GeometryTiltEnd = 22f, SideTrustStart = 0.15f, SideTrustEnd = 0.4f, LearnSideBalance = 0.15f;

        // Paired channels whose far-side score turns to noise once that half of the face turns away from the camera.
        private static readonly (MediaPipeArkitBlendshape left, MediaPipeArkitBlendshape right)[] SidePairs =
        {
            (MediaPipeArkitBlendshape.BrowDownLeft, MediaPipeArkitBlendshape.BrowDownRight),
            (MediaPipeArkitBlendshape.BrowOuterUpLeft, MediaPipeArkitBlendshape.BrowOuterUpRight),
            (MediaPipeArkitBlendshape.CheekSquintLeft, MediaPipeArkitBlendshape.CheekSquintRight),
            (MediaPipeArkitBlendshape.MouthDimpleLeft, MediaPipeArkitBlendshape.MouthDimpleRight),
            (MediaPipeArkitBlendshape.MouthFrownLeft, MediaPipeArkitBlendshape.MouthFrownRight),
            (MediaPipeArkitBlendshape.MouthLowerDownLeft, MediaPipeArkitBlendshape.MouthLowerDownRight),
            (MediaPipeArkitBlendshape.MouthPressLeft, MediaPipeArkitBlendshape.MouthPressRight),
            (MediaPipeArkitBlendshape.MouthSmileLeft, MediaPipeArkitBlendshape.MouthSmileRight),
            (MediaPipeArkitBlendshape.MouthStretchLeft, MediaPipeArkitBlendshape.MouthStretchRight),
            (MediaPipeArkitBlendshape.MouthUpperUpLeft, MediaPipeArkitBlendshape.MouthUpperUpRight),
            (MediaPipeArkitBlendshape.NoseSneerLeft, MediaPipeArkitBlendshape.NoseSneerRight),
        };

        private readonly int[] _directIds;
        private readonly int _idEyeLeftX, _idEyeRightX, _idEyeY;
        private readonly int _idEyeLidLeft, _idEyeLidRight;
        private readonly int _idJawX, _idJawZ, _idCheekPuffSuck, _idCheekPuffSuckLeft, _idCheekPuffSuckRight;
        private readonly int _idMouthX, _idMouthUpperX, _idMouthLowerX;
        private readonly int _idTongueOut, _idJawOpen;

        private BasisAvatar _relayAvatar;
        private FaceTrackingActivityRelay _relay;

        public MediaPipeFaceConverter()
        {
            for (int i = 0; i < _ranges.Length; i++) _ranges[i].Start(0f, 1f, MinSpanFraction);
            foreach (var (shape, neutral, full) in InputRanges) _ranges[(int)shape].Start(neutral, full, MinSpanFraction * (full - neutral));

            _directIds = new int[Direct.Length];
            for (int i = 0; i < Direct.Length; i++)
            {
                _directIds[i] = HVRAddress.AddressToId(Direct[i].addr);
            }

            _idEyeLeftX = HVRAddress.AddressToId("FT/v2/EyeLeftX");
            _idEyeRightX = HVRAddress.AddressToId("FT/v2/EyeRightX");
            _idEyeY = HVRAddress.AddressToId("FT/v2/EyeY");
            _idEyeLidLeft = HVRAddress.AddressToId("FT/v2/EyeLidLeft");
            _idEyeLidRight = HVRAddress.AddressToId("FT/v2/EyeLidRight");
            _idJawX = HVRAddress.AddressToId("FT/v2/JawX");
            _idJawZ = HVRAddress.AddressToId("FT/v2/JawZ");
            _idCheekPuffSuck = HVRAddress.AddressToId("FT/v2/CheekPuffSuck");
            _idCheekPuffSuckLeft = HVRAddress.AddressToId("FT/v2/CheekPuffSuckLeft");
            _idCheekPuffSuckRight = HVRAddress.AddressToId("FT/v2/CheekPuffSuckRight");
            _idMouthX = HVRAddress.AddressToId("FT/v2/MouthX");
            _idMouthUpperX = HVRAddress.AddressToId("FT/v2/MouthUpperX");
            _idMouthLowerX = HVRAddress.AddressToId("FT/v2/MouthLowerX");
            _idTongueOut = HVRAddress.AddressToId("FT/v2/TongueOut");
            _idJawOpen = HVRAddress.AddressToId("FT/v2/JawOpen");
        }

        public Vector2 GazeCenterLeft => _gazeCenterLeft;
        public Vector2 GazeCenterRight => _gazeCenterRight;
        public bool IrisSeen => _irisSeen;

        public void SetGazeCenter(Vector2 left, Vector2 right)
        {
            if (!float.IsFinite(left.x) || !float.IsFinite(left.y) || !float.IsFinite(right.x) || !float.IsFinite(right.y)) return;
            _gazeCenterLeft = left;
            _gazeCenterRight = right;
        }

        public void CalibrateGaze()
        {
            if (_gaze[LeftGazeX].HasSample) _gazeCenterLeft = new Vector2(_gaze[LeftGazeX].Carried, _gaze[LeftGazeY].Carried);
            if (_gaze[RightGazeX].HasSample) _gazeCenterRight = new Vector2(_gaze[RightGazeX].Carried, _gaze[RightGazeY].Carried);
        }

        public void Reset()
        {
            _shapes = null;
            for (int i = 0; i < _gaze.Length; i++) _gaze[i].Reset();
            _tongue.Reset();
            _started = false;
            _lostFor = 0f;
        }

        // Runs every rendered frame whether or not the face is in view. A lost face holds for a moment and then
        // eases every channel back to neutral, so the avatar never freezes mid-expression.
        public void Apply(in BasisMediaPipeResult result, BasisAvatar avatar, in MediaPipeTiming timing)
        {
            if (avatar == null) return;

            AcquisitionService acquisition = AcquisitionService.SceneInstance;
            if (acquisition == null) return;

            float[] raw = result.FaceBlendshapes;
            bool present = result.HasFace && raw != null && raw.Length >= (int)MediaPipeArkitBlendshape.Count;
            if (!present && !_started) return;

            int count = (int)MediaPipeArkitBlendshape.Count;
            if (_shapes == null || _shapes.Length != count)
            {
                _shapes = new MediaPipeScalarFilter[count];
                _smoothed = new float[count];
            }

            if (present)
            {
                _started = true;
                _lostFor = 0f;
                Shape(in result, raw, in timing);
                for (int i = 0; i < count; i++)
                {
                    _smoothed[i] = _shapes[i].Apply(_input[i], in timing);
                }
                _tongueValue = _tongue.Apply(Mathf.Clamp01(result.TongueOut * TongueGain), in timing);
                UpdateGaze(in result, in timing);
            }
            else
            {
                _lostFor += timing.RenderDelta;
                if (_lostFor <= HoldSeconds)
                {
                    for (int i = 0; i < count; i++) _smoothed[i] = _shapes[i].Carry(in timing);
                    _tongueValue = _tongue.Carry(in timing);
                    for (int i = 0; i < _gaze.Length; i++) _gaze[i].Carry(in timing);
                }
                else
                {
                    float alpha = BasisFilterMath.Alpha(RelaxHz, timing.RenderDelta);
                    for (int i = 0; i < count; i++) _smoothed[i] = _shapes[i].Relax(0f, alpha);
                    _tongueValue = _tongue.Relax(0f, alpha);
                    _gaze[LeftGazeX].Relax(_gazeCenterLeft.x, alpha);
                    _gaze[LeftGazeY].Relax(_gazeCenterLeft.y, alpha);
                    _gaze[RightGazeX].Relax(_gazeCenterRight.x, alpha);
                    _gaze[RightGazeY].Relax(_gazeCenterRight.y, alpha);
                }
            }

            Submit(acquisition, avatar);
        }

        // Rescales every score to this person's learned range, lifts cheek puff and nose sneer with what the mesh
        // shows, and leans paired channels on the half of the face the camera can actually see.
        private void Shape(in BasisMediaPipeResult result, float[] raw, in MediaPipeTiming timing)
        {
            float tilt = MediaPipeSpace.IsUsable(result.FaceTransform) ? Quaternion.Angle(result.FaceTransform.rotation, Frontal) : 180f;
            float balance = result.HasFaceGeometry ? result.FaceSideBalance : 0f;
            bool learn = timing.IsNewSample && tilt < LearnTiltDeg && Mathf.Abs(balance) < LearnSideBalance;
            float dt = timing.SampleDelta;
            for (int i = 0; i < _input.Length; i++)
            {
                float x = raw[i];
                if (learn) _ranges[i].Learn(x, dt);
                _input[i] = _ranges[i].Normalize(x);
            }
            if (result.HasFaceGeometry) BlendGeometry(in result, learn, dt, 1f - Ramp(GeometryTiltStart, GeometryTiltEnd, tilt));
            TrustNearSide(balance);
        }

        private void BlendGeometry(in BasisMediaPipeResult result, bool learn, float dt, float frontal)
        {
            if (frontal <= 0f) return;
            float smileLeft = _input[(int)MediaPipeArkitBlendshape.MouthSmileLeft], smileRight = _input[(int)MediaPipeArkitBlendshape.MouthSmileRight];
            float jaw = _input[(int)MediaPipeArkitBlendshape.JawOpen];
            float cheekQuiet = (1f - jaw) * (1f - Mathf.Max(smileLeft, smileRight));
            if (learn)
            {
                if (!_cheekWidth.Started) _cheekWidth.Start(result.CheekWidth, result.CheekWidth + CheekWidthSpan, CheekWidthSpan);
                if (!_noseLeft.Started) _noseLeft.Start(result.NoseLiftLeft, result.NoseLiftLeft + NoseLiftSpan, NoseLiftSpan);
                if (!_noseRight.Started) _noseRight.Start(result.NoseLiftRight, result.NoseLiftRight + NoseLiftSpan, NoseLiftSpan);
                if (cheekQuiet > 0.7f) _cheekWidth.Learn(result.CheekWidth, dt);
                if (smileLeft < 0.3f) _noseLeft.Learn(result.NoseLiftLeft, dt);
                if (smileRight < 0.3f) _noseRight.Learn(result.NoseLiftRight, dt);
            }
            Raise(MediaPipeArkitBlendshape.CheekPuff, _cheekWidth.Normalize(result.CheekWidth) * frontal * cheekQuiet);
            Raise(MediaPipeArkitBlendshape.NoseSneerLeft, _noseLeft.Normalize(result.NoseLiftLeft) * frontal * (1f - smileLeft));
            Raise(MediaPipeArkitBlendshape.NoseSneerRight, _noseRight.Normalize(result.NoseLiftRight) * frontal * (1f - smileRight));
        }

        private void Raise(MediaPipeArkitBlendshape shape, float value)
        {
            if (value > _input[(int)shape]) _input[(int)shape] = value;
        }

        private void TrustNearSide(float balance)
        {
            float farTrust = 1f - Ramp(SideTrustStart, SideTrustEnd, Mathf.Abs(balance));
            if (farTrust >= 1f) return;
            foreach (var (left, right) in SidePairs)
            {
                int near = balance > 0f ? (int)left : (int)right, far = balance > 0f ? (int)right : (int)left;
                _input[far] = Mathf.Lerp(_input[near], _input[far], farTrust);
            }
        }

        private static float Ramp(float start, float end, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(start, end, x));

        // Iris landmarks give the gaze; a closed eye reports no gaze and simply holds the last value.
        private void UpdateGaze(in BasisMediaPipeResult result, in MediaPipeTiming timing)
        {
            if (result.HasLeftGaze)
            {
                _gaze[LeftGazeX].Apply(result.LeftEyeGaze.x, in timing);
                _gaze[LeftGazeY].Apply(result.LeftEyeGaze.y, in timing);
                _irisSeen = true;
            }
            else
            {
                _gaze[LeftGazeX].Carry(in timing);
                _gaze[LeftGazeY].Carry(in timing);
            }
            if (result.HasRightGaze)
            {
                _gaze[RightGazeX].Apply(result.RightEyeGaze.x, in timing);
                _gaze[RightGazeY].Apply(result.RightEyeGaze.y, in timing);
                _irisSeen = true;
            }
            else
            {
                _gaze[RightGazeX].Carry(in timing);
                _gaze[RightGazeY].Carry(in timing);
            }
        }

        private void Submit(AcquisitionService acquisition, BasisAvatar avatar)
        {
            float[] bs = _smoothed;
            FaceTrackingActivityRelay relay = ResolveRelay(avatar);
            relay?.NotifySourceSample(_idEyeLidLeft);
            relay?.NotifySourceSample(_idJawOpen);

            for (int i = 0; i < Direct.Length; i++)
            {
                SubmitIfChanged(acquisition, _directIds[i], Mathf.Clamp01(bs[(int)Direct[i].src]));
            }

            SubmitIfChanged(acquisition, _idJawX, Get(bs, MediaPipeArkitBlendshape.JawRight) - Get(bs, MediaPipeArkitBlendshape.JawLeft));
            SubmitIfChanged(acquisition, _idJawZ, Mathf.Clamp01(Get(bs, MediaPipeArkitBlendshape.JawForward)));
            float cheekPuff = Mathf.Clamp01(Get(bs, MediaPipeArkitBlendshape.CheekPuff));
            SubmitIfChanged(acquisition, _idCheekPuffSuck, cheekPuff);
            SubmitIfChanged(acquisition, _idCheekPuffSuckLeft, cheekPuff);
            SubmitIfChanged(acquisition, _idCheekPuffSuckRight, cheekPuff);
            float mouthX = Mathf.Clamp(Get(bs, MediaPipeArkitBlendshape.MouthRight) - Get(bs, MediaPipeArkitBlendshape.MouthLeft), -1f, 1f);
            SubmitIfChanged(acquisition, _idMouthX, mouthX);
            SubmitIfChanged(acquisition, _idMouthUpperX, mouthX);
            SubmitIfChanged(acquisition, _idMouthLowerX, mouthX);

            float eyeLeftX, eyeRightX, eyeY;
            bool leftIris = _gaze[LeftGazeX].HasSample, rightIris = _gaze[RightGazeX].HasSample;
            if (leftIris || rightIris)
            {
                Vector2 left = leftIris ? new Vector2(_gaze[LeftGazeX].Carried, _gaze[LeftGazeY].Carried) - _gazeCenterLeft : new Vector2(_gaze[RightGazeX].Carried, _gaze[RightGazeY].Carried) - _gazeCenterRight;
                Vector2 right = rightIris ? new Vector2(_gaze[RightGazeX].Carried, _gaze[RightGazeY].Carried) - _gazeCenterRight : left;
                eyeLeftX = left.x * IrisGainX * GazeStrength;
                eyeRightX = right.x * IrisGainX * GazeStrength;
                eyeY = 0.5f * (left.y + right.y) * IrisGainY * GazeStrength;
            }
            else
            {
                eyeLeftX = Get(bs, MediaPipeArkitBlendshape.EyeLookOutLeft) - Get(bs, MediaPipeArkitBlendshape.EyeLookInLeft);
                eyeRightX = Get(bs, MediaPipeArkitBlendshape.EyeLookInRight) - Get(bs, MediaPipeArkitBlendshape.EyeLookOutRight);
                eyeY = 0.5f * (Get(bs, MediaPipeArkitBlendshape.EyeLookUpLeft) + Get(bs, MediaPipeArkitBlendshape.EyeLookUpRight))
                     - 0.5f * (Get(bs, MediaPipeArkitBlendshape.EyeLookDownLeft) + Get(bs, MediaPipeArkitBlendshape.EyeLookDownRight));
            }

            float eyeGainX = InvertEyeX ? -EyeGainX : EyeGainX;
            float eyeGainY = InvertEyeY ? -EyeGainY : EyeGainY;
            SubmitIfChanged(acquisition, _idEyeLeftX, Mathf.Clamp(eyeLeftX * eyeGainX, -1f, 1f));
            SubmitIfChanged(acquisition, _idEyeRightX, Mathf.Clamp(eyeRightX * eyeGainX, -1f, 1f));
            SubmitIfChanged(acquisition, _idEyeY, Mathf.Clamp(eyeY * eyeGainY, -1f, 1f));

            SubmitIfChanged(acquisition, _idEyeLidLeft, EyeLid(Get(bs, MediaPipeArkitBlendshape.EyeBlinkLeft), Get(bs, MediaPipeArkitBlendshape.EyeWideLeft)));
            SubmitIfChanged(acquisition, _idEyeLidRight, EyeLid(Get(bs, MediaPipeArkitBlendshape.EyeBlinkRight), Get(bs, MediaPipeArkitBlendshape.EyeWideRight)));

            SubmitIfChanged(acquisition, _idTongueOut, Mathf.InverseLerp(TongueCutoff, 1f, _tongueValue));
        }

        // Skip submitting near-unchanged values: avoids redundant local SetBlendShapeWeight
        // P/Invokes and network churn for a mostly-neutral face.
        private void SubmitIfChanged(AcquisitionService acquisition, int id, float value)
        {
            if (_lastSubmitted.TryGetValue(id, out float last) && Mathf.Abs(value - last) < SubmitEpsilon) { return; }
            _lastSubmitted[id] = value;
            acquisition.Submit(id, value);
        }

        // FT/v2 lids read 0 closed, 0.75 normally open, 1 wide open.
        private float EyeLid(float blink, float wide)
        {
            float open = Mathf.Clamp01(1f - blink);
            float lid = 0.75f * open + 0.25f * Mathf.Clamp01(wide) * open;
            return EyeLidIsOpenness ? lid : 1f - lid;
        }

        private static float Get(float[] bs, MediaPipeArkitBlendshape s) => bs[(int)s];

        private FaceTrackingActivityRelay ResolveRelay(BasisAvatar avatar)
        {
            if (_relay != null && _relayAvatar == avatar) return _relay;
            _relayAvatar = avatar;
            _relay = FaceTrackingActivityRelay.GetOrCreate(avatar);
            return _relay;
        }
    }
}
