using Unity.Scripting.LifecycleManagement;
using System;
using System.IO;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using UnityEngine;

[Serializable]
public class BasisMirrorSettings
{
    public const int CurrentVersion = 1;

    public int settingsVersion = CurrentVersion;

    public float surfaceWidth = 1f;
    public float surfaceHeight = 1f;
    public bool grabbable = true;
    public bool moveWithPlayspace;

    public int renderWidth = 2048;
    public int renderHeight = 2048;
    public int msaaSamples = 4;
    public int depthBits = 24;
    public int secondaryViewerMaxSize = 1024;

    public float nearClip = 0.01f;
    public float farClip = 25f;
    public float clipPlaneOffset = 0.05f;

    public int reflectionLayers = 477;

    public int clearFlags = (int)BasisSDKMirror.MirrorClearFlags.FromReferenceCamera;
    public Color clearColor = Color.black;
    public bool cutout;

    public int updateEveryNthFrame = 1;
    public float fullRateDistance = 4f;
    public float halfRateDistance = 10f;
    public float cullDistance = 25f;

    // Deliberately not exposed or persisted: the mirror camera enabling post processing stacks it
    // on top of the pass the player camera already ran.
    // public bool renderPostProcessing;
    public bool occlusionCulling = true;
    public bool renderShadows = true;
}

[AutoStaticsCleanup]
public static partial class BasisMirrorSettingsStore
{
    public const string MirrorSettingsJson = "MirrorSettings.json";

    private static BasisMirrorSettings current;
    private static bool loaded;

    public static BasisMirrorSettings Current
    {
        get
        {
            if (!loaded)
            {
                loaded = true;
                current = Load();
            }
            return current;
        }
    }

    private static string SettingsPath => Path.Combine(Application.persistentDataPath, MirrorSettingsJson);

    private static BasisMirrorSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return new BasisMirrorSettings();
            return JsonUtility.FromJson<BasisMirrorSettings>(File.ReadAllText(path)) ?? new BasisMirrorSettings();
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[BasisMirrorSettings] Load failed: {ex.Message}");
            return new BasisMirrorSettings();
        }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonUtility.ToJson(Current, true));
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[BasisMirrorSettings] Save failed: {ex.Message}");
        }
    }

    public static bool IsPersonalMirror(BasisSDKMirror mirror)
    {
        return mirror != null && mirror.TryGetComponent(out BasisPersonalMirror _);
    }

    public static bool IsPersisted(BasisSDKMirror mirror)
    {
        return IsPersonalMirror(mirror) &&
               mirror.GetComponentInParent<BasisCalibrationMirrorRelay>(true) == null;
    }

    public static bool PersonalMirrorGrabbable(BasisSDKMirror mirror)
    {
        return IsPersonalMirror(mirror) &&
               mirror.TryGetComponent(out BasisPickupInteractable pickup) &&
               pickup.InteractableEnabled;
    }

    public static void SetPersonalMirrorGrabbable(BasisSDKMirror mirror, bool grabbable)
    {
        if (!IsPersonalMirror(mirror)) return;
        if (mirror.TryGetComponent(out BasisPickupInteractable pickup))
        {
            pickup.InteractableEnabled = grabbable;
        }
    }

    public static bool PersonalMirrorMovesWithPlayspace(BasisSDKMirror mirror)
    {
        return IsPersonalMirror(mirror) &&
               BasisLocalPlayer.Instance != null &&
               mirror.transform.parent == BasisLocalPlayer.Instance.transform;
    }

    public static void SetPersonalMirrorMovesWithPlayspace(BasisSDKMirror mirror, bool moveWithPlayspace)
    {
        if (!IsPersonalMirror(mirror)) return;

        Transform parent = moveWithPlayspace && BasisLocalPlayer.Instance != null
            ? BasisLocalPlayer.Instance.transform
            : BasisDeviceManagement.Instance != null ? BasisDeviceManagement.Instance.transform : null;

        if (mirror.transform.parent != parent)
        {
            mirror.transform.SetParent(parent, true);
        }
    }

    public static void ApplyPersonalMirrorBehavior(BasisSDKMirror mirror)
    {
        if (!IsPersisted(mirror)) return;

        BasisMirrorSettings settings = Current;
        SetPersonalMirrorGrabbable(mirror, settings.grabbable);
        SetPersonalMirrorMovesWithPlayspace(mirror, settings.moveWithPlayspace);
    }

    public static void CaptureFrom(BasisSDKMirror mirror)
    {
        if (!IsPersisted(mirror)) return;

        BasisMirrorSettings settings = Current;
        if (mirror.HasSurfaceSize)
        {
            Vector2 surface = mirror.SurfaceSize;
            settings.surfaceWidth = surface.x;
            settings.surfaceHeight = surface.y;
        }
        settings.grabbable = PersonalMirrorGrabbable(mirror);
        settings.moveWithPlayspace = PersonalMirrorMovesWithPlayspace(mirror);
        settings.renderWidth = mirror.ReflectionWidth;
        settings.renderHeight = mirror.ReflectionHeight;
        settings.msaaSamples = mirror.MsaaSamples;
        settings.depthBits = mirror.DepthBits;
        settings.secondaryViewerMaxSize = mirror.SecondaryViewerResolutionCap;
        settings.nearClip = mirror.NearClip;
        settings.farClip = mirror.FarClip;
        settings.clipPlaneOffset = mirror.SurfaceClipOffset;
        settings.reflectionLayers = mirror.ReflectionLayers.value;
        settings.clearFlags = (int)mirror.ConfiguredClearFlags;
        settings.clearColor = mirror.ConfiguredClearColor;
        settings.cutout = mirror.CutoutEnabled;
        settings.updateEveryNthFrame = mirror.UpdateInterval;
        settings.fullRateDistance = mirror.FullRateRange;
        settings.halfRateDistance = mirror.HalfRateRange;
        settings.cullDistance = mirror.CullRange;
        // settings.renderPostProcessing = mirror.UsePostProcessing;
        settings.occlusionCulling = mirror.UseOcclusionCulling;
        settings.renderShadows = mirror.RenderShadows;
        settings.settingsVersion = BasisMirrorSettings.CurrentVersion;

        Save();
    }

    public static void ApplyTo(BasisSDKMirror mirror)
    {
        if (!IsPersonalMirror(mirror)) return;

        BasisMirrorSettings settings = Current;
        mirror.NearClip = settings.nearClip;
        mirror.FarClip = settings.farClip;
        mirror.SurfaceClipOffset = settings.clipPlaneOffset;
        mirror.ReflectionLayers = settings.reflectionLayers;
        mirror.ClearFlags = (BasisSDKMirror.MirrorClearFlags)settings.clearFlags;
        mirror.ClearColor = settings.clearColor;
        mirror.UpdateInterval = settings.updateEveryNthFrame;
        mirror.FullRateRange = settings.fullRateDistance;
        mirror.HalfRateRange = settings.halfRateDistance;
        mirror.CullRange = settings.cullDistance;
        // mirror.UsePostProcessing = settings.renderPostProcessing;
        mirror.UseOcclusionCulling = settings.occlusionCulling;
        mirror.RenderShadows = settings.renderShadows;
        mirror.SecondaryViewerResolutionCap = settings.secondaryViewerMaxSize;
        mirror.SurfaceSize = new Vector2(settings.surfaceWidth, settings.surfaceHeight);
        mirror.SetTargetShape(settings.renderWidth, settings.renderHeight, settings.depthBits, settings.msaaSamples);

        // Last: the cutout snapshots the clear settings it is overriding, so they must already be
        // the user's own values or turning it back off would restore the transparent clear.
        mirror.SetCutout(settings.cutout);
    }

    public static void ResetToDefaults(BasisSDKMirror mirror)
    {
        current = new BasisMirrorSettings();
        loaded = true;
        ApplyTo(mirror);
        ApplyPersonalMirrorBehavior(mirror);
        Save();
    }
}
