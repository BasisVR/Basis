using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Behaviour;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Live view of the AdditionalAvatarData pipeline (face tracking et al.):
/// what the local client is submitting/sending out, and what each remote
/// player's receiver is getting and dispatching into avatar behaviours.
/// </summary>
public class BasisAdditionalDataDebugWindow : EditorWindow
{
    private Vector2 _scrollPos;
    private bool _showOutgoing = true;
    private bool _showIncoming = true;
    private readonly Dictionary<ushort, bool> _playerFoldouts = new();

    private readonly Dictionary<long, long> _prevCounts = new();
    private readonly Dictionary<long, float> _rates = new();
    private double _lastSample;
    private bool _sampleThisFrame;
    private readonly StringBuilder _hex = new();

    private System.IO.StreamWriter _csv;
    private string _csvPath;
    private double _csvStart;
    private double _csvLastSample;
    private readonly Dictionary<long, long> _csvPrev = new();
    private const double CsvInterval = 1.0;

    private static readonly Color GoodColor = new Color(0.3f, 0.9f, 0.3f);
    private static readonly Color WarnColor = new Color(1f, 0.8f, 0.2f);
    private static readonly Color ErrorColor = new Color(1f, 0.3f, 0.3f);

    [MenuItem("Basis/Debug/Additional Data (Face Tracking)")]
    public static void ShowWindow()
    {
        var w = GetWindow<BasisAdditionalDataDebugWindow>("Additional Data");
        w.minSize = new Vector2(560, 420);
    }

    private void OnEnable()
    {
        BasisAdditionalDataDebugCapture.Capture = true;
        EditorApplication.update += Tick;
    }

    private void OnDisable()
    {
        BasisAdditionalDataDebugCapture.Capture = false;
        EditorApplication.update -= Tick;
        StopCsv();
    }

    private void Tick()
    {
        // CSV sampling lives on the editor update, not OnGUI, so a run keeps recording
        // while the window is unfocused or hidden behind the Game view.
        if (_csv != null)
        {
            double now = BasisAdditionalDataDebugCapture.Now;
            if (now - _csvLastSample >= CsvInterval)
            {
                float dt = (float)(now - _csvLastSample);
                _csvLastSample = now;
                try
                {
                    WriteCsvSample(now, dt);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[AdditionalData] CSV recording failed, stopping: {e.Message}");
                    StopCsv();
                }
            }
        }
        Repaint();
    }

    private void OnGUI()
    {
        double now = BasisAdditionalDataDebugCapture.Now;
        _sampleThisFrame = now - _lastSample >= 0.5;
        float dt = (float)(now - _lastSample);
        if (_sampleThisFrame) _lastSample = now;

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        EditorGUILayout.LabelField("AdditionalAvatarData Pipeline (face tracking, avatar behaviour params)", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Behaviour submit -> Transmitter -> Compress -> wire -> Decompress -> LinkedAvatarIndex gate -> Behaviour dispatch", EditorStyles.miniLabel);

        EditorGUILayout.BeginHorizontal();
        if (_csv == null)
        {
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Record CSV", GUILayout.Width(110))) StartCsv();
            }
        }
        else
        {
            var prev = GUI.color;
            GUI.color = ErrorColor;
            if (GUILayout.Button($"■ Stop ({now - _csvStart:F0}s)", GUILayout.Width(110))) StopCsv();
            GUI.color = prev;
        }
        if (!string.IsNullOrEmpty(_csvPath))
        {
            if (GUILayout.Button("Open Folder", GUILayout.Width(90)))
            {
                EditorUtility.RevealInFinder(_csvPath);
            }
            EditorGUILayout.LabelField(_csvPath, EditorStyles.miniLabel);
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Clear Counters", GUILayout.Width(110)))
        {
            BasisAdditionalDataDebugCapture.Clear();
            _prevCounts.Clear();
            _rates.Clear();
            _csvPrev.Clear();
        }
        EditorGUILayout.EndHorizontal();

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode to see live data.", MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        EditorGUILayout.Space(4);
        _showOutgoing = EditorGUILayout.BeginFoldoutHeaderGroup(_showOutgoing, "Outgoing — Local Player");
        if (_showOutgoing) DrawOutgoing(now, dt);
        EditorGUILayout.EndFoldoutHeaderGroup();

        EditorGUILayout.Space(4);
        _showIncoming = EditorGUILayout.BeginFoldoutHeaderGroup(_showIncoming, "Incoming — Remote Players");
        if (_showIncoming) DrawIncoming(now, dt);
        EditorGUILayout.EndFoldoutHeaderGroup();

        EditorGUILayout.EndScrollView();
    }

    private void DrawOutgoing(double now, float dt)
    {
        float submittedRate = Rate(1L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderSubmitted), dt);
        float attachedRate = Rate(2L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderFramesWithAdditional), dt);

        float avatarChRate = Rate(5L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderAvatarChannelSent), dt);
        EditorGUILayout.LabelField($"submitted/s: {submittedRate:F1}    frames-with-section/s: {attachedRate:F1}    avatarCh-sent/s: {avatarChRate:F1}    " +
            $"keyframes: {System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderFramesKeyframe)}    " +
            $"deltas: {System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderFramesDelta)}    " +
            $"noTransmitter: {System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderSubmitFailedNoTransmitter)}");

        float storeSubmitRate = Rate(6L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrStoreSubmits), dt);
        float noListenerRate = Rate(7L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrStoreSubmitsNoListener), dt);
        float addrUpdateRate = Rate(8L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerAddressUpdates), dt);
        float newValueRate = Rate(9L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerNewValues), dt);
        float tickRate = Rate(10L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerTicks), dt);
        float tickWithValuesRate = Rate(11L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerTicksWithValues), dt);
        float activityRate = Rate(12L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrActivitySamples), dt);
        EditorGUILayout.LabelField($"HVR wearer: storeSubmits/s: {storeSubmitRate:F1} (noListener/s: {noListenerRate:F1})    " +
            $"netAddrUpdates/s: {addrUpdateRate:F1}    newValues/s: {newValueRate:F1}    " +
            $"ticks/s: {tickRate:F1} (withValues/s: {tickWithValuesRate:F1})    ftSamples/s: {activityRate:F1}");

        if (storeSubmitRate <= 0f && noListenerRate <= 0f && activityRate <= 0f)
        {
            DrawStatus("NO STORE TRAFFIC: nothing (face tracker, eye source) is submitting into the HVR variable store — " +
                "local movement is coming from a path that bypasses the store, so there is nothing to network.", ErrorColor);
        }
        else if (storeSubmitRate > 0f && addrUpdateRate <= 0f)
        {
            DrawStatus("STORE↛NETWORK: values are submitted into a store the wearer networking is NOT listening on " +
                "(listener registered on a different store instance, or RequireVariable never ran).", ErrorColor);
        }
        else if (addrUpdateRate > 0f && newValueRate <= 0f)
        {
            DrawStatus("VALUES FROZEN: networking sees the updates but every value equals the last one (Mathf.Approximately) — nothing queues for send.", WarnColor);
        }
        else if (newValueRate > 0f && tickRate <= 0f)
        {
            DrawStatus("TICK DEAD: values queue but HVRVariableNetworking.DoTick never runs (HVRCommsUpdateDriver not pumping this instance).", ErrorColor);
        }

        if (submittedRate > 0f && attachedRate <= 0f)
        {
            DrawStatus("SENDER BROKEN: behaviours are submitting but Compress never attaches a section to outgoing frames.", ErrorColor);
        }

        var localBehaviours = BasisNetworkPlayer.LocalPlayer?.NetworkBehaviours;
        bool any = false;
        EditorGUILayout.LabelField("Reduction system (high-frequency face stream)", EditorStyles.miniBoldLabel);
        DrawSlotHeader();
        var sent = BasisAdditionalDataDebugCapture.Sent;
        for (int Index = 0; Index < sent.Length; Index++)
        {
            var slot = sent[Index];
            if (slot == null) continue;
            any = true;
            DrawSlotRow((byte)Index, slot, (3L << 60) | (long)Index, now, dt, BehaviourName(localBehaviours, Index));
        }
        if (!any)
        {
            DrawStatus("NOTHING SUBMITTED: no avatar behaviour has handed data to the transmitter since capture started. " +
                "Face tracking output is dying before it ever reaches the network layer.", WarnColor);
        }

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("Avatar channel ch15 (HVR handshake, variable updates, upgrades)", EditorStyles.miniBoldLabel);
        bool anyCh15 = false;
        DrawSlotHeader();
        var sentCh15 = BasisAdditionalDataDebugCapture.SentCh15;
        for (int Index = 0; Index < sentCh15.Length; Index++)
        {
            var slot = sentCh15[Index];
            if (slot == null) continue;
            anyCh15 = true;
            DrawSlotRow((byte)Index, slot, (4L << 60) | (long)Index, now, dt, BehaviourName(localBehaviours, Index));
        }
        if (!anyCh15)
        {
            DrawStatus("No avatar-channel sends: HVR comms never initialized on the local avatar (handshake/variable packets absent).", WarnColor);
        }
    }

    private void DrawIncoming(double now, float dt)
    {
        var players = BasisAdditionalDataDebugCapture.Players;
        if (players.IsEmpty)
        {
            EditorGUILayout.HelpBox("No remote player has delivered an AdditionalAvatarData section since capture started.", MessageType.Info);
            return;
        }

        var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        int receiverCount = BasisNetworkPlayers.ReceiverCount;

        foreach (var pair in players)
        {
            ushort playerId = pair.Key;
            var pc = pair.Value;

            BasisNetworkReceiver receiver = null;
            for (int Index = 0; Index < receiverCount && Index < snapshot.Length; Index++)
            {
                if (snapshot[Index] != null && snapshot[Index].playerId == playerId) { receiver = snapshot[Index]; break; }
            }

            string title = receiver != null ? $"Player {playerId} — {receiver.displayName}" : $"Player {playerId} (no receiver — left?)";
            if (!_playerFoldouts.TryGetValue(playerId, out bool open)) open = true;
            _playerFoldouts[playerId] = EditorGUILayout.Foldout(open, title, true);
            if (!_playerFoldouts[playerId]) continue;

            EditorGUI.indentLevel++;
            long keyBase = (long)playerId << 32;
            float frameRate = Rate(keyBase | 1, System.Threading.Interlocked.Read(ref pc.FramesWithSection), dt);
            float dispatchRate = Rate(keyBase | 2, System.Threading.Interlocked.Read(ref pc.EntriesDispatched), dt);
            float gateRate = Rate(keyBase | 3, System.Threading.Interlocked.Read(ref pc.DroppedLinkedIndex), dt);

            EditorGUILayout.LabelField($"frames-with-section/s: {frameRate:F1}    entries-dispatched/s: {dispatchRate:F1}    " +
                $"gateDrops: {System.Threading.Interlocked.Read(ref pc.DroppedLinkedIndex)}    " +
                $"skippedEmpty: {System.Threading.Interlocked.Read(ref pc.SkippedEmpty)}    " +
                $"skippedIndex: {System.Threading.Interlocked.Read(ref pc.SkippedIndex)}");

            if (frameRate > 0f && dispatchRate <= 0f)
            {
                string why = gateRate > 0f
                    ? "the LinkedAvatarIndex gate is rejecting every frame (avatar index mismatch between sender and receiver)."
                    : "entries are being skipped before any behaviour runs (see skippedEmpty / skippedIndex).";
                DrawStatus("RECEIVER BROKEN: sections arrive for this player but nothing is dispatched — " + why, ErrorColor);
            }
            else if (frameRate <= 0f && now - pc.LastFrameTime > 3.0)
            {
                DrawStatus($"No sections from this player for {now - pc.LastFrameTime:F0}s — their client is not sending (or the server strips it).", WarnColor);
            }

            bool anySlot = false;
            var behaviours = receiver?.NetworkBehaviours;
            EditorGUILayout.LabelField("Reduction system", EditorStyles.miniBoldLabel);
            DrawSlotHeader();
            for (int Index = 0; Index < pc.Slots.Length; Index++)
            {
                var slot = pc.Slots[Index];
                if (slot == null) continue;
                anySlot = true;
                DrawSlotRow((byte)Index, slot, keyBase | (0x100L << 16) | (long)Index, now, dt, BehaviourName(behaviours, Index));
            }
            if (!anySlot) EditorGUILayout.LabelField("(no entries dispatched yet)", EditorStyles.miniLabel);

            EditorGUILayout.LabelField("Avatar channel ch15", EditorStyles.miniBoldLabel);
            bool anyCh15Slot = false;
            DrawSlotHeader();
            for (int Index = 0; Index < pc.SlotsCh15.Length; Index++)
            {
                var slot = pc.SlotsCh15[Index];
                if (slot == null) continue;
                anyCh15Slot = true;
                DrawSlotRow((byte)Index, slot, keyBase | (0x200L << 16) | (long)Index, now, dt, BehaviourName(behaviours, Index));
            }
            if (!anyCh15Slot) EditorGUILayout.LabelField("(no ch15 messages dispatched yet)", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }
    }

    private static void DrawSlotHeader()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("idx", EditorStyles.miniBoldLabel, GUILayout.Width(30));
        EditorGUILayout.LabelField("behaviour", EditorStyles.miniBoldLabel, GUILayout.Width(180));
        EditorGUILayout.LabelField("rate/s", EditorStyles.miniBoldLabel, GUILayout.Width(50));
        EditorGUILayout.LabelField("chg/s", EditorStyles.miniBoldLabel, GUILayout.Width(50));
        EditorGUILayout.LabelField("size", EditorStyles.miniBoldLabel, GUILayout.Width(40));
        EditorGUILayout.LabelField("age", EditorStyles.miniBoldLabel, GUILayout.Width(40));
        EditorGUILayout.LabelField("payload (hex, first bytes)", EditorStyles.miniBoldLabel);
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSlotRow(byte messageIndex, BasisAdditionalDataDebugCapture.Slot slot, long rateKey, double now, float dt, string behaviourName)
    {
        long count = System.Threading.Interlocked.Read(ref slot.Count);
        long changed = System.Threading.Interlocked.Read(ref slot.ChangedCount);
        float rate = Rate(rateKey, count, dt);
        float changedRate = Rate(rateKey | (1L << 62), changed, dt);
        double age = now - slot.LastTime;

        _hex.Clear();
        int previewSize = Mathf.Min(slot.PreviewSize, BasisAdditionalDataDebugCapture.PayloadPreviewBytes);
        for (int Index = 0; Index < previewSize; Index++)
        {
            _hex.Append(slot.Preview[Index].ToString("X2"));
            if ((Index & 3) == 3) _hex.Append(' ');
        }

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(messageIndex.ToString(), GUILayout.Width(30));
        EditorGUILayout.LabelField(behaviourName, GUILayout.Width(180));
        EditorGUILayout.LabelField(rate.ToString("F1"), GUILayout.Width(50));

        var prev = GUI.color;
        if (rate > 0f && changedRate <= 0f) GUI.color = WarnColor;
        EditorGUILayout.LabelField(changedRate.ToString("F1"), GUILayout.Width(50));
        GUI.color = prev;

        EditorGUILayout.LabelField(slot.LastSize.ToString(), GUILayout.Width(40));
        GUI.color = age > 2.0 ? WarnColor : GoodColor;
        EditorGUILayout.LabelField(age < 99 ? $"{age:F1}s" : ">99s", GUILayout.Width(40));
        GUI.color = prev;
        EditorGUILayout.LabelField(_hex.ToString(), EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        if (rate > 0f && changedRate <= 0f)
        {
            DrawStatus("Payload is flowing but its bytes never change — the data conversion upstream of this hop is frozen.", WarnColor);
        }
    }

    private static string BehaviourName(BasisNetworkAvatarBehaviour[] behaviours, int index)
    {
        if (behaviours == null || index >= behaviours.Length) return "?";
        var b = behaviours[index];
        return b == null ? "(destroyed)" : b.GetType().Name;
    }

    private static void DrawStatus(string message, Color color)
    {
        var prev = GUI.color;
        GUI.color = color;
        EditorGUILayout.LabelField("● " + message, EditorStyles.wordWrappedMiniLabel);
        GUI.color = prev;
    }

    private float Rate(long key, long count, float dt)
    {
        if (_sampleThisFrame && dt > 0f)
        {
            _prevCounts.TryGetValue(key, out long prev);
            _rates[key] = (count - prev) / dt;
            _prevCounts[key] = count;
        }
        _rates.TryGetValue(key, out float rate);
        return rate;
    }

    private void StartCsv()
    {
        string dir = System.IO.Path.Combine(Application.persistentDataPath, "AdditionalDataDebug");
        System.IO.Directory.CreateDirectory(dir);
        _csvPath = System.IO.Path.Combine(dir, $"addl_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv");
        _csv = new System.IO.StreamWriter(_csvPath, false, Encoding.UTF8);
        _csv.WriteLine("t,kind,playerId,player,idx,behaviour,count,rate,changed,chgRate,size,age,frames,frameRate,dispatched,dispatchRate,gateDrops,skippedEmpty,skippedIndex,hex");
        _csvStart = BasisAdditionalDataDebugCapture.Now;
        _csvLastSample = _csvStart;
        _csvPrev.Clear();
        Debug.Log($"[AdditionalData] Recording CSV to {_csvPath}");
    }

    private void StopCsv()
    {
        if (_csv == null) return;
        _csv.Flush();
        _csv.Dispose();
        _csv = null;
        Debug.Log($"[AdditionalData] CSV recording stopped: {_csvPath}");
    }

    private float CsvRate(long key, long count, float dt)
    {
        _csvPrev.TryGetValue(key, out long prev);
        _csvPrev[key] = count;
        return dt > 0f ? (count - prev) / dt : 0f;
    }

    private void WriteCsvSample(double now, float dt)
    {
        string t = (now - _csvStart).ToString("F1");

        WriteGlobalRow(t, dt, 1, "SenderSubmitted", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderSubmitted));
        WriteGlobalRow(t, dt, 2, "SenderFramesWithAdditional", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderFramesWithAdditional));
        WriteGlobalRow(t, dt, 3, "SenderFramesKeyframe", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderFramesKeyframe));
        WriteGlobalRow(t, dt, 4, "SenderFramesDelta", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderFramesDelta));
        WriteGlobalRow(t, dt, 5, "SenderSubmitFailedNoTransmitter", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderSubmitFailedNoTransmitter));
        WriteGlobalRow(t, dt, 6, "ReceiverFramesWithAdditional", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.ReceiverFramesWithAdditional));
        WriteGlobalRow(t, dt, 7, "ReceiverEntriesDispatched", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.ReceiverEntriesDispatched));
        WriteGlobalRow(t, dt, 8, "ReceiverDroppedLinkedIndex", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.ReceiverDroppedLinkedIndex));
        WriteGlobalRow(t, dt, 9, "ReceiverDroppedNoBehaviours", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.ReceiverDroppedNoBehaviours));
        WriteGlobalRow(t, dt, 10, "ReceiverDroppedStaleOnDrain", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.ReceiverDroppedStaleOnDrain));
        WriteGlobalRow(t, dt, 11, "ReceiverEntriesSkippedEmpty", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.ReceiverEntriesSkippedEmpty));
        WriteGlobalRow(t, dt, 12, "ReceiverEntriesSkippedIndex", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.ReceiverEntriesSkippedIndex));
        WriteGlobalRow(t, dt, 13, "SenderAvatarChannelSent", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderAvatarChannelSent));
        WriteGlobalRow(t, dt, 14, "ReceiverAvatarChannelDispatched", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.ReceiverAvatarChannelDispatched));
        WriteGlobalRow(t, dt, 15, "ReceiverAvatarChannelDeferred", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.ReceiverAvatarChannelDeferred));
        WriteGlobalRow(t, dt, 16, "ReceiverAvatarChannelDropped", System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.ReceiverAvatarChannelDropped));
        WriteGlobalRow(t, dt, 17, "HvrStoreSubmits", System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrStoreSubmits));
        WriteGlobalRow(t, dt, 18, "HvrStoreSubmitsNoListener", System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrStoreSubmitsNoListener));
        WriteGlobalRow(t, dt, 19, "HvrWearerAddressUpdates", System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerAddressUpdates));
        WriteGlobalRow(t, dt, 20, "HvrWearerNewValues", System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerNewValues));
        WriteGlobalRow(t, dt, 21, "HvrWearerTicks", System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerTicks));
        WriteGlobalRow(t, dt, 22, "HvrWearerTicksWithValues", System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerTicksWithValues));
        WriteGlobalRow(t, dt, 23, "HvrActivitySamples", System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrActivitySamples));

        var localBehaviours = Application.isPlaying ? BasisNetworkPlayer.LocalPlayer?.NetworkBehaviours : null;
        var sent = BasisAdditionalDataDebugCapture.Sent;
        for (int Index = 0; Index < sent.Length; Index++)
        {
            var slot = sent[Index];
            if (slot == null) continue;
            WriteSlotRow(t, dt, "out", string.Empty, string.Empty, Index, BehaviourName(localBehaviours, Index), slot, (20L << 55) | (long)Index, now);
        }
        var sentCh15 = BasisAdditionalDataDebugCapture.SentCh15;
        for (int Index = 0; Index < sentCh15.Length; Index++)
        {
            var slot = sentCh15[Index];
            if (slot == null) continue;
            WriteSlotRow(t, dt, "out15", string.Empty, string.Empty, Index, BehaviourName(localBehaviours, Index), slot, (22L << 55) | (long)Index, now);
        }

        var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        int receiverCount = BasisNetworkPlayers.ReceiverCount;
        foreach (var pair in BasisAdditionalDataDebugCapture.Players)
        {
            ushort playerId = pair.Key;
            var pc = pair.Value;

            BasisNetworkReceiver receiver = null;
            for (int Index = 0; Index < receiverCount && Index < snapshot.Length; Index++)
            {
                if (snapshot[Index] != null && snapshot[Index].playerId == playerId) { receiver = snapshot[Index]; break; }
            }
            string name = Csv(receiver != null ? receiver.displayName : "(gone)");

            long keyBase = (21L << 55) | ((long)playerId << 32);
            long frames = System.Threading.Interlocked.Read(ref pc.FramesWithSection);
            long dispatched = System.Threading.Interlocked.Read(ref pc.EntriesDispatched);
            _csv.WriteLine($"{t},player,{playerId},{name},,,,,,,,," +
                $"{frames},{CsvRate(keyBase | 1, frames, dt):F1}," +
                $"{dispatched},{CsvRate(keyBase | 2, dispatched, dt):F1}," +
                $"{System.Threading.Interlocked.Read(ref pc.DroppedLinkedIndex)}," +
                $"{System.Threading.Interlocked.Read(ref pc.SkippedEmpty)}," +
                $"{System.Threading.Interlocked.Read(ref pc.SkippedIndex)},");

            for (int Index = 0; Index < pc.Slots.Length; Index++)
            {
                var slot = pc.Slots[Index];
                if (slot == null) continue;
                WriteSlotRow(t, dt, "in", playerId.ToString(), name, Index, BehaviourName(receiver?.NetworkBehaviours, Index), slot, keyBase | 0x10000L | (long)Index, now);
            }
            for (int Index = 0; Index < pc.SlotsCh15.Length; Index++)
            {
                var slot = pc.SlotsCh15[Index];
                if (slot == null) continue;
                WriteSlotRow(t, dt, "in15", playerId.ToString(), name, Index, BehaviourName(receiver?.NetworkBehaviours, Index), slot, keyBase | 0x20000L | (long)Index, now);
            }
        }
        _csv.Flush();
    }

    private void WriteGlobalRow(string t, float dt, long key, string counter, long value)
    {
        _csv.WriteLine($"{t},global,,,,{counter},{value},{CsvRate((19L << 55) | key, value, dt):F1},,,,,,,,,,,,");
    }

    private void WriteSlotRow(string t, float dt, string kind, string playerId, string playerName, int index, string behaviour, BasisAdditionalDataDebugCapture.Slot slot, long rateKey, double now)
    {
        long count = System.Threading.Interlocked.Read(ref slot.Count);
        long changed = System.Threading.Interlocked.Read(ref slot.ChangedCount);

        _hex.Clear();
        int previewSize = Mathf.Min(slot.PreviewSize, BasisAdditionalDataDebugCapture.PayloadPreviewBytes);
        for (int Index = 0; Index < previewSize; Index++) _hex.Append(slot.Preview[Index].ToString("X2"));

        _csv.WriteLine($"{t},{kind},{playerId},{playerName},{index},{Csv(behaviour)}," +
            $"{count},{CsvRate(rateKey, count, dt):F1}," +
            $"{changed},{CsvRate(rateKey | (1L << 62), changed, dt):F1}," +
            $"{slot.LastSize},{now - slot.LastTime:F1},,,,,,,,{_hex}");
    }

    private static string Csv(string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : value.Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ').Replace('"', ' ');
    }
}
