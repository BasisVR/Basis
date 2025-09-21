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

using System.Collections;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    /// The class responsible for synchronizing the position and username of a player's name tag to all connected clients
    /// when using the Unity Netcode for Gameobjects networking framework.
    /// The position is synced on each network update and the username is synced every time it's changed.
    /// </summary>
    public class PlayerNameTagNGO : NetworkBehaviour
    {
        [SerializeField] private Text nameTag;
        [SerializeField] private GameObject nameTagGo;
        [SerializeField] private GameObject nameTagPanel;
        [SerializeField] private Transform nameTagContainer;
        [SerializeField] private float heightOffset = 0.3f;

        private Transform _centerEye;
        /// <summary>
        /// The networked string responsible for storing the player's username when using the Unity Netcode for Gameobjects networking framework.
        /// When set, it triggers a visual update of what's displayed in the player name tag for all connected clients.
        /// </summary>
        public NetworkVariable<FixedString128Bytes> PlayerName = new();

        private void Awake()
        {
            PlayerName.OnValueChanged += OnPlayerNameChange;

            if (OVRManager.instance)
            {
                _centerEye = OVRManager.instance.GetComponentInChildren<OVRCameraRig>().centerEyeAnchor;
            }
        }

        public override void OnNetworkSpawn()
        {
            OnPlayerNameChange(PlayerName.Value, PlayerName.Value);
            nameTagGo.SetActive(!IsOwner);
        }

        private IEnumerator UpdateNameUI(string playerName)
        {
            nameTag.text = playerName;

            // refresh nameTag panel
            yield return new WaitForFixedUpdate();
            var vlg = nameTagPanel.GetComponent<VerticalLayoutGroup>();
            vlg.enabled = false;
            // ReSharper disable once Unity.InefficientPropertyAccess
            vlg.enabled = true;
        }

        private void OnPlayerNameChange(FixedString128Bytes previous, FixedString128Bytes current)
        {
            StartCoroutine(UpdateNameUI(current.ToString()));
        }

        public void FixedUpdate()
        {
            if (!IsOwner)
            {
                return;
            }

            if (_centerEye == null)
            {
                return;
            }

            transform.position = _centerEye.transform.position + new Vector3(0, heightOffset, 0);
        }

        private void Update()
        {
            if (IsOwner)
            {
                return;
            }

            if (_centerEye != null)
            {
                nameTagGo.transform.LookAt(_centerEye.position, Vector3.up);
            }
        }

    }
}
