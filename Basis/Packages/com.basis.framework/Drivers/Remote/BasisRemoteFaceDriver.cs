using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using System.Collections.Generic;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Drives automatic facial blinking using skinned mesh blendshapes.
    /// handles eye movement override aswell
    /// </summary>
    /// <remarks>
    /// This driver schedules pseudo-random blink events and animates eye-closure/opening
    /// by writing weights to a set of blink blendshapes on a <see cref="SkinnedMeshRenderer"/>.
    /// It also observes face visibility via <see cref="BasisPlayer.FaceRenderer"/> and disables
    /// updates when the face is not visible.
    /// </remarks>
    [System.Serializable]
    public class BasisRemoteFaceDriver
    {
        /// <summary>
        /// If set to <c>true</c>, overrides and disables the blinking simulation logic.
        /// </summary>
        public bool OverrideBlinking = false;
        /// <summary>
        /// overrides the eye output
        /// </summary>
        public bool OverrideEye = false;
        /// <summary>
        /// Renderer containing blink blendshapes referenced by <see cref="blendShapeIndices"/>.
        /// </summary>
        public SkinnedMeshRenderer meshRenderer;

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
        public bool BlinkingEnabled;

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
                BlinkingEnabled = false;
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

            BlinkingEnabled = meshRenderer != null && blendShapeCount > 0 && meshBlendShapeCount > 0;
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
            BlinkingEnabled = state && meshRenderer != null;

            if (!BlinkingEnabled && meshRenderer != null)
            {
                if (OverrideBlinking == false)
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
    }
}
