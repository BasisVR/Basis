using TMPro;
using UnityEngine;
using Basis.Scripts.Networking;
using System;

public class BasisFrameRateVisualization : MonoBehaviour
{
    public TextMeshProUGUI fpsText;
    public string Title;

    private float deltaTime;

    // Reusable character buffer — adjust size if needed
    private char[] buffer = new char[128];

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        deltaTime += (dt - deltaTime) * 0.1f;
        float fps = 1f / deltaTime;

        int idx = 0;

        // Copy title straight into buffer
        for (int i = 0; i < Title.Length; i++)
            buffer[idx++] = Title[i];

        var peer = BasisNetworkConnection.LocalPlayerPeer;

        if (peer != null)
        {
            idx = Append(buffer, " RTT:", idx);
            idx = AppendInt(peer.RoundTripTime, idx);
            idx = Append(buffer, " STT:", idx);
            idx = AppendInt(peer.Ping, idx);
            idx = Append(buffer, " CCU:", idx);
            idx = AppendInt(BasisNetworkPlayers.ReceiverCount + 1, idx);
        }

        idx = Append(buffer, " FPS:", idx);
        idx = AppendFloat(fps, 2, idx);

        // We don't convert to string → no GC
        fpsText.SetCharArray(buffer, 0, idx);
    }


    // -------- Helpers (no GC) --------

    private int Append(char[] buf, string str, int index)
    {
        for (int i = 0; i < str.Length; i++)
            buf[index++] = str[i];
        return index;
    }

    private int AppendInt(int val, int index)
    {
        return Append(buffer, val.ToString(), index); // Temporary GC? → Replace below if needed
    }

    // Manual float format (no ToString → no garbage)
    private int AppendFloat(float value, int decimals, int index)
    {
        int whole = (int)value;
        float frac = Mathf.Abs(value - whole);

        index = AppendInt(whole, index);
        buffer[index++] = '.';

        for (int i = 0; i < decimals; i++)
        {
            frac *= 10f;
            buffer[index++] = (char)('0' + (int)frac % 10);
        }

        return index;
    }
}
