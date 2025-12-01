using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Basis.Scripts.BasisSdk
{
    /// <summary>
    /// Represents a scene within the Basis system, managing spawn points, respawn behavior,
    /// and lifecycle events for scene readiness and destruction.
    /// </summary>
    public class BasisScene : BasisContentBase
    {
        /// <summary>
        /// Default spawn point for players or objects in the scene.
        /// </summary>
        public Transform SpawnPoint;

        /// <summary>
        /// Height threshold below which respawn is triggered.
        /// </summary>
        public float RespawnHeight = -100;

        /// <summary>
        /// Interval in seconds between checks for respawn conditions.
        /// </summary>
        public float RespawnCheckTimer = 0.1f;

        /// <summary>
        /// Audio mixer group for scene-wide audio routing.
        /// </summary>
        [HideInInspector]
        public UnityEngine.Audio.AudioMixerGroup Group;

        /// <summary>
        /// Singleton-style reference to the active <see cref="BasisScene"/> in this Unity scene.
        /// </summary>
        public static BasisScene Instance;

        /// <summary>
        /// Fired once the scene <see cref="Awake"/> method completes and the scene is ready.
        /// </summary>
        public static Action<BasisScene> Ready;

        /// <summary>
        /// Fired when the scene is destroyed and no longer valid.
        /// </summary>
        public static Action<BasisScene> Destroyed;

        /// <summary>
        /// Reference to the primary camera in the scene.
        /// </summary>
        public Camera MainCamera;

        /// <summary>
        /// Indicates whether the scene has completed its initialization.
        /// </summary>
        [HideInInspector]
        public bool IsReady;

        /// <summary>
        /// Unity Awake hook. Sets <see cref="Instance"/>, fires the <see cref="Ready"/> event, and marks the scene as ready.
        /// </summary>
        public void Awake()
        {
            Instance = this;
            Ready?.Invoke(this);
            IsReady = true;
        }

        /// <summary>
        /// Unity OnDestroy hook. Marks the scene as not ready and fires the <see cref="Destroyed"/> event.
        /// </summary>
        public void OnDestroy()
        {
            IsReady = false;
            Destroyed?.Invoke(this);
        }

        /// <summary>
        /// Attempts to find a <see cref="BasisScene"/> associated with the given <see cref="GameObject"/>.
        /// </summary>
        /// <param name="ObjectInScene">A GameObject within the scene being searched.</param>
        /// <param name="BasisScene">Outputs the located <see cref="BasisScene"/> if found.</param>
        /// <returns><c>true</c> if a <see cref="BasisScene"/> is found; otherwise <c>false</c>.</returns>
        public static bool SceneTraversalFindBasisScene(GameObject ObjectInScene, out BasisScene BasisScene)
        {
            if (ObjectInScene == null)
            {
                BasisDebug.LogError("Missing Gameobject In Scene Parameter!", BasisDebug.LogTag.Scene);
                BasisScene = null;
                return false;
            }
            Scene Scene = ObjectInScene.scene;
            return SceneTraversalFindBasisScene(Scene, out BasisScene);
        }

        /// <summary>
        /// Attempts to find a <see cref="BasisScene"/> within the specified <see cref="Scene"/>.
        /// </summary>
        /// <param name="scene">The Unity scene to search.</param>
        /// <param name="BasisScene">Outputs the located <see cref="BasisScene"/> if found.</param>
        /// <returns><c>true</c> if a <see cref="BasisScene"/> is found; otherwise <c>false</c>.</returns>
        public static bool SceneTraversalFindBasisScene(Scene scene, out BasisScene BasisScene)
        {
            GameObject[] Root = scene.GetRootGameObjects();
            foreach (GameObject root in Root)
            {
                BasisScene = root.GetComponentInChildren<BasisScene>();
                if (BasisScene == null)
                {
                    continue;
                }
                return true;
            }
            BasisScene = null;
            return false;
        }
    }
}
