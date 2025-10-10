using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEngine.AddressableAssets;

[assembly: InternalsVisibleTo("HVR.Basis.Comms.Editor")]
namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Automatic Face Tracking")]
    [HelpURL("https://docs.hai-vr.dev/docs/basis/avatar-customization/face-tracking")]
    public class AutomaticFaceTracking : MonoBehaviour, IHVRInitializable
    {
        [SerializeField] internal bool useCustomMultiplier;
        [SerializeField] internal float eyeTrackingMultiplyX = 1f;
        [SerializeField] internal float eyeTrackingMultiplyY = 1f;

        [SerializeField] internal bool useOverrideDefinitionFiles;
        [SerializeField] internal BlendshapeActuationDefinitionFile[] overrideDefinitionFiles = Array.Empty<BlendshapeActuationDefinitionFile>();

        [SerializeField] internal bool useSupplementalDefinitionFiles;
        [SerializeField] internal BlendshapeActuationDefinitionFile[] supplementalDefinitionFiles = Array.Empty<BlendshapeActuationDefinitionFile>();

        private static BlendshapeActuationDefinitionFile _ueHandle = null;
        private static BlendshapeActuationDefinitionFile _arKitHandle = null;

        private BasisAvatar _avatar;

        // Exposed to the Unity editor for this component
        [NonSerialized] internal bool successful;
        [NonSerialized] internal NamingConvention namingConvention;
        [NonSerialized] internal List<SkinnedMeshRenderer> renderers;
        [NonSerialized] internal OSCAcquisition oscAcquisition;
        [NonSerialized] internal BlendshapeActuation blendshapeActuation;
        [NonSerialized] internal EyeTrackingBoneActuation eyeTrackingBoneActuation;

        private bool _isWearer;

        private void Awake()
        {
            overrideDefinitionFiles = HVRCommsUtil.SlowSanitizeEndUserProvidedObjectArray(overrideDefinitionFiles);
            supplementalDefinitionFiles = HVRCommsUtil.SlowSanitizeEndUserProvidedObjectArray(supplementalDefinitionFiles);

            if (_avatar == null)
            {
                _avatar = HVRCommsUtil.GetAvatar(this);
            }
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
            _isWearer = isWearer;
            Discover();
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
        }

        private void Discover()
        {
            var smrs = _avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            var files = ResolveFilesOrNull(smrs, out namingConvention);
            if (files != null)
            {
                var foundSmrs = FindSkinnedMeshes(files, smrs);
                if (foundSmrs.Count > 0)
                {
                    SetupFaceTracking(files, foundSmrs);
                }
                else Failed();
            }
            else
            {
                Failed();
            }
        }

        public BlendshapeActuationDefinitionFile[] ResolveFilesOrNull(SkinnedMeshRenderer[] smrs, out NamingConvention resolvedNamingConvention)
        {
            _ueHandle ??= Addressables.LoadAssetAsync<BlendshapeActuationDefinitionFile>("HVR.Basis.Comms.FaceTracking.DefaultUnifiedExpressionsDefinitionFile").WaitForCompletion();
            _arKitHandle ??= Addressables.LoadAssetAsync<BlendshapeActuationDefinitionFile>("HVR.Basis.Comms.FaceTracking.DefaultARKitDefinitionFile").WaitForCompletion();

            if (useOverrideDefinitionFiles && overrideDefinitionFiles != null && overrideDefinitionFiles.Length != 0)
            {
                resolvedNamingConvention = NamingConvention.UserDefined;
                return AppendSupplemental(overrideDefinitionFiles);
            }
            else
            {
                resolvedNamingConvention = GuessNamingConvention(smrs);
                if (resolvedNamingConvention is NamingConvention.UnifiedExpressions or NamingConvention.ARKit)
                {
                    return AppendSupplemental(new[] { resolvedNamingConvention == NamingConvention.UnifiedExpressions ? _ueHandle : _arKitHandle });
                }
            }

            return null;
        }

        private BlendshapeActuationDefinitionFile[] AppendSupplemental(BlendshapeActuationDefinitionFile[] initial)
        {
            var toSearch = initial.ToList();
            if (useSupplementalDefinitionFiles && supplementalDefinitionFiles != null && supplementalDefinitionFiles.Length != 0)
            {
                toSearch.AddRange(supplementalDefinitionFiles);
            }
            return toSearch.ToArray();
        }

        private void Failed()
        {
            enabled = false;
        }

        private void SetupFaceTracking(BlendshapeActuationDefinitionFile[] definitionFiles, List<SkinnedMeshRenderer> smrs)
        {
            renderers = smrs;
            if (_isWearer)
            {
                oscAcquisition = CreateOSCAcquisitionIfNotExists();
            }

            blendshapeActuation = CreateGameObject(nameof(BlendshapeActuation), false)
                .AddComponent<BlendshapeActuation>();
            blendshapeActuation.AutoDefine(definitionFiles, smrs);
            blendshapeActuation.gameObject.SetActive(true);

            eyeTrackingBoneActuation = CreateGameObject(nameof(EyeTrackingBoneActuation), false)
                .AddComponent<EyeTrackingBoneActuation>();
            if (useCustomMultiplier)
            {
                eyeTrackingBoneActuation.multiplyX = eyeTrackingMultiplyX;
                eyeTrackingBoneActuation.multiplyY = eyeTrackingMultiplyY;
            }
            eyeTrackingBoneActuation.gameObject.SetActive(true);

            blendshapeActuation.OnHVRAvatarReady(_isWearer);
            eyeTrackingBoneActuation.OnHVRAvatarReady(_isWearer);

            successful = true;
        }

        private OSCAcquisition CreateOSCAcquisitionIfNotExists()
        {
            var acquisition = _avatar.GetComponentInChildren<OSCAcquisition>();
            if (acquisition == null)
            {
                var acquisitionGo = CreateGameObject(nameof(OSCAcquisition));

                acquisition = acquisitionGo.AddComponent<OSCAcquisition>();
                acquisition.OnAvatarReady(_isWearer);
            }

            return acquisition;
        }

        private GameObject CreateGameObject(string suffix, bool active = true)
        {
            var go = new GameObject
            {
                name = $"Generated__{suffix}",
                transform =
                {
                    parent = _avatar.transform,
                }
            };
            if (!active) go.SetActive(false);
            return go;
        }

        public enum NamingConvention
        {
            Unknown,
            UnifiedExpressions,
            ARKit,
            UserDefined
        }

        private NamingConvention GuessNamingConvention(SkinnedMeshRenderer[] smrs)
        {
            var unifiedExpressions = new HashSet<string> { "MouthRaiserLower", "MouthRaiserLowerLeft" };
            var arKit = new HashSet<string> { "mouthShrugLower" };
            foreach (var smr in smrs)
            {
                if (HasAnyBlendshape(smr, unifiedExpressions))
                {
                    return NamingConvention.UnifiedExpressions;
                }
                if (HasAnyBlendshape(smr, arKit))
                {
                    return NamingConvention.ARKit;
                }
            }

            return NamingConvention.Unknown;
        }

        public List<SkinnedMeshRenderer> FindSkinnedMeshes(BlendshapeActuationDefinitionFile[] definitionFiles, SkinnedMeshRenderer[] smrs)
        {
            var foundSmrs = new HashSet<SkinnedMeshRenderer>();
            foreach (var definitionFile in definitionFiles)
            {
                foundSmrs.UnionWith(FindSkinnedMeshes(definitionFile, smrs));
            }
            var foundSmrsAsList = foundSmrs.ToList();
            return foundSmrsAsList;
        }

        private List<SkinnedMeshRenderer> FindSkinnedMeshes(BlendshapeActuationDefinitionFile definitionFile, SkinnedMeshRenderer[] smrs)
        {
            var possibleBlendshapes = definitionFile.definitions
                .SelectMany(definition => definition.blendshapes)
                .Distinct()
                .ToHashSet();

            var validSmrs = new List<SkinnedMeshRenderer>();

            foreach (var smr in smrs)
            {
                if (HasAnyBlendshape(smr, possibleBlendshapes))
                {
                    validSmrs.Add(smr);
                }
            }

            return validSmrs;
        }

        private static bool HasAnyBlendshape(SkinnedMeshRenderer smr, HashSet<string> possibleBlendshapes)
        {
            var sharedMesh = smr.sharedMesh;
            if (sharedMesh != null)
            {
                for (var i = 0; i < sharedMesh.blendShapeCount; i++)
                {
                    var blendShapeName = sharedMesh.GetBlendShapeName(i);
                    if (possibleBlendshapes.Contains(blendShapeName))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
