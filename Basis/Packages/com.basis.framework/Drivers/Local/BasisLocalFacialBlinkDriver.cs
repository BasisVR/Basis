using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using System;
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
    public class BasisLocalFacialBlinkDriver
    {
        /// <summary>
        /// If set to <c>true</c>, overrides and disables the blinking simulation logic.
        /// </summary>
        public bool Override = false;
        /// <summary>
        /// Renderer containing blink blendshapes referenced by <see cref="blendShapeIndices"/>.
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
        public List<int> blendShapeIndices = new List<int>();

        /// <summary>
        /// Cached count of valid blink blendshape indices.
        /// </summary>
        public int blendShapeCount;

        /// <summary>
        /// blendshape mesh count on avatar face blink mesh.
        /// </summary>
        public int meshBlendShapeCount;

        /// <summary>
        /// Player whose face visibility is observed.
        /// </summary>
        public BasisPlayer linkedPlayer;

        /// <summary>
        /// Whether updates are currently enabled (e.g., face visible and renderer present).
        /// </summary>
        public bool IsEnabled;

        /// <summary>
        /// Internal flag indicating an active blink (closing phase).
        /// </summary>
        public bool IsClosingBlink;

        /// <summary>
        /// Internal flag indicating the opening phase after a blink.
        /// </summary>
        public bool IsOpeningBlink;

        /// <summary>
        /// Absolute time at which the next blink should start.
        /// </summary>
        public double NextBlinkTime;

        /// <summary>
        /// Absolute time when the current blink (closing) started.
        /// </summary>
        public double BlinkStartTime;

        /// <summary>
        /// Absolute time when the opening transition started.
        /// </summary>
        public double OpenStartTime;

        /// <summary>
        /// Initializes the blink driver with player and avatar data and wires face visibility callbacks.
        /// </summary>
        /// <param name="player">The owning <see cref="BasisPlayer"/>.</param>
        /// <param name="avatar">Avatar providing blink mesh and viseme indices.</param>
        public void Initialize(BasisPlayer player, BasisAvatar avatar)
        {
            linkedPlayer = player;
            blendShapeIndices.Clear();

            if (avatar == null || avatar.FaceBlinkMesh == null || avatar.BlinkViseme == null)
            {
                IsEnabled = false;
                return;
            }

            meshRenderer = avatar.FaceBlinkMesh;

            // Collect valid blink viseme indices
            for (int Index = 0; Index < avatar.BlinkViseme.Length; Index++)
            {
                int blinkIndex = avatar.BlinkViseme[Index];
                if (blinkIndex != -1)
                {
                    blendShapeIndices.Add(blinkIndex);
                }
            }

            blendShapeCount = blendShapeIndices.Count;
            meshBlendShapeCount = meshRenderer != null && meshRenderer.sharedMesh != null? meshRenderer.sharedMesh.blendShapeCount: 0;

            // Observe face visibility
            if (linkedPlayer != null && linkedPlayer.FaceRenderer != null)
            {
                linkedPlayer.FaceRenderer.Check += UpdateFaceVisibility;
                UpdateFaceVisibility(linkedPlayer.FaceIsVisible);
            }

            IsEnabled = meshRenderer != null && blendShapeCount > 0 && meshBlendShapeCount > 0;

            if(IsEnabled)
            {
                // Start blinking
                SetNextBlinkTime(Time.timeAsDouble);
            }
        }
        /// <summary>
        /// Advances blink timing and writes blendshape weights for close/open phases.
        /// Should be called once per frame.
        /// </summary>
        /// <param name="time">Current absolute time (e.g., Time.timeAsDouble).</param>
        public void Simulate(double time)
        {
            if (IsEnabled == false)
            {
                return;
            }
            if (Override)
            {
                return;
            }
            if (meshRenderer == null)
            {
                return;
            }
            // Start a blink if scheduled
            if (!IsClosingBlink && !IsOpeningBlink && time >= NextBlinkTime)
            {
                IsClosingBlink = true;
                BlinkStartTime = time;

                // Reset weights (closing will raise them)
                for (int i = 0; i < blendShapeCount; i++)
                {
                    SafeSetBlendShape(blendShapeIndices[i], 0f);
                }

                IsOpeningBlink = true;
                OpenStartTime = time;
                return;
            }

            // Closing phase (eyes moving from open -> closed)
            if (IsClosingBlink)
            {
                float normalized = (float)((time - BlinkStartTime) / blinkDuration);
                normalized = math.saturate(normalized); // clamp to [0,1]

                float blendWeight = math.lerp(0f, 100f, normalized);

                for (int i = 0; i < blendShapeCount; i++)
                {
                    SafeSetBlendShape(blendShapeIndices[i], blendWeight);
                }

                if (normalized >= 1f)
                {
                    IsClosingBlink = false;
                    SetNextBlinkTime(time);
                }

                return;
            }

            // Opening phase (eyes moving from closed -> open)
            if (IsOpeningBlink)
            {
                float normalized = (float)((time - OpenStartTime) / visemeTransitionDuration);
                normalized = Mathf.Clamp01(normalized);

                float blendWeight = Mathf.Lerp(100f, 0f, normalized);

                for (int Index = 0; Index < blendShapeCount; Index++)
                {
                    SafeSetBlendShape(blendShapeIndices[Index], blendWeight);
                }

                if (normalized >= 1f)
                {
                    IsOpeningBlink = false;
                }
            }
        }
        /// <summary>
        /// Unsubscribes from face visibility callbacks.
        /// </summary>
        public void OnDestroy()
        {
            if (linkedPlayer != null && linkedPlayer.FaceRenderer != null)
            {
                linkedPlayer.FaceRenderer.Check -= UpdateFaceVisibility;
            }
        }

        /// <summary>
        /// Updates the internal enabled state based on face visibility
        /// and resets blendshape state when disabled.
        /// </summary>
        /// <param name="state">True if the face is currently visible.</param>
        public void UpdateFaceVisibility(bool state)
        {
            IsEnabled = state && meshRenderer != null;

            if (!IsEnabled && meshRenderer != null)
            {
                // Reset all blink state + weights when face becomes invisible
                IsClosingBlink = false;
                IsOpeningBlink = false;

                if (Override == false)
                {
                    for (int i = 0; i < blendShapeCount; i++)
                    {
                        SafeSetBlendShape(blendShapeIndices[i], 0);
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether the provided avatar contains the data needed to run the blink driver.
        /// </summary>
        /// <param name="avatar">Avatar to validate.</param>
        /// <returns>
        /// <c>true</c> if a blink mesh is present and at least one valid blink viseme index exists; otherwise <c>false</c>.
        /// </returns>
        public static bool MeetsRequirements(BasisAvatar avatar)
        {
            if (avatar == null || avatar.FaceBlinkMesh == null || avatar.BlinkViseme == null || avatar.BlinkViseme.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < avatar.BlinkViseme.Length; i++)
            {
                if (avatar.BlinkViseme[i] != -1)
                {
                    return true;
                }
            }

            return false;
        }
        public void SafeSetBlendShape(int idx,float blendWeight)
        {
            if (idx >= 0 && idx < meshBlendShapeCount)
            {
                meshRenderer.SetBlendShapeWeight(idx, blendWeight);
            }
        }
        /// <summary>
        /// Randomizes the absolute time for the next blink within <see cref="minBlinkInterval"/> and <see cref="maxBlinkInterval"/>.
        /// </summary>
        /// <param name="currentTime">Time to offset from.</param>
        public void SetNextBlinkTime(double currentTime)
        {
            NextBlinkTime = currentTime + UnityEngine.Random.Range(minBlinkInterval, maxBlinkInterval);
        }
    }
}
