using System.IO;
using System.Net.Http;
using System;
using UnityEngine;
using FFmpeg.Unity;

public class BasisVideoPlayer : MonoBehaviour
{
    public FFUnity ffmpeg; // FFmpeg integration script or component
    public string contentUrl;  // URL or path to the video file
    public bool stream = false;  // Flag for streaming or local file
    public MeshRenderer mesh;
    public string MaterialTextureId = "_EmissionMap";
    public Material RuntimeMaterial;
    private void Start()
    {
        RuntimeMaterial = Material.Instantiate(mesh.sharedMaterial);
        RuntimeMaterial.mainTextureScale = new Vector2(1, -1);
        RuntimeMaterial.mainTextureOffset = new Vector2(0,1);
        mesh.sharedMaterial = RuntimeMaterial;
        ffmpeg.OnDisplay = OnDisplay;
        if (!string.IsNullOrEmpty(contentUrl))
        {
            Play(contentUrl); // Play the provided URL if it's set
        }
    }

    // Display the video texture on a 3D mesh in Unity
    private void OnDisplay(Texture2D tex)
    {
        RuntimeMaterial.mainTexture = tex;
        RuntimeMaterial.SetTexture(MaterialTextureId, tex);
        mesh.UpdateGIMaterials();
    }

    // Play a video from a URL or local file path
    public void Play(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            Debug.LogError("Error: URL or file path is empty.");
            return;
        }

        contentUrl = url;
        ffmpeg.CanSeek = !contentUrl.StartsWith("rtmp://") && !stream;

        if (stream)
        {
            PlayStream(contentUrl);  // Handle streaming
        }
        else
        {
            PlayFile(contentUrl);  // Handle local file
        }
    }

    // Play a local video file using FFmpeg
    public void PlayFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Debug.Log("Playing local file: " + filePath);
                using (FileStream videoStream = File.OpenRead(filePath))
                {
                    ffmpeg.Play(videoStream, videoStream);
                }
            }
            else
            {
                Debug.LogError("File not found: " + filePath);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Error playing file: " + e.Message);
        }
    }

    // Play a video stream from a URL
    public void PlayStream(string url)
    {
        Debug.Log("Playing stream from URL: " + url);
        ffmpeg.Play(url, url);
    }

    // Optionally, allow for downloading and playing video from the web using HTTP
    public async void PlayFromWeb(string url)
    {
        HttpClient client = new HttpClient();
        try
        {
            HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            Stream videoStream = await response.Content.ReadAsStreamAsync();
            ffmpeg.Play(videoStream, videoStream);
        }
        catch (Exception e)
        {
            Debug.LogError("Error fetching video stream: " + e.Message);
        }
    }
    public void TogglePlay()
    {
        if(ffmpeg.IsPaused)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }
    // API method to pause video playback
    public void Pause()
    {
        if (!ffmpeg.IsPaused)
        {
            ffmpeg.Pause();
        }
    }

    // API method to resume video playback
    public void Resume()
    {
        if (ffmpeg.IsPaused)
        {
            ffmpeg.Resume();
        }
    }
    // API method to seek to a specific time in the video
    public void Seek(double timeInSeconds)
    {
        ffmpeg.Seek(timeInSeconds);
    }

    // API method to set the playback volume
    public void SetVolume(float volume)
    {
        FFUnityAudioHelper.SetVolume(ffmpeg.AudioProcessing.AudioOutput, Mathf.Clamp(volume, 0f, 1f));
    }

    // API method to retrieve the current playback time
    public double GetPlaybackTime()
    {
        return ffmpeg.PlaybackTime;
    }

    // API method to retrieve the timer of the video
    public double GetTimer()
    {
        return ffmpeg.timer;
    }

    // API method to check if the video is paused
    public bool IsPaused()
    {
        return ffmpeg.IsPaused;
    }

    // API method to toggle between stream and file mode
    public void SetStreamMode(bool isStreaming)
    {
        stream = isStreaming;
    }
}