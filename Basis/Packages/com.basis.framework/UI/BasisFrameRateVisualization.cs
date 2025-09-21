using TMPro;
using UnityEngine;
using Basis.Scripts.Networking;
using System.Text; // Import for StringBuilder

public class BasisFrameRateVisualization : MonoBehaviour
{
    public TextMeshProUGUI fpsText; // UI Text element to display the FPS
    private float deltaTime = 0.0f;  // Time between frames
    private float fps = 0.0f;  // Frames per second
    private float timeBetweenUpdates = 0.1f;  // Time between updates (in seconds)
    private float timeAccumulator = 0.0f;  // Time accumulator to track updates
    private StringBuilder stringBuilder = new StringBuilder(); // Reusable StringBuilder

    void Update()
    {
        float Unscaled = Time.unscaledDeltaTime;
        // Calculate the time it took to render the last frame
        deltaTime += (Unscaled - deltaTime) * 0.1f;

        // Calculate FPS based on deltaTime
        fps = 1.0f / deltaTime;

        // Accumulate time
        timeAccumulator += Unscaled;
        // If the accumulated time exceeds the update interval, update the FPS display
        if (timeAccumulator >= timeBetweenUpdates)
        {
            // Clear and construct the string using StringBuilder
            stringBuilder.Clear();
            stringBuilder.Append("FPS: ");
            stringBuilder.Append(fps.ToString("F2"));

            if (BasisNetworkConnection.LocalPlayerPeer != null)
            {
                stringBuilder.Append(" RTT: ");
                stringBuilder.Append(BasisNetworkConnection.LocalPlayerPeer.RoundTripTime);
                stringBuilder.Append(" STT: ");
                stringBuilder.Append(BasisNetworkConnection.LocalPlayerPeer.Ping);
                stringBuilder.Append(" CCU: ");
                stringBuilder.Append(BasisNetworkPlayers.ReceiverCount +1);
            }
            // Update the TextMeshProUGUI text
            fpsText.text = stringBuilder.ToString();

            // Reset the time accumulator
            timeAccumulator = 0.0f;
        }
    }
}
