using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.AddressableAssets;

[assembly: InternalsVisibleTo("HVR.Basis.Comms.Editor")]
namespace HVR.Basis.Comms
{
    [AutoStaticsCleanup]
    [AddComponentMenu("HVR.Basis/Automatic Face Tracking")]
    [HelpURL("https://docs.hai-vr.dev/docs/basis/avatar-customization/face-tracking")]
    public partial class AutomaticFaceTracking : MonoBehaviour, IHVRInitializable
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

        private static readonly HashSet<string> UnifiedExpressionsProbe = new HashSet<string> { "MouthRaiserLower", "MouthRaiserLowerLeft" };
        private static readonly HashSet<string> ArKitProbe = new HashSet<string> { "mouthShrugLower" };
        private static readonly ConditionalWeakTable<BlendshapeActuationDefinitionFile, HashSet<string>> PossibleBlendshapesCache = new ConditionalWeakTable<BlendshapeActuationDefinitionFile, HashSet<string>>();

        private BasisAvatar _avatar;
        internal const int MinimumAutoDetectedBlendshapes = 8;
        private static readonly string[] EyeLidAddresses = { "FT/v2/EyeLidLeft", "FT/v2/EyeLidRight" };
        [NonSerialized] internal bool autoDetected, autoFace = true, autoEye = true;
        [NonSerialized] internal BlendshapeActuationDefinitionFile[] preResolvedFiles;
        [NonSerialized] internal NamingConvention preResolvedConvention;
        private readonly List<BlendshapeActuationDefinitionFile> _generatedFiles = new List<BlendshapeActuationDefinitionFile>();

        // Exposed to the Unity editor for this component
        [NonSerialized] internal bool successful;
        [NonSerialized] internal NamingConvention namingConvention;
        [NonSerialized] internal List<SkinnedMeshRenderer> renderers;
        [NonSerialized] internal OSCAcquisition oscAcquisition;
        [NonSerialized] internal BlendshapeActuation blendshapeActuation;
        [NonSerialized] internal EyeTrackingBoneActuation eyeTrackingBoneActuation;
        [NonSerialized] internal FaceTrackingActivityRelay faceTrackingActivityRelay;

        private bool _isWearer;
        private Action<bool> _onFaceTrackingEnabledChanged;
        private Action<bool> _onEyeTrackingEnabledChanged;

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
            // The actuation components and activity relay are created during OnHVRAvatarReady —
            // AFTER HVRAvatarComms captured its initializables snapshot — so the comms foreach can
            // never reach them. Their NETWORK side lives in OnHVRReadyBothAvatarAndNetwork:
            // comms.RequireVariable for every FT/eye address (without which value changes are
            // silently ignored and never transmitted) and the remote Receiver hookup for eye
            // bones. Forward the event to everything this component created; local actuation
            // works without this, which is what made the omission invisible.
            if (blendshapeActuation != null) blendshapeActuation.OnHVRReadyBothAvatarAndNetwork(isWearer);
            if (eyeTrackingBoneActuation != null) eyeTrackingBoneActuation.OnHVRReadyBothAvatarAndNetwork(isWearer);
            if (faceTrackingActivityRelay != null) faceTrackingActivityRelay.OnHVRReadyBothAvatarAndNetwork(isWearer);
        }

        private void Discover()
        {
            var smrs = _avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (autoDetected)
            {
                DiscoverAutoDetected(smrs);
                return;
            }

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

        private void DiscoverAutoDetected(SkinnedMeshRenderer[] smrs)
        {
            BlendshapeActuationDefinitionFile[] files = null;
            namingConvention = NamingConvention.Unknown;
            if (autoFace)
            {
                files = preResolvedFiles ?? ResolveAutoDetectedFilesOrNull(smrs, out preResolvedConvention);
                namingConvention = files != null ? preResolvedConvention : NamingConvention.Unknown;
                TrackGenerated(files);
            }
            else DestroyGenerated(preResolvedFiles);
            preResolvedFiles = null;
            if (autoEye) files = AppendEyeLidFallback(files, smrs);
            var foundSmrs = files != null ? FindSkinnedMeshes(files, smrs) : new List<SkinnedMeshRenderer>();
            if (foundSmrs.Count > 0 || (autoEye && HasEyeBones(_avatar)))
            {
                SetupFaceTracking(files ?? Array.Empty<BlendshapeActuationDefinitionFile>(), foundSmrs);
            }
            else Failed();
        }

        internal static bool HasEyeBones(BasisAvatar avatar)
        {
            var animator = avatar != null ? avatar.Animator : null;
            return animator != null && animator.isHuman && animator.GetBoneTransform(HumanBodyBones.LeftEye) != null && animator.GetBoneTransform(HumanBodyBones.RightEye) != null;
        }

        internal static BlendshapeActuationDefinitionFile[] ResolveAutoDetectedFilesOrNull(SkinnedMeshRenderer[] smrs, out NamingConvention resolvedNamingConvention)
        {
            LoadDefaultHandles();
            resolvedNamingConvention = GuessNamingConvention(smrs);
            if (resolvedNamingConvention == NamingConvention.UnifiedExpressions && _ueHandle != null) return new[] { _ueHandle };
            if (resolvedNamingConvention == NamingConvention.ARKit && _arKitHandle != null) return new[] { _arKitHandle };
            var matchers = new List<BasisBlendshapeNameMatcher>();
            foreach (var smr in smrs)
            {
                if (smr == null) continue;
                var mesh = smr.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;
                var matcher = BasisBlendshapeNameMatcher.For(mesh);
                if (!matchers.Contains(matcher)) matchers.Add(matcher);
            }
            resolvedNamingConvention = NamingConvention.Unknown;
            if (matchers.Count == 0) return null;
            var arKit = RemapDefinitionFile(_arKitHandle, matchers, out var arKitCount);
            var ue = RemapDefinitionFile(_ueHandle, matchers, out var ueCount);
            var best = arKitCount >= ueCount ? arKit : ue;
            DestroyGenerated(best == arKit ? ue : arKit);
            if (best == null || Mathf.Max(arKitCount, ueCount) < MinimumAutoDetectedBlendshapes)
            {
                DestroyGenerated(best);
                return null;
            }
            resolvedNamingConvention = NamingConvention.AutoDetected;
            return new[] { best };
        }

        private static BlendshapeActuationDefinitionFile RemapDefinitionFile(BlendshapeActuationDefinitionFile source, List<BasisBlendshapeNameMatcher> matchers, out int matchedBlendshapes)
        {
            matchedBlendshapes = 0;
            if (source == null) return null;
            var remapped = new List<BlendshapeActuationDefinition>(source.definitions.Length);
            var matched = new HashSet<string>();
            var names = new List<string>();
            foreach (var definition in source.definitions)
            {
                if (definition.blendshapes == null) continue;
                names.Clear();
                foreach (var candidate in definition.blendshapes)
                {
                    foreach (var matcher in matchers)
                    {
                        var index = matcher.Find(candidate);
                        if (index < 0) continue;
                        var actual = matcher.Mesh.GetBlendShapeName(index);
                        if (!names.Contains(actual)) names.Add(actual);
                    }
                }
                if (names.Count == 0) continue;
                var copy = definition;
                copy.blendshapes = names.ToArray();
                remapped.Add(copy);
                matched.UnionWith(names);
            }
            matchedBlendshapes = matched.Count;
            if (remapped.Count == 0) return null;
            var file = ScriptableObject.CreateInstance<BlendshapeActuationDefinitionFile>();
            file.hideFlags = HideFlags.DontSave;
            file.name = $"AutoDetected_{source.name}";
            file.definitions = remapped.ToArray();
            file.addressOverrides = source.addressOverrides;
            file.internalVersion = source.internalVersion;
            return file;
        }

        private BlendshapeActuationDefinitionFile[] AppendEyeLidFallback(BlendshapeActuationDefinitionFile[] files, SkinnedMeshRenderer[] smrs)
        {
            var blinkMesh = _avatar.FaceBlinkMesh;
            var blinkVisemes = _avatar.BlinkViseme;
            if (blinkMesh == null || blinkMesh.sharedMesh == null || blinkVisemes == null) return files;
            if (files != null && CoversEyeLids(files, smrs)) return files;
            var mesh = blinkMesh.sharedMesh;
            var names = new List<string>();
            foreach (var index in blinkVisemes)
            {
                if (index < 0 || index >= mesh.blendShapeCount) continue;
                var name = mesh.GetBlendShapeName(index);
                if (!names.Contains(name)) names.Add(name);
            }
            if (names.Count == 0) return files;
            var file = ScriptableObject.CreateInstance<BlendshapeActuationDefinitionFile>();
            file.hideFlags = HideFlags.DontSave;
            file.name = "AutoDetected_EyeLids";
            file.definitions = EyeLidAddresses.Select(address => new BlendshapeActuationDefinition { address = address, inStart = 0.75f, inEnd = 0f, outStart = 0f, outEnd = 1f, blendshapes = names.ToArray() }).ToArray();
            file.addressOverrides = EyeLidAddresses.Select(address => new AddressOverride { address = address, overrideDefaultValue = true, defaultValue = 0.8f }).ToArray();
            file.internalVersion = 2;
            _generatedFiles.Add(file);
            return files == null ? new[] { file } : files.Append(file).ToArray();
        }

        private static bool CoversEyeLids(BlendshapeActuationDefinitionFile[] files, SkinnedMeshRenderer[] smrs)
        {
            foreach (var file in files)
            {
                foreach (var definition in file.definitions)
                {
                    if (definition.blendshapes == null || Array.IndexOf(EyeLidAddresses, definition.address) < 0) continue;
                    foreach (var smr in smrs)
                    {
                        var mesh = smr != null ? smr.sharedMesh : null;
                        if (mesh == null) continue;
                        foreach (var blendshape in definition.blendshapes)
                        {
                            if (mesh.GetBlendShapeIndex(blendshape) >= 0) return true;
                        }
                    }
                }
            }
            return false;
        }

        private void TrackGenerated(BlendshapeActuationDefinitionFile[] files)
        {
            if (files == null) return;
            foreach (var file in files)
            {
                if (file != null && file != _ueHandle && file != _arKitHandle && !_generatedFiles.Contains(file)) _generatedFiles.Add(file);
            }
        }

        internal static void DestroyGenerated(BlendshapeActuationDefinitionFile[] files)
        {
            if (files == null) return;
            foreach (var file in files) DestroyGenerated(file);
        }

        private static void DestroyGenerated(BlendshapeActuationDefinitionFile file)
        {
            if (file != null && file != _ueHandle && file != _arKitHandle) Destroy(file);
        }

        private static void LoadDefaultHandles()
        {
            _ueHandle ??= Addressables.LoadAssetAsync<BlendshapeActuationDefinitionFile>("HVR.Basis.Comms.FaceTracking.DefaultUnifiedExpressionsDefinitionFile").WaitForCompletion();
            _arKitHandle ??= Addressables.LoadAssetAsync<BlendshapeActuationDefinitionFile>("HVR.Basis.Comms.FaceTracking.DefaultARKitDefinitionFile").WaitForCompletion();
        }

        public BlendshapeActuationDefinitionFile[] ResolveFilesOrNull(SkinnedMeshRenderer[] smrs, out NamingConvention resolvedNamingConvention)
        {
            LoadDefaultHandles();

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
            faceTrackingActivityRelay = FaceTrackingActivityRelay.GetOrCreate(_avatar);
            faceTrackingActivityRelay.OnHVRAvatarReady(_isWearer);

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
            if (autoDetected && !autoEye) eyeTrackingBoneActuation.enabled = false;

            ApplyFaceTrackingEnabled(BasisSettingsDefaults.EnableFaceTracking.RawValue);
            ApplyEyeTrackingEnabled(BasisSettingsDefaults.EnableEyeTracking.RawValue);

            _onFaceTrackingEnabledChanged = ApplyFaceTrackingEnabled;
            _onEyeTrackingEnabledChanged = ApplyEyeTrackingEnabled;
            BasisSettingsDefaults.EnableFaceTracking.OnChanged += _onFaceTrackingEnabledChanged;
            BasisSettingsDefaults.EnableEyeTracking.OnChanged += _onEyeTrackingEnabledChanged;

            successful = true;
        }

        private void ApplyFaceTrackingEnabled(bool enabledValue)
        {
            if (blendshapeActuation != null)
            {
                blendshapeActuation.enabled = enabledValue;
            }
        }

        private void ApplyEyeTrackingEnabled(bool enabledValue)
        {
            if (eyeTrackingBoneActuation != null)
            {
                eyeTrackingBoneActuation.enabled = enabledValue && (!autoDetected || autoEye);
            }
        }

        private void OnDestroy()
        {
            DestroyGenerated(preResolvedFiles);
            preResolvedFiles = null;
            foreach (var file in _generatedFiles) DestroyGenerated(file);
            _generatedFiles.Clear();
            if (_onFaceTrackingEnabledChanged != null)
            {
                BasisSettingsDefaults.EnableFaceTracking.OnChanged -= _onFaceTrackingEnabledChanged;
                _onFaceTrackingEnabledChanged = null;
            }
            if (_onEyeTrackingEnabledChanged != null)
            {
                BasisSettingsDefaults.EnableEyeTracking.OnChanged -= _onEyeTrackingEnabledChanged;
                _onEyeTrackingEnabledChanged = null;
            }
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
            UserDefined,
            AutoDetected
        }

        private static NamingConvention GuessNamingConvention(SkinnedMeshRenderer[] smrs)
        {
            foreach (var smr in smrs)
            {
                var sharedMesh = smr.sharedMesh;
                if (sharedMesh == null)
                {
                    continue;
                }
                if (ContainsAnyBlendshape(sharedMesh, UnifiedExpressionsProbe))
                {
                    return NamingConvention.UnifiedExpressions;
                }
                if (ContainsAnyBlendshape(sharedMesh, ArKitProbe))
                {
                    return NamingConvention.ARKit;
                }
            }

            return NamingConvention.Unknown;
        }

        private static bool ContainsAnyBlendshape(Mesh sharedMesh, HashSet<string> probe)
        {
            foreach (var blendShapeName in probe)
            {
                if (sharedMesh.GetBlendShapeIndex(blendShapeName) != -1)
                {
                    return true;
                }
            }

            return false;
        }

        public List<SkinnedMeshRenderer> FindSkinnedMeshes(BlendshapeActuationDefinitionFile[] definitionFiles, SkinnedMeshRenderer[] smrs)
        {
            var validSmrs = new List<SkinnedMeshRenderer>();
            foreach (var smr in smrs)
            {
                for (var i = 0; i < definitionFiles.Length; i++)
                {
                    if (HasAnyBlendshape(smr, GetPossibleBlendshapes(definitionFiles[i])))
                    {
                        validSmrs.Add(smr);
                        break;
                    }
                }
            }

            return validSmrs;
        }

        private static HashSet<string> GetPossibleBlendshapes(BlendshapeActuationDefinitionFile definitionFile)
        {
            if (PossibleBlendshapesCache.TryGetValue(definitionFile, out var cached)) return cached;

            var possibleBlendshapes = new HashSet<string>();
            var definitions = definitionFile.definitions;
            for (var i = 0; i < definitions.Length; i++)
            {
                var blendshapes = definitions[i].blendshapes;
                if (blendshapes == null) continue;
                for (var j = 0; j < blendshapes.Length; j++)
                {
                    possibleBlendshapes.Add(blendshapes[j]);
                }
            }

            PossibleBlendshapesCache.Add(definitionFile, possibleBlendshapes);
            return possibleBlendshapes;
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
