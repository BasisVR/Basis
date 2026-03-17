using UnityEngine;
using Cilbox;

namespace Basis
{
    public class VideoPlayerShim : CilboxShim
    {
        private UnityEngine.Video.VideoPlayer videoPlayer;
        public void Awake()
        {
            videoPlayer = gameObject.GetComponent<UnityEngine.Video.VideoPlayer>();
            if (videoPlayer == null)
                videoPlayer = gameObject.AddComponent<UnityEngine.Video.VideoPlayer>();
        }

        public string url { 
            get { return videoPlayer.url; } 
            set {
                string url = value;

                if(url == videoPlayer.url) return;

                Debug.Log($"[VideoPlayerShim] Setting URL to {url}");

                if(!url.StartsWith("https://")) return;

                Basis.BasisUI.BasisMainMenu.Instance.OpenDialogue(
                    "Video Player URL",
                    $"Do you want to load this video?\n{url}",
                    "Accept",
                    "Decline",
                    value => {
                        if (!value) return;
                        videoPlayer.url = url;
                    }
                );
            } 
        }
        public RenderTexture targetTexture { 
            get { return videoPlayer.targetTexture; } 
            set { videoPlayer.targetTexture = value; } 
        }
        public bool isPlaying { get { return videoPlayer.isPlaying; } }
        public double time { 
            get { return videoPlayer.time; } 
            set { videoPlayer.time = value; } 
        }

        public void Play() { videoPlayer.Play(); }
        public void Pause() { videoPlayer.Pause(); }
        public void Stop() { videoPlayer.Stop(); }
        public void Prepare() { videoPlayer.Prepare(); }
    }
}