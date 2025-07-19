using System;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace Basis.Scripts.BasisSdk
{
    public class BasisScene : BasisContentBase
    {
        public Transform SpawnPoint;
        public float RespawnHeight = -100;
        public float RespawnCheckTimer = 0.1f;
        [HideInInspector]
        public UnityEngine.Audio.AudioMixerGroup Group;
        public static BasisScene Instance;
        public static Action<BasisScene> Ready;
        public static Action<BasisScene> Destroyed;
        public Camera MainCamera;
        [HideInInspector]
        public bool IsReady;
        public void Awake()
        {
            Instance = this;
            Ready?.Invoke(this);
            IsReady = true;
        }
        public void OnDestroy()
        {
            IsReady = false;
            Destroyed?.Invoke(this);
        }
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
