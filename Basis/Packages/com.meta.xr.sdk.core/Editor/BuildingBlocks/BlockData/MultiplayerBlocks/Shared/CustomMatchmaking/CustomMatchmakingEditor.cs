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

using Meta.XR.MultiplayerBlocks.Shared;
using UnityEngine;
using UnityEditor;

namespace Meta.XR.BuildingBlocks.Shared.Editor
{
    [CustomEditor(typeof(CustomMatchmaking))]
    internal class CustomMatchmakingEditor : UnityEditor.Editor
    {
        private const int MaxPlayersPerRoom = 256;
        private string _roomToken;
        private string _roomPassword;
        private string _roomLobby;

        private CustomMatchmaking _customMatchmaking;
        private SerializedProperty _onRoomCreationFinishedProperty;
        private SerializedProperty _onRoomJoinFinishedProperty;
        private SerializedProperty _onRoomLeaveFinishedProperty;

        private SerializedProperty _lobbyNameProperty;
        private SerializedProperty _isPrivateRoomProperty;
        private SerializedProperty _isPasswordProtectedProperty;

        private void OnEnable()
        {
            _onRoomCreationFinishedProperty = serializedObject.FindProperty(nameof(CustomMatchmaking.onRoomCreationFinished));
            _onRoomJoinFinishedProperty = serializedObject.FindProperty(nameof(CustomMatchmaking.onRoomJoinFinished));
            _onRoomLeaveFinishedProperty = serializedObject.FindProperty(nameof(CustomMatchmaking.onRoomLeaveFinished));

            _lobbyNameProperty = serializedObject.FindProperty("lobbyName");
            _isPrivateRoomProperty = serializedObject.FindProperty("isPrivate");
            _isPasswordProtectedProperty = serializedObject.FindProperty("isPasswordProtected");

            _customMatchmaking = (CustomMatchmaking)target;
            _customMatchmaking.onRoomLeaveFinished.AddListener(RefreshInspectorUI);
        }

        private void OnDisable()
        {
            _customMatchmaking.onRoomLeaveFinished.RemoveListener(RefreshInspectorUI);

            ResetRoomCredentials();
        }

        private void ResetRoomCredentials()
        {
            _roomToken = null;
            _roomPassword = null;
            _roomLobby = null;
        }

        private void RefreshInspectorUI()
        {
            EditorUtility.SetDirty(target);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            base.OnInspectorGUI();

            DrawSection("Create Room", _onRoomCreationFinishedProperty, DrawCreateRoom);
            DrawSection("Join Room", _onRoomJoinFinishedProperty, DrawJoinRoom);
            DrawSection("Leave Room", _onRoomLeaveFinishedProperty, DrawLeaveRoom);

            if (GUI.changed)
            {
                RefreshInspectorUI();
            }

            serializedObject.ApplyModifiedProperties();
        }

        #region Drawing Methods

        private static void DrawSection(string title, SerializedProperty property, System.Action drawMethod)
        {
            EditorGUILayout.Space();
            GUILayout.Label(title, EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            drawMethod.Invoke();

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(property);
            EditorGUILayout.EndVertical();
        }

        private void DrawCreateRoom()
        {
            EditorGUILayout.PropertyField(_lobbyNameProperty);
            EditorGUILayout.PropertyField(_isPrivateRoomProperty);

            _customMatchmaking.MaxPlayersPerRoom = EditorGUILayout.IntSlider("Max Players Per Room",
                _customMatchmaking.MaxPlayersPerRoom, 1, MaxPlayersPerRoom);

            if (_customMatchmaking.SupportsRoomPassword)
            {
                EditorGUILayout.PropertyField(_isPasswordProtectedProperty);
            }

            using (new EditorGUI.DisabledScope(!Application.isPlaying || _customMatchmaking.IsConnected))
            {
                if (GUILayout.Button("Create Room"))
                {
                    CreateRoom();
                }
            }
        }

        private void DrawJoinRoom()
        {
            using var _ = new EditorGUI.DisabledScope(!Application.isPlaying);

            _roomToken = EditorGUILayout.TextField("Room Token", _roomToken);

            if (_customMatchmaking.SupportsRoomPassword)
            {
                _roomPassword = EditorGUILayout.TextField("Room Password", _roomPassword);
            }

            using (new EditorGUI.DisabledScope(_customMatchmaking.IsConnected))
            {
                if (GUILayout.Button("Join Room"))
                {
                    JoinRoom(_roomToken, _roomPassword);
                }
            }

            EditorGUILayout.Space();

            _roomLobby = EditorGUILayout.TextField("Room Lobby", _roomLobby);

            using (new EditorGUI.DisabledScope(_customMatchmaking.IsConnected))
            {
                if (GUILayout.Button("Join Open Room"))
                {
                    JoinOpenRoom(_roomLobby);
                }
            }
        }

        private void DrawLeaveRoom()
        {
            using var _ = new EditorGUI.DisabledScope(!Application.isPlaying || !_customMatchmaking.IsConnected);

            if (!GUILayout.Button("Leave Room"))
            {
                return;
            }

            _customMatchmaking.LeaveRoom();
            RefreshInspectorUI();
        }

        #endregion

        #region Event Callbacks

        private async void CreateRoom()
        {
            var result = await _customMatchmaking.CreateRoom();
            if (result.IsSuccess)
            {
                _roomToken = result.RoomToken;
                _roomPassword = result.RoomPassword;
            }
            else
            {
                ResetRoomCredentials();
            }

            RefreshInspectorUI();
        }

        private async void JoinRoom(string roomToken, string roomPassword)
        {
            await _customMatchmaking.JoinRoom(roomToken, roomPassword);
            RefreshInspectorUI();
        }

        private async void JoinOpenRoom(string roomLobby)
        {
            await _customMatchmaking.JoinOpenRoom(roomLobby);
            RefreshInspectorUI();
        }

        #endregion
    }
}
