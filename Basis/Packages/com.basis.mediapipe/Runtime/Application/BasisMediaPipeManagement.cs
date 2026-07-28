using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>
    /// Device source that turns webcam MediaPipe landmarks into fake Basis trackers,
    /// finger data, eye gaze and face blendshapes. Add this to the BasisDeviceManagement
    /// GameObject and include it in its BaseTypes list; its per-frame work is driven by
    /// BasisDeviceManagement.Simulate() (the central tick). Inert until the homuler
    /// MediaPipe Unity Plugin is installed (see README).
    /// </summary>
    public class BasisMediaPipeManagement : BasisBaseTypeManagement
    {
        public const string SubSystem = "BasisMediaPipe";
        public string CameraDeviceName = string.Empty;
        public BasisMediaPipeConfig Config = BasisMediaPipeConfig.Default;

        private readonly BasisMediaPipeCamera _camera = new BasisMediaPipeCamera();
        private readonly Dictionary<BasisBoneTrackedRole, BasisInputXRSimulate> _trackers = new();
        private readonly HashSet<BasisBoneTrackedRole> _activeTrackers = new();
        private readonly MediaPipeFaceConverter _faceConverter = new MediaPipeFaceConverter();
        private readonly MediaPipeHandConverter _handConverter = new MediaPipeHandConverter();
        private readonly MediaPipeHeadConverter _headConverter = new MediaPipeHeadConverter();
        private readonly MediaPipeBodyConverter _bodyConverter = new MediaPipeBodyConverter();
        private readonly MediaPipeArmConverter _armConverter = new MediaPipeArmConverter();
        private IBasisMediaPipeBackend _backend;
        private BasisMediaPipeResult _latest;
        private bool _hasLatest;

        private Transform _hipsBone, _headBone, _leftUpperArm, _leftLowerArm, _leftHandBone, _rightUpperArm, _rightLowerArm, _rightHandBone;
        private Transform _leftIndexProximal, _leftMiddleProximal, _leftLittleProximal;
        private Transform _rightIndexProximal, _rightMiddleProximal, _rightLittleProximal;
        private float _leftUpperLen, _leftForeLen, _rightUpperLen, _rightForeLen;
        private bool _armRigValid;
        private bool _handRigValid;
        private BasisAvatar _armRigAvatar;
        private bool _leftArmTracked;
        private bool _rightArmTracked;
        private float _leftHandIK;
        private float _rightHandIK;
        private double _lastSampleMs;
        private float _sampleDelta = 1f / 15f;   // a PRIOR, not a measurement -- see _rateMeasured
        private bool _rateMeasured;
        private bool _armPosePathActive;
        private bool _armDiagLogged;
        public override bool IsDeviceBootable(string BootRequest) => BootRequest == SubSystem;

        /// <summary>Pulls persisted settings into Config and restarts the backend if it is already running.</summary>
        public void ApplySettings()
        {
            LoadSettingsIntoConfig();
            if (_backend != null)
            {
                StopSDK();
                StartSDK();
            }
        }

        private void LoadSettingsIntoConfig()
        {
            BasisMediaPipeSettings.LoadAll();
            CameraDeviceName = BasisMediaPipeSettings.Camera.RawValue;
            Config.EnableFace = BasisMediaPipeSettings.EnableFace.RawValue;
            Config.EnableHands = BasisMediaPipeSettings.EnableHands.RawValue;
            Config.EnableHeadPosition = BasisMediaPipeSettings.EnableHeadPosition.RawValue;
            Config.EnableHeadRotation = BasisMediaPipeSettings.EnableHeadRotation.RawValue;
            Config.EnableHandTracking = BasisMediaPipeSettings.EnableHandTracking.RawValue;
            Config.SwapHands = BasisMediaPipeSettings.SwapHands.RawValue;
            Config.MirrorHorizontally = BasisMediaPipeSettings.Mirror.RawValue;
            Config.CameraWidth = BasisMediaPipeSettings.ResolutionWidth.RawValue;
            Config.CameraHeight = BasisMediaPipeSettings.ResolutionHeight.RawValue;
            Config.TargetFps = BasisMediaPipeSettings.CameraFps.RawValue;
            Config.EnableChest = BasisMediaPipeSettings.EnableBody.RawValue;
            Config.EnableArmElbowPole = BasisMediaPipeSettings.EnableArmElbowPole.RawValue;
            Config.EnablePose = Config.EnableChest || Config.EnableHandTracking;
            ApplyTuning();
        }

        /// <summary>Applies converter sign/gain tuning without restarting the backend.</summary>
        public void ApplyTuning()
        {
            _faceConverter.EyeLidIsOpenness = !BasisMediaPipeSettings.InvertBlink.RawValue;
            _headConverter.InvertYaw = BasisMediaPipeSettings.InvertHeadYaw.RawValue;
            _headConverter.InvertPitch = BasisMediaPipeSettings.InvertHeadPitch.RawValue;
            _faceConverter.InvertEyeX = BasisMediaPipeSettings.InvertHeadYaw.RawValue;
            _faceConverter.InvertEyeY = BasisMediaPipeSettings.InvertHeadPitch.RawValue;
            _headConverter.Smoothing = BasisMediaPipeSettings.HeadSmoothing.RawValue;
            _faceConverter.Smoothing = BasisMediaPipeSettings.FaceSmoothing.RawValue;
            _faceConverter.TongueGain = BasisMediaPipeSettings.EnableTongue.RawValue
                ? BasisMediaPipeSettings.TongueStrength.RawValue
                : 0f;
            _handConverter.PoseSmoothing = BasisMediaPipeSettings.HandSmoothing.RawValue;
            _handConverter.FingerSmoothing = BasisMediaPipeSettings.FingerSmoothing.RawValue;
            _handConverter.UseRotation = BasisMediaPipeSettings.HandRotation.RawValue;
            _armConverter.Smoothing = BasisMediaPipeSettings.HandSmoothing.RawValue;
            _armConverter.HeadAnchor = BasisMediaPipeSettings.ArmHeadAnchor.RawValue;
            _armConverter.ElbowRestBias = BasisMediaPipeSettings.ElbowRestBias.RawValue;
            _bodyConverter.Strength = BasisMediaPipeSettings.ChestMotion.RawValue;
            _bodyConverter.Smoothing = BasisMediaPipeSettings.HeadSmoothing.RawValue;
            _headConverter.PositionGain = BasisMediaPipeSettings.HeadPositionStrength.RawValue;
            _headConverter.HeightOffset = BasisMediaPipeSettings.HeadHeight.RawValue;
            _headConverter.YawGain = BasisMediaPipeSettings.HeadRotationStrength.RawValue;
            _headConverter.PitchGain = BasisMediaPipeSettings.HeadRotationStrength.RawValue;
            _headConverter.RollGain = BasisMediaPipeSettings.HeadRotationStrength.RawValue;
            _headConverter.InvertRoll = BasisMediaPipeSettings.InvertHeadRoll.RawValue;
        }

        public void SetEnabled(bool value)
        {
            if (value)
            {
                if (_backend == null) StartSDK();
            }
            else
            {
                StopSDK();
            }
        }

        public void SetCamera(string deviceName)
        {
            CameraDeviceName = deviceName;
            if (_backend != null)
            {
                _camera.Start(deviceName, Config.CameraWidth, Config.CameraHeight, Config.TargetFps);
            }
        }

        /// <summary>Applies persisted resolution/FPS and restarts only the webcam (no model reload).</summary>
        public void ReloadCamera()
        {
            Config.CameraWidth = BasisMediaPipeSettings.ResolutionWidth.RawValue;
            Config.CameraHeight = BasisMediaPipeSettings.ResolutionHeight.RawValue;
            Config.TargetFps = BasisMediaPipeSettings.CameraFps.RawValue;
            if (_backend != null)
            {
                _camera.Start(CameraDeviceName, Config.CameraWidth, Config.CameraHeight, Config.TargetFps);
            }
        }
        public static BasisMediaPipeManagement Instance;
        public override void StartSDK()
        {
            Instance = this;

            BasisDeviceManagement.OnBootModeChanged -= HandleBootModeChanged;
            BasisDeviceManagement.OnBootModeChanged += HandleBootModeChanged;

            // Webcam tracking is desktop-only; in VR the real HMD/controllers drive the trackers.
            if (BasisDeviceManagement.StaticCurrentMode != BasisConstants.Desktop)
            {
                BasisDebug.Log("BasisMediaPipe: not in Desktop mode; webcam tracking disabled.");
                return;
            }

            LoadSettingsIntoConfig();

            if (!BasisMediaPipeSettings.Enable.RawValue)
            {
                return;
            }

            _backend = BasisMediaPipeBackendRegistry.Create();
            _backend.Initialize(Config);
            BasisDebug.Log($"BasisMediaPipe: backend = {_backend.BackendName}.");

            if (!_backend.IsAvailable)
            {
                BasisDebug.LogError("BasisMediaPipe: MediaPipe plugin not installed; tracking inert. See package README.");
                return;
            }

            if (!_camera.Start(CameraDeviceName, Config.CameraWidth, Config.CameraHeight, Config.TargetFps))
            {
                BasisDebug.LogError("BasisMediaPipe: failed to start webcam.");
            }

            BasisLocalPlayer.OnLocalAvatarChanged -= HandleAvatarChanged;
            BasisLocalPlayer.OnLocalAvatarChanged += HandleAvatarChanged;
            IsDeviceBooted = true;
        }

        private void HandleAvatarChanged()
        {
            DestroyAllTrackers();
            CacheArmRig();
            _armConverter.Reset();
            _handConverter.Reset();
            _bodyConverter.Reset();
            CalibrateHead();
        }

        /// <summary>
        /// Rebuilds the cached arm rig when the avatar changes. Keyed on the avatar instance so a
        /// mid-session enable (hand tracking is off by default, so it is always switched on after the
        /// avatar has already loaded and OnLocalAvatarChanged has been and gone) still gets a rig.
        /// </summary>
        private bool EnsureArmRig()
        {
            BasisAvatar avatar = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.BasisAvatar : null;
            if (avatar == null)
            {
                _armRigValid = false;
                _armRigAvatar = null;
                return false;
            }
            if (!_armRigValid || _armRigAvatar != avatar)
            {
                CacheArmRig();
            }
            return _armRigValid;
        }

        private void CacheArmRig()
        {
            _armRigValid = false;
            _handRigValid = false;
            BasisAvatar avatar = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.BasisAvatar : null;
            _armRigAvatar = avatar;
            Animator anim = avatar != null ? avatar.Animator : null;
            if (anim == null || !anim.isHuman) return;

            _hipsBone = anim.GetBoneTransform(HumanBodyBones.Hips);
            _headBone = anim.GetBoneTransform(HumanBodyBones.Head);
            _leftUpperArm = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _leftLowerArm = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _leftHandBone = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            _rightUpperArm = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _rightLowerArm = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _rightHandBone = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (_hipsBone == null || _leftUpperArm == null || _leftLowerArm == null || _leftHandBone == null
                || _rightUpperArm == null || _rightLowerArm == null || _rightHandBone == null)
            {
                return;
            }

            Transform root = BasisLocalPlayer.Instance.transform;
            _leftUpperLen = Vector3.Distance(root.InverseTransformPoint(_leftUpperArm.position), root.InverseTransformPoint(_leftLowerArm.position));
            _leftForeLen = Vector3.Distance(root.InverseTransformPoint(_leftLowerArm.position), root.InverseTransformPoint(_leftHandBone.position));
            _rightUpperLen = Vector3.Distance(root.InverseTransformPoint(_rightUpperArm.position), root.InverseTransformPoint(_rightLowerArm.position));
            _rightForeLen = Vector3.Distance(root.InverseTransformPoint(_rightLowerArm.position), root.InverseTransformPoint(_rightHandBone.position));
            _armRigValid = true;

            _leftIndexProximal = anim.GetBoneTransform(HumanBodyBones.LeftIndexProximal);
            _leftMiddleProximal = anim.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            _leftLittleProximal = anim.GetBoneTransform(HumanBodyBones.LeftLittleProximal);
            _rightIndexProximal = anim.GetBoneTransform(HumanBodyBones.RightIndexProximal);
            _rightMiddleProximal = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            _rightLittleProximal = anim.GetBoneTransform(HumanBodyBones.RightLittleProximal);
            _handRigValid = _leftIndexProximal != null && _leftMiddleProximal != null && _leftLittleProximal != null
                && _rightIndexProximal != null && _rightMiddleProximal != null && _rightLittleProximal != null;
        }

        /// <summary>
        /// The constant that maps a MediaPipe palm frame onto the avatar's hand bone rotation. Built from the
        /// knuckles, which stay put relative to the hand when the fingers curl, so it is invariant to the
        /// avatar's current pose and does not need a reference pose to capture.
        /// </summary>
        private bool TryBuildHandRig(in MediaPipeArmConverter.AvatarArmRig arm, out MediaPipeHandConverter.AvatarHandRig rig)
        {
            rig = default;
            if (!arm.Valid || !_handRigValid || BasisLocalPlayer.Instance == null) return false;

            Transform root = BasisLocalPlayer.Instance.transform;
            if (!TryHandCorrection(root, _leftHandBone, _leftIndexProximal, _leftMiddleProximal, _leftLittleProximal, true, out Quaternion left)
                || !TryHandCorrection(root, _rightHandBone, _rightIndexProximal, _rightMiddleProximal, _rightLittleProximal, false, out Quaternion right))
            {
                return false;
            }

            GetHandIkOffsetInverses(out Quaternion leftIkInv, out Quaternion rightIkInv);

            rig = new MediaPipeHandConverter.AvatarHandRig
            {
                Body = Quaternion.LookRotation(arm.Forward, arm.Up),
                LeftCorrection = left,
                RightCorrection = right,
                LeftIkOffsetInverse = leftIkInv,
                RightIkOffsetInverse = rightIkInv,
                Valid = true,
            };
            return true;
        }

        /// <summary>
        /// The inverses of the IK's own palm->bone hand offsets, so the hand converter can cancel them (see
        /// MediaPipeHandConverter.AvatarHandRig for why they must be cancelled at all).
        ///
        /// Re-read every frame rather than cached: the offsets are (re)written on calibration, recalibration and
        /// avatar swap, and TryBuildHandRig already runs per frame, so this self-heals instead of pinning a stale
        /// offset from whatever avatar happened to be loaded first.
        ///
        /// Falls back to identity whenever the rig is not up yet -- an uncancelled rotation is the pre-existing
        /// behaviour, and the IK is not solving anything at that point anyway.
        /// </summary>
        private static void GetHandIkOffsetInverses(out Quaternion leftInverse, out Quaternion rightInverse)
        {
            leftInverse = Quaternion.identity;
            rightInverse = Quaternion.identity;

            // Read the offsets through BasisLocalRigDriver, which hands them out as plain quaternions.
            //
            // Reaching for `constraint.data` directly does not compile from here: BasisFullBodyIK derives from
            // RigConstraint<,,>, and `data` is declared on that base -- so touching it would force this package to
            // reference Unity.Animation.Rigging just to fetch two quaternions. Fully qualifying the type is not
            // enough; the ASSEMBLY reference is what's missing. The rig driver already lives in the assembly that
            // references Rigging, so it is the right place to expose them.
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            if (player == null || player.LocalRigDriver == null) return;

            leftInverse = InverseOffsetSafe(player.LocalRigDriver.LeftHandIKOffset);
            rightInverse = InverseOffsetSafe(player.LocalRigDriver.RightHandIKOffset);
        }

        /// <summary>
        /// Inverse of a calibration offset that can never produce NaN.
        ///
        /// A default-initialised Quaternion is (0,0,0,0), NOT identity, and Quaternion.Inverse divides by the
        /// squared norm -- inverting a zero quaternion yields NaN. That NaN would ride into the tracker rotation,
        /// through the IK, and into the bone transforms, where in Unity it PERSISTS: a NaN'd transform does not
        /// recover on the next good frame, so the rig simply dies. The offsets are identity-initialised and then
        /// calibrated, but they are written from several places and there is a window where a reader can catch one
        /// before it is valid. Treating an invalid offset as identity (i.e. "no cancellation") costs nothing.
        /// </summary>
        private static Quaternion InverseOffsetSafe(Quaternion offset)
        {
            float sqrNorm = offset.x * offset.x + offset.y * offset.y + offset.z * offset.z + offset.w * offset.w;
            return sqrNorm < 0.5f ? Quaternion.identity : Quaternion.Inverse(offset);
        }

        private static bool TryHandCorrection(Transform root, Transform hand, Transform index, Transform middle,
            Transform little, bool left, out Quaternion correction)
        {
            correction = Quaternion.identity;
            if (hand == null || index == null || middle == null || little == null) return false;

            if (!MediaPipeSpace.TryPalmFrame(
                    root.InverseTransformPoint(hand.position),
                    root.InverseTransformPoint(index.position),
                    root.InverseTransformPoint(middle.position),
                    root.InverseTransformPoint(little.position),
                    left, out Quaternion palm))
            {
                return false;
            }

            Quaternion rest = Quaternion.Inverse(root.rotation) * hand.rotation;
            correction = Quaternion.Inverse(palm) * rest;
            return true;
        }

        private float CameraAspect()
        {
            WebCamTexture texture = _camera.Texture;
            if (texture != null && texture.width > 16 && texture.height > 16)
            {
                return (float)texture.width / texture.height;
            }
            return Config.CameraHeight > 0 ? (float)Config.CameraWidth / Config.CameraHeight : 1f;
        }

        /// <summary>Avatar body axes from the chest (or hips) bone control — a filtered target the arm solver never writes to.</summary>
        private static bool TryTorsoAxes(out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            right = Vector3.right;
            up = Vector3.up;
            forward = Vector3.forward;
            if (!TryBodyDelta(out Quaternion body)) return false;

            right = body * Vector3.right;
            up = body * Vector3.up;
            forward = body * Vector3.forward;
            return true;
        }

        private bool TryShoulderAxes(Vector3 leftAnchor, Vector3 rightAnchor, Transform root,
            out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            right = Vector3.right;
            up = Vector3.up;
            forward = Vector3.forward;

            Vector3 hip = root.InverseTransformPoint(_hipsBone.position);
            Vector3 rawUp = ((leftAnchor + rightAnchor) * 0.5f) - hip;
            Vector3 rawRight = rightAnchor - leftAnchor;
            if (rawUp.sqrMagnitude < 1e-6f || rawRight.sqrMagnitude < 1e-6f) return false;

            up = rawUp.normalized;
            forward = Vector3.Cross(rawRight.normalized, up).normalized;
            right = Vector3.Cross(up, forward).normalized;
            return true;
        }

        private bool TryBuildArmRig(out MediaPipeArmConverter.AvatarArmRig rig)
        {
            rig = default;
            if (!EnsureArmRig() || BasisLocalPlayer.Instance == null) return false;
            if (_hipsBone == null || _leftUpperArm == null || _rightUpperArm == null) return false;

            Transform root = BasisLocalPlayer.Instance.transform;
            Vector3 leftAnchor = root.InverseTransformPoint(_leftUpperArm.position);
            Vector3 rightAnchor = root.InverseTransformPoint(_rightUpperArm.position);

            // Axes come from the torso bone control (a pre-IK target), NOT from the shoulder bones. The rig
            // driver rotates the clavicles, which MOVES the upper-arm bones, so a frame derived from them turns
            // whenever an arm moves — which then drags the OTHER arm's target and both wrist rotations with it.
            // The anchor still rides the live shoulder, because the hand should hang off where the shoulder
            // actually is; only the axes have to be stable.
            if (!TryTorsoAxes(out Vector3 right, out Vector3 up, out Vector3 forward)
                && !TryShoulderAxes(leftAnchor, rightAnchor, root, out right, out up, out forward))
            {
                return false;
            }

            Vector3 shoulderCenter = (leftAnchor + rightAnchor) * 0.5f;
            Vector3 headLocal = _headBone != null ? root.InverseTransformPoint(_headBone.position) : shoulderCenter + up * 0.2f;

            rig = new MediaPipeArmConverter.AvatarArmRig
            {
                LeftAnchor = leftAnchor,
                RightAnchor = rightAnchor,
                LeftUpperLen = _leftUpperLen,
                LeftForeLen = _leftForeLen,
                RightUpperLen = _rightUpperLen,
                RightForeLen = _rightForeLen,
                Right = right,
                Up = up,
                Forward = forward,
                HeadLocal = headLocal,
                HeadMetric = Vector3.Distance(headLocal, shoulderCenter),
                Valid = true,
            };
            return true;
        }

        private void HandleBootModeChanged(string mode)
        {
            if (mode != BasisConstants.Desktop)
            {
                if (_backend != null) StopSDK();
            }
            else if (_backend == null && BasisMediaPipeSettings.Enable.RawValue)
            {
                StartSDK();
            }
        }

        public override void StopSDK()
        {
            BasisLocalPlayer.OnLocalAvatarChanged -= HandleAvatarChanged;
            _camera.Stop();
            DestroyAllTrackers();
            _backend?.Shutdown();
            _backend = null;
            _hasLatest = false;
            IsDeviceBooted = false;
        }

        public override void Simulate()
        {
            if(IsDeviceBooted == false)
            {
                return;
            }
            if (_backend == null || !_backend.IsAvailable)
            {
                return;
            }
            if (_camera.IsReady)
            {
                _backend.SubmitFrame(_camera.Texture, Time.realtimeSinceStartupAsDouble * 1000.0);
            }

            bool isNewSample = false;
            if (_backend.TryGetLatestResult(out BasisMediaPipeResult result))
            {
                TrackSampleRate(result.TimestampMs);
                _latest = result;
                _hasLatest = true;
                isNewSample = true;
            }

            if (_hasLatest)
            {
                ApplyResult(in _latest, new MediaPipeTiming(Time.deltaTime, _sampleDelta, isNewSample));
            }
        }

        // Three models share one worker thread, so what actually comes back is well under the camera's fps and
        // varies with load. Measure it from the result timestamps rather than assuming, because everything
        // downstream filters on this clock — guess it wrong and the hands either stutter or swim.
        private void TrackSampleRate(double timestampMs)
        {
            if (_lastSampleMs > 0.0)
            {
                float delta = (float)((timestampMs - _lastSampleMs) / 1000.0);

                // Accept anything physically possible. The old ceiling was 0.5s, so a pipeline that had actually
                // collapsed to 1-2 Hz had every one of its samples thrown away, and _sampleDelta just sat where
                // it was -- at the 15 Hz it is SEEDED with, if nothing valid ever landed. An instrument that
                // fails toward "healthy" is worse than no instrument: it reports the number you hoped for
                // exactly when it is wrong. And _sampleDelta is not decoration -- it is the clock every
                // converter's one-euro filter tunes its cutoff against, so a stale one mis-smooths the hands too.
                if (delta > 1e-3f && delta < 10f)
                {
                    _sampleDelta = Mathf.Lerp(_sampleDelta, delta, 0.2f);
                    _rateMeasured = true;
                }
            }
            _lastSampleMs = timestampMs;
        }

        private void ApplyResult(in BasisMediaPipeResult result, in MediaPipeTiming timing)
        {
            if (Config.EnableFace && result.HasFace)
            {
                _faceConverter.Apply(in result, BasisLocalPlayer.Instance.BasisAvatar);
            }
            if (Config.EnableHands)
            {
                _handConverter.Apply(in result, in timing);
            }

            if (Config.EnableHeadPosition || Config.EnableHeadRotation)
            {
                if (result.HasFace && BasisLocalBoneDriver.EyeControl != null && _headConverter.TryGetHeadOffset(in result, out Quaternion headOffset, out Vector3 headPositionOffset))
                {
                    BasisLocalBoneControl eye = BasisLocalBoneDriver.EyeControl;
                    BasisLocalBoneControl headControl = BasisLocalBoneDriver.HeadControl;
                    float eyeToHeadY = headControl != null ? headControl.TposeLocalScaled.position.y - eye.TposeLocalScaled.position.y : 0f;

                    Vector3 headPosition = eye.OutGoingData.position;
                    headPosition.y += eyeToHeadY + headPositionOffset.y;
                    if (Config.EnableHeadPosition)
                    {
                        headPosition.x += headPositionOffset.x;
                        headPosition.z += headPositionOffset.z;
                    }
                    Quaternion headRotation = Config.EnableHeadRotation ? eye.OutGoingData.rotation * headOffset : eye.OutGoingData.rotation;
                    WriteTracker(BasisBoneTrackedRole.Head, headPosition, headRotation);
                }
                else
                {
                    RemoveTracker(BasisBoneTrackedRole.Head);
                }
            }
            else
            {
                RemoveTracker(BasisBoneTrackedRole.Head);
            }

            if (Config.EnableHandTracking)
            {
                bool posePath = result.HasPose && _armRigValid;
                if (!_armDiagLogged || posePath != _armPosePathActive)
                {
                    _armDiagLogged = true;
                    _armPosePathActive = posePath;
                    BasisDebug.Log($"BasisMediaPipe arms: HasPose={result.HasPose} armRig={_armRigValid} handRig={_handRigValid}"
                        + $" sidesSwapped={result.PoseSidesSwapped}"
                        + $" visL={MediaPipeSpace.ArmVisibility(result.PoseVisibility, true):F2} visR={MediaPipeSpace.ArmVisibility(result.PoseVisibility, false):F2}"
                        + $" -> {(posePath ? "pose retarget" : "hand fallback")}");
                }
                ApplyHandTracker(in result, true, in timing);
                ApplyHandTracker(in result, false, in timing);
            }
            else
            {
                RemoveTracker(BasisBoneTrackedRole.LeftHand);
                RemoveTracker(BasisBoneTrackedRole.RightHand);
                RemoveTracker(BasisBoneTrackedRole.LeftLowerArm);
                RemoveTracker(BasisBoneTrackedRole.RightLowerArm);
            }

            if (Config.EnableChest)
            {
                if (result.HasPose && _bodyConverter.TryGetTorsoOffset(in result, in timing, out Quaternion torsoOffset)
                    && TryComposeChest(torsoOffset, out Vector3 chestPosition, out Quaternion chestRotation))
                {
                    WriteTracker(BasisBoneTrackedRole.Chest, chestPosition, chestRotation);
                }
                else
                {
                    RemoveTracker(BasisBoneTrackedRole.Chest);
                }

            }
            else
            {
                RemoveTracker(BasisBoneTrackedRole.Chest);
            }
        }


        /// <summary>Torso orientation in player space, as a delta from the avatar's rest pose.</summary>
        private static bool TryBodyDelta(out Quaternion body)
        {
            body = Quaternion.identity;
            BasisLocalBoneControl torso = BasisLocalBoneDriver.ChestControl != null
                ? BasisLocalBoneDriver.ChestControl
                : BasisLocalBoneDriver.HipsControl;
            if (torso == null) return false;

            Quaternion rest = torso.TposeLocal.rotation;
            Quaternion live = torso.OutGoingData.rotation;
            if (!IsUsable(rest) || !IsUsable(live)) return false;

            body = live * Quaternion.Inverse(rest);
            return true;
        }

        /// <summary>
        /// Places the chest by carrying it on the hips and adding the webcam's lean/twist on top. The converter
        /// hands back an OFFSET from neutral; writing that straight onto the tracker (as this used to) pinned the
        /// chest to a fixed rotation in player space, so it stopped turning with the body altogether.
        /// </summary>
        private static bool TryComposeChest(Quaternion torsoOffset, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            BasisLocalBoneControl hips = BasisLocalBoneDriver.HipsControl;
            BasisLocalBoneControl chest = BasisLocalBoneDriver.ChestControl;
            if (hips == null || chest == null) return false;

            Quaternion hipsRest = hips.TposeLocal.rotation;
            Quaternion chestRest = chest.TposeLocal.rotation;
            if (!IsUsable(hipsRest) || !IsUsable(chestRest)) return false;

            Basis.Scripts.Common.BasisCalibratedCoords hipsNow = hips.OutGoingData;
            Quaternion body = hipsNow.rotation * Quaternion.Inverse(hipsRest);

            position = hipsNow.position + body * (chest.TposeLocal.position - hips.TposeLocal.position);
            rotation = body * torsoOffset * chestRest;
            return true;
        }

        /// <summary>
        /// The only door camera data gets through to a tracker, and it is barred to anything non-finite.
        ///
        /// This is not belt-and-braces. Every threshold guard in this package reads `x &lt; epsilon`, and that
        /// comparison is FALSE for NaN — so one NaN landmark walks through all of them untouched, latches into the
        /// one-euro filters (which lerp it forward forever), and finally reaches the Burst IK job, where
        /// `BasisArmBendLookup.SampleTrilinear` turns it into `(int)NaN` == int.MinValue and aborts the process
        /// with no managed stack to read. Dropping the tracker instead costs one frame of arm and is recoverable.
        /// </summary>
        private void WriteTracker(BasisBoneTrackedRole role, Vector3 position, Quaternion rotation)
        {
            if (!IsFinite(position) || !IsFinite(rotation))
            {
                if (!_nonFiniteLogged)
                {
                    _nonFiniteLogged = true;
                    BasisDebug.LogError($"BasisMediaPipe: refused a non-finite {role} target ({position}, {rotation}). "
                        + "Tracking data went bad; the tracker is being dropped rather than handed to the IK. "
                        + "If this repeats, the pose model is emitting NaN.");
                }
                RemoveTracker(role);
                return;
            }
            EnsureTracker(role).FollowMovement.SetLocalPositionAndRotation(position, rotation);
        }

        private bool _nonFiniteLogged;

        private static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

        // Also rejects the zero quaternion: a struct field nobody assigned is (0,0,0,0), which is not a rotation.
        private static bool IsFinite(Quaternion q) =>
            float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w)
            && q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 1e-6f;

        private static bool IsUsable(Quaternion q) =>
            q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f;

        // The pose model always emits all 33 landmarks, extrapolating any limb it cannot actually see, so an arm
        // out of frame or tucked behind the back still yields a confident-looking wrist. Gate on the reported
        // visibility, with hysteresis so an arm hovering at the threshold does not strobe in and out, and drop
        // the tracker entirely when it goes — a released arm falls back to normal IK instead of chasing noise.
        private const float ArmVisibilityGain = 0.7f;
        private const float ArmVisibilityLose = 0.5f;

        // ~200 ms in, ~330 ms out. Fading out slower than in matters: tracking drops out in brief flickers, and a
        // slow release rides straight over them without the arm twitching, while a fast acquire means the hand is
        // there the moment you are.
        private const float HandFadeInHz = 5f;
        private const float HandFadeOutHz = 3f;

        /// <summary>
        /// Eases the hand's IK weight toward tracked/untracked instead of switching it. Returns false once the
        /// hand has faded fully out and is free, meaning the caller should release it altogether.
        ///
        /// A hand that is CARRYING something never fades: the held object is constrained to the hand BONE, so
        /// letting the IK go would fling it to the avatar's rest pose. It stays pinned at full weight, holding its
        /// last good pose, until you let go — at which point it fades out normally.
        /// </summary>
        private bool FadeHandIK(bool left, bool trackable, BasisBoneTrackedRole role, float deltaTime)
        {
            BasisLocalBoneControl control = left
                ? BasisLocalBoneDriver.LeftHandControl
                : BasisLocalBoneDriver.RightHandControl;

            bool holding = HandIsHolding(role);
            float target = trackable || holding ? 1f : 0f;
            float weight = left ? _leftHandIK : _rightHandIK;

            float rate = target > weight ? HandFadeInHz : HandFadeOutHz;
            weight = holding
                ? 1f
                : Mathf.Lerp(weight, target, 1f - Mathf.Exp(-rate * Mathf.Max(deltaTime, 1e-4f)));
            if (weight < 0.001f) weight = 0f;

            if (left) _leftHandIK = weight;
            else _rightHandIK = weight;

            if (control != null) control.RigLayerWeight = weight;
            return weight > 0f;
        }

        /// <summary>Whether this hand is currently gripping an interactable, in which case its IK must stay put.</summary>
        private static bool HandIsHolding(BasisBoneTrackedRole role)
        {
            BasisPlayerInteract interact = BasisPlayerInteract.Instance;
            if (interact == null || interact.InteractInputs == null) return false;

            for (int i = 0; i < interact.InteractInputs.Length; i++)
            {
                BasisInteractInput slot = interact.InteractInputs[i];
                if (slot.input == null || slot.lastTarget == null) continue;
                if (!slot.input.TryGetRole(out BasisBoneTrackedRole slotRole) || slotRole != role) continue;
                if (slot.lastTarget.IsInteractingWith(slot.input)) return true;
            }
            return false;
        }

        private bool ArmIsTrackable(in BasisMediaPipeResult result, bool avatarLeft)
        {
            float arm = MediaPipeSpace.ArmVisibility(result.PoseVisibility, avatarLeft);
            // The body frame is built from BOTH shoulders, so one of them leaving frame rotates the frame that
            // places every target — gate on it too, not just on this arm's own joints.
            float torso = MediaPipeSpace.TorsoVisibility(result.PoseVisibility);
            if (arm < 0f || torso < 0f) return true;

            float visibility = Mathf.Min(arm, torso);
            bool tracked = avatarLeft ? _leftArmTracked : _rightArmTracked;
            tracked = visibility >= (tracked ? ArmVisibilityLose : ArmVisibilityGain);
            if (avatarLeft) _leftArmTracked = tracked;
            else _rightArmTracked = tracked;
            return tracked;
        }

        private void ApplyHandTracker(in BasisMediaPipeResult result, bool left, in MediaPipeTiming timing)
        {
            Vector3[] landmarks = left ? result.LeftHandLandmarks : result.RightHandLandmarks;
            bool handDetected = left ? result.HasLeftHand : result.HasRightHand;
            BasisBoneTrackedRole handRole = left ? BasisBoneTrackedRole.LeftHand : BasisBoneTrackedRole.RightHand;
            BasisBoneTrackedRole elbowRole = left ? BasisBoneTrackedRole.LeftLowerArm : BasisBoneTrackedRole.RightLowerArm;

            bool trackable = !Config.EnablePose || !result.HasPose || ArmIsTrackable(in result, left);
            if (!FadeHandIK(left, trackable, handRole, timing.RenderDelta))
            {
                // Faded all the way out and nothing is being carried: let go entirely, so the arm returns to its
                // normal animated pose instead of hanging on the last landmark we saw.
                RemoveTracker(handRole);
                RemoveTracker(elbowRole);
                return;
            }

            if (!trackable)
            {
                // Mid-fade, or holding something. Leave the tracker exactly where it was: the IK weight is what is
                // moving, so the hand eases out of its last good pose rather than snapping out of it.
                return;
            }

            if (Config.EnablePose && result.HasPose && TryBuildArmRig(out MediaPipeArmConverter.AvatarArmRig rig)
                && _armConverter.TryGetArm(result.PoseWorldLandmarks, in rig, left, in timing,
                    out Vector3 wristLocal, out Vector3 elbowLocal, out Quaternion forearmRotation))
            {
                Quaternion wristRotation = forearmRotation;
                if (TryBuildHandRig(in rig, out MediaPipeHandConverter.AvatarHandRig handRig)
                    && _handConverter.TryGetHandRotation(in result, in handRig, left, in timing, out Quaternion handRotation))
                {
                    wristRotation = handRotation;
                }

                WriteTracker(handRole, wristLocal, wristRotation);

                if (Config.EnableArmElbowPole)
                {
                    WriteTracker(elbowRole, elbowLocal, forearmRotation);
                }
                else
                {
                    RemoveTracker(elbowRole);
                }
                return;
            }

            RemoveTracker(elbowRole);
            if (!handDetected || landmarks == null || landmarks.Length < MediaPipeSpace.HandCount)
            {
                RemoveTracker(handRole);
                return;
            }

            // No body pose: place the wrist from the hand landmarker, anchored to the head (face) when present.
            float faceSize = result.HasFace ? result.FaceImageSize : 0f;
            if (TryBuildArmRig(out MediaPipeArmConverter.AvatarArmRig handOnlyRig)
                && _armConverter.TryGetArmFromHand(landmarks[MediaPipeSpace.HandWrist], result.HeadImagePosition,
                    faceSize, CameraAspect(), in handOnlyRig, left, in timing, out Vector3 handWrist, out Quaternion handWristRotation))
            {
                WriteTracker(handRole, handWrist, handWristRotation);
                return;
            }

            RemoveTracker(handRole);
        }

        public void CalibrateHead()
        {
            if (!_hasLatest)
            {
                return;
            }

            _headConverter.Calibrate(_latest);
            _bodyConverter.Calibrate(_latest);
        }

        public string DiagnosticsText()
        {
            if (_backend == null)
            {
                return "Not running.";
            }

            string status = $"Backend: {_backend.BackendName}\nAvailable: {_backend.IsAvailable}\nCamera: {(_camera.IsReady ? "ready" : "not ready")}";
            if (_hasLatest)
            {
                status += $"\nFace: {_latest.HasFace}   L-Hand: {_latest.HasLeftHand}   R-Hand: {_latest.HasRightHand}   Pose: {_latest.HasPose}";
                status += $"\nArm rig: {(_armRigValid ? "ready" : "unavailable")}";
                status += _rateMeasured
                    ? $"\nTracking: {1f / Mathf.Max(_sampleDelta, 1e-4f):F1} Hz (camera set to {Config.TargetFps})"
                    : $"\nTracking: not measured yet (camera set to {Config.TargetFps})";

                // Where the milliseconds actually go. The stages are serial, so the biggest one IS the bottleneck.
                string breakdown = _backend.TimingBreakdown();
                if (!string.IsNullOrEmpty(breakdown))
                {
                    status += $"\n  {breakdown}";
                }
            }
            else
            {
                status += "\n(no result yet)";
            }
            return status;
        }

        private void RemoveTracker(BasisBoneTrackedRole role)
        {
            if (role == BasisBoneTrackedRole.LeftHand) _leftHandIK = 0f;
            else if (role == BasisBoneTrackedRole.RightHand) _rightHandIK = 0f;

            if (!_activeTrackers.Remove(role))
            {
                return;
            }
            if (_trackers.TryGetValue(role, out BasisInputXRSimulate input) && input != null)
            {
                input.UnAssignTracker();
            }
        }

        private BasisInputXRSimulate EnsureTracker(BasisBoneTrackedRole role)
        {
            if (_trackers.TryGetValue(role, out BasisInputXRSimulate existing) && existing != null)
            {
                if (_activeTrackers.Add(role))
                {
                    existing.AssignRoleAndTracker(role);
                }
                return existing;
            }

            RegisterDeviceMatch();

            string id = $"{SubSystem}:{role}";
            GameObject go = new GameObject(id)
            {
                transform =
                {
                    parent = BasisLocalPlayer.Instance.transform
                }
            };
            Transform move = new GameObject($"{id} move").transform;
            move.parent = BasisLocalPlayer.Instance.transform;

            BasisInputXRSimulate input = go.AddComponent<BasisInputXRSimulate>();
            input.IsCameraTracked = true;
            input.TrackingHardware = BasisTrackingHardware.Optical;
            input.FollowMovement = move;
            input.InitializeTracking(id, SubSystem, SubSystem, false, role);
            input.AssignRoleAndTracker(role);

            BasisDeviceManagement.Instance.TryAdd(input);

            _trackers[role] = input;
            _activeTrackers.Add(role);
            return input;
        }

        // Declare our virtual devices to the matcher with raycast OFF, so InitializeTracking
        // resolves these settings instead of generating a raycast-enabled fallback (the default
        // for a forced non-CenterEye role). These are pose trackers, not UI pointers.
        private static bool _deviceMatchRegistered;

        private static void RegisterDeviceMatch()
        {
            if (_deviceMatchRegistered) return;

            BasisDeviceManagement dm = BasisDeviceManagement.Instance;
            if (dm == null || dm.BasisDeviceNameMatcher == null)
            {
                return;
            }

            dm.BasisDeviceNameMatcher.BasisDevice.Add(new DeviceSupportInformation
            {
                DeviceID = SubSystem,
                matchableDeviceIds = new[] { SubSystem },
                HasRayCastSupport = false,
                HasRayCastVisual = false,
                HasRayCastRadical = false,
            });
            _deviceMatchRegistered = true;
        }

        private void DestroyAllTrackers()
        {
            foreach (KeyValuePair<BasisBoneTrackedRole, BasisInputXRSimulate> kvp in _trackers)
            {
                if (kvp.Value != null)
                {
                    BasisDeviceManagement.Instance.RemoveDevicesFrom(SubSystem, $"{SubSystem}:{kvp.Key}");
                }
            }
            _trackers.Clear();
            _activeTrackers.Clear();
        }
    }
}
