using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Contacts
{
    public enum BasisContactShape : byte
    {
        Sphere = 0,
        Capsule = 1,
    }

    public enum BasisContactReceiverMode : byte
    {
        Constant = 0,
        Proximity = 1,
        OnEnter = 2,
    }

    public static class BasisContactTags
    {
        public const string Head = "Head";
        public const string Torso = "Torso";
        public const string Hand = "Hand";
        public const string HandLeft = "HandLeft";
        public const string HandRight = "HandRight";
        public const string Finger = "Finger";
        public const string FingerLeft = "FingerLeft";
        public const string FingerRight = "FingerRight";
        public const string Foot = "Foot";
        public const string FootLeft = "FootLeft";
        public const string FootRight = "FootRight";
    }

    [AutoStaticsCleanup]
    public static partial class BasisContacts
    {
        public const int MaxSenders = 4096;
        public const int MaxReceivers = 1024;
        public const int MaxTagsPerComponent = 16;
        public const float MaxRadius = 5f;
        public const float MaxHeight = 10f;
        public const int NoPlayer = -1;
        public static readonly List<BasisContactSender> Senders = new List<BasisContactSender>();
        public static readonly List<BasisContactReceiver> Receivers = new List<BasisContactReceiver>();
        public static int Version;
        public static event Action<BasisContactReceiver> ReceiverChanged;

        internal static bool Register(BasisContactSender sender)
        {
            if (Senders.Contains(sender))
            {
                return true;
            }
            if (Senders.Count >= MaxSenders)
            {
                BasisDebug.LogWarning($"Contact senders are capped at {MaxSenders}; ignoring {sender.name}.");
                return false;
            }
            Senders.Add(sender);
            Version++;
            return true;
        }

        internal static void Unregister(BasisContactSender sender)
        {
            if (Senders.Remove(sender))
            {
                Version++;
            }
        }

        internal static bool Register(BasisContactReceiver receiver)
        {
            if (Receivers.Contains(receiver))
            {
                return true;
            }
            if (Receivers.Count >= MaxReceivers)
            {
                BasisDebug.LogWarning($"Contact receivers are capped at {MaxReceivers}; ignoring {receiver.name}.");
                return false;
            }
            Receivers.Add(receiver);
            Version++;
            return true;
        }

        internal static void Unregister(BasisContactReceiver receiver)
        {
            if (Receivers.Remove(receiver))
            {
                Version++;
            }
        }

        internal static void RaiseReceiverChanged(BasisContactReceiver receiver)
        {
            if (ReceiverChanged == null)
            {
                return;
            }
            try
            {
                ReceiverChanged(receiver);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Contact receiver listener threw: {e}");
            }
        }

        public static void Clear()
        {
            Senders.Clear();
            Receivers.Clear();
            Version++;
        }

        public static void GetSegment(Transform transform, BasisContactShape shape, Vector3 center, float radius, float height, out Vector3 a, out Vector3 b, out float worldRadius)
        {
            Vector3 scale = transform.lossyScale;
            float uniform = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            worldRadius = Mathf.Clamp(radius, 0f, MaxRadius) * uniform;
            Vector3 worldCenter = transform.TransformPoint(center);
            if (shape != BasisContactShape.Capsule)
            {
                a = worldCenter;
                b = worldCenter;
                return;
            }
            float half = Mathf.Max(0f, Mathf.Clamp(height, 0f, MaxHeight) * 0.5f - Mathf.Clamp(radius, 0f, MaxRadius)) * uniform;
            Vector3 up = transform.up * half;
            a = worldCenter - up;
            b = worldCenter + up;
        }

        public static float SegmentDistanceSquared(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            const float epsilon = 1e-8f;
            if (a <= epsilon && e <= epsilon)
            {
                return r.sqrMagnitude;
            }
            if (a <= epsilon)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= epsilon)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom > epsilon ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }
            Vector3 closest1 = p1 + d1 * s, closest2 = p2 + d2 * t;
            return (closest1 - closest2).sqrMagnitude;
        }

        public static float Proximity(float distance, float receiverRadius, float senderRadius)
        {
            float reach = receiverRadius + senderRadius;
            if (reach <= 0f)
            {
                return distance <= 0f ? 1f : 0f;
            }
            return Mathf.Clamp01(1f - distance / reach);
        }
    }
}
