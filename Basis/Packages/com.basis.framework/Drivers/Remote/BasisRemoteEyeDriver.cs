using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;

/// <summary>
/// Controls remote player eye simulation, adding randomized,
/// natural-looking micro-movements to create lifelike avatar behavior.
/// </summary>
[System.Serializable]
public class BasisRemoteEyeDriver
{
    /// <summary>
    /// If set to <c>true</c>, overrides and disables the eye simulation logic.
    /// </summary>
    public bool Override = false;

    /// <summary>
    /// Minimum time (in seconds) between randomized look-around events.
    /// </summary>
    public const float MinLookAroundInterval = 1f;

    /// <summary>
    /// Maximum time (in seconds) between randomized look-around events.
    /// </summary>
    public const float MaxLookAroundInterval = 6f;

    /// <summary>
    /// Maximum horizontal offset (in normalized degrees) for random look targets.
    /// </summary>
    public const float MaxHorizontalLook = 0.75f;

    /// <summary>
    /// Maximum vertical offset (in normalized degrees) for random look targets.
    /// </summary>
    public const float MaxVerticalLook = 0.75f;

    /// <summary>
    /// Speed multiplier controlling how quickly the eyes interpolate toward their target.
    /// </summary>
    public const float LookSpeed = 15;

    /// <summary>
    /// The next scheduled time (in Unity’s timeAsDouble) when a new look-around should be triggered.
    /// </summary>
    private double nextLookAroundTime;

    /// <summary>
    /// Current randomized target position for the eyes (X = horizontal, Y = vertical).
    /// </summary>
    private Vector2 targetLookPosition;

    /// <summary>
    /// Indicates whether the eyes are currently moving toward a target.
    /// </summary>
    private bool isLooking = false;

    /// <summary>
    /// The remote player whose eye data is being driven.
    /// </summary>
    public BasisRemotePlayer LinkedPlayer;

    /// <summary>
    /// The avatar driver responsible for managing this player's avatar.
    /// </summary>
    public BasisAvatarDriver BasisAvatarDriver;
    /// <summary>
    /// Whether the eye simulation is currently active.
    /// Controlled by face visibility and initialization state.
    /// </summary>
    public bool IsEnabled;

    /// <summary>
    /// Called when the object is destroyed. Unsubscribes from face visibility events.
    /// </summary>
    public void OnDestroy()
    {
        if (LinkedPlayer != null && LinkedPlayer.FaceRenderer != null)
        {
            LinkedPlayer.FaceRenderer.Check -= UpdateFaceVisibility;
        }
    }

    /// <summary>
    /// Initializes the eye driver with the given avatar and player,
    /// subscribes to face visibility events, and schedules the first look-around.
    /// </summary>
    /// <param name="avatarDriver">The avatar driver managing the player’s model.</param>
    /// <param name="player">The remote player being linked.</param>
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

    /// <summary>
    /// Updates the simulation state based on whether the face is currently visible.
    /// </summary>
    /// <param name="state">True if the face is visible, false otherwise.</param>
    private void UpdateFaceVisibility(bool state)
    {
        IsEnabled = state;
    }

    /// <summary>
    /// Simulates natural eye movement by periodically selecting random look targets
    /// and smoothly interpolating the eyes toward them. Applies mirrored movement
    /// for left and right eyes to maintain realism.
    /// </summary>
    public void Simulate(double TimeAsDouble)
    {
        if (IsEnabled == false)
        {
            return;
        }
        if (Override)
        {
            return;
        }

        if (TimeAsDouble >= nextLookAroundTime)
        {
            ScheduleNextLookAround();
            PickNewTarget();
            isLooking = true;
        }

        if (isLooking)
        {
            float[] eyes = LinkedPlayer.NetworkReceiver.EyesAndMouth;
            float DeltaSpeed = LookSpeed * Time.deltaTime;

            // Apply vertical movement (same for both eyes)
            eyes[0] = Mathf.Lerp(eyes[0], targetLookPosition.y, DeltaSpeed); // Left eye vertical
            eyes[2] = Mathf.Lerp(eyes[2], targetLookPosition.y, DeltaSpeed); // Right eye vertical

            // Apply horizontal movement (mirrored)
            eyes[1] = Mathf.Lerp(eyes[1], targetLookPosition.x, DeltaSpeed);  // Left eye horizontal
            eyes[3] = Mathf.Lerp(eyes[3], -targetLookPosition.x, DeltaSpeed); // Right eye horizontal (mirrored)

            // Stop looking once close to target
            if (Mathf.Abs(eyes[0] - targetLookPosition.y) < 0.01f &&
                Mathf.Abs(eyes[1] - targetLookPosition.x) < 0.01f)
            {
                isLooking = false;
            }
            LinkedPlayer.NetworkReceiver.EyesAndMouth = eyes;
        }
    }

    /// <summary>
    /// Schedules the next randomized look-around time.
    /// Uses Unity’s random range between <see cref="MinLookAroundInterval"/> and <see cref="MaxLookAroundInterval"/>.
    /// </summary>
    private void ScheduleNextLookAround()
    {
        double interval = UnityEngine.Random.Range(MinLookAroundInterval, MaxLookAroundInterval);
        nextLookAroundTime = Time.timeAsDouble + interval;
    }

    /// <summary>
    /// Picks a new random target look position within the allowed horizontal and vertical ranges.
    /// </summary>
    private void PickNewTarget()
    {
        targetLookPosition = new Vector2(
            UnityEngine.Random.Range(-MaxHorizontalLook, MaxHorizontalLook),
            UnityEngine.Random.Range(-MaxVerticalLook, MaxVerticalLook)
        );
    }
}
