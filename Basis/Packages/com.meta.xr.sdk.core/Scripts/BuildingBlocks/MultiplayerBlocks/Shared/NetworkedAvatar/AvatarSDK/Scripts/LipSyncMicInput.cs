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

#if META_AVATAR_SDK_DEFINED
using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

#if UNITY_ANDROID && !UNITY_EDITOR
using Oculus.Avatar2;
using System.Collections;
using UnityEngine.Android;
#endif


public enum LipSyncMicInputMode
{
    HoldToSpeak, // Constantly hold a button to enable the mic
    PushToSpeak, // Press a button to enable the mic; press again to disable
    ConstantSpeak, // Mic is always on
    Manual // Call StartMicrophone and StopMicrophone to control externally
}

[RequireComponent(typeof(AudioSource))]
public class LipSyncMicInput : MonoBehaviour
{
    private const string logScope = "micInput";

    // Serialized Members

    [Tooltip("Manual specification of Audio Source. Default will use any attached to the same object.")]
    [SerializeField]
    private AudioSource _audioSource;

    [Range(0f, 1f)]
    [Tooltip("Microphone input volume control.")]
    [SerializeField]
    private float _micInputVolume = 1f;

    [SerializeField]
    private LipSyncMicInputMode _micInputMode = LipSyncMicInputMode.ConstantSpeak;

    [Tooltip("Button name used to drive HoldToSpeak and PushToSpeak methods of control.")]
    [SerializeField]
    private string _inputButtonName = "";

    [SerializeField]
    private bool _stopRecordingWhilePaused = true;
    [SerializeField]
    private bool _stopRecordingWhileUnfocused = true;

    [Tooltip("The max amount of time to wait for mic recording to start.")]
    [SerializeField]
    private float _micCaptureTimeout = 5f;

    [SerializeField]
    private bool _preferOculusMic = true;

    // Public Properties

    public AudioSource audioSource => _audioSource;
    public int MicFrequency => _micFrequency;

    public float MicInputVolume
    {
        get { return _micInputVolume; }
        set
        {
            _micInputVolume = Mathf.Clamp01(value);
            _audioSource.volume = _micInputVolume;
        }
    }

    public bool micSelected => _selectedDevice?.Length > 0;

    // Active will be true whenever the mic should be recording according to the InputMode
    // Active can still be true when the mic is not recording due to lack of focus or because the app is paused
    public bool active { get; private set; }

    // Other Private members

    private bool _initialized;
    private bool _askingPermission;
    private int _micFrequency = 48000;
    private bool _focused = true;
    private bool _paused;
    private string _selectedDevice;
    private int _minFreq, _maxFreq;

    // Core Private Functions

    private void Awake()
    {
        if (_audioSource == null)
        {
            _audioSource = GetComponent<AudioSource>();
        }
    }

    private void Start()
    {
        _audioSource.loop = true;
        _audioSource.mute = false;
        _audioSource.volume = _micInputVolume;
    }

    private void Update()
    {
        // Lazy Microphone initialization (needed on Android)
        if (!_initialized)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if( Permission.HasUserAuthorizedPermission("android.permission.RECORD_AUDIO") )
            {
                InitializeMicrophone();
            }
            else if(!_askingPermission)
            {
                _askingPermission = true;

                OvrAvatarManager.Instance.RequestMicPermission();
                return;
            }
            else
            {
                return;
            }
#else
            InitializeMicrophone();
#endif
        }

        ProcessMicActivity();
    }

    // Events

    private void OnApplicationFocus(bool focus)
    {
        _focused = focus;
        if (!_focused && _stopRecordingWhileUnfocused) StopMicrophone_Internal();
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        _paused = pauseStatus;
        if (_paused && _stopRecordingWhilePaused) StopMicrophone_Internal();
    }

    // Other Private Functions

    private void InitializeMicrophone()
    {
#if !OVR_DISABLE_MICROPHONE
        if (_initialized) return;
        if (Microphone.devices.Length == 0) return;

        _selectedDevice = Microphone.devices[0];
        if (_preferOculusMic)
        {
            foreach (var device in Microphone.devices)
            {
                if (device.Contains("Oculus") || device.Contains("Rift"))
                {
                    _selectedDevice = device;
                    break;
                }
            }
        }
        Debug.Log($"Selected microphone {_selectedDevice}");

        GetMicCaps();

        _initialized = true;
#endif
    }

    private void ProcessMicActivity()
    {
#if !OVR_DISABLE_MICROPHONE
        //Hold To Speak
        if (_micInputMode == LipSyncMicInputMode.HoldToSpeak)
        {
            active = Input.GetButton(_inputButtonName);
        }
        //Push To Talk
        else if (_micInputMode == LipSyncMicInputMode.PushToSpeak)
        {
            if (Input.GetButtonDown(_inputButtonName))
            {
                active = !active;
            }
        }
        //Constant Speak
        else if (_micInputMode == LipSyncMicInputMode.ConstantSpeak)
        {
            active = true;
        }

        // Activation
        if (active)
        {
            if (CanStartMic() && !Microphone.IsRecording(_selectedDevice))
            {
                StartMicrophone_Internal();
            }
        }
        else if (Microphone.IsRecording(_selectedDevice))
        {
            StopMicrophone_Internal();
        }
#endif
    }

    private void GetMicCaps()
    {
#if !OVR_DISABLE_MICROPHONE
        if (micSelected == false) return;

        //Gets the frequency of the device
        Microphone.GetDeviceCaps(_selectedDevice, out _minFreq, out _maxFreq);

        if (_minFreq == 0 && _maxFreq == 0)
        {
            Debug.Log("OvrAvatarLipSyncMicInput: GetMicCaps warning - min and max frequencies are 0");
            _minFreq = 48000;
            _maxFreq = 48000;
        }

        if (_micFrequency > _maxFreq)
        {
            _micFrequency = _maxFreq;
        }
#endif
    }

    private bool CanStartMic()
    {
        if (!micSelected) return false;
        if (!_focused && _stopRecordingWhileUnfocused) return false;
        if (_paused && _stopRecordingWhilePaused) return false;
        return true;
    }

    private void StartMicrophone_Internal()
    {
#if !OVR_DISABLE_MICROPHONE
        Debug.Log($"Starting microphone recording with frequency {_micFrequency}");

        //Starts recording
        _audioSource.clip = Microphone.Start(_selectedDevice, true, 10, _micFrequency);
        _audioSource.loop = true;

        Stopwatch timer = Stopwatch.StartNew();

        // Wait until the recording has started
        while (!(Microphone.GetPosition(_selectedDevice) > 0) && timer.Elapsed.TotalMilliseconds < _micCaptureTimeout)
        {
            Thread.Sleep(5);
        }

        var samplesRecorded = Microphone.GetPosition(_selectedDevice);
        if (samplesRecorded <= 0)
        {
            throw new Exception("Timeout initializing microphone " + _selectedDevice);
        }

        // Play the audio source
        var latency = (float)samplesRecorded / _micFrequency;
        Debug.Log($"Microphone recording started with latency {latency * 1000.0} ms");
        _audioSource.Play();
#endif
    }

    private void StopMicrophone_Internal()
    {
#if !OVR_DISABLE_MICROPHONE
        if (micSelected == false) return;

        Debug.Log($"Stopping microphone recording");

        // Don't stop the audio source if it is overridden with a clip to play
        if (_audioSource != null &&
            _audioSource.clip != null &&
            _audioSource.clip.name == "Microphone")
        {
            _audioSource.Stop();
        }

        Microphone.End(_selectedDevice);
#endif
    }

    //:: Public Functions

    public void SetMode(LipSyncMicInputMode newMode)
    {
        _micInputMode = newMode;
        active = false;

        if (_initialized) ProcessMicActivity();
    }

    public void StartMicrophone()
    {
#if !OVR_DISABLE_MICROPHONE
        if (_micInputMode != LipSyncMicInputMode.Manual)
        {
            Debug.LogWarning("Starting Microphone while not in Manual Input Mode.");
        }

        active = true;

        if (CanStartMic() && !Microphone.IsRecording(_selectedDevice))
        {
            StartMicrophone_Internal();
        }
#endif
    }

    public void StopMicrophone()
    {
#if !OVR_DISABLE_MICROPHONE
        if (_micInputMode != LipSyncMicInputMode.Manual)
        {
            Debug.LogWarning("Starting Microphone while not in Manual Input Mode.");
        }

        active = false;

        if (Microphone.IsRecording(_selectedDevice))
        {
            StopMicrophone_Internal();
        }
#endif
    }
}
#endif // META_AVATAR_SDK_DEFINED
