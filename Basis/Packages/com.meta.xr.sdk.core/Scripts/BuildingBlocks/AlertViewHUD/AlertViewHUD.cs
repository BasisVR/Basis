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

using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif // UNITY_EDITOR

public class AlertViewHUD : MonoBehaviour
{
    public enum MessageType
    {
        Info, Warning, Error
    }

    private static AlertViewHUD Instance { get; set; }

    [Tooltip("Set -1 to show always.")]
    [SerializeField] internal int _hideAfterSec = 20;

    public int HideAfterSec
    {
        get => _hideAfterSec;
        set => _hideAfterSec = value;
    }

    [SerializeField] internal bool _centerInCamera = true;
    public bool CenterInCamera
    {
        get => _centerInCamera;
        set => _centerInCamera = value;
    }

    [SerializeField] private GameObject _panel;
    [SerializeField] private Sprite _warningIcon;
    [SerializeField] private Sprite _errorIcon;
    [SerializeField] private Sprite _infoIcon;
    [SerializeField] private Text _messageTextField;
    [SerializeField] private Text _messageTypeTextField;
    [SerializeField] private Image _messageTypeIconField;

    private Transform _centerEyeTransform;
    private bool Hidden => !_panel.activeSelf;

    private float _initialTime;
    private Vector3 _initialPosition;
    private Quaternion _initialRotation;

    private float _speed = 7f;

    private void Awake()
    {
        Instance = this;

#pragma warning disable CS0618 // Type or member is obsolete
        _centerEyeTransform = FindObjectOfType<OVRCameraRig>()?.centerEyeAnchor;
#pragma warning restore CS0618 // Type or member is obsolete
        _initialTime = Time.time;
        _initialPosition = transform.position;
        _initialRotation = transform.rotation;

        Hide();
    }

    public static void PostMessage(string message, MessageType messageType = MessageType.Warning)
    {
        if (Instance == null)
        {
            return;
        }
        Instance.Post(message, messageType);
    }

    private void Post(string message, MessageType type)
    {
        switch (type)
        {
            case MessageType.Info:
                _messageTypeIconField.sprite = _infoIcon;
                _messageTypeTextField.text = "Info";
                break;
            case MessageType.Warning:
                _messageTypeIconField.sprite = _warningIcon;
                _messageTypeTextField.text = "Warning";
                break;
            case MessageType.Error:
                _messageTypeIconField.sprite = _errorIcon;
                _messageTypeTextField.text = "Error";
                break;
        }
        _messageTextField.text = message + "\n";
        Reset();
    }

    private void ClearMessage() => _messageTextField.text = "";

    private void Update()
    {
        CalculateHideAfterMessage();
        FollowCamera();
    }

    private void CalculateHideAfterMessage()
    {
        if (HideAfterSec == -1 || Hidden)
        {
            return;
        }

        if (Time.time - _initialTime >= HideAfterSec)
        {
            Hide();
        }
    }

    private void Reset()
    {
        _initialTime = Time.time;
        _panel.SetActive(true);
    }

    private void Hide() => _panel.SetActive(false);

    private void FollowCamera()
    {
        if (_centerEyeTransform == null || Hidden || !CenterInCamera)
        {
            return;
        }

        var targetPosition = _centerEyeTransform.TransformPoint(_initialPosition);
        var targetRotation = _centerEyeTransform.rotation * _initialRotation;

        var p = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * _speed);
        var r = Quaternion.Lerp(transform.rotation, targetRotation, Time.deltaTime * _speed);
        transform.SetPositionAndRotation(p, r);
    }
}

#if UNITY_EDITOR

[CustomEditor(typeof(AlertViewHUD))]
public class AlertViewHUDEditor : Editor
{
    private bool _fold;

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AlertViewHUD._hideAfterSec)));
        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AlertViewHUD._centerInCamera)));

        _fold = EditorGUILayout.Foldout(_fold, "References");
        if (_fold)
        {
            DrawPropertiesExcluding(serializedObject,
                nameof(AlertViewHUD._hideAfterSec),
                nameof(AlertViewHUD._centerInCamera));
        }

        serializedObject.ApplyModifiedProperties();
    }
}

#endif // UNITY_EDITOR
