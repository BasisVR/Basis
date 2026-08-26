using Basis.Scripts.Device_Management.Devices;
using UnityEngine;
#if UNITY_GLTFAST
using System.Collections;
using System.Threading.Tasks;
using Basis.Scripts.Device_Management;
using GLTFast;
using UnityEngine.XR.OpenXR;
#endif

/// <summary>
/// Loads the real controller model for the active OpenXR runtime at runtime through Valve's
/// <c>XR_EXT_render_model</c> feature (glTF binary imported with glTFast), so controllers render as
/// their physical geometry instead of the generic sphere fallback. Only runtimes implementing the
/// render-model extensions (SteamVR / Steam Frame) return a model; elsewhere the owner keeps its
/// baked/sphere fallback. Requires the <c>com.unity.cloud.gltfast</c> package.
/// </summary>
public class BasisOpenXRRenderModel : MonoBehaviour
{
#if UNITY_GLTFAST
    private const float RenderModelCheckInterval = 0.25f;
    private const float RenderModelWaitTimeout = 10f;

    private BasisInput owner;
    private ValveRenderModelFeature feature;
    private Coroutine loadRoutine;

    /// <summary>
    /// Local rotation (Euler) applied to reconcile the glTF/OpenXR render-model authoring axes with
    /// the device node's frame. Defaults to a 180° yaw for SteamVR-authored models.
    /// </summary>
    public static Vector3 RotationOffsetEuler = new Vector3(0f, 180f, 0f);

    /// <summary>
    /// Shared entry point for a device's <see cref="BasisInput.TryShowRuntimeDeviceModel"/>. Spawns a
    /// loader for the controller hand when the render-model extension is enabled, reusing the one
    /// already in flight. Returns true when the runtime path is handling the visual.
    /// </summary>
    public static bool TryLoad(BasisInput owner, bool isLeftHand, ref BasisOpenXRRenderModel active)
    {
        if (!OpenXRRuntime.IsExtensionEnabled("XR_EXT_render_model"))
        {
            return false;
        }
        ValveRenderModelFeature feature = OpenXRSettings.Instance != null
            ? OpenXRSettings.Instance.GetFeature<ValveRenderModelFeature>()
            : null;
        if (feature == null || !feature.enabled)
        {
            return false;
        }
        if (active != null)
        {
            return true;
        }
        GameObject host = new GameObject("OpenXRRenderModel");
        host.transform.SetParent(owner.GetVisualAnchor(), false);
        active = host.AddComponent<BasisOpenXRRenderModel>();
        active.Load(feature, isLeftHand, owner);
        return true;
    }

    private void Load(ValveRenderModelFeature renderModelFeature, bool isLeftHand, BasisInput basisInput)
    {
        feature = renderModelFeature;
        owner = basisInput;
        loadRoutine = StartCoroutine(LoadRoutine(isLeftHand));
    }

    private IEnumerator LoadRoutine(bool isLeftHand)
    {
        float waited = 0f;
        while (!feature.IsRenderModelAvailable(isLeftHand))
        {
            if (waited >= RenderModelWaitTimeout)
            {
                Fallback();
                yield break;
            }
            yield return new WaitForSecondsRealtime(RenderModelCheckInterval);
            waited += RenderModelCheckInterval;
        }

        if (!TryGetAssetData(isLeftHand, out byte[] gltfBytes) || gltfBytes == null || gltfBytes.Length == 0)
        {
            Fallback();
            yield break;
        }

        GltfImport gltf = new GltfImport();
        Task<bool> loadTask = gltf.Load(gltfBytes);
        yield return new WaitUntil(() => loadTask.IsCompleted);
        if (loadTask.Status != TaskStatus.RanToCompletion || !loadTask.Result)
        {
            BasisDebug.LogError("OpenXR render model glTF import failed.");
            Fallback();
            yield break;
        }

        Task<bool> instantiateTask = gltf.InstantiateMainSceneAsync(transform);
        yield return new WaitUntil(() => instantiateTask.IsCompleted);
        if (instantiateTask.Status != TaskStatus.RanToCompletion || !instantiateTask.Result)
        {
            BasisDebug.LogError("OpenXR render model glTF instantiation failed.");
            Fallback();
            yield break;
        }

        loadRoutine = null;
        AttachVisualTracker();
    }

    private bool TryGetAssetData(bool isLeftHand, out byte[] bytes)
    {
        bytes = null;
        ulong modelHandle = 0;
        ulong assetHandle = 0;
        try
        {
            return feature.GetRenderModelAssetData(isLeftHand, out modelHandle, out assetHandle, out _, out bytes);
        }
        catch (System.Exception e)
        {
            BasisDebug.LogError($"OpenXR render model query failed: {e.Message}");
            return false;
        }
        finally
        {
            if (assetHandle != 0)
            {
                feature.DestroyRenderModel(assetHandle);
            }
            if (modelHandle != 0)
            {
                feature.DestroyRenderModelHandle(modelHandle);
            }
        }
    }

    private void AttachVisualTracker()
    {
        if (owner == null)
        {
            return;
        }
        BasisVisualTracker tracker = gameObject.AddComponent<BasisVisualTracker>();
        tracker.ModelRotationOffset = Quaternion.Euler(RotationOffsetEuler);
        owner.BasisVisualTracker = tracker;
        tracker.Initialization(owner);
    }

    private void Fallback()
    {
        loadRoutine = null;
        BasisInput recover = owner;
        owner = null;
        if (recover != null)
        {
            recover.ShowBakedOrFallbackVisual();
        }
        if (this != null)
        {
            Destroy(gameObject);
        }
    }

    public void OnDestroy()
    {
        if (loadRoutine != null)
        {
            StopCoroutine(loadRoutine);
            loadRoutine = null;
        }
    }
#else
    public static bool TryLoad(BasisInput owner, bool isLeftHand, ref BasisOpenXRRenderModel active)
    {
        return false;
    }
#endif
}
