/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Meta.XR.Util;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;
using Debug = UnityEngine.Debug;
using Permission = UnityEngine.Android.Permission;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System.Linq;
#endif

/// <summary>
/// A manager for <see cref="OVRSceneAnchor"/>s created using the Room Setup feature.
/// </summary>
/// <remarks>
///  "OVRSceneManager and associated classes are deprecated (v65), please use [MR Utility Kit](https://developer.oculus.com/documentation/unity/unity-mr-utility-kit-overview)" instead.
/// </remarks>
[HelpURL("https://developer.oculus.com/documentation/unity/unity-scene-use-scene-anchors/")]
[Obsolete(DeprecationMessage)]
[Feature(Feature.Scene)]
public class OVRSceneManager : MonoBehaviour
{
    internal const string DeprecationMessage =
        "OVRSceneManager and associated classes are deprecated (v65), please use MR Utility Kit instead (https://developer.oculus.com/documentation/unity/unity-mr-utility-kit-overview)";

    /// <summary>
    /// A prefab that will be used to instantiate any Plane found
    /// when querying the Scene model. If the anchor contains both
    /// Volume and Plane elements, <see cref="VolumePrefab"/> will
    /// be used instead. If null, no object will be instantiated,
    /// unless a prefab override is provided.
    /// </summary>
    [FormerlySerializedAs("planePrefab")]
    [Tooltip("A prefab that will be used to instantiate any Plane found " +
             "when querying the Scene model. If the anchor contains both " +
             "Volume and Plane elements, Volume will be used instead.")]
    public OVRSceneAnchor PlanePrefab;

    /// <summary>
    /// A prefab that will be used to instantiate any Volume found
    /// when querying the Scene model. This anchor may also contain
    /// Plane elements. If null, no object will be instantiated,
    /// unless a prefab override is provided.
    /// </summary>
    [FormerlySerializedAs("volumePrefab")]
    [Tooltip("A prefab that will be used to instantiate any Volume found " +
             "when querying the Scene model. This anchor may also contain " +
             "Plane elements.")]
    public OVRSceneAnchor VolumePrefab;

    /// <summary>
    /// Overrides the instantiation of the generic Plane and Volume prefabs with specialized ones.
    /// If null is provided, no object will be instantiated for that label.
    /// </summary>
    [FormerlySerializedAs("prefabOverrides")]
    [Tooltip("Overrides the instantiation of the generic Plane/Volume prefabs with specialized ones.")]
    public List<OVRScenePrefabOverride> PrefabOverrides = new List<OVRScenePrefabOverride>();

    /// <summary>
    /// When enabled, only rooms the user is currently in will be instantiated.
    /// </summary>
    /// <remarks>
    /// When `True`,  <see cref="OVRSceneManager"/> will instantiate an <see cref="OVRSceneRoom"/> and all of its child
    /// scene anchors (walls, floor, ceiling, and furniture) only if the user is located in the room when
    /// <see cref="LoadSceneModel"/> is called.
    ///
    /// When `False`, the <see cref="OVRSceneManager"/> will instantiate an <see cref="OVRSceneRoom"/> for each room,
    /// regardless of the user's location.
    ///
    /// The 2D boundary points of the room's floor are used to determine whether the user is inside a room.
    ///
    /// If a room exists, but the user is not inside it, then the <see cref="NoSceneModelToLoad"/> event is invoked as
    /// if the user had not yet run Space Setup.
    /// </remarks>
    [Tooltip("Scene manager will only present the room(s) the user is currently in.")]
    public bool ActiveRoomsOnly = true;

    /// <summary>
    /// When true, verbose debug logs will be emitted.
    /// </summary>
    [FormerlySerializedAs("verboseLogging")]
    [Tooltip("When enabled, verbose debug logs will be emitted.")]
    public bool VerboseLogging;

    /// <summary>
    /// The maximum number of scene anchors that will be updated each frame.
    /// </summary>
    [Tooltip("The maximum number of scene anchors that will be updated each frame.")]
    public int MaxSceneAnchorUpdatesPerFrame = 3;

    /// <summary>
    /// The parent transform to which each new <see cref="OVRSceneAnchor"/> or <see cref="OVRSceneRoom"/>
    /// will be parented upon instantiation.
    /// </summary>
    /// <remarks>
    /// if null, <see cref="OVRSceneRoom"/>(s) instantiated by <see cref="OVRSceneManager"/> will have no parent, and
    /// <see cref="OVRSceneAnchor"/>(s) will have either a <see cref="OVRSceneRoom"/> as their parent or null, that is
    /// they will be instantiated at the scene root. If non-null, <see cref="OVRSceneAnchor"/>(s) that do not
    /// belong to any <see cref="OVRSceneRoom"/>, and <see cref="OVRSceneRoom"/>(s) along with its child
    /// <see cref="OVRSceneAnchor"/>(s) will be parented to <see cref="InitialAnchorParent"/>.
    ///
    /// Changing this value does not affect existing <see cref="OVRSceneAnchor"/>(s) or <see cref="OVRSceneRoom"/>(s).
    /// </remarks>
    public Transform InitialAnchorParent
    {
        get => _initialAnchorParent;
        set => _initialAnchorParent = value;
    }

    [SerializeField]
    [Tooltip("(Optional) The parent transform for each new scene anchor. " +
             "Changing this value does not affect existing scene anchors. May be null.")]
    internal Transform _initialAnchorParent;

    #region Events

    /// <summary>
    /// This event fires when the <see cref="OVRSceneManager"/> has instantiated prefabs
    /// for the Scene Anchors in a Scene Model.
    /// </summary>
    /// <remarks>
    /// Wait until this event has been fired before accessing any <see cref="OVRSceneAnchor"/>s,
    /// as this event waits for any additional initialization logic to be executed first.
    /// Access <see cref="OVRSceneAnchor"/>s using
    /// <see cref="OVRSceneAnchor.GetSceneAnchors(List{OVRSceneAnchor})"/>.
    /// </remarks>
    public Action SceneModelLoadedSuccessfully;

    /// <summary>
    /// This event fires when a query load the Scene Model returns no result. It can indicate that the,
    /// user never used the Room Setup in the space they are in.
    /// </summary>
    public Action NoSceneModelToLoad;

    /// <summary>
    /// Unable to load the scene model because the user has not granted permission to use Scene.
    /// </summary>
    /// <remarks>
    /// Apps that wish to use Scene must have "Spatial data" sharing permission. This is a runtime permission that
    /// must be granted before loading the scene model. If the permission has not been granted, then calling
    /// <see cref="LoadSceneModel"/> will result in this event.
    ///
    /// The permission string is "com.oculus.permission.USE_SCENE". See Unity's
    /// [Android Permission API](https://docs.unity3d.com/ScriptReference/Android.Permission.html) for more information
    /// on interacting with permissions.
    /// </remarks>
    public event Action LoadSceneModelFailedPermissionNotGranted;

    /// <summary>
    /// This event will fire after the Room Setup successfully returns. It can be trapped to load the
    /// scene Model.
    /// </summary>
    public Action SceneCaptureReturnedWithoutError;

    /// <summary>
    /// This event will fire if an error occurred while trying to send the user to Room Setup.
    /// </summary>
    public Action UnexpectedErrorWithSceneCapture;

    /// <summary>
    /// This event fires when the OVR Scene Manager detects a change in the room layout.
    /// It indicates that the user performed Room Setup while the application was paused.
    /// Upon receiving this event, user can call <see cref="LoadSceneModel" /> to reload the scene model.
    /// </summary>
    public Action NewSceneModelAvailable;

    #endregion

    /// <summary>
    /// Represents the available classifications for each <see cref="OVRSceneAnchor"/>.
    /// </summary>
    /// <remarks>
    /// Classifications can be useful for functionally describing the contents of an <see cref="OVRSceneRoom"/>.
    /// By iterating all <see cref="OVRSceneAnchor"/>s and comparing their Classifications with those needed,
    /// your application can determine whether a room meets its minimum requirements and/or can adjust its behavior
    /// to fit the physical surroundings.
    ///
    /// <see cref="OVRSceneManager"/> and associated classes are deprecated (v65), please use [MR Utility Kit](https://developer.oculus.com/documentation/unity/unity-mr-utility-kit-overview)" instead.
    /// </remarks>
    [Obsolete(DeprecationMessage)]
    public static class Classification
    {
        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a floor.
        /// </summary>
        public const string Floor = "FLOOR";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a ceiling.
        /// </summary>
        public const string Ceiling = "CEILING";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a wall face.
        /// </summary>
        public const string WallFace = "WALL_FACE";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a desk.
        /// This label has been deprecated in favor of <see cref="Table"/>.
        /// </summary>
        [Obsolete("Deprecated. Use Table classification instead.")]
        public const string Desk = "DESK";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a couch.
        /// </summary>
        public const string Couch = "COUCH";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a door frame.
        /// </summary>
        public const string DoorFrame = "DOOR_FRAME";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a window frame.
        /// </summary>
        public const string WindowFrame = "WINDOW_FRAME";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as other.
        /// </summary>
        public const string Other = "OTHER";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a storage (e.g., cabinet, shelf).
        /// </summary>
        public const string Storage = "STORAGE";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a bed.
        /// </summary>
        public const string Bed = "BED";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a screen (e.g., TV, computer monitor).
        /// </summary>
        public const string Screen = "SCREEN";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a lamp.
        /// </summary>
        public const string Lamp = "LAMP";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a plant.
        /// </summary>
        public const string Plant = "PLANT";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a table.
        /// </summary>
        public const string Table = "TABLE";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as wall art.
        /// </summary>
        public const string WallArt = "WALL_ART";


        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as an invisible wall face.
        /// All invisible wall faces are also classified as a <see cref="WallFace"/> in order to
        /// provide backwards compatibility for apps that expect closed rooms to only consist of
        /// wall faces, instead of a sequence composed of either invisible wall faces or wall faces.
        /// </summary>
        public const string InvisibleWallFace = "INVISIBLE_WALL_FACE";

        /// <summary>
        /// Represents an <see cref="OVRSceneAnchor"/> that is classified as a global mesh.
        /// </summary>
        public const string GlobalMesh = "GLOBAL_MESH";

        /// <summary>
        /// The list of all possible semantic labels that can be applied to an <see cref="OVRSceneAnchor"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="Set"/> exposes this list as a HashSet.
        /// </remarks>
        public static IReadOnlyList<string> List { get; } = new[]
        {
            Floor,
            Ceiling,
            WallFace,
#pragma warning disable CS0618 // Type or member is obsolete
            Desk,
#pragma warning restore CS0618 // Type or member is obsolete
            Couch,
            DoorFrame,
            WindowFrame,
            Other,
            Storage,
            Bed,
            Screen,
            Lamp,
            Plant,
            Table,
            WallArt,
            InvisibleWallFace,
            GlobalMesh,
        };

        /// <summary>
        /// The set of possible semantic labels that can be applied to an <see cref="OVRSceneAnchor"/>.
        /// </summary>
        /// <remarks>
        /// This is the same as <see cref="List"/> but allows for faster lookup.
        /// </remarks>
        public static HashSet<string> Set { get; } = new(List);
    }

    /// <summary>
    /// A container for the set of <see cref="OVRSceneAnchor"/>s representing a room's Walls, Floor, and Ceiling as
    /// <see cref="OVRScenePlane"/>s, which provide detailed geometry for each.
    /// </summary>
    /// <remarks>
    /// <see cref="OVRSceneManager"/> and associated classes are deprecated (v65), please use [MR Utility Kit](https://developer.oculus.com/documentation/unity/unity-mr-utility-kit-overview)" instead.
    /// </remarks>
    [Obsolete("RoomLayoutInformation is obsoleted. For each room's layout information " +
              "(floor, ceiling, walls) see " + nameof(OVRSceneRoom) + ".", false)]
    public class RoomLayoutInformation
    {
        /// <summary>
        /// The <see cref="OVRScenePlane"/> representing the floor of the room.
        /// </summary>
        public OVRScenePlane Floor;

        /// <summary>
        /// The <see cref="OVRScenePlane"/> representing the ceiling of the room.
        /// </summary>
        public OVRScenePlane Ceiling;

        /// <summary>
        /// The set of <see cref="OVRScenePlane"/> representing the walls of the room.
        /// </summary>
        public List<OVRScenePlane> Walls = new();
    }

    /// <summary>
    /// Describes the room layout of a room in the scene model.
    /// </summary>
    [Obsolete(
        "RoomLayout is obsoleted. For each room's layout information (floor, ceiling, walls) see " +
        nameof(OVRSceneRoom) +
        ".",
        false)]
    public RoomLayoutInformation RoomLayout;

    #region Private Vars

    // We use this to store the request id when attempting to load the scene
    private ulong _sceneCaptureRequestId = ulong.MaxValue;

    private OVRCameraRig _cameraRig;

    private int _sceneAnchorUpdateIndex;

    private bool _hasLoadBeenRequested;

    #endregion

    #region Logging

    internal struct LogForwarder
    {
        public void Log(string context, string message, GameObject gameObject = null) =>
            Debug.Log($"[{context}] {message}", gameObject);
        public void LogWarning(string context, string message, GameObject gameObject = null) =>
            Debug.LogWarning($"[{context}] {message}", gameObject);
        public void LogError(string context, string message, GameObject gameObject = null) =>
            Debug.LogError($"[{context}] {message}", gameObject);
    }

    internal LogForwarder? Verbose => VerboseLogging ? new LogForwarder() : (LogForwarder?)null;

    internal static class Development
    {
        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        public static void Log(string context, string message, GameObject gameObject = null) =>
            Debug.Log($"[{context}] {message}", gameObject);

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        public static void LogWarning(string context, string message, GameObject gameObject = null) =>
            Debug.LogWarning($"[{context}] {message}", gameObject);

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        public static void LogError(string context, string message, GameObject gameObject = null) =>
            Debug.LogError($"[{context}] {message}", gameObject);
    }

    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    static void Log(string message, GameObject gameObject = null) =>
        Development.Log(nameof(OVRSceneManager), message, gameObject);

    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    static void LogWarning(string message, GameObject gameObject = null) =>
        Development.LogWarning(nameof(OVRSceneManager), message, gameObject);

    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    static void LogError(string message, GameObject gameObject = null) =>
        Development.LogError(nameof(OVRSceneManager), message, gameObject);

    #endregion

    void Awake()
    {
        // Only allow one instance at runtime.
        if (FindObjectsByType<OVRSceneManager>(FindObjectsSortMode.None).Length > 1)
        {
            new LogForwarder().LogError(nameof(OVRSceneManager),
                $"Found multiple {nameof(OVRSceneManager)}s. Destroying '{name}'.");
            enabled = false;
            DestroyImmediate(this);
        }
    }

    void Start()
    {
        OVRTelemetry.Start(OVRTelemetryConstants.Scene.MarkerId.UseOVRSceneManager)
            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.UsingBasicPrefabs,
                (PlanePrefab != null || VolumePrefab != null) ? "true" : "false")
            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.UsingPrefabOverrides,
                (PrefabOverrides.Count > 0) ? "true" : "false")
            .AddAnnotation(OVRTelemetryConstants.Scene.AnnotationType.ActiveRoomsOnly,
                ActiveRoomsOnly ? "true" : "false")
            .Send();
    }

    private static void LogResult(OVRAnchor.FetchResult value)
    {
        if (((OVRPlugin.Result)value).IsSuccess())
        {
            Log($"xrDiscoverSpacesMETA completed successfully with result {value}.");
        }
        else
        {
            LogError($"xrDiscoverSpacesMETA failed with error {value}.");
        }
    }

    internal static async OVRTask<bool> FetchAnchorsAsync<T>(List<OVRAnchor> anchors,
        Action<List<OVRAnchor>, int> incrementalResultsCallback = null) where T : struct, IOVRAnchorComponent<T>
    {
        Log($"Fetching anchors of type {default(T).Type} using xrDiscoverSpacesMETA.");
        var result = await OVRAnchor.FetchAnchorsAsync(anchors, new OVRAnchor.FetchOptions
        {
            SingleComponentType = typeof(T),
        }, incrementalResultsCallback);

        LogResult(result.Status);
        return result.Success;
    }

    internal static async OVRTask<bool> FetchAnchorsAsync(IEnumerable<Guid> uuids, List<OVRAnchor> anchors)
    {
        Log($"Fetching {uuids.ToNonAlloc().GetCount()} anchors by UUID using xrDiscoverSpacesMETA.");
        var result = await OVRAnchor.FetchAnchorsAsync(anchors, new OVRAnchor.FetchOptions
        {
            Uuids = uuids,
        });

        LogResult(result.Status);
        return result.Success;
    }

    internal async void OnApplicationPause(bool isPaused)
    {
        // if we haven't loaded scene, we won't check anchor status
        if (isPaused || !_hasLoadBeenRequested) return;

        using (new OVRObjectPool.ListScope<OVRAnchor>(out var anchors))
        {
            var success = await FetchAnchorsAsync<OVRRoomLayout>(anchors);
            if (!success)
            {
                Verbose?.Log(nameof(OVRSceneManager), "Failed to retrieve scene model information on resume.");
                return;
            }

            // check whether room anchors have changed
            foreach (var anchor in anchors)
            {
                if (!OVRSceneAnchor.SceneAnchors.ContainsKey(anchor.Uuid))
                {
                    Verbose?.Log(nameof(OVRSceneManager),
                        $"Scene model changed. Invoking {nameof(NewSceneModelAvailable)} event.");
                    NewSceneModelAvailable?.Invoke();
                    break;
                }
            }
        }

        QueryForExistingAnchorsTransform();
    }

    private async void QueryForExistingAnchorsTransform()
    {
        using (new OVRObjectPool.ListScope<OVRAnchor>(out var anchors))
        using (new OVRObjectPool.ListScope<Guid>(out var uuids))
        {
            foreach (var anchor in OVRSceneAnchor.SceneAnchorsList)
            {
                if (!anchor.Space.Valid || !anchor.IsTracked)
                    continue;

                uuids.Add(anchor.Uuid);
            }

            if (uuids.Count > 0)
            {
                await FetchAnchorsAsync(uuids, anchors);
            }

            UpdateAllSceneAnchors();
        }
    }

    /// <summary>
    /// Loads the scene model
    /// </summary>
    /// <remarks>
    /// The "scene model" consists of all the anchors (i.e., floor, ceiling, walls, and furniture) for all the rooms
    /// defined during Space Setup.
    ///
    /// When running on Quest, Scene is queried to retrieve the entities describing the Scene Model. In the Editor,
    /// the Scene Model is loaded over Link.
    /// </remarks>
    /// <returns>Returns true if the query was successfully initiated.</returns>
    public bool LoadSceneModel()
    {
        _hasLoadBeenRequested = true;

        DestroyExistingAnchors();

        var task = LoadSceneModelAsync();
        if (!task.IsCompleted)
        {
            AwaitTask(task);
            return true;
        }

        return InterpretResult(task.GetResult());

        async void AwaitTask(OVRTask<LoadSceneModelResult> task) => InterpretResult(await task);

        bool InterpretResult(LoadSceneModelResult result)
        {
            switch (result)
            {
                case LoadSceneModelResult.Success:
                {
                    Log($"Scene model loaded successfully. Invoking {nameof(SceneModelLoadedSuccessfully)}");
                    SceneModelLoadedSuccessfully?.Invoke();
                    return true;
                }
                case LoadSceneModelResult.FailureScenePermissionNotGranted:
                {
                    LogWarning($"Cannot retrieve anchors because {OVRPermissionsRequester.ScenePermission} has " +
                               $"not been granted. Invoking {nameof(LoadSceneModelFailedPermissionNotGranted)}",
                        gameObject);

                    LoadSceneModelFailedPermissionNotGranted?.Invoke();

                    // true because the query didn't fail
                    return true;
                }
                case LoadSceneModelResult.NoSceneModelToLoad:
                {
                    LogWarning($"Although the app has {OVRPermissionsRequester.ScenePermission} permission, " +
                               $"loading the Scene definition yielded no result. Typically, this means the user has not " +
                               $"captured the room they are in yet. Alternatively, an internal error may be preventing " +
                               $"this app from accessing scene. Invoking {nameof(NoSceneModelToLoad)}");

                    NoSceneModelToLoad?.Invoke();
                    return true;
                }
                default: return false;
            }
        }
    }

    public enum LoadSceneModelResult
    {
        Success = 0,
        NoSceneModelToLoad = 1,
        FailureScenePermissionNotGranted = -1,
        FailureUnexpectedError = -2,
    }

    internal struct Metrics
    {
        public int TotalRoomCount;
        public int CandidateRoomCount;
        public int Loaded;
        public int Failed;
        public int SkippedUserNotInRoom;
        public int SkippedAlreadyInstantiated;

        public static Metrics operator +(Metrics lhs, Metrics rhs) => new()
        {
            TotalRoomCount = lhs.TotalRoomCount + rhs.TotalRoomCount,
            CandidateRoomCount = lhs.CandidateRoomCount + rhs.CandidateRoomCount,
            Loaded = lhs.Loaded + rhs.Loaded,
            Failed = lhs.Failed + rhs.Failed,
            SkippedUserNotInRoom = lhs.SkippedUserNotInRoom + rhs.SkippedUserNotInRoom,
            SkippedAlreadyInstantiated = lhs.SkippedAlreadyInstantiated + rhs.SkippedAlreadyInstantiated,
        };
    }

    internal struct RoomLayoutUuids
    {
        public Guid Floor;
        public Guid Ceiling;
        public Guid[] Walls;
    }

    private async OVRTask<Metrics> ProcessBatch(List<OVRAnchor> rooms, int startingIndex)
    {
        Log($"Processing batch [{startingIndex}..{rooms.Count - 1}]", gameObject);

        var metrics = new Metrics
        {
            // Rooms in this batch
            TotalRoomCount = rooms.Count - startingIndex,
        };

        using (new OVRObjectPool.ListScope<OVRAnchor>(out var candidateRooms))
        using (new OVRObjectPool.DictionaryScope<OVRAnchor, RoomLayoutUuids>(out var layoutUuids))
        {
            for (var i = startingIndex; i < rooms.Count; i++)
            {
                var room = rooms[i];
                var layout = default(RoomLayoutUuids);
                if (room.GetComponent<OVRRoomLayout>()
                    .TryGetRoomLayout(out layout.Ceiling, out layout.Floor, out layout.Walls))
                {
                    layoutUuids.Add(room, layout);
                    candidateRooms.Add(room);
                }
                else
                {
                    LogError($"Unable to retrieve room layout information for {room.Uuid}. Ignoring.", gameObject);
                    metrics.Failed++;
                }
            }

            metrics.CandidateRoomCount = candidateRooms.Count;

            if (candidateRooms.Count == 0)
            {
                return metrics;
            }

            if (ActiveRoomsOnly)
            {
                Log($"Filtering inactive rooms.");

                LoadSceneModelResult result;
                (result, metrics.SkippedUserNotInRoom) = await FilterByActiveRoom(candidateRooms, layoutUuids);
                if ((int)result < 0)
                {
                    metrics.Failed += metrics.CandidateRoomCount;
                    return metrics;
                }

                if (metrics.SkippedUserNotInRoom > 0)
                {
                    LogWarning($"{metrics.SkippedUserNotInRoom} of {metrics.CandidateRoomCount} candidate " +
                               $"room(s) were ignored because the user is not in them.");
                }

                // We must have filtered them all out
                if (candidateRooms.Count == 0)
                {
                    return metrics;
                }
            }

            using (new OVRObjectPool.ListScope<bool>(out var taskResults))
            using (new OVRObjectPool.ListScope<OVRTask<bool>>(out var tasks))
            {
                foreach (var room in candidateRooms)
                {
                    // Skip pre-existing rooms
                    if (OVRSceneAnchor.SceneAnchors.TryGetValue(room.Uuid, out var sceneAnchor))
                    {
                        LogWarning($"Skipping {sceneAnchor.name} because it has already been instantiated.");
                        sceneAnchor.IsTracked = true;
                        metrics.SkippedAlreadyInstantiated++;
                        continue;
                    }

                    var layout = layoutUuids[room];
                    var roomGameObject = new GameObject($"Room {room.Uuid}");
                    roomGameObject.transform.parent = _initialAnchorParent;

                    sceneAnchor = roomGameObject.AddComponent<OVRSceneAnchor>();
                    sceneAnchor.Initialize(room);

                    var sceneRoom = roomGameObject.AddComponent<OVRSceneRoom>();
                    tasks.Add(sceneRoom.LoadRoom(layout.Floor, layout.Ceiling, layout.Walls));
                }

                await OVRTask.WhenAll(tasks, taskResults);

                foreach (var loadSuccessful in taskResults)
                {
                    if (loadSuccessful)
                    {
                        metrics.Loaded++;
                    }
                    else
                    {
                        metrics.Failed++;
                    }
                }
            }
        }

        return metrics;
    }

    private async OVRTask<LoadSceneModelResult> LoadSceneModelAsync()
    {
        if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
        {
            return LoadSceneModelResult.FailureScenePermissionNotGranted;
        }

        using (new OVRObjectPool.ListScope<Metrics>(out var taskResults))
        using (new OVRObjectPool.ListScope<OVRTask<Metrics>>(out var tasks))
        using (new OVRObjectPool.ListScope<OVRAnchor>(out var rooms))
        {
            var result = await FetchAnchorsAsync<OVRRoomLayout>(rooms, (rooms, startingIndex) =>
            {
                tasks.Add(ProcessBatch(rooms, startingIndex));
            });

            // Wait for all batches to complete
            await OVRTask.WhenAll(tasks, taskResults);

            if (!result)
            {
                LogError($"Unable to query for rooms.", gameObject);
                return LoadSceneModelResult.FailureUnexpectedError;
            }

            var combinedMetrics = default(Metrics);
            foreach (var batchMetrics in taskResults)
            {
                combinedMetrics += batchMetrics;
            }

            Log($"{nameof(LoadSceneModelAsync)} Report:\n" +
                     $"\t{combinedMetrics.TotalRoomCount} total rooms\n" +
                     $"\t{combinedMetrics.CandidateRoomCount} candidate rooms\n" +
                     $"\t{combinedMetrics.Loaded} loaded\n" +
                     $"\t{combinedMetrics.Failed} failed\n" +
                     $"\t{combinedMetrics.SkippedAlreadyInstantiated} skipped because the room is already instantiated\n" +
                     $"\t{combinedMetrics.SkippedUserNotInRoom} skipped because the user is not in the room",
                gameObject);

            if (combinedMetrics.Loaded > 0)
            {
#pragma warning disable CS0618 // Type or member is obsolete
                RoomLayout = GetRoomLayoutInformation();
#pragma warning restore CS0618
                Log($"{LoadSceneModelResult.Success}: Successfully loaded {combinedMetrics.Loaded} room(s)");
                return LoadSceneModelResult.Success;
            }

            if (combinedMetrics.SkippedAlreadyInstantiated > 0)
            {
                Log($"{LoadSceneModelResult.Success}: Did not load any new rooms, but there are {combinedMetrics.SkippedAlreadyInstantiated} room(s) that were loaded previously.");
                return LoadSceneModelResult.Success;
            }

            if (combinedMetrics.SkippedUserNotInRoom > 0)
            {
                LogWarning($" {LoadSceneModelResult.NoSceneModelToLoad}: There are {combinedMetrics.TotalRoomCount} room(s) but the user is not in any of them.");
                return LoadSceneModelResult.NoSceneModelToLoad;
            }

            if (combinedMetrics.Failed > 0)
            {
                LogError($" {LoadSceneModelResult.FailureUnexpectedError}: Failed to load {combinedMetrics.Failed} room(s).");
                return LoadSceneModelResult.FailureUnexpectedError;
            }

            // Nothing was loaded, skipped, or failed, and we have permission, so there must not be a scene model
            Log($" {LoadSceneModelResult.NoSceneModelToLoad}: Query succeeded and {OVRPermissionsRequester.ScenePermission} permission has been granted, but there are no results.");
            return LoadSceneModelResult.NoSceneModelToLoad;
        }
    }

    private static async OVRTask<(LoadSceneModelResult, int)> FilterByActiveRoom(List<OVRAnchor> rooms,
        Dictionary<OVRAnchor, RoomLayoutUuids> layouts)
    {
        rooms.Clear();
        var skipped = 0;
        var userPosition = OVRPlugin.GetNodePose(OVRPlugin.Node.EyeCenter, OVRPlugin.Step.Render)
                                .Position
                                .FromVector3f();

        using (new OVRObjectPool.ListScope<OVRAnchor>(out var floorAndCeilingAnchors))
        using (new OVRObjectPool.ListScope<Guid>(out var floorAndCeilingUuids))
        {
            foreach (var layout in layouts.Values)
            {
                floorAndCeilingUuids.Add(layout.Ceiling);
                floorAndCeilingUuids.Add(layout.Floor);
            }

            var result = await FetchAnchorsAsync(floorAndCeilingUuids, floorAndCeilingAnchors);
            if (!result)
            {
                Development.LogError(nameof(OVRSceneManager), $"Unable to load floor and ceiling anchors.");
                return (LoadSceneModelResult.FailureUnexpectedError, 0);
            }

            using (new OVRObjectPool.ListScope<bool>(out var results))
            using (new OVRObjectPool.ListScope<OVRTask<bool>>(out var tasks))
            {
                foreach (var anchor in floorAndCeilingAnchors)
                {
                    if (anchor.TryGetComponent<OVRLocatable>(out var locatable))
                    {
                        tasks.Add(locatable.SetEnabledAsync(true));
                    }
                }

                await OVRTask.WhenAll(tasks, results);
            }

            using (new OVRObjectPool.DictionaryScope<Guid, OVRAnchor>(out var uuidToAnchor))
            {
                foreach (var anchor in floorAndCeilingAnchors)
                {
                    uuidToAnchor.Add(anchor.Uuid, anchor);
                }

                foreach (var (room, layout) in layouts)
                {
                    if (uuidToAnchor.TryGetValue(layout.Floor, out var floor) &&
                        uuidToAnchor.TryGetValue(layout.Ceiling, out var ceiling) &&
                        IsUserInRoom(userPosition, floor: floor, ceiling: ceiling))
                    {
                        rooms.Add(room);
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
        }

        return (LoadSceneModelResult.Success, skipped);
    }

    #region Loading active room(s)

    private static bool IsUserInRoom(Vector3 userPosition, OVRAnchor floor, OVRAnchor ceiling)
    {
        // Get floor anchor's pose
        if (!OVRPlugin.TryLocateSpace(floor.Handle, OVRPlugin.GetTrackingOriginType(), out var floorPose))
        {
            Development.LogError(nameof(OVRSceneManager), $"Could not locate floor anchor {floor.Uuid}");
            return false;
        }

        // Get ceiling anchor's pose
        if (!OVRPlugin.TryLocateSpace(ceiling.Handle, OVRPlugin.GetTrackingOriginType(), out var ceilingPose))
        {
            Development.LogError(nameof(OVRSceneManager), $"Could not locate ceiling anchor {ceiling.Uuid}");
            return false;
        }

        // Get room boundary vertices (assumes floor and ceiling have the same 2d boundary)
        if (!OVRPlugin.GetSpaceBoundary2DCount(floor.Handle, out var count))
        {
            Development.LogWarning(nameof(OVRSceneManager), $"Could not get floor boundary {floor.Uuid}");
            return false;
        }

        using var boundaryVertices = new NativeArray<Vector2>(count, Allocator.Temp);
        if (!OVRPlugin.GetSpaceBoundary2D(floor.Handle, boundaryVertices))
            return false;

        if (userPosition.y < floorPose.Position.y)
            return false;

        if (userPosition.y > ceilingPose.Position.y)
            return false;

        // Perform location check
        var offsetWithFloor = userPosition - floorPose.Position.FromVector3f();
        var userPositionInRoom = Quaternion.Inverse(floorPose.Orientation.FromQuatf()) * offsetWithFloor;

        return PointInPolygon2D(boundaryVertices, userPositionInRoom);
    }

    #endregion

    private void DestroyExistingAnchors()
    {
        // Remove all the scene entities in memory. Update with scene entities from new query.
        using (new OVRObjectPool.ListScope<OVRSceneAnchor>(out var anchors))
        {
            OVRSceneAnchor.GetSceneAnchors(anchors);

            foreach (var sceneAnchor in anchors)
            {
                Destroy(sceneAnchor.gameObject);
            }
        }

#pragma warning disable CS0618 // Type or member is obsolete
        RoomLayout = null;
#pragma warning restore CS0618
    }

    /// <summary>
    /// Requests scene capture from the Room Setup.
    /// </summary>
    /// <returns>Returns true if scene capture succeeded, otherwise false.</returns>
    public bool RequestSceneCapture()
    {
#if !UNITY_EDITOR
        bool result = OVRPlugin.RequestSceneCapture(out _sceneCaptureRequestId);
        if (!result)
        {
            UnexpectedErrorWithSceneCapture?.Invoke();
        }
        // When a scene capture has been successfuly requested, silent fall through as it does not imply a successful scene capture
        return result;
#else
        Development.LogWarning(nameof(OVRSceneManager),
            "Scene Capture does not work over Link.\n"
            + "Please capture a scene with the HMD in standalone mode, then access the scene model over Link.");
        UnexpectedErrorWithSceneCapture?.Invoke();
        return false;
#endif
    }

    /// <summary>
    /// Check if a room setup exists with specified anchors classifications.
    /// </summary>
    /// <param name="requestedAnchorClassifications">Anchors classifications to check.</param>
    /// <returns>OVRTask that gives a boolean answer if the room setup exists upon completion.</returns>
    public OVRTask<bool> DoesRoomSetupExist(IEnumerable<string> requestedAnchorClassifications)
    {
        var task = OVRTask.FromGuid<bool>(Guid.NewGuid());
        CheckIfClassificationsAreValid(requestedAnchorClassifications);
        using (new OVRObjectPool.ListScope<OVRAnchor>(out var roomAnchors))
        {
            var roomsTask = FetchAnchorsAsync<OVRRoomLayout>(roomAnchors);
            roomsTask.ContinueWith((result, anchors) => CheckClassificationsInRooms(result, anchors, requestedAnchorClassifications, task), roomAnchors);
        }
        return task;
    }

    private static void CheckIfClassificationsAreValid(IEnumerable<string> requestedAnchorClassifications)
    {
        if (requestedAnchorClassifications == null)
        {
            throw new ArgumentNullException(nameof(requestedAnchorClassifications));
        }

        foreach (var classification in requestedAnchorClassifications)
        {
            if (!Classification.Set.Contains(classification))
            {
                throw new ArgumentException(
                    $"{nameof(requestedAnchorClassifications)} contains invalid anchor {nameof(Classification)} {classification}.");
            }
        }
    }

    private static void GetUuidsToQuery(OVRAnchor anchor, HashSet<Guid> uuidsToQuery)
    {
        if (anchor.TryGetComponent<OVRAnchorContainer>(out var container))
        {
            foreach (var uuid in container.Uuids)
            {
                uuidsToQuery.Add(uuid);
            }
        }
    }

    private static void CheckClassificationsInRooms(bool success, List<OVRAnchor> rooms, IEnumerable<string> requestedAnchorClassifications, OVRTask<bool> task)
    {
        if (!success)
        {
            Development.Log(nameof(OVRSceneManager),
                $"{nameof(OVRAnchor.FetchAnchorsAsync)} failed on {nameof(DoesRoomSetupExist)}() request to fetch room anchors.");
            return;
        }

        using (new OVRObjectPool.HashSetScope<Guid>(out var uuidsToQuery))
        using (new OVRObjectPool.ListScope<Guid>(out var anchorUuids))
        {
            for (int i = 0; i < rooms.Count; i++)
            {
                GetUuidsToQuery(rooms[i], uuidsToQuery);
                anchorUuids.AddRange(uuidsToQuery);
                uuidsToQuery.Clear();
            }

            using (new OVRObjectPool.ListScope<OVRAnchor>(out var roomAnchors))
            {
                FetchAnchorsAsync(anchorUuids, roomAnchors)
                    .ContinueWith(result => CheckIfAnchorsContainClassifications(result, roomAnchors, requestedAnchorClassifications, task));
            }
        }
    }

    private static void CheckIfAnchorsContainClassifications(bool success, List<OVRAnchor> roomAnchors, IEnumerable<string> requestedAnchorClassifications, OVRTask<bool> task)
    {
        if (!success)
        {
            Development.Log(nameof(OVRSceneManager),
                $"{nameof(OVRAnchor.FetchAnchorsAsync)} failed on {nameof(DoesRoomSetupExist)}() request to fetch anchors in rooms.");
            return;
        }

        using (new OVRObjectPool.ListScope<string>(out var labels))
        {
            CollectLabelsFromAnchors(roomAnchors, labels);

            foreach (var classification in requestedAnchorClassifications)
            {
                var labelIndex = labels.IndexOf(classification);
                if (labelIndex >= 0)
                {
                    labels.RemoveAt(labelIndex);
                }
                else
                {
                    task.SetResult(false);
                    return;
                }
            }
        }
        task.SetResult(true);
    }

    private static void CollectLabelsFromAnchors(List<OVRAnchor> anchors, List<string> labels)
    {
        for (int i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];

            if (anchor.TryGetComponent<OVRSemanticLabels>(out var classification))
            {
                labels.AddRange(classification.Labels.Split(OVRSemanticClassification.LabelSeparator));
            }
        }
    }

    private static void OnTrackingSpaceChanged(Transform trackingSpace)
    {
        // Tracking space changed, update all scene anchors using their cache
        UpdateAllSceneAnchors();
    }

    private void Update()
    {
        UpdateSomeSceneAnchors();
    }

    private static void UpdateAllSceneAnchors()
    {
        foreach (var sceneAnchor in OVRSceneAnchor.SceneAnchors.Values)
        {
            sceneAnchor.TryUpdateTransform(useCache: true);

            if (sceneAnchor.TryGetComponent(out OVRScenePlane plane))
            {
                plane.UpdateTransform();
                plane.RequestBoundary();
            }

            if (sceneAnchor.TryGetComponent(out OVRSceneVolume volume))
            {
                volume.UpdateTransform();
            }
        }
    }

    private void UpdateSomeSceneAnchors()
    {
        for (var i = 0; i < Math.Min(OVRSceneAnchor.SceneAnchorsList.Count, MaxSceneAnchorUpdatesPerFrame); i++)
        {
            _sceneAnchorUpdateIndex %= OVRSceneAnchor.SceneAnchorsList.Count;
            var anchor = OVRSceneAnchor.SceneAnchorsList[_sceneAnchorUpdateIndex++];
            anchor.TryUpdateTransform(useCache: false);
        }
    }

#pragma warning disable CS0618 // Type or member is obsolete
    private RoomLayoutInformation GetRoomLayoutInformation()
    {
        var roomLayout = new RoomLayoutInformation();
#pragma warning restore CS0618 // Type or member is obsolete
        if (OVRSceneRoom.SceneRoomsList.Count > 0)
        {
            roomLayout.Floor = OVRSceneRoom.SceneRoomsList[0].Floor;
            roomLayout.Ceiling = OVRSceneRoom.SceneRoomsList[0].Ceiling;
            roomLayout.Walls.Clear();
            roomLayout.Walls.AddRange(OVRSceneRoom.SceneRoomsList[0].Walls);
        }

        return roomLayout;
    }

    private void OnEnable()
    {
        // Bind events
        OVRManager.SceneCaptureComplete += OVRManager_SceneCaptureComplete;

        if (OVRManager.display != null)
        {
            OVRManager.display.RecenteredPose += UpdateAllSceneAnchors;
        }

        if (!_cameraRig)
        {
            _cameraRig = FindAnyObjectByType<OVRCameraRig>();
        }

        if (_cameraRig)
        {
            _cameraRig.TrackingSpaceChanged += OnTrackingSpaceChanged;
        }
    }

    private void OnDisable()
    {
        // Unbind events
        OVRManager.SceneCaptureComplete -= OVRManager_SceneCaptureComplete;

        if (OVRManager.display != null)
        {
            OVRManager.display.RecenteredPose -= UpdateAllSceneAnchors;
        }

        if (_cameraRig)
        {
            _cameraRig.TrackingSpaceChanged -= OnTrackingSpaceChanged;
        }
    }

    /// <summary>
    /// Determines if a point is inside of a 2d polygon.
    /// </summary>
    /// <param name="boundaryVertices">The vertices that make up the bounds of the polygon</param>
    /// <param name="target">The target point to test</param>
    /// <returns>True if the point is inside the polygon, false otherwise</returns>
    internal static bool PointInPolygon2D(NativeArray<Vector2> boundaryVertices, Vector2 target)
    {
        if (boundaryVertices.Length < 3)
            return false;

        int collision = 0;
        var x = target.x;
        var y = target.y;

        for (int i = 0; i < boundaryVertices.Length; i++)
        {
            var x1 = boundaryVertices[i].x;
            var y1 = boundaryVertices[i].y;

            var x2 = boundaryVertices[(i + 1) % boundaryVertices.Length].x;
            var y2 = boundaryVertices[(i + 1) % boundaryVertices.Length].y;

            if (y < y1 != y < y2 &&
                x < x1 + ((y - y1) / (y2 - y1)) * (x2 - x1))
            {
                collision += (y1 < y2) ? 1 : -1;
            }
        }

        return collision != 0;
    }

    #region Action callbacks

    private void OVRManager_SceneCaptureComplete(UInt64 requestId, bool result)
    {
        if (requestId != _sceneCaptureRequestId)
        {
            Verbose?.LogWarning(nameof(OVRSceneManager),
                $"Scene Room Setup with requestId: [{requestId}] was ignored, as it was not issued by this Scene Load request.");
            return;
        }

        Development.Log(nameof(OVRSceneManager),
            $"{nameof(OVRManager_SceneCaptureComplete)}() requestId: [{requestId}] result: [{result}]");

        if (result)
        {
            // Either the user created a room, or they confirmed that the existing room is up to date. We can now load it.
            Development.Log(nameof(OVRSceneManager),
                $"The Room Setup returned without errors. Invoking {nameof(SceneCaptureReturnedWithoutError)}.");
            SceneCaptureReturnedWithoutError?.Invoke();
        }
        else
        {
            Development.LogError(nameof(OVRSceneManager),
                $"An error occurred when sending the user to the Room Setup. Invoking {nameof(UnexpectedErrorWithSceneCapture)}.");
            UnexpectedErrorWithSceneCapture?.Invoke();
        }
    }

    internal OVRSceneAnchor InstantiateSceneAnchor(OVRAnchor anchor, OVRSceneAnchor prefab)
    {
        var space = (OVRSpace)anchor.Handle;
        var uuid = anchor.Uuid;

        // Query for the semantic classification of the object
        var hasSemanticLabels = OVRPlugin.GetSpaceSemanticLabels(space, out var labelString);
        var labels = hasSemanticLabels
            ? labelString.Split(',')
            : Array.Empty<string>();

        // Search the prefab override for a matching label, and if found override the prefab
        if (PrefabOverrides.Count > 0)
        {
            foreach (var label in labels)
            {
                // Skip empty labels
                if (string.IsNullOrEmpty(label)) continue;

                // Search the prefab override for an entry matching the label
                foreach (var @override in PrefabOverrides)
                {
                    if (@override.ClassificationLabel == label)
                    {
                        prefab = @override.Prefab;
                        break;
                    }
                }
            }
        }

        // This can occur if neither the prefab nor any matching override prefab is set in the inspector
        if (prefab == null)
        {
            Verbose?.Log(nameof(OVRSceneManager),
                $"No prefab was provided for space: [{space}]"
                + (labels.Length > 0 ? $" with semantic label {labels[0]}" : ""));
            return null;
        }

        var sceneAnchor = Instantiate(prefab, Vector3.zero, Quaternion.identity, _initialAnchorParent);
        sceneAnchor.gameObject.SetActive(true);
        sceneAnchor.Initialize(anchor);

        return sceneAnchor;
    }

    #endregion
}
