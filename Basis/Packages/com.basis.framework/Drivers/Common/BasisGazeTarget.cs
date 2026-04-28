using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Attach to any object that should attract eye gaze (mirrors, cameras, signage, etc.).
/// The local eye driver scores all active targets + focuses on the best one.
///
/// For dynamic focus points (e.g., mirror reflections), set <see cref="UseTransformPosition"/>
/// to false and update <see cref="FocusPoint"/> from your own script each frame.
/// </summary>
public class BasisGazeTarget : MonoBehaviour
{
    [Tooltip("Higher priority targets win when competing at similar scores. Players default to 1.")]
    public float Priority = 1f;

    [Tooltip("World-space focus point. Ignored when UseTransformPosition is true.")]
    public Vector3 FocusPoint;

    [Tooltip("If true, focus point tracks transform.position each frame.")]
    public bool UseTransformPosition = true;

    /// <summary>All currently active gaze targets.</summary>
    public static readonly List<BasisGazeTarget> ActiveTargets = new List<BasisGazeTarget>();

    void OnEnable() => ActiveTargets.Add(this);
    void OnDisable() => ActiveTargets.Remove(this);

    public float3 GetWorldFocusPoint()
    {
        return UseTransformPosition ? (float3)transform.position : (float3)FocusPoint;
    }
}
