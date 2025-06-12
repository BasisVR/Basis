#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

// This component uses code from the following sources:
// UnityYoutubePlayer, courtesy iBicha (SPDX-License-Identifier: Unlicense) https://github.com/iBicha/UnityYoutubePlayer
// USharpVideo, Copyright (c) 2020 Merlin, (SPDX-License-Identifier: MIT) https://github.com/MerlinVR/USharpVideo/

// TODO(float3): add this to the AudioLinkMiniPlayer

namespace AudioLink
{

    public class ytdlpPlayer : MonoBehaviour
    {
        public enum TextureTransformMode
        {
            AsIs,
            Normalized,
            ByPixels
        }

        public string ytdlpURL = "https://www.youtube.com/watch?v=vGXyAKy-X6s";
        ytdlpRequest _currentRequest = null;

        public bool showVideoPreviewInComponent = false;
        public VideoPlayer videoPlayer = null;
        public Resolution resolution = Resolution._720p;

        public bool enableGlobalVideoTexture = false;
        public string globalTextureName = "_Udon_VideoTex";
        public TextureTransformMode textureTransformMode = TextureTransformMode.Normalized;
        public Vector2Int texturePixelOrigin = new Vector2Int(0, 0);
        public Vector2Int texturePixelSize = new Vector2Int(0, 0);
        public Vector2 textureTiling = new Vector2(1f, 1f);
        public Vector2 textureOffset = new Vector2(0f, 0f);
        public bool forceStandbyTexture = false;
        public bool showStandbyIfPaused = true;
        public Texture2D standbyTexture;

        private int _globalTextureId;
        private int _globalTextureTransformId;
        private bool _globalTextureActive = false;
        internal Vector4 _lastGlobalST = Vector4.zero;

        public enum Resolution
        {
            [InspectorName("360p")] _360p = 360,
            [InspectorName("480p")] _480p = 480,
            [InspectorName("720p")] _720p = 720,
            [InspectorName("1080p")] _1080p = 1080,
            [InspectorName("1440p")] _1440p = 1440,
            [InspectorName("2160p")] _2160p = 2160,
        }

        void OnEnable()
        {
            _globalTextureId = Shader.PropertyToID(globalTextureName);
            _globalTextureTransformId = Shader.PropertyToID(globalTextureName + "_ST");
            RequestPlay();
        }

        public void RequestPlay()
        {
            _currentRequest = ytdlpURLResolver.Resolve(ytdlpURL, (int)resolution);
        }

        private void Update()
        {
            if (_currentRequest != null && _currentRequest.isDone)
            {
                UpdateUrl(_currentRequest.resolvedURL);
                _currentRequest = null;
            }
        }

        private void LateUpdate() => ExportGlobalVideoTexture();

        public void UpdateUrl(string resolved)
        {
            if (videoPlayer == null)
                return;

            videoPlayer.prepareCompleted -= MediaReady;
            videoPlayer.prepareCompleted += MediaReady;
            videoPlayer.url = resolved;
            videoPlayer.Prepare();
        }

        private void MediaReady(VideoPlayer player)
        {
            SetPlaybackTime(player, 0.0f);
            if (player.length > 0) player.Play();
        }

        public float GetPlaybackTime()
        {
            if (videoPlayer != null && videoPlayer.length > 0)
                return (float)(videoPlayer.length > 0 ? videoPlayer.time / videoPlayer.length : 0);
            else
                return 0;
        }

        public void SetPlaybackTime(VideoPlayer player, float time)
        {
            if (player != null && player.length > 0 && player.canSetTime)
                player.time = player.length * Mathf.Clamp(time, 0.0f, 1.0f);
        }

        public string FormattedTimestamp(double seconds, double maxSeconds = 0)
        {
            double formatValue = maxSeconds > 0 ? maxSeconds : seconds;
            string formatString = formatValue >= 3600.0 ? @"hh\:mm\:ss" : @"mm\:ss";
            return TimeSpan.FromSeconds(seconds).ToString(formatString);
        }

        public string PlaybackTimestampFormatted()
        {
            if (videoPlayer != null && videoPlayer.length > 0)
            {
                return $"{FormattedTimestamp(videoPlayer.time, videoPlayer.length)} / {FormattedTimestamp(videoPlayer.length)}";
            }
            else
            {
                return "00:00 / 00:00";
            }
        }

        public bool GetAudioSourceVolume(out float volume)
        {
            volume = 0;

            if (videoPlayer != null)
            {
                AudioSource audioSourceOutput = videoPlayer.GetTargetAudioSource(0);
                if (audioSourceOutput != null)
                {
                    volume = audioSourceOutput.volume;
                    return true;
                }
            }

            return false;
        }

        public void SetAudioSourceVolume(float volume)
        {
            if (videoPlayer != null)
            {
                AudioSource audioSourceOutput = videoPlayer.GetTargetAudioSource(0);
                if (audioSourceOutput != null)
                    audioSourceOutput.volume = Mathf.Clamp01(volume);
            }
        }

        private void ExportGlobalVideoTexture()
        {
            Texture texture = null;
            bool showStandby = forceStandbyTexture;
            if (!showStandby && videoPlayer != null)
            {
                // enable pausing or stopping the video to make the standby texture show
                showStandby |= (!videoPlayer.isPaused || showStandbyIfPaused) && !videoPlayer.isPlaying;
                if (!showStandby) texture = videoPlayer.targetTexture != null ? videoPlayer.targetTexture : videoPlayer.texture;
            }

            showStandby |= texture == null;
            if (showStandby && standbyTexture != null)
                texture = standbyTexture;

            Vector4 st = new Vector4(1, 1, 0, 0);
            if (enableGlobalVideoTexture)
            {
                if (texture != null && !showStandby)
                {
                    switch (textureTransformMode)
                    {
                        case TextureTransformMode.Normalized:
                            st.x = textureTiling.x;
                            st.y = textureTiling.y;
                            st.z = textureOffset.x;
                            st.w = textureOffset.y;
                            break;
                        case TextureTransformMode.ByPixels:
                            // calculate offset/tiling from source texture pixel size.
                            float sourceWidth = texture.width;
                            float sourceHeight = texture.height;
                            float targetWidth = texturePixelSize.x;
                            float targetHeight = texturePixelSize.y;
                            float targetX = texturePixelOrigin.x;
                            float targetY = texturePixelOrigin.y;
                            if (targetWidth == 0) targetWidth = sourceWidth;
                            if (targetHeight == 0) targetHeight = sourceHeight;
                            st.x = targetWidth / sourceWidth;
                            st.y = targetHeight / sourceHeight;
                            st.z = targetX / sourceWidth;
                            st.w = (sourceHeight - targetHeight - targetY) / sourceHeight;
                            break;
                    }
                }

                Shader.SetGlobalVector(_globalTextureTransformId, st);
                Shader.SetGlobalTexture(_globalTextureId, texture);
                _lastGlobalST = st;
                _globalTextureActive = true;
            }
            // if globals ever get disabled, unset the custom texture global flag as well
            else if (_globalTextureActive)
            {
                _globalTextureActive = false;
                _lastGlobalST = st;
                Shader.SetGlobalVector(_globalTextureTransformId, st);
                Shader.SetGlobalTexture(_globalTextureId, null);
            }
        }
    }

    public class ytdlpRequest
    {
        public bool isDone;
        public string resolvedURL;
    }

    public static class ytdlpURLResolver
    {
        private static string _localytdlpPath = Application.dataPath + "\\AudioLink\\yt-dlp.exe";

        private static string _ytdlpPath = "";
        private static bool _ytdlpFound = false;

        private const string userDefinedYTDLPathKey = "YTDL-PATH-CUSTOM";
        private const string userDefinedYTDLPathMenu = "Tools/AudioLink/Select Custom YTDL Location";

        // don't enable custom YTDL location menu item if project is VRCWorld type. It's only used for avatar testing, so it's useless otherwise.
#if !UDONSHARP
        [MenuItem(userDefinedYTDLPathMenu, priority = 1)]
        private static void SelectYtdlInstall()
        {
            if (Menu.GetChecked(userDefinedYTDLPathMenu))
            {
                EditorPrefs.SetString(userDefinedYTDLPathKey, string.Empty);
                return;
            }

            string ytdlPath = EditorPrefs.GetString(userDefinedYTDLPathKey, "");
            string tpath = ytdlPath.Substring(0, ytdlPath.LastIndexOf("/", StringComparison.Ordinal) + 1);
            string path = EditorUtility.OpenFilePanel("Select YTDL Location", tpath, "");
            EditorPrefs.SetString(userDefinedYTDLPathKey, path ?? string.Empty);
        }

        [MenuItem(userDefinedYTDLPathMenu, true, priority = 1)]
        private static bool ValidateSelectYrdlInstall()
        {
            Menu.SetChecked(userDefinedYTDLPathMenu, EditorPrefs.GetString(userDefinedYTDLPathKey, string.Empty) != string.Empty);
            return true;
        }
#endif

        public static bool IsytdlpAvailable()
        {
            if (_ytdlpFound)
                return true;

            Locateytdlp();
            return _ytdlpFound;
        }

        public static void Locateytdlp()
        {
            _ytdlpFound = false;

            // check for a custom install location
            string customPath = EditorPrefs.GetString(userDefinedYTDLPathKey, string.Empty);
            if (!string.IsNullOrEmpty(customPath))
            {
                if (File.Exists(customPath))
                {
                    Debug.Log($"[AudioLink:ytdlp] Custom YTDL location found: {customPath}");
                    _ytdlpPath = customPath;
                    _ytdlpFound = true;
                    return;
                }

                Debug.LogWarning($"[AudioLink:ytdlp] Custom YTDL location detected but does not exist: {customPath}");
                Debug.Log("[AudioLink:ytdlp] Checking other locations...");
            }


#if UNITY_EDITOR_WIN
            string[] splitPath = Application.persistentDataPath.Split('/', '\\');
            _ytdlpPath = string.Join("\\", splitPath.Take(splitPath.Length - 2)) + "\\VRChat\\VRChat\\Tools\\yt-dlp.exe";
            if (!File.Exists(_ytdlpPath))
                _ytdlpPath = _localytdlpPath;
#else
            _ytdlpPath = "/usr/bin/yt-dlp";
#endif

            if (!File.Exists(_ytdlpPath))
            {
                string[] possibleExecutableNames = { "yt-dlp", "ytdlp", "youtube-dlp", "youtubedlp", "yt-dl", "ytdl", "youtube-dl", "youtubedl" };
                _ytdlpPath = LocateExecutable(possibleExecutableNames);
            }

            if (!File.Exists(_ytdlpPath))
                return;

            _ytdlpFound = true;
            Debug.Log($"[AudioLink:ytdlp] Found yt-dlp at path '{_ytdlpPath}'");
        }

        public static ytdlpRequest Resolve(string url, int resolution = 720)
        {
            if (!_ytdlpFound)
            {
                Locateytdlp();
            }

            if (!_ytdlpFound)
            {
                Debug.LogWarning($"[AudioLink:ytdlp] Unable to resolve URL '{url}' : yt-dlp not found");
            }

            System.Diagnostics.Process proc = new System.Diagnostics.Process();
            ytdlpRequest request = new ytdlpRequest();

            proc.EnableRaisingEvents = false;

            proc.StartInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.FileName = _ytdlpPath;
            proc.StartInfo.Arguments = $"--no-check-certificate --no-cache-dir --rm-cache-dir -f \"mp4[height<=?{resolution}][protocol^=http]/best[height<=?{resolution}][protocol^=http]\" --get-url \"{url}\"";

            proc.Exited += (sender, args) => { proc.Dispose(); };

            proc.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    request.resolvedURL = args.Data;
                    request.isDone = true;
                }
            };

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();

                return request;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AudioLink:ytdlp] Unable to resolve URL '{url}' : " + e.Message);
                return null;
            }
        }

        private static string LocateExecutable(params string[] names)
        {
            string exists = names.FirstOrDefault(File.Exists);
            // check for any names being a valid exact path
            if (!string.IsNullOrEmpty(exists)) return Path.GetFullPath(exists);
            // search in path
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
#if UNITY_EDITOR_OSX
            // M-series Macs use a different location for Homebrew packages to prevent conflicts with x86_64 binaries.
            // As a result, ARM packages are found in the "/opt/homebrew/bin" location, which is not normally in PATH.
            path += Path.PathSeparator + "/opt/homebrew/bin/";
#endif
            string[] paths = path.Split(Path.PathSeparator);
            // check each possible executable name
            foreach (string n in names)
            {
                string name = n;
#if UNITY_EDITOR_WIN
                // append the windows file extension
                name += ".exe";
#endif
                // return the full name if it has a directory prefix and is part of a valid and existing full path
                if (Path.GetDirectoryName(name) != string.Empty)
                {
                    string full = Path.GetFullPath(name);
                    if (File.Exists(full)) return full;
                }

                // otherwise go through each possible PATH location to check for a valid executable.
                foreach (string dir in paths)
                {
                    string trimmed = dir.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    string combined = Path.Combine(trimmed, name);
                    if (File.Exists(combined)) return Path.GetFullPath(combined);
                }
            }

            // no executable was found...
            return string.Empty;
        }
    }

    [CustomEditor(typeof(ytdlpPlayer))]
    public class ytdlpPlayerEditor : UnityEditor.Editor
    {
        private ytdlpPlayer _ytdlpPlayer;

        private SerializedProperty ytdlpURL;
        private SerializedProperty resolution;

        private SerializedProperty enableGlobalVideoTexture;
        private SerializedProperty globalTextureName;
        private SerializedProperty transformTextureMode;
        private SerializedProperty texturePixelOrigin;
        private SerializedProperty texturePixelSize;
        private SerializedProperty textureTiling;
        private SerializedProperty textureOffset;
        private SerializedProperty showStandbyIfPaused;
        private SerializedProperty forceStandbyTexture;
        private SerializedProperty standbyTexture;

        void OnEnable()
        {
            _ytdlpPlayer = (ytdlpPlayer)target;
            // If video player is on the same gameobject, assign it automatically
            if (_ytdlpPlayer.gameObject.GetComponent<VideoPlayer>() != null)
                _ytdlpPlayer.videoPlayer = _ytdlpPlayer.gameObject.GetComponent<VideoPlayer>();

            ytdlpURL = serializedObject.FindProperty(nameof(_ytdlpPlayer.ytdlpURL));
            resolution = serializedObject.FindProperty(nameof(_ytdlpPlayer.resolution));
            enableGlobalVideoTexture = serializedObject.FindProperty(nameof(_ytdlpPlayer.enableGlobalVideoTexture));
            globalTextureName = serializedObject.FindProperty(nameof(_ytdlpPlayer.globalTextureName));
            transformTextureMode = serializedObject.FindProperty(nameof(_ytdlpPlayer.textureTransformMode));
            texturePixelOrigin = serializedObject.FindProperty(nameof(_ytdlpPlayer.texturePixelOrigin));
            texturePixelSize = serializedObject.FindProperty(nameof(_ytdlpPlayer.texturePixelSize));
            textureTiling = serializedObject.FindProperty(nameof(_ytdlpPlayer.textureTiling));
            textureOffset = serializedObject.FindProperty(nameof(_ytdlpPlayer.textureOffset));
            showStandbyIfPaused = serializedObject.FindProperty(nameof(_ytdlpPlayer.showStandbyIfPaused));
            forceStandbyTexture = serializedObject.FindProperty(nameof(_ytdlpPlayer.forceStandbyTexture));
            standbyTexture = serializedObject.FindProperty(nameof(_ytdlpPlayer.standbyTexture));
        }

        public override bool RequiresConstantRepaint()
        {
            if (_ytdlpPlayer.videoPlayer != null)
                return _ytdlpPlayer.videoPlayer.isPlaying;
            else
                return false;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
#if UNITY_EDITOR_LINUX
            bool available = false;
#else
            bool available = ytdlpURLResolver.IsytdlpAvailable();
#endif
            bool hasVideoPlayer = _ytdlpPlayer.videoPlayer != null;
            float playbackTime = hasVideoPlayer ? _ytdlpPlayer.GetPlaybackTime() : 0;
            double videoLength = hasVideoPlayer ? _ytdlpPlayer.videoPlayer.length : 0;

            using (new EditorGUI.DisabledScope(!available))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(" Video URL", EditorGUIUtility.IconContent("CloudConnect").image), GUILayout.Width(100));
                    EditorGUILayout.PropertyField(ytdlpURL, GUIContent.none);
                    EditorGUILayout.PropertyField(resolution, GUIContent.none, GUILayout.Width(65));
                }

                using (new EditorGUI.DisabledScope(!hasVideoPlayer || !EditorApplication.isPlaying))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        // Timestamp/Reload button
                        EditorGUILayout.LabelField(new GUIContent(" Seek: " + _ytdlpPlayer.PlaybackTimestampFormatted(), EditorGUIUtility.IconContent("d_Slider Icon").image));

                        GUIContent reloadButtonContent = new GUIContent(" Reload URL", EditorGUIUtility.IconContent("TreeEditor.Refresh").image);

                        bool updateURL = GUILayout.Button(reloadButtonContent);
                        if (updateURL)
                            _ytdlpPlayer.RequestPlay();
                    }

                    // Seekbar/Time input
                    using (new EditorGUI.DisabledScope(!hasVideoPlayer || videoLength == 0))
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        // Seekbar input
                        EditorGUI.BeginChangeCheck();
                        playbackTime = GUILayout.HorizontalSlider(playbackTime, 0, 1);
                        if (EditorGUI.EndChangeCheck())
                            _ytdlpPlayer.SetPlaybackTime(_ytdlpPlayer.videoPlayer, playbackTime);

                        // Timestamp input
                        EditorGUI.BeginChangeCheck();
                        double time = hasVideoPlayer ? _ytdlpPlayer.videoPlayer.time : 0;
                        string currentTimestamp = _ytdlpPlayer.FormattedTimestamp(time, videoLength);
                        string seekTimestamp = EditorGUILayout.DelayedTextField(currentTimestamp, GUILayout.MaxWidth(8 * currentTimestamp.Length));
                        if (EditorGUI.EndChangeCheck() && videoLength > 0)
                        {
                            TimeSpan inputTimestamp;
                            // Add extra 00:'s to force TimeSpan.TryParse to interpret times properly
                            // 22 -> 00:00:22, 2:22 -> 00:02:22, 2:22:22 -> 00:2:22:22
                            if (seekTimestamp.Length < 5)
                                seekTimestamp = "00:" + seekTimestamp;
                            if (TimeSpan.TryParse($"00:{seekTimestamp}", out inputTimestamp))
                            {
                                playbackTime = (float)(inputTimestamp.TotalSeconds / videoLength);
                                _ytdlpPlayer.SetPlaybackTime(_ytdlpPlayer.videoPlayer, playbackTime);
                            }
                        }
                    }

                    // Media Controls
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool isPlaying = hasVideoPlayer && _ytdlpPlayer.videoPlayer.isPlaying;
                        bool isPaused = hasVideoPlayer && _ytdlpPlayer.videoPlayer.isPaused;
                        bool isStopped = !isPlaying && !isPaused;

                        bool play = GUILayout.Toggle(isPlaying, new GUIContent(" Play", EditorGUIUtility.IconContent("d_PlayButton On").image), "Button") != isPlaying;
                        bool pause = GUILayout.Toggle(isPaused, new GUIContent(" Pause", EditorGUIUtility.IconContent("d_PauseButton On").image), "Button") != isPaused;
                        bool stop = GUILayout.Toggle(isStopped, new GUIContent(" Stop", EditorGUIUtility.IconContent("d_Record Off").image), "Button") != isStopped;

                        if (hasVideoPlayer)
                        {
                            if (play)
                                _ytdlpPlayer.videoPlayer.Play();
                            else if (pause)
                                _ytdlpPlayer.videoPlayer.Pause();
                            else if (stop)
                                _ytdlpPlayer.videoPlayer.Stop();
                        }
                    }
                }

                float volume;
                using (new EditorGUI.DisabledScope(!_ytdlpPlayer.GetAudioSourceVolume(out volume)))
                {
                    EditorGUI.BeginChangeCheck();
                    volume = EditorGUILayout.Slider(new GUIContent("  AudioSource Volume", EditorGUIUtility.IconContent("d_Profiler.Audio").image), volume, 0.0f, 1.0f);
                    if (EditorGUI.EndChangeCheck())
                        _ytdlpPlayer.SetAudioSourceVolume(volume);
                }
            }

            bool videoPlayerOnThisObject = _ytdlpPlayer.gameObject.GetComponent<VideoPlayer>() != null;

            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying || videoPlayerOnThisObject))
            {
                _ytdlpPlayer.videoPlayer = (VideoPlayer)EditorGUILayout.ObjectField(new GUIContent("  VideoPlayer", EditorGUIUtility.IconContent("d_Profiler.Video").image), _ytdlpPlayer.videoPlayer, typeof(VideoPlayer), allowSceneObjects: true);
            }

            // Video preview
            using (new EditorGUI.DisabledScope(!available || !hasVideoPlayer))
            {
                _ytdlpPlayer.showVideoPreviewInComponent = EditorGUILayout.Toggle(new GUIContent("  Show Video Preview", EditorGUIUtility.IconContent("d_ViewToolOrbit On").image), _ytdlpPlayer.showVideoPreviewInComponent);

                if (_ytdlpPlayer.showVideoPreviewInComponent && available && hasVideoPlayer && _ytdlpPlayer.videoPlayer.texture != null)
                {
                    // Draw video preview with the same aspect ratio as the video
                    Texture videoPlayerTexture = _ytdlpPlayer.videoPlayer.texture;
                    EditorGUILayout.LabelField($"Resolution: {videoPlayerTexture.width}x{videoPlayerTexture.height}");
                    float aspectRatio = (float)videoPlayerTexture.width / videoPlayerTexture.height;
                    Rect previewRect = GUILayoutUtility.GetAspectRect(aspectRatio);
                    EditorGUI.DrawPreviewTexture(previewRect, videoPlayerTexture, null, ScaleMode.ScaleToFit);
                }

                EditorGUILayout.PropertyField(enableGlobalVideoTexture, new GUIContent("Enable Global Video Texture"));
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    if (_ytdlpPlayer.enableGlobalVideoTexture)
                    {
                        EditorGUILayout.LabelField("Global Video Texture Settings");

                        EditorGUILayout.HelpBox("Global Video Texture is NOT part of AudioLink and is only provided as a convenience for testing avatars in editor.", MessageType.Info);

                        using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                            EditorGUILayout.PropertyField(globalTextureName, new GUIContent("Global Texture Property Target"));
                        EditorGUILayout.PropertyField(transformTextureMode, new GUIContent("Transform Texture (" + _ytdlpPlayer.globalTextureName + "_ST)"));
                        EditorGUI.indentLevel++;
                        switch (_ytdlpPlayer.textureTransformMode)
                        {
                            case ytdlpPlayer.TextureTransformMode.Normalized:
                                EditorGUILayout.PropertyField(textureTiling, new GUIContent("Tiling"));
                                EditorGUILayout.PropertyField(textureOffset, new GUIContent("Offset"));
                                break;
                            case ytdlpPlayer.TextureTransformMode.ByPixels:
                                EditorGUILayout.PropertyField(texturePixelOrigin, new GUIContent("Pixel Origin (from Top-Left)"));
                                EditorGUILayout.PropertyField(texturePixelSize, new GUIContent("Pixel Size (0 = Texture Source Size)"));
                                if (EditorApplication.isPlaying)
                                    using (new EditorGUI.DisabledScope(true))
                                        EditorGUILayout.LabelField($"Normalized: {_ytdlpPlayer._lastGlobalST}");
                                break;
                        }

                        EditorGUI.indentLevel--;
                        EditorGUILayout.PropertyField(showStandbyIfPaused, new GUIContent("Show Standby Texture when Paused"));
                        EditorGUILayout.PropertyField(forceStandbyTexture, new GUIContent("Force Show Standby Texture"));
                        EditorGUILayout.PropertyField(standbyTexture, new GUIContent("Standby Texture"), GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    }
                }
            }

            if (!available)
            {
#if UNITY_EDITOR_LINUX
                EditorGUILayout.HelpBox("The yt-dlp Player is currently not supported on Linux, as Unity on Linux cannot play the required file formats.", MessageType.Warning);
#elif UNITY_EDITOR_WIN
                EditorGUILayout.HelpBox("Failed to locate yt-dlp executable. To fix this, either install and launch VRChat once, or install yt-dlp and make sure the executable is on your PATH. Once this is done, enter play mode to retry.", MessageType.Warning);
#else
                EditorGUILayout.HelpBox("Failed to locate yt-dlp executable. To fix this, install yt-dlp and make sure the executable is on your PATH. Once this is done, enter play mode to retry.", MessageType.Warning);
#endif
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
