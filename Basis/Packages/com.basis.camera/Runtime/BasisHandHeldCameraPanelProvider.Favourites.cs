using System;
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;

namespace Basis.BasisUI.HandHeldCamera
{
    public partial class BasisHandHeldCameraPanelProvider
    {
        private readonly struct CameraFavourite
        {
            public readonly string Key;
            public readonly Func<BasisHandHeldCamera, bool> Get;
            public readonly Action<BasisHandHeldCamera, bool> Set;
            public CameraFavourite(string key, Func<BasisHandHeldCamera, bool> get, Action<BasisHandHeldCamera, bool> set)
            {
                Key = key;
                Get = get;
                Set = set;
            }
        }

        [NoAutoStaticsCleanup] private static readonly CameraFavourite[] CameraFavourites =
        {
            new CameraFavourite("camera.hideCamera", c => c.IsCameraHidden, (c, v) => c.SetCameraHidden(v)),
            new CameraFavourite("camera.hearFromCamera", c => BasisHandHeldCameraAudioListener.IsHeldBy(c), (c, v) => BasisHandHeldCameraAudioListener.Set(c, v)),
            new CameraFavourite("camera.selfie", c => c.HandHeld.IsSelfieMode, (c, v) => { if (c.HandHeld.IsSelfieMode != v) c.HandHeld.ToggleSelfie(); }),
            new CameraFavourite("camera.closeHidesInstead", c => c.HandHeld.CloseHidesCamera, (c, v) => c.HandHeld.CloseHidesCamera = v),
            new CameraFavourite("camera.flyMode", c => c.IsFlyModeEnabled, (c, v) => c.SetFlyModeEnabled(v)),
            new CameraFavourite("camera.flyOnMenu", c => c.showFlyOnMainMenu, (c, v) => c.SetShowFlyOnMainMenu(v)),
            new CameraFavourite("camera.flyMomentum", c => c.useMomentum, (c, v) => c.useMomentum = v),
            new CameraFavourite("camera.flyMovementFollowsPitch", c => c.vrFlyMovementFollowsPitch, (c, v) => c.SetVRFlyMovementFollowsPitch(v)),
            new CameraFavourite("camera.leftHandFly", c => c.vrLeftHandFlyEnabled, (c, v) => c.SetVRLeftHandFlyEnabled(v)),
            new CameraFavourite("camera.rightHandFlyRotate", c => c.vrRightHandFlyRotateEnabled, (c, v) => c.SetVRRightHandFlyRotateEnabled(v)),
            new CameraFavourite("camera.autoLevel", c => c.useAutoLeveling, (c, v) => c.useAutoLeveling = v),
            new CameraFavourite("camera.resize", c => c.ResizeWithGesture, (c, v) => c.SetResizeWithGesture(v)),
            new CameraFavourite("camera.puckPreview", c => c.puckLookAtPreview, (c, v) => c.SetPuckLookAtPreview(v)),
            new CameraFavourite("camera.rollControl", c => c.cameraRollEnabled, (c, v) => c.SetCameraRollEnabled(v)),
            new CameraFavourite("camera.directToScreen", c => c.DirectToScreen, (c, v) => c.SetDirectToScreen(v)),
            new CameraFavourite("camera.exposureOnCamera", c => c.HandHeld.ShowExposureOnCamera, (c, v) => c.HandHeld.SetExposureOnCameraVisible(v)),
            new CameraFavourite("camera.gif.loop", c => c.GifLoop, (c, v) => c.GifLoop = v),
            new CameraFavourite("camera.gif.dither", c => c.GifDither, (c, v) => c.GifDither = v),
            new CameraFavourite("camera.video.timeLimit", c => c.VideoRecordingTimeLimit, (c, v) => c.VideoRecordingTimeLimit = v),
            new CameraFavourite("camera.video.autoNewClip", c => c.VideoContinuousClips, (c, v) => c.VideoContinuousClips = v),
            new CameraFavourite("camera.passes.separate", c => c.SeparatePasses, (c, v) => c.SetSeparatePasses(v)),
            new CameraFavourite("camera.anchorFollowsBody", c => c.anchorFollowsBody, (c, v) => c.anchorFollowsBody = v),
            new CameraFavourite("camera.body.flash", c => c.FlashEnabled, (c, v) => c.SetFlashEnabled(v)),
        };

        private static void RegisterFavourites()
        {
            for (int Index = 0; Index < CameraFavourites.Length; Index++)
            {
                CameraFavourite favourite = CameraFavourites[Index];
                BasisSettingFavourites.RegisterLive(favourite.Key,
                    () => FavouriteCamera(out BasisHandHeldCamera camera) ? favourite.Get(camera) : null,
                    v =>
                    {
                        if (FavouriteCamera(out BasisHandHeldCamera camera)) favourite.Set(camera, v);
                    });
            }
        }

        private static void NotifyFavourites()
        {
            for (int Index = 0; Index < CameraFavourites.Length; Index++) BasisSettingFavourites.LiveChanged(CameraFavourites[Index].Key);
        }

        /// <summary>Marks a panel toggle as favouritable when its title key has a live favourite above.</summary>
        private static PanelToggle Favouritable(PanelToggle toggle, string key)
        {
            if (toggle != null && BasisSettingFavourites.IsLive(key)) toggle.LiveFavouriteKey = key;
            return toggle;
        }

        /// <summary>The camera a favourite acts on: the one open in the panel, else the first one spawned.</summary>
        private static bool FavouriteCamera(out BasisHandHeldCamera camera)
        {
            camera = _instance != null ? _instance._activeCamera : null;
            if (camera != null) return true;
            IReadOnlyList<BasisHandHeldCamera> cameras = BasisHandHeldCameraRegistry.Cameras;
            for (int Index = 0; Index < cameras.Count; Index++)
            {
                camera = cameras[Index];
                if (camera != null) return true;
            }
            camera = null;
            return false;
        }
    }
}
