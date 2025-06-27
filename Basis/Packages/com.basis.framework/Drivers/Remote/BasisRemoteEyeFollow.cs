using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;
[System.Serializable]
public class BasisRemoteEyeFollow 
{
    public bool Override = false;
    // Adjustable parameterss
    public const float MinLookAroundInterval = 1f; // Minimum time between look-arounds
    public const float MaxLookAroundInterval = 6f; // Maximum time between look-arounds
    public const float MaxHorizontalLook = 0.75f; // Maximum horizontal movement in degrees
    public const float MaxVerticalLook = 0.75f; // Maximum vertical movement in degrees
    public const float LookSpeed = 15; // Speed of eye movement

    private double nextLookAroundTime;
    private Vector2 targetLookPosition; // Target position for both eyes (X = Horizontal, Y = Vertical)
    private bool isLooking = false;

    public BasisRemotePlayer LinkedPlayer;
    public BasisAvatarDriver BasisAvatarDriver;

    public void OnDestroy()
    {
        if (LinkedPlayer != null && LinkedPlayer.FaceRenderer != null)
        {
            LinkedPlayer.FaceRenderer.Check -= UpdateFaceVisibility;
        }
    }

    public void Initalize(BasisAvatarDriver avatarDriver, BasisRemotePlayer player)
    {
        BasisAvatarDriver = avatarDriver;
        LinkedPlayer = player;

        if (LinkedPlayer != null && LinkedPlayer.FaceRenderer != null)
        {
            LinkedPlayer.FaceRenderer.Check += UpdateFaceVisibility;
            UpdateFaceVisibility(LinkedPlayer.FaceIsVisible);
        }
        ScheduleNextLookAround();
        IsEnabled = true;
    }
    public bool IsEnabled;
    private void UpdateFaceVisibility(bool state)
    {
        IsEnabled = state;
    }
    /// <summary>
    /// Simulates natural eye movement by synchronizing both eyes.
    /// </summary>
    public void Simulate()
    {
        if (IsEnabled == false)
        {
            return;
        }
        if (Override)
        {
            return;
        }

        if (Time.timeAsDouble >= nextLookAroundTime)
        {
            ScheduleNextLookAround();
            PickNewTarget();
            isLooking = true;
        }

        if (isLooking)
        {
            float[] eyes = LinkedPlayer.NetworkReceiver.Eyes;
            // Smoothly move both eyes toward the target position
            float DeltaSpeed = LookSpeed * Time.deltaTime;

            // Apply vertical movement (Y is the same for both eyes)
            eyes[0] = Mathf.Lerp(eyes[0], targetLookPosition.y, DeltaSpeed); // Vertical movement (left eye)
            eyes[2] = Mathf.Lerp(eyes[2], targetLookPosition.y, DeltaSpeed); // Vertical movement (right eye)

            // Apply horizontal movement (mirror for one eye)
            eyes[1] = Mathf.Lerp(eyes[1], targetLookPosition.x, DeltaSpeed); // Horizontal movement (left eye)
            eyes[3] = Mathf.Lerp(eyes[3], -targetLookPosition.x, DeltaSpeed); // Horizontal movement (right eye, mirrored)

            // Stop looking when close to the target
            if (Mathf.Abs(eyes[0] - targetLookPosition.y) < 0.01f &&
                Mathf.Abs(eyes[1] - targetLookPosition.x) < 0.01f)
            {
                isLooking = false;
            }
            LinkedPlayer.NetworkReceiver.Eyes = eyes;
        }
    }

    private void ScheduleNextLookAround()
    {
        // Schedule the next look-around time using Time.timeAsDouble
        double interval = UnityEngine.Random.Range(MinLookAroundInterval, MaxLookAroundInterval);
        nextLookAroundTime = Time.timeAsDouble + interval;
    }

    private void PickNewTarget()
    {
        // Pick a new random target position within the range
        targetLookPosition = new Vector2(
            UnityEngine.Random.Range(-MaxHorizontalLook, MaxHorizontalLook),
            UnityEngine.Random.Range(-MaxVerticalLook, MaxVerticalLook)
        );
    }
}
