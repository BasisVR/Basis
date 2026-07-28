using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Owns the persistent GPU buffers and asynchronous readback state used by the
    /// depth-buffer animation visibility mode. Capacity grows on demand while one readback
    /// remains in flight at a time. If a larger allocation is unavailable, excess candidates
    /// rotate through later batches and retain the physics fallback meanwhile.
    /// </summary>
    internal static class BasisAnimatedImageDepthVisibility
    {
        internal const int SamplesPerCard = 5;
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Rendering;
        private const int InitialCardCapacity =
            BasisImagePickupSettings.MaxConcurrentImagesPerSender;
        private const int SampleStrideBytes = sizeof(float) * 4;
        private const int VisibilityStrideBytes = sizeof(uint);
        private const float CapacityRetryDelaySeconds = 5f;

        private static readonly ProfilerMarker PrepareMarker = new("Basis.ImagePickup.AnimatedImage.DepthPrepare");
        private static readonly ProfilerMarker ReadbackMarker = new("Basis.ImagePickup.AnimatedImage.DepthReadback");

        private static Vector4[] _sampleUpload = Array.Empty<Vector4>();
        private static BasisAnimatedImagePlayer[] _preparedPlayers =
            Array.Empty<BasisAnimatedImagePlayer>();
        private static BasisAnimatedImagePlayer[] _readbackPlayers =
            Array.Empty<BasisAnimatedImagePlayer>();
        private static readonly Plane[] _frustumPlanes = new Plane[6];

        private static GraphicsBuffer _sampleBuffer;
        private static GraphicsBuffer _visibilityBuffer;
        private static int _cardCapacity;
        private static int _lastFailedCapacity;
        private static float _nextCapacityRetryTime;
        private static Camera _preparedCamera;
        private static Camera _readbackCamera;
        private static int _preparedFrame = -1;
        private static int _readbackFrame = -1;
        private static int _preparedCount;
        private static int _readbackCount;
        private static int _readbackGeneration;
        private static int _readbackInFlightGeneration;
        private static float _nextPrepareTime;
        private static int _candidateStartIndex;
        private static bool _readbackPending;
        private static bool _readbackInFlight;
        private static bool _fallbackWarningLogged;
        private static bool _successfulReadbackLogged;
        private static bool _readbackErrorLogged;
        private static bool _active;

        internal static int PreparedCount => _preparedCount;
        internal static bool IsActive => _active;

        internal static void Initialize()
        {
            if (_active)
                return;
            _active = true;
            TryEnsureCapacity(1, Time.unscaledTime);
        }

        internal static void PrepareFrame(List<BasisAnimatedImagePlayer> players, Camera camera, float unscaledTime)
        {
            using var scope = PrepareMarker.Auto();

            if (!CanUseDepthCamera(camera))
            {
                InvalidateReadback();
                ResetPreparedPlayers();
                ResetResults(players);
                return;
            }

            if (_readbackInFlight || unscaledTime < _nextPrepareTime)
                return;
            _nextPrepareTime =
                unscaledTime
                + BasisImagePickupSettings.AnimationFaceOcclusionCheckIntervalSeconds;

            for (int i = 0; i < _preparedCount; i++)
                _preparedPlayers[i] = null;

            GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);
            camera.transform.GetPositionAndRotation(out Vector3 cameraPosition, out Quaternion cameraRotation);
            Vector3 cameraForward = cameraRotation * Vector3.forward;
            int candidateCount = 0;
            int playerCount = players?.Count ?? 0;
            int normalizedStartIndex =
                playerCount > 0 ? _candidateStartIndex % playerCount : 0;
            int nextStartIndex = normalizedStartIndex;
            bool capacityExceeded = false;

            for (int offset = 0; offset < playerCount; offset++)
            {
                int playerIndex = (normalizedStartIndex + offset) % playerCount;
                BasisAnimatedImagePlayer player = players[playerIndex];
                if (player == null || !player.IsInitialized)
                    continue;

                BasisImagePickupObject pickup = player.Pickup;
                Vector3 faceCenter = default;
                Vector3 frontNormal = default;
                if (pickup != null)
                    pickup.GetFrontFacePose(out faceCenter, out frontNormal);
                if (
                    !IsMainCameraCandidate(
                        player,
                        pickup,
                        camera,
                        _frustumPlanes,
                        faceCenter,
                        frontNormal,
                        cameraPosition,
                        cameraForward
                    )
                )
                {
                    player.ResetFaceVisibility();
                    player.SetDepthVisibility(false, unscaledTime, false);
                    continue;
                }

                if (candidateCount >= _cardCapacity && !TryEnsureCapacity(candidateCount + 1, unscaledTime))
                {
                    capacityExceeded = true;
                    player.ResetDepthVisibility();
                    continue;
                }

                if (!player.DepthVisibilityFromGpu)
                    player.ResetDepthVisibility();

                _preparedPlayers[candidateCount] = player;
                int sampleOffset = candidateCount * SamplesPerCard;
                for (int sampleIndex = 0; sampleIndex < SamplesPerCard; sampleIndex++)
                {
                    Vector3 sample = pickup.GetFrontFaceOcclusionSample(sampleIndex, frontNormal);
                    _sampleUpload[sampleOffset + sampleIndex] = new Vector4(sample.x, sample.y, sample.z, 1f);
                }
                candidateCount++;
                nextStartIndex = (playerIndex + 1) % playerCount;
            }

            _candidateStartIndex =
                playerCount <= 0 ? 0
                : capacityExceeded ? nextStartIndex
                : (normalizedStartIndex + 1) % playerCount;
            if (capacityExceeded && !_fallbackWarningLogged)
            {
                _fallbackWarningLogged = true;
                BasisDebug.LogWarning(
                    $"Image pickup depth visibility is limited to the current {_cardCapacity:N0}-card "
                        + "GPU capacity; additional cards rotate through later batches and use "
                        + "physics fallback meanwhile.",
                    LogTag
                );
            }

            _preparedCamera = camera;
            _preparedFrame = Time.frameCount;
            _preparedCount = candidateCount;

            if (candidateCount <= 0)
                return;

            if (!TryEnsureCapacity(candidateCount, unscaledTime))
            {
                ResetPreparedPlayers();
                return;
            }

            try
            {
                _sampleBuffer.SetData(_sampleUpload, 0, 0, candidateCount * SamplesPerCard);
            }
            catch (Exception exception)
            {
                HandleBufferFailure("uploading depth visibility samples", exception, unscaledTime);
                ResetPreparedPlayers();
            }
        }

        internal static bool TryBeginDispatch(
            Camera camera,
            out GraphicsBuffer sampleBuffer,
            out GraphicsBuffer visibilityBuffer,
            out int cardCount,
            out Action<AsyncGPUReadbackRequest> readbackCallback
        )
        {
            sampleBuffer = null;
            visibilityBuffer = null;
            cardCount = 0;
            readbackCallback = null;

            if (
                !_active
                || _readbackInFlight
                || _preparedCount <= 0
                || _preparedFrame != Time.frameCount
                || !ReferenceEquals(camera, _preparedCamera)
                || _sampleBuffer == null
                || _visibilityBuffer == null
            )
            {
                return false;
            }

            _readbackCount = _preparedCount;
            for (int i = 0; i < _readbackCount; i++)
            {
                _readbackPlayers[i] = _preparedPlayers[i];
                _preparedPlayers[i] = null;
            }

            _readbackCamera = camera;
            _readbackFrame = Time.frameCount;
            int generation = ++_readbackGeneration;
            _readbackInFlightGeneration = generation;
            _readbackPending = true;
            _readbackInFlight = true;
            _preparedCount = 0;
            _preparedFrame = -1;
            _preparedCamera = null;
            sampleBuffer = _sampleBuffer;
            visibilityBuffer = _visibilityBuffer;
            cardCount = _readbackCount;
            Camera capturedCamera = _readbackCamera;
            int capturedFrame = _readbackFrame;
            readbackCallback = request =>
                CompleteReadback(request, generation, capturedCamera, capturedFrame);
            return true;
        }

        internal static bool CanUseDepthCamera(Camera camera)
        {
            return camera != null
                && camera.isActiveAndEnabled
                && camera.cameraType == CameraType.Game
                && !camera.orthographic
                && !camera.stereoEnabled
                && SystemInfo.supportsComputeShaders
                && SystemInfo.supportsAsyncGPUReadback;
        }

        internal static bool IsMainCameraCandidate(
            BasisAnimatedImagePlayer player,
            BasisImagePickupObject pickup,
            Camera camera,
            Plane[] frustumPlanes,
            Vector3 faceCenter,
            Vector3 frontNormal,
            Vector3 cameraPosition,
            Vector3 cameraForward
        )
        {
            if (
                player == null
                || player.IsHidden
                || pickup == null
                || !pickup.HasFrontRenderer
                || camera == null
                || frustumPlanes == null
                || frustumPlanes.Length < 6
            )
            {
                return false;
            }

            return BasisImagePickupManager.IsCpuFrontFacingCandidate(
                pickup.FrontRendererLayer,
                pickup.FrontRendererBounds,
                frustumPlanes,
                frontNormal,
                faceCenter,
                cameraPosition,
                cameraForward,
                false,
                camera.cullingMask
            );
        }

        private static void CompleteReadback(AsyncGPUReadbackRequest request, int generation, Camera camera, int frame)
        {
            using var scope = ReadbackMarker.Auto();
            if (!_readbackInFlight || generation != _readbackInFlightGeneration)
            {
                return;
            }

            _readbackInFlight = false;
            _readbackInFlightGeneration = 0;
            if (
                !_readbackPending
                || generation != _readbackGeneration
                || frame != _readbackFrame
                || !ReferenceEquals(camera, _readbackCamera)
                || !CanUseDepthCamera(camera)
            )
            {
                ClearReadbackCapture(true);
                return;
            }

            bool acceptResult = !request.hasError;
            if (acceptResult)
            {
                var data = request.GetData<uint>();
                acceptResult = data.Length >= _readbackCount;
                if (acceptResult)
                {
                    if (!_successfulReadbackLogged)
                    {
                        _successfulReadbackLogged = true;
                        BasisDebug.Log(
                            "Image pickup depth-buffer visibility is active and returning GPU results.",
                            LogTag
                        );
                    }

                    float unscaledTime = Time.unscaledTime;
                    for (int i = 0; i < _readbackCount; i++)
                    {
                        BasisAnimatedImagePlayer player = _readbackPlayers[i];
                        if (player != null)
                        {
                            player.ResetFaceVisibility();
                            player.SetDepthVisibility(data[i] != 0u, unscaledTime);
                        }
                    }
                }
            }

            if (!acceptResult)
            {
                if (request.hasError && !_readbackErrorLogged)
                {
                    _readbackErrorLogged = true;
                    BasisDebug.LogWarning(
                        "Image pickup depth visibility readback failed; using the physics fallback.",
                        LogTag
                    );
                }

                for (int i = 0; i < _readbackCount; i++)
                    _readbackPlayers[i]?.ResetDepthVisibility();
            }

            ClearReadbackCapture(false);
        }

        private static void InvalidateReadback()
        {
            if (_readbackPending)
                _readbackGeneration++;
            ClearReadbackCapture(true);
        }

        private static void ClearReadbackCapture(bool resetPlayers)
        {
            for (int i = 0; i < _readbackCount; i++)
            {
                if (resetPlayers)
                    _readbackPlayers[i]?.ResetDepthVisibility();
                _readbackPlayers[i] = null;
            }

            _readbackCount = 0;
            _readbackPending = false;
            _readbackCamera = null;
            _readbackFrame = -1;
        }

        private static bool TryEnsureCapacity(int minimumRequired, float unscaledTime)
        {
            if (minimumRequired <= 0)
                return true;
            if (_cardCapacity >= minimumRequired && _sampleBuffer != null && _visibilityBuffer != null)
            {
                return true;
            }
            if (unscaledTime < _nextCapacityRetryTime)
                return false;

            // Unity exposes the hard per-buffer limit, not currently free VRAM. Keep the
            // existing buffers alive while probing the larger allocation so failure leaves
            // the current GPU batch capacity usable.
            int maximumCapacity = CalculateMaximumCardCapacity(SystemInfo.maxGraphicsBufferSize);
            int targetCapacity = CalculateGrowthCapacity(_cardCapacity, minimumRequired, maximumCapacity);
            if (targetCapacity < minimumRequired)
            {
                LogCapacityFailure(
                    minimumRequired,
                    $"the platform permits at most {maximumCapacity:N0} cards per buffer",
                    unscaledTime
                );
                return false;
            }

            GraphicsBuffer newSampleBuffer = null;
            GraphicsBuffer newVisibilityBuffer = null;
            try
            {
                int sampleCount = checked(targetCapacity * SamplesPerCard);
                var newSampleUpload = new Vector4[sampleCount];
                var newPreparedPlayers = new BasisAnimatedImagePlayer[targetCapacity];
                var newReadbackPlayers = new BasisAnimatedImagePlayer[targetCapacity];

                Array.Copy(_sampleUpload, newSampleUpload, Math.Min(_sampleUpload.Length, newSampleUpload.Length));
                Array.Copy(
                    _preparedPlayers,
                    newPreparedPlayers,
                    Math.Min(_preparedPlayers.Length, newPreparedPlayers.Length)
                );
                Array.Copy(
                    _readbackPlayers,
                    newReadbackPlayers,
                    Math.Min(_readbackPlayers.Length, newReadbackPlayers.Length)
                );

                newSampleBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sampleCount, SampleStrideBytes);
                newVisibilityBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    targetCapacity,
                    VisibilityStrideBytes
                );

                int previousCapacity = _cardCapacity;
                ReleaseGpuBuffers();
                _sampleBuffer = newSampleBuffer;
                _visibilityBuffer = newVisibilityBuffer;
                newSampleBuffer = null;
                newVisibilityBuffer = null;
                _sampleUpload = newSampleUpload;
                _preparedPlayers = newPreparedPlayers;
                _readbackPlayers = newReadbackPlayers;
                _cardCapacity = targetCapacity;
                _lastFailedCapacity = 0;
                _nextCapacityRetryTime = 0f;
                _fallbackWarningLogged = false;

                if (targetCapacity > previousCapacity && previousCapacity > 0)
                {
                    BasisDebug.Log(
                        $"Image pickup depth visibility GPU capacity grew from "
                            + $"{previousCapacity:N0} to {targetCapacity:N0} cards.",
                        LogTag
                    );
                }
                return true;
            }
            catch (Exception exception)
            {
                newSampleBuffer?.Dispose();
                newVisibilityBuffer?.Dispose();
                LogCapacityFailure(targetCapacity, exception.GetBaseException().Message, unscaledTime);
                return false;
            }
        }

        internal static int CalculateMaximumCardCapacity(long maximumBufferBytes)
        {
            if (maximumBufferBytes <= 0)
                return int.MaxValue / SamplesPerCard;

            long maximumBySamples =
                maximumBufferBytes / (SamplesPerCard * (long)SampleStrideBytes);
            long maximumByVisibility = maximumBufferBytes / VisibilityStrideBytes;
            long maximumByElementCount = int.MaxValue / SamplesPerCard;
            return (int)
                Math.Max(0, Math.Min(maximumByElementCount, Math.Min(maximumBySamples, maximumByVisibility)));
        }

        internal static int CalculateGrowthCapacity(int currentCapacity, int minimumRequired, int maximumCapacity)
        {
            if (minimumRequired <= currentCapacity)
                return currentCapacity;
            if (maximumCapacity < minimumRequired)
                return currentCapacity;

            long doubled =
                currentCapacity > 0 ? (long)currentCapacity * 2L : InitialCardCapacity;
            long target = Math.Max(minimumRequired, doubled);
            return (int)Math.Min(target, maximumCapacity);
        }

        private static void LogCapacityFailure(int requestedCapacity, string reason, float unscaledTime)
        {
            _nextCapacityRetryTime = unscaledTime + CapacityRetryDelaySeconds;
            if (_lastFailedCapacity == requestedCapacity)
                return;

            _lastFailedCapacity = requestedCapacity;
            BasisDebug.LogWarning(
                $"Image pickup depth visibility could not grow its GPU capacity to "
                    + $"{requestedCapacity:N0} cards ({reason}). The existing "
                    + $"{_cardCapacity:N0}-card capacity and physics fallback remain active.",
                LogTag
            );
        }

        private static void HandleBufferFailure(string operation, Exception exception, float unscaledTime)
        {
            ReleaseGpuBuffers();
            _nextCapacityRetryTime = unscaledTime + CapacityRetryDelaySeconds;
            BasisDebug.LogWarning(
                $"Image pickup depth visibility failed while {operation} "
                    + $"({exception.GetBaseException().Message}); using physics fallback "
                    + "until GPU buffers can be recreated.",
                LogTag
            );
        }

        private static void ResetPreparedPlayers()
        {
            for (int i = 0; i < _preparedCount; i++)
            {
                _preparedPlayers[i]?.ResetDepthVisibility();
                _preparedPlayers[i] = null;
            }
            _preparedCount = 0;
            _preparedFrame = -1;
            _preparedCamera = null;
        }

        private static void ReleaseGpuBuffers()
        {
            _sampleBuffer?.Dispose();
            _sampleBuffer = null;
            _visibilityBuffer?.Dispose();
            _visibilityBuffer = null;
        }

        private static void ResetResults(List<BasisAnimatedImagePlayer> players)
        {
            int count = players?.Count ?? 0;
            for (int i = 0; i < count; i++)
                players[i]?.ResetDepthVisibility();
        }

        internal static void Shutdown()
        {
            if (!_active)
                return;
            _active = false;

            InvalidateReadback();
            _readbackInFlight = false;
            _readbackInFlightGeneration = 0;
            ResetPreparedPlayers();
            ReleaseGpuBuffers();

            int preparedPlayerCount = _preparedPlayers.Length;
            for (int i = 0; i < preparedPlayerCount; i++)
            {
                _preparedPlayers[i] = null;
                _readbackPlayers[i] = null;
            }

            _sampleUpload = Array.Empty<Vector4>();
            _preparedPlayers = Array.Empty<BasisAnimatedImagePlayer>();
            _readbackPlayers = Array.Empty<BasisAnimatedImagePlayer>();
            _cardCapacity = 0;
            _lastFailedCapacity = 0;
            _nextCapacityRetryTime = 0f;
            _nextPrepareTime = 0f;
            _candidateStartIndex = 0;
            _fallbackWarningLogged = false;
            _successfulReadbackLogged = false;
            _readbackErrorLogged = false;
        }
    }
}
