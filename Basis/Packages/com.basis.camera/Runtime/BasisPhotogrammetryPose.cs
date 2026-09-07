using UnityEngine;

/// <summary>
/// Pure geometry for photogrammetry capture: no camera, no scene, no Unity objects touched — the
/// same "testable without pixels" shape as <see cref="BasisCameraPrintResize"/>.
/// </summary>
public static class BasisPhotogrammetryPose
{
    /// <summary>
    /// Converts a Unity world pose (left-handed, +Y up, +Z forward) into a nerfstudio/instant-ngp
    /// camera-to-world matrix (right-handed, +Y up, camera looks down -Z — confirmed against
    /// nerfstudio's own data-conventions doc: "+X is right, +Y is up, and +Z is pointing back and
    /// away from the camera").
    ///
    /// <para>Handedness flips by negating one world axis; Z is the one negated here. A point at
    /// local (0,0,d) — straight ahead of a Unity camera — must land at local (0,0,-d) in the NeRF
    /// convention, since NeRF's forward is -Z rather than +Z. Working through what that does to
    /// each basis vector gives: right and up keep their X/Y and negate Z, while the matrix's Z
    /// column is the negated (Z-flipped) forward, not the raw one. An identity Unity rotation at
    /// the origin must fall out of this as <see cref="Matrix4x4.identity"/> — the one
    /// hand-checkable case, and what the accompanying test pins.</para>
    /// </summary>
    public static Matrix4x4 BuildNerfTransformMatrix(Vector3 position, Quaternion rotation)
    {
        Vector3 right = rotation * Vector3.right;
        Vector3 up = rotation * Vector3.up;
        Vector3 forward = rotation * Vector3.forward;

        Vector3 column0 = new Vector3(right.x, right.y, -right.z);
        Vector3 column1 = new Vector3(up.x, up.y, -up.z);
        Vector3 column2 = new Vector3(-forward.x, -forward.y, forward.z);
        Vector3 column3 = new Vector3(position.x, position.y, -position.z);

        Matrix4x4 matrix = default;
        matrix.SetColumn(0, new Vector4(column0.x, column0.y, column0.z, 0f));
        matrix.SetColumn(1, new Vector4(column1.x, column1.y, column1.z, 0f));
        matrix.SetColumn(2, new Vector4(column2.x, column2.y, column2.z, 0f));
        matrix.SetColumn(3, new Vector4(column3.x, column3.y, column3.z, 1f));
        return matrix;
    }

    /// <summary>
    /// Pinhole intrinsics from Unity's vertical field of view, assuming square pixels (true here:
    /// the capture target's aspect always matches the source it was cropped from). <paramref
    /// name="horizontalFieldOfViewRadians"/> is written for parsers that key off <c>camera_angle_x</c>
    /// instead of <c>fl_x</c>.
    /// </summary>
    public static void ComputeIntrinsics(float verticalFieldOfViewDegrees, int width, int height,
        out float focalLengthX, out float focalLengthY,
        out float principalPointX, out float principalPointY,
        out float horizontalFieldOfViewRadians)
    {
        float verticalFieldOfViewRadians = verticalFieldOfViewDegrees * Mathf.Deg2Rad;
        focalLengthY = height / (2f * Mathf.Tan(verticalFieldOfViewRadians * 0.5f));
        focalLengthX = focalLengthY;
        principalPointX = width * 0.5f;
        principalPointY = height * 0.5f;
        horizontalFieldOfViewRadians = 2f * Mathf.Atan(width / (2f * focalLengthX));
    }

    /// <summary>
    /// Whether the camera has moved or turned enough since the last capture to be worth another
    /// one — the whole mechanism that spreads shots across a world instead of clustering them at
    /// wherever the operator lingers.
    /// </summary>
    public static bool ShouldCapture(Vector3 lastPosition, Quaternion lastRotation, Vector3 position, Quaternion rotation,
        float distanceThresholdMeters, float angleThresholdDegrees)
    {
        if (Vector3.Distance(lastPosition, position) >= distanceThresholdMeters) return true;
        return Quaternion.Angle(lastRotation, rotation) >= angleThresholdDegrees;
    }
}
