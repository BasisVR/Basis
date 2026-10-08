using System.Runtime.CompilerServices;
using UnityEngine;


/// <summary>
/// The single funnel for every main-thread <see cref="Transform"/> get/set on the local player's
/// per-frame path. Exists to make those operations countable: a Transform property is an ICall into
/// native code, and — the reason this matters more than interop cost — a main-thread Transform read
/// blocks until every in-flight transform job lands. ScheduleReadOnly does not exempt it. So each of
/// these is a potential sync point, and until they route through one place there is no way to see how
/// many there are or where.
///
/// Every accessor is a pass-through: same value, same order, same side effects as the raw property.
/// The only addition is a call-site record, and that whole path is compiled out of player builds —
/// outside the editor and development builds these are plain inlined property access with no extra
/// arguments (see the #else arm of each pair).
///
/// Attribution is free: [CallerFilePath] and [CallerLineNumber] are filled in by the compiler as
/// constants, so a site identifies itself without anyone maintaining an enum of names.
///
/// Recording is off until <see cref="BasisTransformAudit.Enabled"/> is set from
/// Basis/Debug/Transform Access.
///
/// Naming: these deliberately do NOT reuse Unity's method names. An extension method loses overload
/// resolution to an instance method of the same name, so a `GetPositionAndRotation` extension would
/// silently never be called. Every name here is one Transform does not already have.
/// </summary>
public static class BasisTransformAccess
{
    // ── World position ──────────────────────────────────────────────────────────────────────────
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    public static Vector3 GetPosition(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetPosition);
        return t.position;
    }

    public static void SetPosition(this Transform t, Vector3 value, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.SetPosition);
        BasisLocalPose.NotifyWrite(t);
        t.position = value;
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 GetPosition(this Transform t) => t.position;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetPosition(this Transform t, Vector3 value)
    {
        BasisLocalPose.NotifyWrite(t);
        t.position = value;
    }
#endif

    // ── World rotation ──────────────────────────────────────────────────────────────────────────
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    public static Quaternion GetRotation(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetRotation);
        return t.rotation;
    }

    public static void SetRotation(this Transform t, Quaternion value, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.SetRotation);
        BasisLocalPose.NotifyWrite(t);
        t.rotation = value;
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion GetRotation(this Transform t) => t.rotation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetRotation(this Transform t, Quaternion value)
    {
        BasisLocalPose.NotifyWrite(t);
        t.rotation = value;
    }
#endif

    // ── World pose (the combined API — one interop round trip instead of two) ───────────────────
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    public static void GetPose(this Transform t, out Vector3 position, out Quaternion rotation, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetPose);
        t.GetPositionAndRotation(out position, out rotation);
    }

    public static void SetPose(this Transform t, Vector3 position, Quaternion rotation, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.SetPose);
        BasisLocalPose.NotifyWrite(t);
        t.SetPositionAndRotation(position, rotation);
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetPose(this Transform t, out Vector3 position, out Quaternion rotation)
        => t.GetPositionAndRotation(out position, out rotation);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetPose(this Transform t, Vector3 position, Quaternion rotation)
    {
        BasisLocalPose.NotifyWrite(t);
        t.SetPositionAndRotation(position, rotation);
    }
#endif

    // ── Local position / rotation ───────────────────────────────────────────────────────────────
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    public static Vector3 GetLocalPosition(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetLocalPosition);
        return t.localPosition;
    }

    public static void SetLocalPosition(this Transform t, Vector3 value, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.SetLocalPosition);
        BasisLocalPose.NotifyWrite(t);
        t.localPosition = value;
    }

    public static Quaternion GetLocalRotation(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetLocalRotation);
        return t.localRotation;
    }

    public static void SetLocalRotation(this Transform t, Quaternion value, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.SetLocalRotation);
        BasisLocalPose.NotifyWrite(t);
        t.localRotation = value;
    }

    public static void GetLocalPose(this Transform t, out Vector3 position, out Quaternion rotation, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetLocalPose);
        t.GetLocalPositionAndRotation(out position, out rotation);
    }

    public static void SetLocalPose(this Transform t, Vector3 position, Quaternion rotation, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.SetLocalPose);
        BasisLocalPose.NotifyWrite(t);
        t.SetLocalPositionAndRotation(position, rotation);
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 GetLocalPosition(this Transform t) => t.localPosition;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLocalPosition(this Transform t, Vector3 value)
    {
        BasisLocalPose.NotifyWrite(t);
        t.localPosition = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion GetLocalRotation(this Transform t) => t.localRotation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLocalRotation(this Transform t, Quaternion value)
    {
        BasisLocalPose.NotifyWrite(t);
        t.localRotation = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetLocalPose(this Transform t, out Vector3 position, out Quaternion rotation)
        => t.GetLocalPositionAndRotation(out position, out rotation);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLocalPose(this Transform t, Vector3 position, Quaternion rotation)
    {
        BasisLocalPose.NotifyWrite(t);
        t.SetLocalPositionAndRotation(position, rotation);
    }
#endif

    // ── Scale ───────────────────────────────────────────────────────────────────────────────────
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    public static Vector3 GetLocalScale(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetLocalScale);
        return t.localScale;
    }

    public static void SetLocalScale(this Transform t, Vector3 value, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.SetLocalScale);
        BasisLocalPose.NotifyWrite(t);
        t.localScale = value;
    }

    /// <summary>Walks every ancestor natively — the most expensive read on this class.</summary>
    public static Vector3 GetLossyScale(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetLossyScale);
        return t.lossyScale;
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 GetLocalScale(this Transform t) => t.localScale;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLocalScale(this Transform t, Vector3 value)
    {
        BasisLocalPose.NotifyWrite(t);
        t.localScale = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 GetLossyScale(this Transform t) => t.lossyScale;
#endif

    // ── Matrices ────────────────────────────────────────────────────────────────────────────────
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    public static Matrix4x4 GetLocalToWorld(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetLocalToWorld);
        return t.localToWorldMatrix;
    }

    public static Matrix4x4 GetWorldToLocal(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetWorldToLocal);
        return t.worldToLocalMatrix;
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix4x4 GetLocalToWorld(this Transform t) => t.localToWorldMatrix;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix4x4 GetWorldToLocal(this Transform t) => t.worldToLocalMatrix;
#endif

    // ── Basis vectors ───────────────────────────────────────────────────────────────────────────
    // Each of these is a full rotation read plus a multiply on the native side. Reading `rotation`
    // once and deriving two or three of them is strictly cheaper — GetRotation then `rot * Vector3.up`.
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    public static Vector3 GetForward(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetForward);
        return t.forward;
    }

    public static Vector3 GetRight(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetRight);
        return t.right;
    }

    public static Vector3 GetUp(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetUp);
        return t.up;
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 GetForward(this Transform t) => t.forward;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 GetRight(this Transform t) => t.right;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 GetUp(this Transform t) => t.up;
#endif

    // ── Hierarchy ───────────────────────────────────────────────────────────────────────────────
    // A reparent invalidates every cached world pose beneath it, so Reparent drops the whole cache
    // rather than trying to work out which slots moved.
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    public static Transform GetParent(this Transform t, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.GetParent);
        return t.parent;
    }

    public static void Reparent(this Transform t, Transform parent, bool worldPositionStays = true, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.Reparent);
        BasisLocalPose.InvalidateAll();
        t.SetParent(parent, worldPositionStays);
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Transform GetParent(this Transform t) => t.parent;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reparent(this Transform t, Transform parent, bool worldPositionStays = true)
    {
        BasisLocalPose.InvalidateAll();
        t.SetParent(parent, worldPositionStays);
    }
#endif

    // ── Space conversion ────────────────────────────────────────────────────────────────────────
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    public static Vector3 ToWorldPoint(this Transform t, Vector3 localPoint, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.ToWorldPoint);
        return t.TransformPoint(localPoint);
    }

    public static Vector3 ToLocalPoint(this Transform t, Vector3 worldPoint, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.ToLocalPoint);
        return t.InverseTransformPoint(worldPoint);
    }

    public static Vector3 ToWorldDir(this Transform t, Vector3 localDirection, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.ToWorldDir);
        return t.TransformDirection(localDirection);
    }

    public static Vector3 ToLocalDir(this Transform t, Vector3 worldDirection, [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
    {
        BasisTransformAudit.Record(file, line, BasisTransformOp.ToLocalDir);
        return t.InverseTransformDirection(worldDirection);
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 ToWorldPoint(this Transform t, Vector3 localPoint) => t.TransformPoint(localPoint);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 ToLocalPoint(this Transform t, Vector3 worldPoint) => t.InverseTransformPoint(worldPoint);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 ToWorldDir(this Transform t, Vector3 localDirection) => t.TransformDirection(localDirection);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 ToLocalDir(this Transform t, Vector3 worldDirection) => t.InverseTransformDirection(worldDirection);
#endif
}
