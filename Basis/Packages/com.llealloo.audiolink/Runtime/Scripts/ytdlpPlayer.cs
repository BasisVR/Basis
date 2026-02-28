#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.IO;
using System.Linq;
using System.Threading;
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
        ResolvingRequest _currentRequest = null;

        public bool showVideoPreviewInComponent = false;
        public VideoPlayer videoPlayer = null;
        public AudioLink audioLink = null;
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
            if (audioLink == null)
            {
                audioLink = GetComponentInParent<AudioLink>();
                if (audioLink == null)
                {
                    audioLink = FindFirstObjectByType<AudioLink>();
                }
            }
            _globalTextureId = Shader.PropertyToID(globalTextureName);
            _globalTextureTransformId = Shader.PropertyToID(globalTextureName + "_ST");
            RequestPlay();
        }

        void OnDisable()
        {
            if (audioLink != null)
            {
                audioLink.autoSetMediaState = true;
            }
        }

        public void RequestPlay()
        {
            ytdlpURLResolver.FetchEditorPrefs();

            ytdlpURLResolver.TryResolve((bool dumpJson) => {
                ytdlpURLResolver.Resolve(ytdlpURL, (ResolvingRequest newRequest) => _currentRequest = newRequest, (int)resolution, dumpJson);
            });
        }

        private void Update()
        {
            if (_currentRequest != null && _currentRequest.isDone)
            {
                UpdateUrl(_currentRequest.resolvedURL);
                _currentRequest = null;
            }
            if (audioLink != null && videoPlayer != null)
            {
                audioLink.autoSetMediaState = false;
                if (_currentRequest != null && !_currentRequest.isDone)
                    audioLink.SetMediaPlaying(MediaPlaying.Loading);
                else if (videoPlayer.isPaused)
                    audioLink.SetMediaPlaying(MediaPlaying.Paused);
                else if (videoPlayer.isPlaying)
                    audioLink.SetMediaPlaying(MediaPlaying.Playing);
                else
                    audioLink.SetMediaPlaying(MediaPlaying.Stopped);
                audioLink.SetMediaTime(GetPlaybackTime());
                audioLink.SetMediaLoop(videoPlayer.isLooping ? MediaLoop.LoopOne : MediaLoop.None);
                if (GetAudioSourceVolume(out float volume)) audioLink.SetMediaVolume(volume);
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

    public class CachedEditorPrefs
    {
        public string ytdlpPath;
        public string ffmpegPath;
        public bool useFFmpeg;
    }

    public class ResolvingRequest
    {
        public bool isDone;
        public string resolvedURL;
    }

    public class VideoMeta
    {
        public string id;
        public Double duration;

        public VideoMeta(string _id = "", double _duration = 0)
        {
            id = _id; duration = _duration;
        }
    }

    public class VideoFormat
    {
        public string VideoCodec { get; }
        public string AudioCodec { get; }
        public string Container { get; }

        public VideoFormat(string videoCodec = "vp8", string audioCodec = "libvorbis", string container = "webm")
        {
            VideoCodec = videoCodec; AudioCodec = audioCodec; Container = container;
        }
    }

    public static class ytdlpURLResolver
    {
        private static int _mainThreadId;

        [InitializeOnLoadMethod]
        private static void CaptureMainThreadId_Editor()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        private static bool IsMainThread()
        {
            // If somehow not initialized yet, assume current is main (editor init order can be weird)
            if (_mainThreadId == 0) _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            return Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }
        
        private static string _localytdlpPath = Application.dataPath + "\\AudioLink\\yt-dlp.exe";

        private static CachedEditorPrefs _cachedEditorPrefs = new CachedEditorPrefs();

        private static string _ytdlpPath = "";
        private static bool _ytdlpFound = false;
        private static VideoMeta _ytdlpJson;

        public static bool useFFmpeg = false;
        private static System.Diagnostics.Process _ffmpegProc;
        private static string _ffmpegError;
        private static string _ffmpegPath = "";
        private static bool _ffmpegFound = false;
        private static string _ffmpegCache = "Video Cache";

        private const string userDefinedYTDLPathKey = "YTDL-PATH-CUSTOM";
        private const string userDefinedYTDLPathMenu = "Tools/AudioLink/Select Custom YTDL Location";

        private const string userDefinedFFmpegPathKey = "MPEG-PATH-CUSTOM";
        private const string userDefinedFFmpegPathMenu = "Tools/AudioLink/Select Custom FFmpeg Location";

        private const string useFFmpegTranscodeKey = "USE-FFMPEG-TRANSCODE";

        private const string _ffErrorIdentifier = ", from 'http";

#if UNITY_EDITOR_WIN
        private static VideoFormat platformVideoFormat = new VideoFormat(videoCodec: "h264", audioCodec: "aac", container: "mp4");
#elif UNITY_EDITOR_LINUX
        private static VideoFormat platformVideoFormat = new VideoFormat(videoCodec: "vp8", audioCodec: "libvorbis", container: "webm");
#elif UNITY_EDITOR_OSX
        private static VideoFormat platformVideoFormat = new VideoFormat(videoCodec: "vp8", audioCodec: "libvorbis", container: "webm");
#endif

        private static void SelectToolInstall(string title, string pathMenu, string pathKey)
        {
            if (Menu.GetChecked(pathMenu))
            {
                EditorPrefs.SetString(pathKey, string.Empty);
                return;
            }

            string currentPath = EditorPrefs.GetString(pathKey, "");
            string dirPath = currentPath.Substring(0, currentPath.LastIndexOf("/", StringComparison.Ordinal) + 1);
            string path = EditorUtility.OpenFilePanel(title, dirPath, "");
            EditorPrefs.SetString(pathKey, path ?? string.Empty);
        }

        [MenuItem(userDefinedYTDLPathMenu, priority = 1)]
        private static void SelectYtdlInstall()
        {
            SelectToolInstall("Select YTDL Location", userDefinedYTDLPathMenu, userDefinedYTDLPathKey);
        }

        [MenuItem(userDefinedFFmpegPathMenu, priority = 1)]
        private static void SelectFFmpegInstall()
        {
            SelectToolInstall("Select FFmpeg Location", userDefinedFFmpegPathMenu, userDefinedFFmpegPathKey);
        }

        [MenuItem(userDefinedYTDLPathMenu, true, priority = 1)]
        private static bool ValidateSelectYtdlInstall()
        {
            Menu.SetChecked(userDefinedYTDLPathMenu, EditorPrefs.GetString(userDefinedYTDLPathKey, string.Empty) != string.Empty);
            return true;
        }

        [MenuItem(userDefinedFFmpegPathMenu, true, priority = 1)]
        private static bool ValidateSelectFFmpegInstall()
        {
            Menu.SetChecked(userDefinedFFmpegPathMenu, EditorPrefs.GetString(userDefinedFFmpegPathKey, string.Empty) != string.Empty);
            return true;
        }

        public static bool IsytdlpAvailable()
        {
            if (_ytdlpFound)
                return true;

            Locateytdlp();
            return _ytdlpFound;
        }

        public static bool IsFFmpegAvailable()
        {
            if (_ffmpegFound)
                return true;

            LocateFFmpeg();
            return _ffmpegFound;
        }

        public static void FetchEditorPrefs()
        {
            bool platformDefaultUseFFmpegTranscode = false;
            #if UNITY_EDITOR_LINUX
            platformDefaultUseFFmpegTranscode = true;
            #endif

            if (!IsMainThread()) return; // Do not throw; just keep the existing cached values.
            
            try
            {
                _cachedEditorPrefs.ffmpegPath = EditorPrefs.GetString(userDefinedFFmpegPathKey, string.Empty);
                _cachedEditorPrefs.ytdlpPath = EditorPrefs.GetString(userDefinedYTDLPathKey, string.Empty);
                _cachedEditorPrefs.useFFmpeg = EditorPrefs.GetBool(useFFmpegTranscodeKey, platformDefaultUseFFmpegTranscode);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        public static void Locateytdlp()
        {
            _ytdlpFound = false;

            // CRITICAL: Ensure cached prefs are up-to-date. Otherwise, custom path may be ignored after reload.
            FetchEditorPrefs();

            // check for a custom install location
            string customPath = _cachedEditorPrefs.ytdlpPath;
            if (!string.IsNullOrEmpty(customPath))
            {
                if (File.Exists(customPath))
                {
                    Debug.Log($"[AudioLink:YT-dlp] Custom YTDL location found: {customPath}");
                    _ytdlpPath = customPath;
                    _ytdlpFound = true;
                    return;
                }

                Debug.LogWarning($"[AudioLink:YT-dlp] Custom YTDL location detected but does not exist: {customPath}");
                Debug.Log("[AudioLink:YT-dlp] Checking other locations...");
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
            Debug.Log($"[AudioLink:YT-dlp] Found yt-dlp at path '{_ytdlpPath}'");
        }

        public static void LocateFFmpeg()
        {
            _ffmpegFound = false;

            // CRITICAL: Ensure cached prefs are up-to-date. Otherwise, custom path may be ignored after reload.
            FetchEditorPrefs();

            // check for a custom install location
            string customPath = _cachedEditorPrefs.ffmpegPath;
            if (!string.IsNullOrEmpty(customPath))
            {
                if (File.Exists(customPath))
                {
                    Debug.Log($"[AudioLink:FFmpeg] Custom FFmpeg location found: {customPath}");
                    _ffmpegPath = customPath;
                    _ffmpegFound = true;
                    return;
                }

                Debug.LogWarning($"[AudioLink:FFmpeg] Custom FFmpeg location detected but does not exist: {customPath}");
                Debug.Log("[AudioLink:FFmpeg] Checking other locations...");

            }

#if !UNITY_EDITOR_WIN
            _ffmpegPath = "/usr/bin/ffmpeg";
#endif

            if (!File.Exists(_ffmpegPath))
            {
                string[] possibleExecutableNames = { "ffmpeg" };
                _ffmpegPath = LocateExecutable(possibleExecutableNames);
            }

            if (!File.Exists(_ffmpegPath))
                return;

            _ffmpegFound = true;
            Debug.Log($"[AudioLink:FFmpeg] Found FFmpeg at path '{_ffmpegPath}'");
        }

        public static System.Diagnostics.Process ResolvingProcess(string resolverPath, string[] args)
        {
            System.Diagnostics.Process resolver = new System.Diagnostics.Process();

            resolver.EnableRaisingEvents = true;

            resolver.StartInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            resolver.StartInfo.CreateNoWindow = true;
            resolver.StartInfo.UseShellExecute = false;
            resolver.StartInfo.RedirectStandardInput = true;
            resolver.StartInfo.RedirectStandardOutput = true;
            resolver.StartInfo.RedirectStandardError = true;

            resolver.StartInfo.FileName = resolverPath;

            foreach (string argument in args)
                resolver.StartInfo.Arguments += argument + " ";

            return resolver;
        }

        public static void Transcode(string url, Action<ResolvingRequest> callback, string outPath, VideoFormat videoFormat)
        {

            ResolvingRequest transcode = new ResolvingRequest();

            string[] ffmpegArgs = new string[13] {
                "-hide_banner",

                "-y",

                "-hwaccel auto",

                "-i", $"\"{url}\"",

                "-c:a", $"{videoFormat.AudioCodec}",

                "-c:v", $"{videoFormat.VideoCodec}",

                videoFormat.VideoCodec == "vp8" ? "-cpu-used 6 -deadline realtime -qmin 0 -qmax 50 -crf 5 -minrate 1M -maxrate 1M -b:v 1M" : "",

                "-f", $"{videoFormat.Container}",

                $"\"{outPath}\""
            };

            _ffmpegError = "";

            _ffmpegProc = ResolvingProcess(_ffmpegPath, ffmpegArgs);

            _ffmpegProc.Exited += (sender, args) =>
            {

                if (File.Exists(outPath))
                {
#if UNITY_EDITOR_WIN
                    transcode.resolvedURL = outPath;
#else
                    transcode.resolvedURL = "file://" + outPath;
#endif
                    transcode.isDone = true;

                    Debug.Log($"[AudioLink:FFmpeg] Transcode completed sucessfully. ({_ytdlpJson.id})");
                }
                else
                Debug.LogError($"[AudioLink:FFmpeg] Failed to transcode Video! ({_ytdlpJson.id})\n{_ffmpegError}");

                _ytdlpJson.id = "";

                callback(transcode);

                _ffmpegProc.Dispose();
                _ffmpegProc = null;
            };

            _ffmpegProc.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    if (args.Data == "Press [q] to stop, [?] for help")
                        Debug.Log($"[AudioLink:FFmpeg] Starting transcode. ({_ytdlpJson.id})");
                    else if (args.Data.StartsWith("frame="))
                    {
                        string progressTimeString = args.Data;
                        int progressTimeIndex = progressTimeString.IndexOf("time=") + 5;
                        int progressTimeLength = progressTimeString.IndexOf("bitrate=") - progressTimeIndex;

                        string progressTime = progressTimeString.Substring(progressTimeIndex, progressTimeLength);
                        TimeSpan ffmpegProgress = TimeSpan.Parse(progressTime);

                        string progressSeconds = ffmpegProgress.ToString();
                        progressSeconds = progressSeconds.Contains('.') ? progressSeconds.Substring(0, progressSeconds.IndexOf('.')) : progressSeconds;
                        progressSeconds += "s";
                        string progressPercent = _ytdlpJson.duration == 0.0 ? "" : $"- {Mathf.FloorToInt((float)(ffmpegProgress.TotalSeconds / _ytdlpJson.duration) * 100f)}%";

                        Debug.Log($"[AudioLink:FFmpeg] Transcode progress ({_ytdlpJson.id}): {progressSeconds} {progressPercent}");
                    }
                    else
                    {
                        if (args.Data.Contains(_ffErrorIdentifier))
                        {
                            _ffmpegError += args.Data.Substring(0, args.Data.IndexOf(_ffErrorIdentifier)) + "\n";
                        }
                        else
                        {
                            _ffmpegError += args.Data + "\n";
                        }
                    }
                }
            };

            try
            {
                _ffmpegProc.Start();
                _ffmpegProc.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AudioLink:FFmpeg] Unable to transcode URL '{url}' : " + e.Message);
                callback(null);
            }
        }

        public static void TryResolve(Action<bool> callback)
        {
            if (!_ytdlpFound)
            {
                Locateytdlp();
            }

            bool dumpJson = true;

            System.Diagnostics.Process ytdlpJsonDumpCheckProc = ResolvingProcess(_ytdlpPath, new string[] { "--dump-json" });

            ytdlpJsonDumpCheckProc.Exited += (sender, args) => {
                ytdlpJsonDumpCheckProc.Dispose();
                callback(dumpJson);
                };

            ytdlpJsonDumpCheckProc.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    if (args.Data.Contains("error: no such option: --dump-json"))
                        dumpJson = false;
                }
            };

            try
            {
                ytdlpJsonDumpCheckProc.Start();
                ytdlpJsonDumpCheckProc.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AudioLink:YT-dlp] Unable to check for \"--dump-json\" : " + e.Message);
                callback(false);
            }
        }

        private static string SanitizeURL(string url, string identifier, char seperator)
        {
            if (url.StartsWith(identifier) && url.Contains(seperator))
                url = url.Substring(0, url.IndexOf(seperator));

            return url;
        }

        public static void Resolve(string url, Action<ResolvingRequest> callback, int resolution = 720, bool dumpJson = false)
        {
            if (!_ytdlpFound)
            {
                Locateytdlp();
            }

            if (!_ytdlpFound)
            {
                Debug.LogWarning($"[AudioLink:YT-dlp] Unable to resolve URL '{url}' : yt-dlp not found");
            }

            if (!_ffmpegFound)
            {
                LocateFFmpeg();
            }

            useFFmpeg = _cachedEditorPrefs.useFFmpeg;

            // Catch playlist runaway
            url = SanitizeURL(url, "https://www.youtube.com/", '&');
            url = SanitizeURL(url, "https://youtu.be/", '?');

            string tempPath = Path.GetFullPath(Path.Combine("Temp", _ffmpegCache));

#if !UNITY_EDITOR_LINUX
            if (IsFFmpegAvailable())
#endif
            if (!Directory.Exists(tempPath))
                Directory.CreateDirectory(tempPath);

            string urlHash = Hash128.Compute(url).ToString();
            string fullUrlHash = Path.Combine(tempPath, urlHash + $".{platformVideoFormat.Container}");

            ResolvingRequest request = new ResolvingRequest();

            if (File.Exists(fullUrlHash))
            {

#if UNITY_EDITOR_WIN
                request.resolvedURL = fullUrlHash;
#else
                request.resolvedURL = "file://" + fullUrlHash;
#endif
                request.isDone = true;

                callback(request);

                Debug.Log($"[AudioLink:FFmpeg] Loaded cached video ({url}).");
                return;
            }

            _ytdlpJson = new VideoMeta(_id: url);

            if (_ffmpegProc != null)
            {
                _ffmpegProc.StandardInput.Write('q');
                _ffmpegProc.StandardInput.Flush();
            }

            string[] ytdlpArgs = new string[8] {
                "--no-check-certificate",
                "--no-cache-dir",
                "--rm-cache-dir",

                dumpJson ? "--dump-json" : "",

                "-f", $"\"mp4[height<=?{resolution}][protocol^=http]/best[height<=?{resolution}][protocol^=http]\"",

                "--get-url", $"\"{url}\""
            };

            System.Diagnostics.Process ytdlpProc = ResolvingProcess(_ytdlpPath, ytdlpArgs);

            ytdlpProc.Exited += (sender, args) => { ytdlpProc.Dispose(); };

            ytdlpProc.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    if (args.Data.StartsWith("{"))
                    {
                        _ytdlpJson = JsonUtility.FromJson<VideoMeta>(args.Data);
                    }
                    else
                    {
                        string debugStdout = args.Data;
                        if (args.Data.Contains("ip="))
                        {
                            int filterStart = args.Data.IndexOf("ip=");
                            int filterEnd = args.Data.Substring(filterStart).IndexOf("&");

                            debugStdout = args.Data.Replace(args.Data.Substring(filterStart + 3, filterEnd - 3), "[REDACTED]");
                        }

                        Debug.Log("[AudioLink:YT-dlp] ytdlp resolved: " + debugStdout);

                        if (useFFmpeg && IsFFmpegAvailable())
                        {
                            Transcode(args.Data, callback, fullUrlHash, platformVideoFormat);
                        }
                        else
                        {
                            if (useFFmpeg)
                                Debug.LogWarning($"[AudioLink:FFmpeg] Unable to convert URL '{url}' : FFmpeg not found");

                            request.resolvedURL = args.Data;
                            request.isDone = true;

                            callback(request);
                        }
                    }
                }
            };

            ytdlpProc.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    if (args.Data.StartsWith("WARNING: "))
                    {
                        Debug.LogWarning("[AudioLink:YT-dlp] YT-dlp " + args.Data);
                    } else {
                        Debug.LogError("[AudioLink:YT-dlp] YT-dlp " + args.Data);
                    }
                }
            };

            try
            {
                ytdlpProc.Start();
                ytdlpProc.BeginOutputReadLine();
                ytdlpProc.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AudioLink:YT-dlp] Unable to resolve URL '{url}' : " + e.Message);
                callback(null);
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

        private const string showVideoPreviewInComponentKey = "YTDLP-VIDEO-PREVIEW";
        private const string useFFmpegTranscodeKey = "USE-FFMPEG-TRANSCODE";

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
            bool available = ytdlpURLResolver.IsytdlpAvailable() && ytdlpURLResolver.IsFFmpegAvailable();
#else
            bool available = ytdlpURLResolver.IsytdlpAvailable();
#endif

            bool hasVideoPlayer = _ytdlpPlayer.videoPlayer != null;
            float playbackTime = hasVideoPlayer ? _ytdlpPlayer.GetPlaybackTime() : 0;
            double videoLength = hasVideoPlayer ? _ytdlpPlayer.videoPlayer.length : 0;

            if (ytdlpURLResolver.useFFmpeg && ytdlpURLResolver.IsFFmpegAvailable())
                EditorGUILayout.HelpBox("Using FFmpeg to transcode test videos into a compatible format locally.\n\nThis may play videos that *are not* supported in VRChat / CilloutVR,\nadditionally it does not support livestreams.\n\nIf you encounter any issues, specify you're using FFmpeg Transcoding when reporting issues.", MessageType.Info);

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

            // FFmpeg toggle
            {
                bool platformDefaultUseFFmpegTranscode = false;
#if UNITY_EDITOR_LINUX
                platformDefaultUseFFmpegTranscode = true;
#endif

                bool wasUsingFFmpeg = EditorPrefs.GetBool(useFFmpegTranscodeKey, platformDefaultUseFFmpegTranscode);
                ytdlpURLResolver.useFFmpeg = EditorGUILayout.ToggleLeft(new GUIContent("Use FFmpeg Transcoding"), wasUsingFFmpeg);

                if (wasUsingFFmpeg != ytdlpURLResolver.useFFmpeg)
                    EditorPrefs.SetBool(useFFmpegTranscodeKey, ytdlpURLResolver.useFFmpeg);
            }

            // Video preview
            using (new EditorGUI.DisabledScope(!available || !hasVideoPlayer))
            {
                bool wasShowingPreview = EditorPrefs.GetBool(showVideoPreviewInComponentKey, false);
                _ytdlpPlayer.showVideoPreviewInComponent = EditorGUILayout.Toggle(new GUIContent("  Show Video Preview", EditorGUIUtility.IconContent("d_ViewToolOrbit On").image), wasShowingPreview);

                if (wasShowingPreview != _ytdlpPlayer.showVideoPreviewInComponent)
                    EditorPrefs.SetBool(showVideoPreviewInComponentKey, _ytdlpPlayer.showVideoPreviewInComponent);

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

            bool ffmpegNotFound = (ytdlpURLResolver.useFFmpeg && !ytdlpURLResolver.IsFFmpegAvailable());
            if (!available || ffmpegNotFound)
            {
#if UNITY_EDITOR_LINUX
                EditorGUILayout.HelpBox("Failed to locate yt-dlp & ffmpeg executables.\n\nTo fix this, install yt-dlp and ffmpeg via your package manager,\nor make sure the portable executables are in your PATH.\n\nOnce this is done, enter play mode to retry.", MessageType.Warning);
#elif UNITY_EDITOR_WIN
                if (ffmpegNotFound)
                    EditorGUILayout.HelpBox("Failed to locate ffmpeg executable.\n\nTo fix this, install ffmpeg and make sure the executable is on your PATH.\n\nOnce this is done, enter play mode to retry.", MessageType.Warning);
                if (!available)
                    EditorGUILayout.HelpBox("Failed to locate yt-dlp executable.\n\nTo fix this, either install and launch VRChat once,\nor install yt-dlp and make sure the executable is on your PATH.\n\nOnce this is done, enter play mode to retry.", MessageType.Warning);
#else
                EditorGUILayout.HelpBox("Failed to locate yt-dlp & ffmpeg executables.\n\nTo fix this, install yt-dlp and ffmpeg via *homebrew*: \"brew install yt-dlp ffmpeg\", or make sure the executables are in your PATH.\n\nOnce this is done, enter play mode to retry.", MessageType.Warning);
#endif
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
