using LiteNetLib;
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
        public static SceneNetworkMessageReceiveEvent OnNetworkMessageReceived;
        public static SceneNetworkMessageSendEvent OnNetworkMessageSend;
        /// <summary>
        /// this is used for sending Network Messages
        /// very much a data sync that can be used more like a traditional sync method
        /// </summary>
        /// <param name="MessageIndex"></param>
        /// <param name="buffer"></param>
        /// <param name="DeliveryMethod"></param>
        /// <param name="Recipients">if null everyone but self, you can include yourself to make it loop back over the network</param>
        public static void NetworkMessageSend(ushort MessageIndex, byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable, ushort[] Recipients = null)
        {
            OnNetworkMessageSend?.Invoke(MessageIndex, buffer, DeliveryMethod, Recipients);
        }
        /// <summary>
        /// this is used for sending Network Messages,
        /// more like a RPC then a data sync
        /// </summary>
        /// <param name="MessageIndex"></param>
        /// <param name="DeliveryMethod"></param>
        /// <param name="Recipients">if null everyone but self, you can include yourself to make it loop back over the network</param>
        public static void NetworkMessageSend(ushort MessageIndex, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable)
        {
            OnNetworkMessageSend?.Invoke(MessageIndex, null, DeliveryMethod);
        }
        /// <summary>
        /// this is used for Receiving Network Messages
        /// </summary>
        /// <param name="MessageIndex"></param>
        /// <param name="buffer"></param>
        public delegate void SceneNetworkMessageReceiveEvent(ushort PlayerID, ushort MessageIndex, byte[] buffer, LiteNetLib.DeliveryMethod deliveryMethod);


        /// <summary>
        /// this is used for sending Network Messages
        /// </summary>
        /// <param name="MessageIndex"></param>
        /// <param name="buffer"></param>
        /// <param name="DeliveryMethod"></param>

        public delegate void SceneNetworkMessageSendEvent(ushort MessageIndex, byte[] buffer, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable, ushort[] Recipients = null);

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
