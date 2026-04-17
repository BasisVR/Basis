using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Basis.Scripts.Networking;
using System;
public class BasisFrameRateVisualization : MonoBehaviour
{
    public TextMeshProUGUI fpsText;
    public string Title;

    private float deltaTime;
    private int cachedHour, cachedMinute, cachedSecond;
    private float nextTimeUpdate;

    // Reusable character buffer — adjust size if needed
    private char[] buffer = new char[160];

    // Throttle redraws to 10 Hz. SetCharArray forces a TMP vertex/layout
    // rebuild, and the user can't read FPS updates faster than this anyway.
    private const float RedrawInterval = 0.1f;
    private float _redrawTimer;

    private bool _cascadeFrozen;

    /// <summary>
    /// The panel Title label lives inside Title → Title Content → Panel Element Base,
    /// each with a ContentSizeFitter. Without this, every 10 Hz SetCharArray cascades
    /// a layout rebuild up through all of them, which shows up as multi-percent cost
    /// in LayoutRebuilder.PerformLayoutCalculation/Control on the profiler.
    /// Walking up and pinning each ancestor's current size into a LayoutElement
    /// (then disabling its CSF) stops the cascade at the main menu panel root.
    /// </summary>
    private void FreezeAncestorCascade()
    {
        if (_cascadeFrozen || fpsText == null) return;
        _cascadeFrozen = true;

        Transform t = fpsText.transform;
        int depth = 0;
        while (t != null && depth < 5)
        {
            if (t is RectTransform rt && rt.TryGetComponent(out ContentSizeFitter csf) && csf.enabled)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
                if (!rt.TryGetComponent(out LayoutElement le))
                {
                    le = rt.gameObject.AddComponent<LayoutElement>();
                }
                le.preferredHeight = rt.rect.height;
                le.minHeight = rt.rect.height;
                csf.enabled = false;
            }
            t = t.parent;
            depth++;
        }
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        // Keep smoothing the delta every frame so the displayed value stays accurate.
        deltaTime += (dt - deltaTime) * 0.1f;

        _redrawTimer += dt;
        if (_redrawTimer < RedrawInterval) return;
        _redrawTimer -= RedrawInterval;

        float fps = 1f / deltaTime;

        // Only fetch system time once per second
        float time = Time.unscaledTime;
        if (time >= nextTimeUpdate)
        {
            var now = DateTime.Now;
            cachedHour = now.Hour;
            cachedMinute = now.Minute;
            cachedSecond = now.Second;
            nextTimeUpdate = time + 1f;
        }

        int idx = 0;

        // Copy title straight into buffer
        for (int i = 0; i < Title.Length; i++)
            buffer[idx++] = Title[i];

        // Scale down stats relative to title
        idx = Append(buffer, "     <size=70%>Time:", idx);
        idx = AppendTwoDigit(cachedHour, idx);
        buffer[idx++] = ':';
        idx = AppendTwoDigit(cachedMinute, idx);
        buffer[idx++] = ':';
        idx = AppendTwoDigit(cachedSecond, idx);

        var peer = BasisNetworkConnection.LocalPlayerPeer;

        if (peer != null)
        {
            idx = Append(buffer, " RTT:", idx);
            idx = AppendInt(peer.RoundTripTime, idx);
            idx = Append(buffer, " STT:", idx);
            idx = AppendInt(peer.Ping, idx);
            idx = Append(buffer, " CCU:", idx);
            idx = AppendInt(BasisNetworkPlayers.ReceiverCount + 1, idx);
            int peerLimit = BasisNetworkManagement.ServerMetaDataMessage.PeerLimit;
            if (peerLimit > 0)
            {
                buffer[idx++] = '/';
                idx = AppendInt(peerLimit, idx);
            }
        }

        idx = Append(buffer, " FPS:", idx);
        idx = AppendFloat(fps, 2, idx);

        // We don't convert to string → no GC
        fpsText.SetCharArray(buffer, 0, idx);

        // First tick with real content is present — freeze the ancestor CSF
        // chain so subsequent ticks don't cascade layout rebuilds.
        FreezeAncestorCascade();
    }

    // -------- Helpers (no GC) --------

    private int Append(char[] buf, string str, int index)
    {
        for (int i = 0; i < str.Length; i++)
            buf[index++] = str[i];
        return index;
    }

    private int AppendTwoDigit(int val, int index)
    {
        buffer[index++] = (char)('0' + val / 10);
        buffer[index++] = (char)('0' + val % 10);
        return index;
    }

    private int AppendInt(int val, int index)
    {
        if (val < 0)
        {
            buffer[index++] = '-';
            val = -val;
        }
        if (val == 0)
        {
            buffer[index++] = '0';
            return index;
        }
        int start = index;
        while (val > 0)
        {
            buffer[index++] = (char)('0' + val % 10);
            val /= 10;
        }
        // Reverse digits in-place
        int end = index - 1;
        while (start < end)
        {
            char tmp = buffer[start];
            buffer[start] = buffer[end];
            buffer[end] = tmp;
            start++;
            end--;
        }
        return index;
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
