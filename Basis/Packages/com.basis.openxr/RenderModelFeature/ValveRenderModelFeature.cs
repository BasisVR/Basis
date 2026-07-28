using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
using UnityEditor.PackageManager;
#endif
using UnityEngine;
using UnityEngine.XR.OpenXR.Features;
using Object = UnityEngine.Object;

#if UNITY_EDITOR
[OpenXRFeature(UiName = "Valve Utils: Render Model Sample",
    Desc = "Support for OpenXR render models",
    Company = "Valve",
    Version = "0.1.0",
    BuildTargetGroups = new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android },
    FeatureId = "com.valve.openxr.feature.render_models",
    OpenxrExtensionStrings = "XR_EXT_render_model XR_EXT_interaction_render_model XR_EXT_uuid"
)]
#endif
public partial class ValveRenderModelFeature : OpenXRFeature
{
    private ulong _xrInstance;
    private ulong _xrSession;

    // From XR_EXT_render_model
    private PFN_xrCreateRenderModelEXT _xrCreateRenderModelEXT;
    private PFN_xrGetRenderModelPropertiesEXT _xrGetRenderModelPropertiesEXT;
    private PFN_xrCreateRenderModelAssetEXT _xrCreateRenderModelAssetEXT;
    private PFN_xrGetRenderModelAssetDataEXT _xrGetRenderModelAssetDataEXT;
    private PFN_xrGetRenderModelAssetPropertiesEXT _xrGetRenderModelAssetPropertiesEXT;
    private PFN_xrDestroyRenderModelAssetEXT _xrDestroyRenderModelAssetEXT;
    private PFN_xrDestroyRenderModelEXT _xrDestroyRenderModelEXT;
    private PFN_xrGetPredictedDisplayTime _xrGetPredictedDisplayTime;
    private PFN_xrGetRenderModelStateEXT _xrGetRenderModelStateEXT;
    // From XR_EXT_interaction_render_model
    private PFN_xrEnumerateInteractionRenderModelIdsEXT _xrEnumerateInteractionRenderModelIdsEXT;
    private PFN_xrEnumerateRenderModelSubactionPathsEXT _xrEnumerateRenderModelSubactionPathsEXT;

    public class ModelNodeState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Visible;
    }

    public bool IsRenderModelAvailable(bool isLeftHand)
    {
        if (_xrSession == 0
            || _xrEnumerateInteractionRenderModelIdsEXT == null
            || _xrEnumerateRenderModelSubactionPathsEXT == null)
        {
            return false;
        }

        return GetRenderModelId(isLeftHand) != 0;
    }

    private XrRenderModelIdEXT GetRenderModelId(bool isLeftHand)
    {
        return FindRenderModelByPath(GetRenderModelPath(isLeftHand));
    }

    private static string GetRenderModelPath(bool isLeftHand)
    {
        return $"/user/hand/{(isLeftHand ? "left" : "right")}";
    }

    public bool GetRenderModelAssetData(
        bool isLeftHand,
        out ulong renderModelHandle,
        out ulong renderModelAssetHandle,
        out List<string> animatableNodeNames,
        out byte[] renderModelAssetBytes)
    {
        renderModelHandle = 0;
        renderModelAssetHandle = 0;
        animatableNodeNames = null;
        renderModelAssetBytes = null;

        // Enumerate the render models and search for one with the desired model path
        XrRenderModelIdEXT renderModelId = GetRenderModelId(isLeftHand);
        if (renderModelId == 0)
        {
            Debug.LogError($"No render model found for path: {GetRenderModelPath(isLeftHand)}");
            return false;
        }

        if (!CreateRenderModel(renderModelId, ref renderModelHandle))
        {
            return false;
        }
        XrUuidEXT assetId = default(XrUuidEXT);
        if (!GetRenderModelAssetId(renderModelHandle, ref assetId))
        {
            return false;
        }
        
        if (!CreateRenderModelAsset(assetId, ref renderModelAssetHandle))
        {
            return false;
        }

        LoadAssetData(renderModelAssetHandle, out renderModelAssetBytes);
        var animatableNodeCount = GetAnimatableNodeCount(renderModelHandle);
        animatableNodeNames = GetAnimatableNodeNames(renderModelAssetHandle, animatableNodeCount);
        return true;
    }

    public bool GetRenderModelStates(ulong renderModel, ref List<ModelNodeState> nodeStates)
    {
        Debug.Assert(renderModel != 0);
        
        XrRenderModelNodeStateEXT[] states = new XrRenderModelNodeStateEXT[nodeStates.Count];
        GCHandle hStates = GCHandle.Alloc(states, GCHandleType.Pinned);

        try
        {
            var renderModelState = new XrRenderModelStateEXT
            {
                type = XrStructureType.XR_TYPE_RENDER_MODEL_STATE_EXT,
                nodeStateCount = (uint)nodeStates.Count,
                nodeStates = hStates.AddrOfPinnedObject()
            };

            var stateGetInfo = new XrRenderModelStateGetInfoEXT
            {
                type = XrStructureType.XR_TYPE_RENDER_MODEL_STATE_GET_INFO_EXT,
                displayTime = ToXrTime(Time.time)
            };

            if (!XrSucceeded(_xrGetRenderModelStateEXT(renderModel, ref stateGetInfo, ref renderModelState)))
            {
                Debug.LogError($"_xrGetRenderModelStateEXT failed!");
                return false;
            }
            
            for (var i = 0; i < states.Length; ++i)
            {
                nodeStates[i].Visible = states[i].isVisible != 0;
                nodeStates[i].Position = states[i].nodePose.Position.AsVector3();
                nodeStates[i].Rotation = states[i].nodePose.Orientation.AsQuaternion();
            }
        }
        finally
        {
            hStates.Free();
        }
        
        return true;
    }
    
    public bool DestroyRenderModel(ulong asset)
    {
        if (!XrSucceeded(_xrDestroyRenderModelAssetEXT(asset)))
        {
            Debug.LogError($"xrDestroyRenderModelAssetEXT failed!");
            return false;
        }

        return true;
    }
    
    
 #region "OpenXRFeature overrides"   
 
    protected override bool OnInstanceCreate(ulong xrInstance)
    {
        _xrInstance = xrInstance;
        
        if (xrGetInstanceProcAddr == IntPtr.Zero)
        {
            Debug.LogError("[RenderModelFeature] xrGetInstanceProcAddr is null.");
            return false;
        }

        _xrGetRenderModelPropertiesEXT = GetOpenXrInstanceProc<PFN_xrGetRenderModelPropertiesEXT>("xrGetRenderModelPropertiesEXT");
        _xrCreateRenderModelEXT = GetOpenXrInstanceProc<PFN_xrCreateRenderModelEXT>("xrCreateRenderModelEXT");
        _xrCreateRenderModelAssetEXT = GetOpenXrInstanceProc<PFN_xrCreateRenderModelAssetEXT>("xrCreateRenderModelAssetEXT");
        _xrGetRenderModelAssetDataEXT = GetOpenXrInstanceProc<PFN_xrGetRenderModelAssetDataEXT>("xrGetRenderModelAssetDataEXT");
        _xrGetRenderModelAssetPropertiesEXT = GetOpenXrInstanceProc<PFN_xrGetRenderModelAssetPropertiesEXT>("xrGetRenderModelAssetPropertiesEXT");
        _xrDestroyRenderModelAssetEXT = GetOpenXrInstanceProc<PFN_xrDestroyRenderModelAssetEXT>("xrDestroyRenderModelAssetEXT");
        _xrDestroyRenderModelEXT = GetOpenXrInstanceProc<PFN_xrDestroyRenderModelEXT>("xrDestroyRenderModelEXT");
        _xrGetRenderModelStateEXT = GetOpenXrInstanceProc<PFN_xrGetRenderModelStateEXT>("xrGetRenderModelStateEXT");
        _xrEnumerateInteractionRenderModelIdsEXT = GetOpenXrInstanceProc<PFN_xrEnumerateInteractionRenderModelIdsEXT>("xrEnumerateInteractionRenderModelIdsEXT");
        _xrEnumerateRenderModelSubactionPathsEXT = GetOpenXrInstanceProc<PFN_xrEnumerateRenderModelSubactionPathsEXT>("xrEnumerateRenderModelSubactionPathsEXT");

        return true;
    }
    
    protected override void OnSessionCreate(ulong session)
    {
        _xrSession = session;
    }

#if UNITY_EDITOR
        protected override void GetValidationChecks(List<OpenXRFeature.ValidationRule> results, BuildTargetGroup target)
        {
            const string gltfPackageName = "com.unity.cloud.gltfast";
            string[] requiredAssetFilenames = new[] { "glTF-pbrMetallicRoughness.shadergraph" };

#if !UNITY_GLTFAST
            results.Add(new ValidationRule(this)
            {
                message = "This feature requires the GLTFast package to be installed.",
                error = true,
                checkPredicate = () => false,
                fixIt = () =>
                {
                    Client.Add("com.unity.cloud.gltfast");
                },
                fixItAutomatic = true,
                fixItMessage = "Install the Unity GLTFast package."
            });
#else
            // Preloading shaders as indicated in Unity glTFast docs
            // https://docs.unity3d.com/Packages/com.unity.cloud.gltfast@6.0/manual/ProjectSetup.html
            // Automating the process here for Steam Frame under XR Project Validation rules for convenience.
            results.Add(new ValidationRule(this)
            {
                message = "GLTFast needs to preload expected shaders/variants to avoid being stripped out of builds.",
                error = false,
                checkPredicate = () =>
                {
                    var preloaded = PlayerSettings.GetPreloadedAssets() ?? Array.Empty<Object>();
                    var preloadedGLTFFiles = preloaded
                        .Select(a => AssetDatabase.GetAssetPath(a))
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Where(p => p.StartsWith($"Packages/{gltfPackageName}"))
                        .Select(Path.GetFileName)
                        .ToList();
                   
                    // Find any missing required assets
                    foreach (var filename in requiredAssetFilenames)
                    {
                        if (!preloadedGLTFFiles.Contains(filename))
                        {
                            return false;
                        }
                    }
                    return true;
                },
                fixIt = () =>
                {
                    var packageFolder = Path.GetFullPath($"Packages/{gltfPackageName}");
                    var preloaded = PlayerSettings.GetPreloadedAssets().ToHashSet();
                    foreach (var filename in requiredAssetFilenames)
                    {
                        var files = Directory.GetFiles(packageFolder, filename, SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            var path = Path.Combine("Packages", gltfPackageName, file.Remove(0, packageFolder.Length+1));
                            var asset = AssetDatabase.LoadMainAssetAtPath(path);
                            preloaded.Add(asset);
                        }
                    }
                    PlayerSettings.SetPreloadedAssets(preloaded.Where(p => p).ToArray());
                },
                fixItAutomatic = true,
                fixItMessage = "Add top level shaders to preloaded assets."
            });
#endif
        }        
#endif        
    
#endregion

    private ulong RenderModelIdToHandle(ulong session, XrRenderModelIdEXT modelId)
    {
        var createInfo = new XrRenderModelCreateInfoEXT
        {
            type = XrStructureType.XR_TYPE_RENDER_MODEL_CREATE_INFO_EXT,
            renderModelId = modelId,
            gltfExtensionCount = 0,
            gltfExtensions = IntPtr.Zero,
        };

        if (!XrSucceeded(_xrCreateRenderModelEXT(session, ref createInfo, out var hRenderModel)))
        {
            Debug.LogError($"Failed to create render model handle!");
            return 0;
        }

        return hRenderModel;
    }

    private bool CreateRenderModel(XrRenderModelIdEXT renderModelId, ref ulong handle)
    {
        Debug.Assert(_xrSession != 0);

        var createInfo = new XrRenderModelCreateInfoEXT
        {
            type = XrStructureType.XR_TYPE_RENDER_MODEL_CREATE_INFO_EXT,
            renderModelId = renderModelId,
            gltfExtensionCount = 0,
            gltfExtensions = IntPtr.Zero
        };
        
        if (!XrSucceeded(_xrCreateRenderModelEXT(_xrSession, ref createInfo, out ulong renderModel)))
        {
            Debug.LogError($"CreateRenderModelEXT failed!");
            return false;
        }

        handle = renderModel;
        return true;
    }
    
    private bool GetRenderModelAssetId(ulong hRenderModel, ref XrUuidEXT renderModelId)
    {
        var propGetInfo = new XrRenderModelPropertiesGetInfoEXT { type = XrStructureType.XR_TYPE_RENDER_MODEL_PROPERTIES_GET_INFO_EXT };
        var props = new XrRenderModelPropertiesEXT { type = XrStructureType.XR_TYPE_RENDER_MODEL_PROPERTIES_EXT };

        if (!XrSucceeded(_xrGetRenderModelPropertiesEXT(hRenderModel, ref propGetInfo, ref props)))
        {
            Debug.LogError($"xrGetRenderModelPropertiesEXT failed!");
            return false;
        }
        
        renderModelId = props.assetId;
        return true;
    }

    private int GetAnimatableNodeCount(ulong hRenderModel)
    {
        var propGetInfo = new XrRenderModelPropertiesGetInfoEXT { type = XrStructureType.XR_TYPE_RENDER_MODEL_PROPERTIES_GET_INFO_EXT };
        var props = new XrRenderModelPropertiesEXT { type = XrStructureType.XR_TYPE_RENDER_MODEL_PROPERTIES_EXT };

        if (!XrSucceeded(_xrGetRenderModelPropertiesEXT(hRenderModel, ref propGetInfo, ref props)))
        {
            Debug.LogError($"xrGetRenderModelPropertiesEXT failed!");
            return -1;
        }
        
        return (int)props.animatableNodeCount;
    }
    
    private bool CreateRenderModelAsset(XrUuidEXT assetId, ref ulong hAsset)
    {
        Debug.Assert(_xrSession != 0);

        var assetCreate = new XrRenderModelAssetCreateInfoEXT
        {
            type = XrStructureType.XR_TYPE_RENDER_MODEL_ASSET_CREATE_INFO_EXT,
            cacheId = assetId
        };

        if (!XrSucceeded(_xrCreateRenderModelAssetEXT(_xrSession, ref assetCreate, out ulong asset)))
        {
            Debug.LogError($"_xrCreateRenderModelAssetEXT failed!");
            return false;
        }
        
        hAsset = asset;
        return true;
    }

    private void LoadAssetData(ulong hAsset, out byte[] gltfData)
    {
        gltfData = null;

        // Two-call idiom to get model buffer size
        var assetGetInfo = new XrRenderModelAssetDataGetInfoEXT { type = XrStructureType.XR_TYPE_RENDER_MODEL_ASSET_DATA_GET_INFO_EXT };
        var assetData = new XrRenderModelAssetDataEXT
        {
            type = XrStructureType.XR_TYPE_RENDER_MODEL_ASSET_DATA_EXT,
            bufferCapacityInput = 0,
            bufferCountOutput = 0,
            buffer = IntPtr.Zero
        };

        _xrGetRenderModelAssetDataEXT(hAsset, ref assetGetInfo, out assetData );

        gltfData = new byte[assetData.bufferCountOutput];
        var handle = GCHandle.Alloc(gltfData, GCHandleType.Pinned);
        assetData.bufferCapacityInput = assetData.bufferCountOutput;
        assetData.buffer = handle.AddrOfPinnedObject();

        _xrGetRenderModelAssetDataEXT(hAsset, ref assetGetInfo, out assetData);
        handle.Free();
    }

    private XrRenderModelIdEXT FindRenderModelByPath(string path)
    {
        Debug.Assert(_xrSession != 0);
        Debug.Assert(_xrEnumerateInteractionRenderModelIdsEXT != null);

        var info = new XrInteractionRenderModelIdsEnumerateInfoEXT { type = XrStructureType.XR_TYPE_INTERACTION_RENDER_MODEL_IDS_ENUMERATE_INFO_EXT };
        _xrEnumerateInteractionRenderModelIdsEXT(_xrSession, ref info, 0, out var count, null);
        var ids = new XrRenderModelIdEXT[count];
        _xrEnumerateInteractionRenderModelIdsEXT(_xrSession, ref info, count, out count, ids);

        var subActionPathInfo = new XrInteractionRenderModelSubactionPathInfoEXT { type = XrStructureType.XR_TYPE_INTERACTION_RENDER_MODEL_SUBACTION_PATH_INFO_EXT };
        foreach (var id in ids)
        {
            ulong renderModel = RenderModelIdToHandle(_xrSession, id);
            _xrEnumerateRenderModelSubactionPathsEXT(renderModel, ref subActionPathInfo, 0, out var pathCount, IntPtr.Zero);
            var xrPaths = new XrPath[pathCount];
            GCHandle hXrPaths = GCHandle.Alloc(xrPaths, GCHandleType.Pinned);
            try
            {
                IntPtr xrPathsPtr = hXrPaths.AddrOfPinnedObject();
                _xrEnumerateRenderModelSubactionPathsEXT(renderModel, ref subActionPathInfo, (uint)xrPaths.Length, out pathCount, xrPathsPtr);
                if (xrPaths.Any(p => PathToString(p.Value).Equals(path)))
                {
                    return id;
                }
            }
            finally
            {
                hXrPaths.Free();                
            }
        }

        Debug.LogWarning("[RenderModelLoader] No render models found.");
        return 0;
    }

    private List<string> GetAnimatableNodeNames(ulong asset, int nodeCount)
    {
        if (asset == 0 || nodeCount <= 0)
        {
            Debug.LogError($"GetanimatableNodeNames - bad input args!");
            return new List<string>();
        }

        IntPtr nodeArrayPtr = XrRenderModelAssetNodePropertiesEXT.Allocate(nodeCount);        

        var list = new List<string>(nodeCount);

        try
        {
            var getInfo = new XrRenderModelAssetPropertiesGetInfoEXT { type = XrStructureType.XR_TYPE_RENDER_MODEL_ASSET_PROPERTIES_GET_INFO_EXT };
            var assetProps = new XrRenderModelAssetPropertiesEXT
            {
                type = XrStructureType.XR_TYPE_RENDER_MODEL_ASSET_PROPERTIES_EXT,
                nodePropertyCount = (uint)nodeCount,
                nodeProperties = nodeArrayPtr
            };

            if (!XrSucceeded(_xrGetRenderModelAssetPropertiesEXT(asset, ref getInfo, ref assetProps)))
            {
                Debug.LogError($"xrGetRenderModelAssetPropertiesEXT failed!");
                return list;
            }

            // Convert names to strings
            for (int i = 0; i < nodeCount; i++)
            {
                IntPtr elemPtr = IntPtr.Add(nodeArrayPtr, i * XrRenderModelAssetNodePropertiesEXT.stride);
                list.Add(XrRenderModelAssetNodePropertiesEXT.HandleToUtf8String(elemPtr));
            }
        }
        finally
        {
            XrRenderModelAssetNodePropertiesEXT.Free(nodeArrayPtr);
        }
            
        return list;
    }
}
