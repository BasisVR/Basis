using Basis.Scripts.Drivers;
using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Players
{
    /// <summary>
    /// Base component for both local and remote players within the Basis SDK.
    /// Provides common identity, avatar, visibility, progress, and lifecycle events.
    /// </summary>
    public abstract class BasisPlayer : MonoBehaviour
    {
        /// <summary>
        /// Indicates whether this player represents the local user.
        /// </summary>
        public bool IsLocal { get; set; }

        /// <summary>
        /// Returns the runtime platform associated with this player.
        /// </summary>
        /// <returns>
        /// For local players, returns <see cref="Application.platform"/>.
        /// For remote players, logs an error and returns <see cref="RuntimePlatform.WindowsPlayer"/> as a placeholder.
        /// </returns>
        /// <remarks>
        /// Remote platform detection is not implemented; callers should not rely on the fallback value.
        /// </remarks>
        public RuntimePlatform GetRuntimePlatform()
        {
            if (IsLocal)
            {
                return Application.platform;
            }
            else
            {
                BasisDebug.LogError("this is not implemented talk with the creators of basis");
                return RuntimePlatform.WindowsPlayer;
            }
        }

        /// <summary>
        /// Raw (untrusted) display name as provided by the source (user or network).
        /// </summary>
        public string DisplayName;

        /// <summary>
        /// Unique identifier for the player.
        /// </summary>
        public string UUID;

        /// <summary>
        /// Display-safe version of <see cref="DisplayName"/> with formatting tags removed.
        /// </summary>
        public string SafeDisplayName;

        /// <summary>
        /// Active avatar instance for this player, if any.
        /// </summary>
        public BasisAvatar BasisAvatar;

        /// <summary>
        /// Root transform for the avatar representation (if separate from the player object).
        /// </summary>
        public Transform AvatarTransform;

        /// <summary>
        /// Transform of the avatar's animator component
        /// </summary>
        public Transform AvatarAnimatorTransform;

        /// <summary>
        /// Cached self transform for quick access.
        /// </summary>
        public Transform PlayerSelf; // yes caching myself is faster.

        /// <summary>
        /// Raised when the player's avatar switches to a new one (non-fallback).
        /// </summary>
        public event Action OnAvatarSwitched;

        /// <summary>
        /// Progress reporter for the current avatar load operation (high-level).
        /// </summary>
        public BasisProgressReport ProgressReportAvatarLoad = new BasisProgressReport();

        /// <summary>
        /// Network-downloadable avatar load mode constant (value <c>0</c>).
        /// </summary>
        public const byte LoadModeNetworkDownloadable = 0;

        /// <summary>
        /// Local avatar load mode constant (value <c>1</c>).
        /// </summary>
        public const byte LoadModeLocal = 1;

        /// <summary>
        /// Error avatar load mode constant (value <c>2</c>).
        /// </summary>
        public const byte LoadModeError = 2;

        /// <summary>
        /// Whether the face portion of the avatar is currently visible.
        /// </summary>
        public bool FaceIsVisible;

        /// <summary>
        /// Helper used to determine whether a face renderer is currently visible to the camera.
        /// </summary>
        public BasisMeshRendererCheck FaceRenderer;

        /// <summary>
        /// Fine-grained progress reporter for avatar operations.
        /// </summary>
        public BasisProgressReport AvatarProgress = new BasisProgressReport();

        /// <summary>
        /// Callback invoked when audio data is received for this player.
        /// </summary>
        public Action AudioReceived;

        /// <summary>
        /// Delegate signature for simulation hooks (e.g., pre-bone simulation).
        /// </summary>
        public delegate void SimulationHandler();

        /// <summary>
        /// Called before bone simulation updates for this player, if subscribed.
        /// </summary>
        public SimulationHandler OnLatePollData;

        /// <summary>
        /// Called before bone simulation updates for this player, if subscribed.
        /// </summary>
        public SimulationHandler OnRenderPollData;

        /// <summary>
        /// Called before bone simulation updates for this player, if subscribed.
        /// </summary>
        public SimulationHandler OnVirtualData;

        /// <summary>
        /// Whether the currently active avatar is considered a fallback (placeholder) asset.
        /// </summary>
        public bool IsConsideredFallBackAvatar = true;

        /// <summary>
        /// The current avatar load mode for this player (0 = downloading, 1 = local).
        /// </summary>
        public byte AvatarLoadMode; // 0 downloading 1 local

        /// <summary>
        /// Metadata describing the avatar bundle used to create the current avatar.
        /// </summary>
        [HideInInspector]
        public BasisLoadableBundle AvatarMetaData;

        /// <summary>
        /// Computes and stores a display-safe version of <see cref="DisplayName"/> by stripping any &lt;...&gt; tags.
        /// </summary>
        public void SetSafeDisplayname()
        {
            // Regex pattern to match any <...> tags
            SafeDisplayName = Regex.Replace(DisplayName, "<.*?>", string.Empty);
        }

        /// <summary>
        /// Updates whether the face is considered visible.
        /// </summary>
        /// <param name="State">True if the face is visible; otherwise false.</param>
        public void UpdateFaceVisibility(bool State)
        {
            FaceIsVisible = State;
        }

        /// <summary>
        /// Triggers the <see cref="OnAvatarSwitched"/> event.
        /// </summary>
        public void AvatarSwitched()
        {
            OnAvatarSwitched?.Invoke();
        }
    }
}
