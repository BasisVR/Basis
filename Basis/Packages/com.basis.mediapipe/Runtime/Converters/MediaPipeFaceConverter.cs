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
    public sealed class MediaPipeFaceConverter
    {
        public float EyeGainX = 1f;
        public float EyeGainY = 1f;
        public float GazeStrength = 1f;
        public bool InvertEyeX = false;
        public bool InvertEyeY = false;
        public bool EyeLidIsOpenness = true;
        public float Smoothing = 0.5f;
        public float TongueGain = 1f;

        private const float CutoffResponsive = 10f, CutoffSmooth = 1.5f, Beta = 1f, IrisGainX = 1.5f, IrisGainY = 2f, HoldSeconds = 0.5f, RelaxHz = 3f;
        private const int LeftGazeX = 0, LeftGazeY = 1, RightGazeX = 2, RightGazeY = 3;
        private MediaPipeScalarFilter[] _shapes;
        private readonly MediaPipeScalarFilter[] _gaze = new MediaPipeScalarFilter[4];
        private MediaPipeScalarFilter _tongue;
        private float[] _smoothed;
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

        private readonly int[] _directIds;
        private readonly int _idEyeLeftX, _idEyeRightX, _idEyeY;
        private readonly int _idEyeLidLeft, _idEyeLidRight;
        private readonly int _idJawX, _idJawZ, _idCheekPuffSuck;
        private readonly int _idTongueOut;

        private BasisAvatar _relayAvatar;
        private FaceTrackingActivityRelay _relay;

        public MediaPipeFaceConverter()
        {
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
            _idTongueOut = HVRAddress.AddressToId("FT/v2/TongueOut");
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
            float cutoff = timing.Scaled(Mathf.Lerp(CutoffResponsive, CutoffSmooth, Mathf.Clamp01(Smoothing)));

            if (present)
            {
                _started = true;
                _lostFor = 0f;
                for (int i = 0; i < count; i++)
                {
                    _smoothed[i] = _shapes[i].Apply(raw[i], in timing, cutoff, Beta);
                }
                _tongueValue = _tongue.Apply(Mathf.Clamp01(result.TongueOut * TongueGain), in timing, cutoff, Beta);
                UpdateGaze(in result, in timing, cutoff);
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

        // Iris landmarks give the gaze; a closed eye reports no gaze and simply holds the last value.
        private void UpdateGaze(in BasisMediaPipeResult result, in MediaPipeTiming timing, float cutoff)
        {
            if (result.HasLeftGaze)
            {
                _gaze[LeftGazeX].Apply(result.LeftEyeGaze.x, in timing, cutoff, Beta);
                _gaze[LeftGazeY].Apply(result.LeftEyeGaze.y, in timing, cutoff, Beta);
                _irisSeen = true;
            }
            else
            {
                _gaze[LeftGazeX].Carry(in timing);
                _gaze[LeftGazeY].Carry(in timing);
            }
            if (result.HasRightGaze)
            {
                _gaze[RightGazeX].Apply(result.RightEyeGaze.x, in timing, cutoff, Beta);
                _gaze[RightGazeY].Apply(result.RightEyeGaze.y, in timing, cutoff, Beta);
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
            ResolveRelay(avatar)?.NotifySourceSample(_idEyeLidLeft);

            for (int i = 0; i < Direct.Length; i++)
            {
                SubmitIfChanged(acquisition, _directIds[i], Mathf.Clamp01(bs[(int)Direct[i].src]));
            }

            SubmitIfChanged(acquisition, _idJawX, Get(bs, MediaPipeArkitBlendshape.JawRight) - Get(bs, MediaPipeArkitBlendshape.JawLeft));
            SubmitIfChanged(acquisition, _idJawZ, Mathf.Clamp01(Get(bs, MediaPipeArkitBlendshape.JawForward)));
            SubmitIfChanged(acquisition, _idCheekPuffSuck, Mathf.Clamp01(Get(bs, MediaPipeArkitBlendshape.CheekPuff)));

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

            SubmitIfChanged(acquisition, _idEyeLidLeft, EyeLid(Get(bs, MediaPipeArkitBlendshape.EyeBlinkLeft)));
            SubmitIfChanged(acquisition, _idEyeLidRight, EyeLid(Get(bs, MediaPipeArkitBlendshape.EyeBlinkRight)));

            SubmitIfChanged(acquisition, _idTongueOut, _tongueValue);
        }

        // Skip submitting near-unchanged values: avoids redundant local SetBlendShapeWeight
        // P/Invokes and network churn for a mostly-neutral face.
        private void SubmitIfChanged(AcquisitionService acquisition, int id, float value)
        {
            if (_lastSubmitted.TryGetValue(id, out float last) && Mathf.Abs(value - last) < SubmitEpsilon) { return; }
            _lastSubmitted[id] = value;
            acquisition.Submit(id, value);
        }

        private float EyeLid(float blink) => EyeLidIsOpenness ? Mathf.Clamp01(1f - blink) : Mathf.Clamp01(blink);

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
