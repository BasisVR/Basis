using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Drives automatic facial blinking using skinned mesh blendshapes.
    /// </summary>
    /// <remarks>
    /// This driver schedules pseudo-random blink events and animates eye-closure/opening
    /// by writing weights to a set of blink blendshapes on a <see cref="SkinnedMeshRenderer"/>.
    /// It also observes face visibility via <see cref="BasisPlayer.FaceRenderer"/> and disables
    /// updates when the face is not visible.
    /// </remarks>
    [System.Serializable]
    public class BasisFacialBlinkDriver
    {
        /// <summary>
        /// Renderer containing blink blendshapes referenced by <see cref="blendShapeIndex"/>.
        /// </summary>
        public SkinnedMeshRenderer meshRenderer;

        /// <summary>
        /// Minimum interval in seconds between blinks.
        /// </summary>
        public float minBlinkInterval = 5f;

        /// <summary>
        /// Maximum interval in seconds between blinks.
        /// </summary>
        public float maxBlinkInterval = 25f;

        /// <summary>
        /// Time in seconds for the eyes to close fully during a blink.
        /// </summary>
        public float blinkDuration = 0.2f;

        /// <summary>
        /// Time in seconds for the eyes to transition back to open after a blink.
        /// </summary>
        public float visemeTransitionDuration = 0.05f;

        /// <summary>
        /// Blendshape indices on <see cref="meshRenderer"/> used for blinking.
        /// Values of <c>-1</c> in the avatar data are ignored.
        /// </summary>
        public List<int> blendShapeIndex = new List<int>();

        /// <summary>
        /// Cached count of valid blink blendshape indices.
        /// </summary>
        public int blendShapeCount = 0;

        /// <summary>
        /// Internal flag indicating an active blink (closing phase).
        /// </summary>
        private bool isBlinking = false;

        /// <summary>
        /// Absolute time at which the next blink should start.
        /// </summary>
        private double nextBlinkTime;

        /// <summary>
        /// Absolute time when the current blink started.
        /// </summary>
        private double blinkStartTime;

        /// <summary>
        /// Internal flag indicating the opening phase after a blink.
        /// </summary>
        private bool isVisemeClosing = false;

        /// <summary>
        /// Absolute time when the opening transition started.
        /// </summary>
        private double visemeStartTime;

        /// <summary>
        /// Player whose face visibility is observed.
        /// </summary>
        public BasisPlayer LinkedPlayer;

        /// <summary>
        /// Whether updates are currently enabled (e.g., face visible and renderer present).
        /// </summary>
        private bool IsEnabled;

        /// <summary>
        /// blendshape mesh count on avatar faceblinkmesh
        /// </summary>
        public int MeshblendShapeCount;

        /// <summary>
        /// Initializes the blink driver with player and avatar data and wires face visibility callbacks.
        /// </summary>
        /// <param name="Player">The owning <see cref="BasisPlayer"/>.</param>
        /// <param name="Avatar">Avatar providing blink mesh and viseme indices.</param>
        public void Initialize(BasisPlayer Player, BasisAvatar Avatar)
        {
            LinkedPlayer = Player;
            blendShapeIndex.Clear();
            meshRenderer = Avatar.FaceBlinkMesh;

            // Collect valid blink viseme indices
            for (int Index = 0; Index < Avatar.BlinkViseme.Length; Index++)
            {
                int Blink = Avatar.BlinkViseme[Index];
                if (Blink != -1)
                {
                    blendShapeIndex.Add(Blink);
                }
            }

            blendShapeCount = blendShapeIndex.Count;
            MeshblendShapeCount = meshRenderer.sharedMesh.blendShapeCount;

            // Start blinking
            SetNextBlinkTime(Time.time);

            // Observe face visibility
            if (LinkedPlayer != null && LinkedPlayer.FaceRenderer != null)
            {
                // BasisDebug.Log("Wired up Renderer Check For Blinking", BasisDebug.LogTag.Avatar);
                LinkedPlayer.FaceRenderer.Check += UpdateFaceVisibility;
                UpdateFaceVisibility(LinkedPlayer.FaceIsVisible);
            }

            IsEnabled = meshRenderer != null;
        }

        /// <summary>
        /// Unsubscribes from face visibility callbacks.
        /// </summary>
        public void OnDestroy()
        {
            if (LinkedPlayer != null && LinkedPlayer.FaceRenderer != null)
            {
                LinkedPlayer.FaceRenderer.Check -= UpdateFaceVisibility;
            }
        }

        /// <summary>
        /// Updates the internal enabled state based on face visibility.
        /// </summary>
        /// <param name="State">True if the face is currently visible.</param>
        public void UpdateFaceVisibility(bool State)
        {
            IsEnabled = State;
        }

        /// <summary>
        /// Checks whether the provided avatar contains the data needed to run the blink driver.
        /// </summary>
        /// <param name="Avatar">Avatar to validate.</param>
        /// <returns>
        /// <c>true</c> if a blink mesh is present and at least one valid blink viseme index exists; otherwise <c>false</c>.
        /// </returns>
        public static bool MeetsRequirements(BasisAvatar Avatar)
        {
            if (Avatar != null)
            {
                if (Avatar.FaceBlinkMesh != null)
                {
                    if (Avatar.BlinkViseme != null && Avatar.BlinkViseme.Length >= 1)
                    {
                        for (int Index = 0; Index < Avatar.BlinkViseme.Length; Index++)
                        {
                            int Blink = Avatar.BlinkViseme[Index];
                            if (Blink != -1)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Advances blink timing and writes blendshape weights for close/open phases.
        /// Should be called once per frame.
        /// </summary>
        public void Simulate(double Time)// Time.time
        {
            if (IsEnabled && meshRenderer != null)
            {
                // Start a blink if scheduled
                if (!isBlinking && Time >= nextBlinkTime)
                {
                    isBlinking = true;
                    blinkStartTime = Time;

                    // Reset weights (closing will raise them)
                    for (int Index = 0; Index < blendShapeCount; Index++)
                    {
                        int ConvertedIndex = blendShapeIndex[Index];
                        if (MeshblendShapeCount >= ConvertedIndex)
                        {
                            meshRenderer.SetBlendShapeWeight(blendShapeIndex[Index], 0);
                        }
                    }

                    isVisemeClosing = true;
                    visemeStartTime = Time;
                }
                // Closing phase (eyes moving from open -> closed)
                else if (isBlinking)
                {
                    float CalCulatedTime = (float)((Time - blinkStartTime) / blinkDuration);
                    CalCulatedTime = math.saturate(CalCulatedTime); // clamp to [0,1]

                    float blendWeight = math.lerp(0, 100, CalCulatedTime);

                    for (int Index = 0; Index < blendShapeCount; Index++)
                    {
                        int ConvertedIndex = blendShapeIndex[Index];
                        if (ConvertedIndex >= 0 && ConvertedIndex < MeshblendShapeCount)
                        {
                            meshRenderer.SetBlendShapeWeight(ConvertedIndex, blendWeight);
                        }
                    }

                    if (CalCulatedTime >= 1f)
                    {
                        isBlinking = false;
                        SetNextBlinkTime(Time);
                    }
                }
                // Opening phase (eyes moving from closed -> open)
                else if (isVisemeClosing)
                {
                    float CalCulatedTime = (float)((Time - visemeStartTime) / visemeTransitionDuration);
                    CalCulatedTime = Mathf.Clamp01(CalCulatedTime);

                    float blendWeight = Mathf.Lerp(100, 0, CalCulatedTime);

                    for (int Index = 0; Index < blendShapeCount; Index++)
                    {
                        int ConvertedIndex = blendShapeIndex[Index];
                        if (ConvertedIndex >= 0 && ConvertedIndex < MeshblendShapeCount)
                        {
                            meshRenderer.SetBlendShapeWeight(ConvertedIndex, blendWeight);
                        }
                    }

                    if (CalCulatedTime >= 1f)
                    {
                        isVisemeClosing = false;
                    }
                }
            }
        }

        /// <summary>
        /// Randomizes the absolute time for the next blink within <see cref="minBlinkInterval"/> and <see cref="maxBlinkInterval"/>.
        /// </summary>
        public void SetNextBlinkTime(double Time)
        {
            nextBlinkTime = Time + UnityEngine.Random.Range(minBlinkInterval, maxBlinkInterval);
        }
    }
}
