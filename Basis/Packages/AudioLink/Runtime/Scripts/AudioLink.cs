using System;

using UnityEngine;

namespace AudioLink
{
#if UDONSHARP
    using UdonSharp;
    using VRC.SDK3.Rendering;
    using VRC.SDKBase;
    using static VRC.SDKBase.VRCShader;

    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public partial class AudioLink : UdonSharpBehaviour
#else
    using Unity.Collections;
    using UnityEngine.Rendering;
    using static Shader;

#if UNITY_WEBGL
    using System.Runtime.InteropServices;
#endif

    public partial class AudioLink : MonoBehaviour
#endif
    {
        const float AudioLinkVersionNumberMajor = 2.00f;
        const float AudioLinkVersionNumberMinor = 1.00f;

        [Header("Main Settings")]
        [Tooltip("Should be used with AudioLinkInput unless source is 2D. WARNING: if used with a custom 3D audio source (not through AudioLinkInput), audio reactivity will be attenuated by player position away from the Audio Source")]
        public AudioSource audioSource;
        [Tooltip("Optional Right Audio Source for Dual Mono setups (AVPro video players)")]
        public AudioSource optionalRightAudioSource;

        [Header("Basic EQ")]
        [Range(0.0f, 2.0f)]
        [Tooltip("Warning: this setting might be taken over by AudioLinkController")]
        public float gain = 1f;

        [Range(0.0f, 2.0f)]
        [Tooltip("Warning: this setting might be taken over by AudioLinkController")]
        public float bass = 1f;

        [Range(0.0f, 2.0f)]
        [Tooltip("Warning: this setting might be taken over by AudioLinkController")]
        public float treble = 1f;

        [Header("4 Band Crossover")]
        [Range(0.0f, 0.168f)]
        [Tooltip("Bass / low mid crossover")]
        public float x0 = 0.0f;

        [Range(0.242f, 0.387f)]
        [Tooltip("Bass / low mid crossover")]
        public float x1 = 0.25f;

        [Range(0.461f, 0.628f)]
        [Tooltip("Low mid / high mid crossover")]
        public float x2 = 0.5f;

        [Range(0.704f, 0.953f)]
        [Tooltip("High mid / treble crossover")]
        public float x3 = 0.75f;

        [Header("4 Band Threshold Points (Sensitivity)")]
        [Range(0.0f, 1.0f)]
        [Tooltip("Bass threshold level (lower is more sensitive)")]
        public float threshold0 = 0.45f;

        [Range(0.0f, 1.0f)]
        [Tooltip("Low mid threshold level (lower is more sensitive)")]
        public float threshold1 = 0.45f;

        [Range(0.0f, 1.0f)]
        [Tooltip("High mid threshold level (lower is more sensitive)")]
        public float threshold2 = 0.45f;

        [Range(0.0f, 1.0f)]
        [Tooltip("Treble threshold level (lower is more sensitive)")]
        public float threshold3 = 0.45f;

        [Header("Fade Controls")]
        [Range(0.0f, 1.0f)]
        [Tooltip("Amplitude fade amount. This creates a linear fade-off / trails effect. Warning: this setting might be taken over by AudioLinkController")]
        public float fadeLength = 0.25f;

        [Range(0.0f, 1.0f)]
        [Tooltip("Amplitude fade exponential falloff. This attenuates the above (linear) fade-off exponentially, creating more of a pulsed effect. Warning: this setting might be taken over by AudioLinkController")]
        public float fadeExpFalloff = 0.75f;

        [Header("Autogain")]
        public bool autogain = true;
        [Range(0.001f, 1.0f)]
        public float autogainDerate = 0.1f;

        [Header("Theme Colors")]
        [Tooltip("Enable for custom theme colors for Avatars to use.")]
#if UNITY_EDITOR
        [Editor.StringInList("ColorChord Colors", "Custom")]
#endif
        public int themeColorMode;
        public Color customThemeColor0 = new Color(1.0f, 1.0f, 0.0f, 1.0f);
        public Color customThemeColor1 = new Color(0.0f, 0.0f, 1.0f, 1.0f);
        public Color customThemeColor2 = new Color(1.0f, 0.0f, 0.0f, 1.0f);
        public Color customThemeColor3 = new Color(0.0f, 1.0f, 0.0f, 1.0f);

        [Header("Custom Global Strings")]
        [UdonSynced] public string customString1;
        [UdonSynced] public string customString2;

        [HideInInspector] public Material audioMaterial;
        [HideInInspector] public CustomRenderTexture audioRenderTexture;

        [Header("Misc")]
        [Tooltip("Automatically determines media states, such as whether audio is currently playing or not, and makes it available to AudioLink compatible shaders. Disable this if you intend to control media states via script, for example to support custom video players.")]
        public bool autoSetMediaState = true;

        [Header("Experimental (Limits performance)")]
        [Tooltip("Enable Udon audioData array. Required by AudioReactiveLight and AudioReactiveObject. Uses ReadPixels which carries a performance hit. For experimental use when performance is less of a concern")]
        [HideInInspector] public bool audioDataToggle = false;

        [NonSerialized] public Color[] audioData = new Color[AudioLinkWidth * AudioLinkHeight];
        [HideInInspector] public Texture2D audioData2D; // Texture2D reference for hacked Blit, may eventually be depreciated

        private bool _audioLinkEnabled = true;

        private float[] _audioFramesL = new float[1023 * 4];
        private float[] _audioFramesR = new float[1023 * 4];
        private float[] _samples = new float[1023];

        private string _masterName;
        // Mechanism to provide sync'd instance time to all avatars.
        [UdonSynced] private double _masterInstanceJoinTime;
        private double _elapsedTime = 0;
        private double _elapsedTimeMSW = 0;
        private int _networkTimeMS;
        private double _networkTimeMSAccumulatedError;
#if UDONSHARP
        private bool _hasInitializedTime = false;
        private VRCPlayerApi _localPlayer;
#endif
        private double _fpsTime = 0;
        private int _fpsCount = 0;

#if UDONSHARP
        private double GetElapsedSecondsSince2019() { return (Networking.GetNetworkDateTime() - new DateTime(2020, 1, 1)).TotalSeconds; }
        //private double GetElapsedSecondsSinceMidnightUTC() { return (Networking.GetNetworkDateTime() - DateTime.UtcNow.Date ).TotalSeconds; }
#else
        private double GetElapsedSecondsSince2019() { return (DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds; }
#endif

        // Fix for AVPro mono game output bug (if running the game with a mono output source like a headset)
        private int _rightChannelTestDelay = 300;
        private int _rightChannelTestCounter;
        private bool _ignoreRightChannel = false;

        #region PropertyIDs

        // ReSharper disable InconsistentNaming

        private int _AudioTexture;

        // AudioLink 4 Band
        private int _FadeLength;
        private int _FadeExpFalloff;
        private int _Gain;
        private int _Bass;
        private int _Treble;
        private int _X0;
        private int _X1;
        private int _X2;
        private int _X3;
        private int _Threshold0;
        private int _Threshold1;
        private int _Threshold2;
        private int _Threshold3;

        // Autogain
        private int _Autogain;
        private int _AutogainDerate;

        private int _SourceVolume;
        private int _SourceDistance;
        private int _SourceSpatialBlend;
        private int _SourcePosition;

        // Theme Colors
        private int _ThemeColorMode;
        private int _CustomThemeColor0;
        private int _CustomThemeColor1;
        private int _CustomThemeColor2;
        private int _CustomThemeColor3;

        // Global strings
        private int _StringLocalPlayer;
        private int _StringMasterPlayer;
        private int _StringCustom1;
        private int _StringCustom2;

        // Set by Udon
        private int _AdvancedTimeProps0;
        private int _AdvancedTimeProps1;
        private int _PlayerCountAndData;
        private int _VersionNumberAndFPSProperty;

        //Raw audio data.
        private int _Samples0L;
        private int _Samples1L;
        private int _Samples2L;
        private int _Samples3L;

        private int _Samples0R;
        private int _Samples1R;
        private int _Samples2R;
        private int _Samples3R;
        // ReSharper restore InconsistentNaming

#if UNITY_WEBGL

        public static WebALPeer audioLinkWebPeer { get; private set; }

        [DllImport("__Internal")]
        private static extern int SetupAnalyzerSpace();
        [DllImport("__Internal")]
        private static extern int LinkAnalyzer(int ID, float duration, int bufferSize);
        [DllImport("__Internal")]
        private static extern int UnlinkAnalyzer(int ID);
        [DllImport("__Internal")]
        private static extern int FetchAnalyzerLeft(int ID, float[] timeDomainDataLeft, int size);
        [DllImport("__Internal")]
        private static extern int FetchAnalyzerRight(int ID, float[] timeDomainDataRight, int size);

        private int WebALID = 0;

#endif

        private bool _IsInitialized = false;
        private void InitIDs()
        {
            if (_IsInitialized)
                return;

            _AudioTexture = PropertyToID("_AudioTexture");

            _FadeLength = PropertyToID("_FadeLength");
            _FadeExpFalloff = PropertyToID("_FadeExpFalloff");
            _Gain = PropertyToID("_Gain");
            _Bass = PropertyToID("_Bass");
            _Treble = PropertyToID("_Treble");
            _X0 = PropertyToID("_X0");
            _X1 = PropertyToID("_X1");
            _X2 = PropertyToID("_X2");
            _X3 = PropertyToID("_X3");
            _Threshold0 = PropertyToID("_Threshold0");
            _Threshold1 = PropertyToID("_Threshold1");
            _Threshold2 = PropertyToID("_Threshold2");
            _Threshold3 = PropertyToID("_Threshold3");

            _Autogain = PropertyToID("_Autogain");
            _AutogainDerate = PropertyToID("_AutogainDerate");

            _SourceVolume = PropertyToID("_SourceVolume");
            _SourceDistance = PropertyToID("_SourceDistance");
            _SourceSpatialBlend = PropertyToID("_SourceSpatialBlend");
            _SourcePosition = PropertyToID("_SourcePosition");

            _ThemeColorMode = PropertyToID("_ThemeColorMode");
            _CustomThemeColor0 = PropertyToID("_CustomThemeColor0");
            _CustomThemeColor1 = PropertyToID("_CustomThemeColor1");
            _CustomThemeColor2 = PropertyToID("_CustomThemeColor2");
            _CustomThemeColor3 = PropertyToID("_CustomThemeColor3");

            _StringLocalPlayer = PropertyToID("_StringLocalPlayer");
            _StringMasterPlayer = PropertyToID("_StringMasterPlayer");
            _StringCustom1 = PropertyToID("_StringCustom1");
            _StringCustom2 = PropertyToID("_StringCustom2");

            _AdvancedTimeProps0 = PropertyToID("_AdvancedTimeProps0");
            _AdvancedTimeProps1 = PropertyToID("_AdvancedTimeProps1");
            _VersionNumberAndFPSProperty = PropertyToID("_VersionNumberAndFPSProperty");
            _PlayerCountAndData = PropertyToID("_PlayerCountAndData");

            _Samples0L = PropertyToID("_Samples0L");
            _Samples1L = PropertyToID("_Samples1L");
            _Samples2L = PropertyToID("_Samples2L");
            _Samples3L = PropertyToID("_Samples3L");

            _Samples0R = PropertyToID("_Samples0R");
            _Samples1R = PropertyToID("_Samples1R");
            _Samples2R = PropertyToID("_Samples2R");
            _Samples3R = PropertyToID("_Samples3R");

            _IsInitialized = true;
        }
        #endregion

        // TODO(3): try to port this to standalone
        void Start()
        {
#if UDONSHARP
            {
                // Handle sync'd time stuff.
                // OLD NOTES
                //Originally used GetServerTimeInMilliseconds
                //Networking.GetServerTimeInMilliseconds will alias to every 49.7 days (2^32ms). GetServerTimeInSeconds also aliases.
                //We still alias, but TCL suggested using Networking.GetNetworkDateTime.
                //DateTime currentDate = Networking.GetNetworkDateTime();
                //UInt64 currentTimeTicks = (UInt64)(currentDate.Ticks/TimeSpan.TicksPerMillisecond);
                // NEW NOTES
                //We now just compute delta times per frame.

                double startTime = GetElapsedSecondsSince2019();
                _networkTimeMS = Networking.GetServerTimeInMilliseconds();
                if (Networking.IsMaster)
                {
                    _masterInstanceJoinTime = startTime;
                    RequestSerialization();
                }

                //_networkTimeOfDayUTC = GetElapsedSecondsSinceMidnightUTC();
                //Debug.Log($"[AudioLink] _networkTimeOfDayUTC = {_networkTimeOfDayUTC}" );
                //Debug.Log($"[AudioLink] _networkTimeMS = {_networkTimeMS}");
                //Debug.Log($"[AudioLink] Time Sync Debug: IsMaster: {Networking.IsMaster} startTime: {startTime}");

                _rightChannelTestCounter = _rightChannelTestDelay;

                // Set localplayer name on start
                _localPlayer = Networking.LocalPlayer;
                if (VRC.SDKBase.Utilities.IsValid(_localPlayer))
                {
                    UpdateGlobalString(_StringLocalPlayer, _localPlayer.displayName);
                }

                // Set master name once on start
                FindAndUpdateMasterName();
            }
#elif UNITY_WEBGL && !UNITY_EDITOR

            SetupAnalyzerSpace();
            audioLinkWebPeer = new WebALPeer();

            WebALID = UnityEngine.Random.Range(0, 99999);

            LinkAnalyzer(WebALID, audioSource.clip.length, 4096);

            Application.focusChanged += (focus) => 
            {
                if (_audioLinkEnabled) 
                {
                    if (focus) 
                    {
                        LinkAnalyzer(WebALID, audioSource.clip.length, 4096);
                    }
                    else
                        UnlinkAnalyzer(WebALID);
                }
            };

#endif

            UpdateSettings();
            UpdateThemeColors();
            UpdateCustomStrings();
            if (audioSource == null)
            {
                Debug.LogWarning("[AudioLink] No audioSource provided. AudioLink will not do anything until an audio source has been assigned.");
            }

            gameObject.SetActive(true); // client disables extra cameras, so set it true
            transform.position = new Vector3(0f, 10000000f, 0f); // keep this in a far away place


            // Disable camera on start if user didn't ask for it
            if (!audioDataToggle)
            {
                DisableReadback();
            }
        }

        // TODO(3): try to port this to standalone
        // Only happens once per second.
        private void FPSUpdate()
        {
#if UDONSHARP
            if (!_hasInitializedTime)
            {
                if (_masterInstanceJoinTime > 0.00001)
                {
                    //We can now do our time setup.
                    double Now = GetElapsedSecondsSince2019();
                    _elapsedTime = Now - _masterInstanceJoinTime;
                    //Debug.Log($"[AudioLink] Time Sync Debug: Received instance time of {_masterInstanceJoinTime} and current time of {Now} delta of {_elapsedTime}");
                    _hasInitializedTime = true;
                    _fpsTime = _elapsedTime;
                }
                else if (_elapsedTime > 10 && Networking.IsMaster)
                {
                    //Have we gone more than 10 seconds and we're master?
                    //Debug.Log("[AudioLink] Time Sync Debug: You were master.  But no _masterInstanceJoinTime was provided for 10 seconds.  Resetting instance time.");
                    _masterInstanceJoinTime = GetElapsedSecondsSince2019();
                    RequestSerialization();
                    _hasInitializedTime = true;
                    _elapsedTime = 0;
                    _fpsTime = _elapsedTime;
                }
            }
#endif
            // The red channel should be 3.02f forever - this is the last version before the versioning change.
            audioMaterial.SetVector(_VersionNumberAndFPSProperty, new Vector4(3.02f, AudioLinkVersionNumberMajor, _fpsCount, AudioLinkVersionNumberMinor));
#if UDONSHARP
            audioMaterial.SetVector(_PlayerCountAndData, new Vector4(
                VRCPlayerApi.GetPlayerCount(),
                Networking.IsMaster ? 1.0f : 0.0f,
#if UNITY_EDITOR
                    0.0f,
#else
                    _localPlayer.isInstanceOwner ? 1.0f : 0.0f,
#endif
                0));

#else
            audioMaterial.SetVector(_PlayerCountAndData, new Vector4(
            0,
            0,
            0,
            0));
#endif
            _fpsCount = 0;
            _fpsTime++;

            // Other things to handle every second.

            // This handles wrapping of the ElapsedTime so we don't lose precision
            // onthe floating point.
            const double elapsedTimeMSWBoundary = 1024;
            if (_elapsedTime >= elapsedTimeMSWBoundary)
            {
                //For particularly long running instances, i.e. several days, the first
                //few frames will be spent federating _elapsedTime into _elapsedTimeMSW.
                //This is fine.  It just means over time, the
                _fpsTime = 0;
                _elapsedTime -= elapsedTimeMSWBoundary;
                _elapsedTimeMSW++;
            }

            // Finely adjust our network time estimate if needed.
#if UDONSHARP
            int networkTimeMSNow = Networking.GetServerTimeInMilliseconds();
#else
            int networkTimeMSNow = (int)(Time.time * 1000.0f);
#endif
            int networkTimeDelta = networkTimeMSNow - _networkTimeMS;
            if (networkTimeDelta > 3000)
            {
                //Major upset, reset.
                _networkTimeMS = networkTimeMSNow;
            }
            else if (networkTimeDelta < -3000)
            {
                //Major upset, reset.
                _networkTimeMS = networkTimeMSNow;
            }
            else
            {
                //Slowly correct the timebase.
                _networkTimeMS += networkTimeDelta / 20;
            }
            //Debug.Log( $"[AudioLink] Refinement: ${networkTimeDelta}" );
        }

        private void Update()
        {
            if (!_audioLinkEnabled)
            {
                return;
            }

            if (audioDataToggle)
            {
#if UDONSHARP
                VRCAsyncGPUReadback.Request(audioRenderTexture, 0, TextureFormat.RGBAFloat, (VRC.Udon.Common.Interfaces.IUdonEventReceiver)(Component)this);
#else
                AsyncGPUReadback.Request(audioRenderTexture, 0, TextureFormat.RGBAFloat, OnAsyncGpuReadbackComplete);
#endif
            }

            // Tested: There does not appear to be any drift updating it this way.
            _elapsedTime += Time.deltaTime;

            // Advance the current network time by a little.
            // this algorithm also takes into account sub-millisecond jitter.
            {
                double deltaTimeMS = Time.deltaTime * 1000.0;
                int advanceTimeMS = (int)(deltaTimeMS);
                _networkTimeMSAccumulatedError += deltaTimeMS - advanceTimeMS;
                if (_networkTimeMSAccumulatedError > 1)
                {
                    _networkTimeMSAccumulatedError--;
                    advanceTimeMS++;
                }
                _networkTimeMS += advanceTimeMS;
            }

            _fpsCount++;

            if (_elapsedTime >= _fpsTime)
            {
                FPSUpdate();
            }

            // use _AdvancedTimeProps0.w for Debugging
            audioMaterial.SetVector(_AdvancedTimeProps0, new Vector4(
                (float)_elapsedTime,
                (float)_elapsedTimeMSW,
                (float)DateTime.Now.TimeOfDay.TotalSeconds));

            // Jan 1, 1970 = 621355968000000000.0 ticks.
            double utcSecondsUnix = DateTime.UtcNow.Ticks / 10000000.0 - 62135596800.0;
            audioMaterial.SetVector(_AdvancedTimeProps1, new Vector4(
                (float)((_networkTimeMS) & 65535),
                (float)((_networkTimeMS) >> 16),
                (float)(Math.Floor(utcSecondsUnix / 86400)),
                (float)(utcSecondsUnix % 86400)
            ));

            // General Profiling Notes:
            //    Profiling done on 2021-05-26 on an Intel Intel Core i7-8750H CPU @ 2.20GHz
            //    Running loop 255 times (So divide all times by 255)
            //    Base load of system w/o for loop: ~420us in merlin profile land.
            //    With loop, with just summer: 1.2ms / 255
            //    Calling material.SetVeactor( ... new Vector4 ) in the loop:  2.7ms / 255
            //    Setting a float in the loop (to see if there's a difference): 1.9ms / 255
            //                             but setting 4 floats individually... is 3.0ms / 255
            //    The whole shebang with Networking.GetServerTimeInMilliseconds(); 2.3ms / 255
            //    Material.SetFloat with Networking.GetServerTimeInMilliseconds(); 2.3ms / 255
            //    Material.SetFloat with Networking.GetServerTimeInMilliseconds(), twice; 2.9ms / 255
            //    Casting and encoding as UInt32 as 2 floats, to prevent aliasing, twice: 5.1ms / 255
            //    Casting and encoding as UInt32 as 2 floats, to prevent aliasing, once: 3.2ms / 255

            if (audioSource != null)
            {
                SendAudioOutputData();

                // Used to correct for the volume of the audio source component

                audioMaterial.SetFloat(_SourceVolume, audioSource.volume);
                audioMaterial.SetFloat(_SourceSpatialBlend, audioSource.spatialBlend);
                audioMaterial.SetVector(_SourcePosition, audioSource.transform.position);


                if (autoSetMediaState)
                {
                    SetMediaVolume(audioSource.volume);

                    float time = 0f;
                    if (audioSource.clip != null)
                    {
                        time = audioSource.time / audioSource.clip.length;
                    }
                    SetMediaTime(time);

                    if (audioSource.isPlaying)
                    {
                        SetMediaPlaying(MediaPlaying.Playing);
                    }
                    else
                    {
                        SetMediaPlaying(MediaPlaying.Stopped);
                    }

                    if (audioSource.loop)
                    {
                        SetMediaLoop(MediaLoop.Loop);
                    }
                    else
                    {
                        SetMediaLoop(MediaLoop.None);
                    }
                }


#if UDONSHARP
                if (VRC.SDKBase.Utilities.IsValid(_localPlayer))
                {
                    float distanceToSource = Vector3.Distance(_localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position, audioSource.transform.position);
                    audioMaterial.SetFloat(_SourceDistance, distanceToSource);
                }
#endif
            }


            // As an optimization: when in-game, require others to call these after
            // setting values on this object.
            // Since we expect changes to values on this object in editor through the GUI,
            // we do not have explicit events to when things change.
#if UNITY_EDITOR
            UpdateSettings();
            UpdateThemeColors();
            UpdateCustomStrings();
#endif
        }

#if UDONSHARP
        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
        {
            if (request.hasError || !request.done) return;

            request.TryGetData(audioData);
        }
#else
        public void OnAsyncGpuReadbackComplete(AsyncGPUReadbackRequest request)
        {
            if (request.hasError || !request.done) return;

            NativeArray<Color> data = request.GetData<Color>();
            for (int i = 0; i < data.Length; i++)
            {
                audioData[i] = data[i];
            }
        }
#endif

        private void OnEnable()
        {
            EnableAudioLink();
        }

        private void OnDisable()
        {
            DisableAudioLink();
        }

        public void UpdateSettings()
        {
            InitIDs();
            audioMaterial.SetFloat(_Gain, gain);
            audioMaterial.SetFloat(_FadeLength, fadeLength);
            audioMaterial.SetFloat(_FadeExpFalloff, fadeExpFalloff);
            audioMaterial.SetFloat(_Bass, bass);
            audioMaterial.SetFloat(_Treble, treble);
            audioMaterial.SetFloat(_X0, x0);
            audioMaterial.SetFloat(_X1, x1);
            audioMaterial.SetFloat(_X2, x2);
            audioMaterial.SetFloat(_X3, x3);
            audioMaterial.SetFloat(_Threshold0, threshold0);
            audioMaterial.SetFloat(_Threshold1, threshold1);
            audioMaterial.SetFloat(_Threshold2, threshold2);
            audioMaterial.SetFloat(_Threshold3, threshold3);
            audioMaterial.SetFloat(_Autogain, autogain ? 1 : 0);
            audioMaterial.SetFloat(_AutogainDerate, autogainDerate);
        }

        // Note: These might be changed frequently so as an optimization, they're in a different function
        // rather than bundled in with the other things in UpdateSettings().
        public void UpdateThemeColors()
        {
            InitIDs();
            audioMaterial.SetInt(_ThemeColorMode, themeColorMode);
            audioMaterial.SetColor(_CustomThemeColor0, customThemeColor0);
            audioMaterial.SetColor(_CustomThemeColor1, customThemeColor1);
            audioMaterial.SetColor(_CustomThemeColor2, customThemeColor2);
            audioMaterial.SetColor(_CustomThemeColor3, customThemeColor3);
        }

        private static float IntToFloatBits24Bit(uint value)
        {
            uint frac = value & 0x007FFFFF;
            return (frac / 8388608F) * 1.1754944e-38F;
        }

#if UDONSHARP
        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (VRC.SDKBase.Utilities.IsValid(player) && player.isMaster)
            {
                _masterName = player.displayName;
                UpdateGlobalString(_StringMasterPlayer, player.displayName);
            }
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (VRC.SDKBase.Utilities.IsValid(player) && (player.isMaster || player.displayName == _masterName))
            {
                FindAndUpdateMasterName();
            }
        }

        private void FindAndUpdateMasterName()
        {
            VRCPlayerApi[] players = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
            VRCPlayerApi.GetPlayers(players);
            foreach (VRCPlayerApi player in players)
            {
                if (player != null)
                {
                    if (VRC.SDKBase.Utilities.IsValid(player) && player.isMaster)
                    {
                        _masterName = player.displayName;
                        UpdateGlobalString(_StringMasterPlayer, player.displayName);
                        break;
                    }
                }
            }
        }
#endif

        public void UpdateCustomStrings()
        {
#if UDONSHARP
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(_localPlayer, gameObject);
#endif

            UpdateGlobalString(_StringCustom1, customString1);
            UpdateGlobalString(_StringCustom2, customString2);

#if UDONSHARP
            RequestSerialization();
#endif
        }

#if UDONSHARP
        public override void OnDeserialization()
        {
            if (!Networking.IsOwner(gameObject))
            {
                UpdateGlobalString(_StringCustom1, customString1);
                UpdateGlobalString(_StringCustom2, customString2);
            }
        }
#endif

        private const int GlobalStringMaxLength = 32;
        private readonly int[] globalStringCodePoints = new int[GlobalStringMaxLength];
        private const int GlobalStringPackedVectorsLength = GlobalStringMaxLength / 4;
        private readonly Vector4[] globalStringPackedVectors = new Vector4[GlobalStringPackedVectorsLength];

        private void UpdateGlobalString(int nameID, string input)
        {
            InitIDs();
            int inputLength = input.Length;
            // Truncate the input if it exceeds the max length
            if (inputLength > GlobalStringMaxLength)
            {
                input = input.Substring(0, GlobalStringMaxLength);
            }
            // Get unicode codepoints, clearing previous values to prevent leftover data
            Array.Clear(globalStringCodePoints, 0, GlobalStringMaxLength);
            int codePointsLength = 0;

            for (int i = 0; i < inputLength; i++)
            {
                globalStringCodePoints[codePointsLength++] = char.ConvertToUtf32(input, i);
                if (char.IsHighSurrogate(input[i]))
                {
                    i += 1;
                }
            }

            // Pack them into vectors, clearing previous values in vecs array
            Array.Clear(globalStringPackedVectors, 0, GlobalStringPackedVectorsLength);
            int j = 0;
            for (int i = 0; i < GlobalStringPackedVectorsLength; i++)
            {
                if (j < codePointsLength) globalStringPackedVectors[i].x = IntToFloatBits24Bit((uint)globalStringCodePoints[j++]); else break;
                if (j < codePointsLength) globalStringPackedVectors[i].y = IntToFloatBits24Bit((uint)globalStringCodePoints[j++]); else break;
                if (j < codePointsLength) globalStringPackedVectors[i].z = IntToFloatBits24Bit((uint)globalStringCodePoints[j++]); else break;
                if (j < codePointsLength) globalStringPackedVectors[i].w = IntToFloatBits24Bit((uint)globalStringCodePoints[j++]); else break;
            }

            // Expose the vectors to shader without causing additional allocations
            audioMaterial.SetVectorArray(nameID, globalStringPackedVectors);
        }
        public void ToggleAudioLink()
        {
            SetAudioLinkState(!_audioLinkEnabled);
        }

        public void SetAudioLinkState(bool state)
        {
            if (state)
            {
                EnableAudioLink();
            }
            else
            {
                DisableAudioLink();
            }
        }

        public void EnableAudioLink()
        {
            InitIDs();
            _audioLinkEnabled = true;
            audioRenderTexture.updateMode = CustomRenderTextureUpdateMode.Realtime;
            SetGlobalTextureWrapper(_AudioTexture, audioRenderTexture, UnityEngine.Rendering.RenderTextureSubElement.Default);

#if UNITY_WEBGL && !UNITY_EDITOR
            SetupAnalyzerSpace();
            LinkAnalyzer(WebALID, audioSource.clip.length, 4096);
#endif
        }

        public void DisableAudioLink()
        {
            _audioLinkEnabled = false;
            if (audioRenderTexture != null) { audioRenderTexture.updateMode = CustomRenderTextureUpdateMode.OnDemand; }
            SetGlobalTextureWrapper(_AudioTexture, null, UnityEngine.Rendering.RenderTextureSubElement.Default);

#if UNITY_WEBGL && !UNITY_EDITOR
            UnlinkAnalyzer(WebALID);
#endif
        }

        public void SetGlobalTextureWrapper(int nameID, RenderTexture value, UnityEngine.Rendering.RenderTextureSubElement element)
        {
#if UDONSHARP
            SetGlobalTexture(nameID, value);
#else
            SetGlobalTexture(nameID, value, element);
#endif
        }

        public void EnableReadback()
        {
            audioDataToggle = true;
        }

        public void DisableReadback()
        {
            audioDataToggle = false;
        }

        public void SendAudioOutputData()
        {
            InitIDs();

#if UNITY_WEBGL && !UNITY_EDITOR

            if (audioSource.isPlaying)
            {
                FetchAnalyzerLeft(WebALID, audioLinkWebPeer.WaveformSamplesLeft, 4096);
                FetchAnalyzerRight(WebALID, audioLinkWebPeer.WaveformSamplesRight, 4096);
            }

            _audioFramesL = audioLinkWebPeer.WaveformSamplesLeft;
            _audioFramesR = audioLinkWebPeer.WaveformSamplesRight;

#else

            audioSource.GetOutputData(_audioFramesL, 0);                // left channel

#if UDONSHARP
            bool hasDualMono = VRC.SDKBase.Utilities.IsValid(optionalRightAudioSource);
#else
            bool hasDualMono = optionalRightAudioSource != null;
#endif

            if (_rightChannelTestCounter > 0)
            {
                if (_ignoreRightChannel)
                {
                    Array.Copy(_audioFramesL, 0, _audioFramesR, 0, 4092);
                }
                else
                {
                    if (hasDualMono)
                    {
                        optionalRightAudioSource.GetOutputData(_audioFramesR, 0);
                    } else audioSource.GetOutputData(_audioFramesR, 1);
                }
                _rightChannelTestCounter--;
            }
            else
            {
                _rightChannelTestCounter = _rightChannelTestDelay;                  // reset test countdown
                _audioFramesR[0] = 0f;                                              // reset tested array element to zero just in case
                if (hasDualMono)                                                    // check if dual mono is present
                {
                    optionalRightAudioSource.GetOutputData(_audioFramesR, 0);       // right channel test
                } else audioSource.GetOutputData(_audioFramesR, 1);                 // right channel test
                _ignoreRightChannel = (_audioFramesR[0] == 0f) ? true : false;
            }

#endif

            Array.Copy(_audioFramesL, 0, _samples, 0, 1023); // 4092 - 1023 * 4
            audioMaterial.SetFloatArray(_Samples0L, _samples);
            Array.Copy(_audioFramesL, 1023, _samples, 0, 1023); // 4092 - 1023 * 3
            audioMaterial.SetFloatArray(_Samples1L, _samples);
            Array.Copy(_audioFramesL, 2046, _samples, 0, 1023); // 4092 - 1023 * 2
            audioMaterial.SetFloatArray(_Samples2L, _samples);
            Array.Copy(_audioFramesL, 3069, _samples, 0, 1023); // 4092 - 1023 * 1
            audioMaterial.SetFloatArray(_Samples3L, _samples);

            Array.Copy(_audioFramesR, 0, _samples, 0, 1023); // 4092 - 1023 * 4
            audioMaterial.SetFloatArray(_Samples0R, _samples);
            Array.Copy(_audioFramesR, 1023, _samples, 0, 1023); // 4092 - 1023 * 3
            audioMaterial.SetFloatArray(_Samples1R, _samples);
            Array.Copy(_audioFramesR, 2046, _samples, 0, 1023); // 4092 - 1023 * 2
            audioMaterial.SetFloatArray(_Samples2R, _samples);
            Array.Copy(_audioFramesR, 3069, _samples, 0, 1023); // 4092 - 1023 * 1
            audioMaterial.SetFloatArray(_Samples3R, _samples);
        }

        private float Remap(float t, float a, float b, float u, float v)
        {
            return ((t - a) / (b - a)) * (v - u) + u;
        }
    }
}
