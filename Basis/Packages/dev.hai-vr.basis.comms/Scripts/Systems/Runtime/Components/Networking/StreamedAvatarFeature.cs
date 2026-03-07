using System;
using System.Collections.Generic;
using Basis.Network.Core;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Internal/Streamed Avatar Feature")]
    public class StreamedAvatarFeature : MonoBehaviour
    {
        private const bool PrioritizeLargeChanges = true;

        private const int HeaderBytes = 3;
        // 1/60 makes for a maximum encoded delta time of 4.25 seconds.
        private const float DeltaLocalIntToSeconds = 1 / 60f;
        private const float DeltaTimeUsedForResyncs = 1 / 29f; // 29 is just a random number I picked. It really doesn't matter what value we're using for resyncs.
        // We use 254, not 255 (leaving 1 value out), because 254 divided by 2 is a round number, 127.
        // This makes the value of 0 in range [-1:1] encodable as 127.
        private const float EncodingRange = 254f;

        public DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable;
        private const float TransmissionDeltaSeconds = 0.1f;

        [NonSerialized] public byte valueArraySize = 8; // Must not change after first enabled.
        [NonSerialized] public IHVRTransmitter transmitter;
        [NonSerialized] public bool isWearer;
        [NonSerialized] public byte localIdentifier;

        private readonly Queue<StreamedAvatarFeaturePayload> _queue = new Queue<StreamedAvatarFeaturePayload>();
        private float[] current;
        private float[] previous;
        private float[] target;
        private float _deltaTime;
        private float _timeLeft;
        private bool _isOutOfTape;
        private bool _writtenThisFrame;

        public event InterpolatedDataChanged OnInterpolatedDataChanged;
        public delegate void InterpolatedDataChanged(float[] current);

        private void Awake()
        {
            EnsureBuffers();
        }

        private void OnDisable()
        {
            _writtenThisFrame = false;
        }

        public void Store(int index, float value)
        {
            EnsureBuffers();
            current[index] = value;
            if (PrioritizeLargeChanges && isWearer)
            {
                // When prioritizing large changes, we want to put an emphasis on values that are further away from the previous value.
                // We use the "target" array on the sender to store the furthest value,
                // and the "previous" array on the sender to store the last value we sent for networking.
                var previousValue = previous[index];
                if (Mathf.Abs(value - previousValue) > Mathf.Abs(target[index] - previousValue))
                {
                    target[index] = value;
                }
            }
        }

        public void InitializeNormalizedValues(IReadOnlyList<float> normalizedValues)
        {
            EnsureBuffers();

            int count = Mathf.Min(valueArraySize, normalizedValues?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                float clamped = Mathf.Clamp01(normalizedValues[i]);
                current[i] = clamped;
                previous[i] = clamped;
                target[i] = clamped;
            }
        }

        /// Exposed for testing purposes.
        public void QueueEvent(StreamedAvatarFeaturePayload message)
        {
            _queue.Enqueue(message);
        }

        private void Update()
        {
            if (isWearer)
            {
                OnSender();
            }
            else
            {
                OnReceiver();
            }
        }

        private void OnSender()
        {
            _timeLeft += Time.deltaTime;

            if (_timeLeft > TransmissionDeltaSeconds)
            {
                var toSend = new StreamedAvatarFeaturePayload
                {
                    DeltaTime = _timeLeft,
                    FloatValues = PrioritizeLargeChanges ? target : current // Not copied: Process this message immediately
                };
                EncodeAndSubmit(toSend, null);
                if (PrioritizeLargeChanges)
                {
                    // Order matters: Modify target after EncodeAndSubmit() executes.
                    for (var i = 0; i < current.Length; i++)
                    {
                        previous[i] = target[i];
                        target[i] = current[i];
                    }
                }

                _timeLeft = 0;
            }
        }

        private void OnReceiver()
        {
            var timePassed = Time.deltaTime;
            _timeLeft -= timePassed;

            float totalQueueSeconds = 0;
            foreach (StreamedAvatarFeaturePayload payload in _queue)
            {
                totalQueueSeconds += payload.DeltaTime;
            }
            // Debug.Log($"Queue time is {totalQueueSeconds} seconds, size is {_queue.Count}");

            while (_timeLeft <= 0 && _queue.TryDequeue(out var eval))
            {
                // Debug.Log($"Unpacking delta {eval.DeltaTime} as {string.Join(',', eval.FloatValues.Select(f => $"{f}"))}");
                var effectiveDeltaTime = _queue.Count <= 5 || totalQueueSeconds < 0.2f
                    ? eval.DeltaTime
                    : (eval.DeltaTime * Mathf.Lerp(0.66f, 0.05f, Mathf.InverseLerp(DeltaTimeUsedForResyncs, totalQueueSeconds, 4f)));

                _timeLeft += effectiveDeltaTime;
                previous = target;
                target = eval.FloatValues;
                _deltaTime = effectiveDeltaTime;
            }

            if (_timeLeft <= 0)
            {
                if (!_isOutOfTape)
                {
                    _writtenThisFrame = true;
                    for (var i = 0; i < valueArraySize; i++)
                    {
                        current[i] = target[i];
                    }

                    _isOutOfTape = true;
                }
                else
                {
                    _writtenThisFrame = false;
                }
                _timeLeft = 0;
            }
            else
            {
                _writtenThisFrame = true;
                var progression01 = 1 - Mathf.Clamp01(_timeLeft / _deltaTime);
                for (var i = 0; i < valueArraySize; i++)
                {
                    current[i] = Mathf.Lerp(previous[i], target[i], progression01);
                }
                _isOutOfTape = false;
            }

            if (_writtenThisFrame)
            {
                OnInterpolatedDataChanged?.Invoke(current);
            }
        }

        #region Network Payload

        public void OnPacketReceived(ArraySegment<byte> subBuffer)
        {
            // FIXME: there's something I fundamentally don't get, this code doesn't work if the following line isn't commended out
            // if (!isActiveAndEnabled) return;

            if (TryDecode(subBuffer, out var result))
            {
                _queue.Enqueue(result);
            }
        }

        // Header:
        // - Scoped Index (1 byte)
        // - Sub-header:
        //   - Delta Time (1 byte)
        //   - Float Values (valueArraySize bytes)

        private void EncodeAndSubmit(StreamedAvatarFeaturePayload message, ushort[] recipientsNullable)
        {
            var buffer = new byte[HeaderBytes + valueArraySize];//3 + 256 = 259
            buffer[0] = AvatarMessageProcessing.NewNet_WearerData;
            buffer[1] = localIdentifier;
            buffer[2] = (byte)(message.DeltaTime / DeltaLocalIntToSeconds);

            for (var i = 0; i < current.Length; i++)
            {
                buffer[HeaderBytes + i] = (byte)(message.FloatValues[i] * EncodingRange);
            }
            if (recipientsNullable == null || recipientsNullable.Length == 0)
            {
                transmitter.ServerReductionSystemMessageSend(buffer);
            }
            else
            {
                transmitter.NetworkMessageSend(buffer, DeliveryMethod, recipientsNullable);
            }
        }

        private bool TryDecode(ArraySegment<byte> subBuffer, out StreamedAvatarFeaturePayload result)
        {
            const int dataStart = 1;

            if (subBuffer.Count != dataStart + valueArraySize)
            {
                result = default;
                return false;
            }

            var buffer = subBuffer.Array;
            var offset = subBuffer.Offset;

            // First byte: delta time
            var deltaTimeInFractions = buffer[offset];

            // Only allocate what you actually need
            var floatValues = new float[valueArraySize];

            for (var i = 0; i < valueArraySize; i++)
            {
                floatValues[i] = buffer[offset + dataStart + i] / EncodingRange;
            }

            result = new StreamedAvatarFeaturePayload
            {
                DeltaTime = deltaTimeInFractions * DeltaLocalIntToSeconds,
                FloatValues = floatValues
            };

            return true;
        }
        #endregion

        public void OnResyncEveryoneRequested()
        {
            EncodeAndSubmit(new StreamedAvatarFeaturePayload
            {
                DeltaTime = DeltaTimeUsedForResyncs,
                FloatValues = current
            }, null);
        }

        public void OnResyncRequested(ushort[] whoAsked)
        {
            EncodeAndSubmit(new StreamedAvatarFeaturePayload
            {
                DeltaTime = DeltaTimeUsedForResyncs,
                FloatValues = current
            }, whoAsked);
        }

        private void EnsureBuffers()
        {
            previous ??= new float[valueArraySize];
            target ??= new float[valueArraySize];
            current ??= new float[valueArraySize];
        }
    }
    public class StreamedAvatarFeaturePayload
    {
        public float DeltaTime;
        public float[] FloatValues;
    }
}
