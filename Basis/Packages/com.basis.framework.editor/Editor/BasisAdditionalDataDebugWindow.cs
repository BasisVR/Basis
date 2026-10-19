using Basis.Editor.Localization;
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

    [MenuItem("Basis/Debug/Face Tracking Data", false, 605)]
    public static void ShowWindow()
    {
        var w = GetWindow<BasisAdditionalDataDebugWindow>(BasisEditorLocalization.Get("editor.additionalData.windowTitle"));
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
        BasisEditorUI.Header(BasisEditorLocalization.Get("editor.additionalData.title"),
            BasisEditorLocalization.Get("editor.additionalData.subtitle"));

        double now = BasisAdditionalDataDebugCapture.Now;
        _sampleThisFrame = now - _lastSample >= 0.5;
        float dt = (float)(now - _lastSample);
        if (_sampleThisFrame) _lastSample = now;

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.additionalData.pipelineTitle"));
        BasisEditorUI.Note(BasisEditorLocalization.Get("editor.additionalData.pipelineFlow"));

        EditorGUILayout.BeginHorizontal();
        if (_csv == null)
        {
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.additionalData.recordCsv"), GUILayout.Width(110))) StartCsv();
            }
        }
        else
        {
            var prev = GUI.color;
            GUI.color = ErrorColor;
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.additionalData.stopRecording", now - _csvStart), GUILayout.Width(110))) StopCsv();
            GUI.color = prev;
        }
        if (!string.IsNullOrEmpty(_csvPath))
        {
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.additionalData.openFolder"), GUILayout.Width(90)))
            {
                EditorUtility.RevealInFinder(_csvPath);
            }
            EditorGUILayout.LabelField(_csvPath, EditorStyles.miniLabel);
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(BasisEditorLocalization.Get("editor.additionalData.clearCounters"), GUILayout.Width(110)))
        {
            BasisAdditionalDataDebugCapture.Clear();
            _prevCounts.Clear();
            _rates.Clear();
            _csvPrev.Clear();
        }
        EditorGUILayout.EndHorizontal();

        if (!Application.isPlaying)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.additionalData.enterPlayMode"), MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        EditorGUILayout.Space(4);
        _showOutgoing = EditorGUILayout.BeginFoldoutHeaderGroup(_showOutgoing, BasisEditorLocalization.Get("editor.additionalData.outgoing"));
        if (_showOutgoing) DrawOutgoing(now, dt);
        EditorGUILayout.EndFoldoutHeaderGroup();

        EditorGUILayout.Space(4);
        _showIncoming = EditorGUILayout.BeginFoldoutHeaderGroup(_showIncoming, BasisEditorLocalization.Get("editor.additionalData.incoming"));
        if (_showIncoming) DrawIncoming(now, dt);
        EditorGUILayout.EndFoldoutHeaderGroup();

        EditorGUILayout.EndScrollView();
    }

    private void DrawOutgoing(double now, float dt)
    {
        float submittedRate = Rate(1L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderSubmitted), dt);
        float attachedRate = Rate(2L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderFramesWithAdditional), dt);

        float avatarChRate = Rate(5L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderAvatarChannelSent), dt);
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.senderStats", submittedRate, attachedRate, avatarChRate,
            System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderFramesKeyframe),
            System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderFramesDelta),
            System.Threading.Interlocked.Read(ref BasisAdditionalDataDiagnostics.SenderSubmitFailedNoTransmitter)));

        float storeSubmitRate = Rate(6L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrStoreSubmits), dt);
        float noListenerRate = Rate(7L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrStoreSubmitsNoListener), dt);
        float addrUpdateRate = Rate(8L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerAddressUpdates), dt);
        float newValueRate = Rate(9L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerNewValues), dt);
        float tickRate = Rate(10L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerTicks), dt);
        float tickWithValuesRate = Rate(11L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrWearerTicksWithValues), dt);
        float activityRate = Rate(12L << 60, System.Threading.Interlocked.Read(ref BasisAdditionalDataDebugCapture.HvrActivitySamples), dt);
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.wearerStats", storeSubmitRate, noListenerRate, addrUpdateRate, newValueRate, tickRate, tickWithValuesRate, activityRate));

        if (storeSubmitRate <= 0f && noListenerRate <= 0f && activityRate <= 0f)
        {
            DrawStatus(BasisEditorLocalization.Get("editor.additionalData.noStoreTraffic"), ErrorColor);
        }
        else if (storeSubmitRate > 0f && addrUpdateRate <= 0f)
        {
            DrawStatus(BasisEditorLocalization.Get("editor.additionalData.storeNotNetworked"), ErrorColor);
        }
        else if (addrUpdateRate > 0f && newValueRate <= 0f)
        {
            DrawStatus(BasisEditorLocalization.Get("editor.additionalData.valuesFrozen"), WarnColor);
        }
        else if (newValueRate > 0f && tickRate <= 0f)
        {
            DrawStatus(BasisEditorLocalization.Get("editor.additionalData.tickDead"), ErrorColor);
        }

        if (submittedRate > 0f && attachedRate <= 0f)
        {
            DrawStatus(BasisEditorLocalization.Get("editor.additionalData.senderBroken"), ErrorColor);
        }

        var localBehaviours = BasisNetworkPlayer.LocalPlayer?.NetworkBehaviours;
        bool any = false;
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.reductionSystemOutgoing"), EditorStyles.miniBoldLabel);
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
            DrawStatus(BasisEditorLocalization.Get("editor.additionalData.nothingSubmitted"), WarnColor);
        }

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.avatarChannelOutgoing"), EditorStyles.miniBoldLabel);
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
            DrawStatus(BasisEditorLocalization.Get("editor.additionalData.noChannelSends"), WarnColor);
        }
    }

    private void DrawIncoming(double now, float dt)
    {
        var players = BasisAdditionalDataDebugCapture.Players;
        if (players.IsEmpty)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.additionalData.noRemoteData"), MessageType.Info);
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

            string title = receiver != null ? BasisEditorLocalization.Get("editor.additionalData.playerTitle", playerId, receiver.displayName) : BasisEditorLocalization.Get("editor.additionalData.playerNoReceiver", playerId);
            if (!_playerFoldouts.TryGetValue(playerId, out bool open)) open = true;
            _playerFoldouts[playerId] = EditorGUILayout.Foldout(open, title, true);
            if (!_playerFoldouts[playerId]) continue;

            EditorGUI.indentLevel++;
            long keyBase = (long)playerId << 32;
            float frameRate = Rate(keyBase | 1, System.Threading.Interlocked.Read(ref pc.FramesWithSection), dt);
            float dispatchRate = Rate(keyBase | 2, System.Threading.Interlocked.Read(ref pc.EntriesDispatched), dt);
            float gateRate = Rate(keyBase | 3, System.Threading.Interlocked.Read(ref pc.DroppedLinkedIndex), dt);

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.playerStats", frameRate, dispatchRate,
                System.Threading.Interlocked.Read(ref pc.DroppedLinkedIndex),
                System.Threading.Interlocked.Read(ref pc.SkippedEmpty),
                System.Threading.Interlocked.Read(ref pc.SkippedIndex)));

            if (frameRate > 0f && dispatchRate <= 0f)
            {
                string message = gateRate > 0f
                    ? BasisEditorLocalization.Get("editor.additionalData.receiverBrokenGate")
                    : BasisEditorLocalization.Get("editor.additionalData.receiverBrokenSkipped");
                DrawStatus(message, ErrorColor);
            }
            else if (frameRate <= 0f && now - pc.LastFrameTime > 3.0)
            {
                DrawStatus(BasisEditorLocalization.Get("editor.additionalData.noSectionsFromPlayer", now - pc.LastFrameTime), WarnColor);
            }

            bool anySlot = false;
            var behaviours = receiver?.NetworkBehaviours;
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.reductionSystemIncoming"), EditorStyles.miniBoldLabel);
            DrawSlotHeader();
            for (int Index = 0; Index < pc.Slots.Length; Index++)
            {
                var slot = pc.Slots[Index];
                if (slot == null) continue;
                anySlot = true;
                DrawSlotRow((byte)Index, slot, keyBase | (0x100L << 16) | (long)Index, now, dt, BehaviourName(behaviours, Index));
            }
            if (!anySlot) BasisEditorUI.Note(BasisEditorLocalization.Get("editor.additionalData.noEntriesYet"));

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.avatarChannelIncoming"), EditorStyles.miniBoldLabel);
            bool anyCh15Slot = false;
            DrawSlotHeader();
            for (int Index = 0; Index < pc.SlotsCh15.Length; Index++)
            {
                var slot = pc.SlotsCh15[Index];
                if (slot == null) continue;
                anyCh15Slot = true;
                DrawSlotRow((byte)Index, slot, keyBase | (0x200L << 16) | (long)Index, now, dt, BehaviourName(behaviours, Index));
            }
            if (!anyCh15Slot) BasisEditorUI.Note(BasisEditorLocalization.Get("editor.additionalData.noCh15Yet"));
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }
    }

    private static void DrawSlotHeader()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.colIdx"), EditorStyles.miniBoldLabel, GUILayout.Width(30));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.colBehaviour"), EditorStyles.miniBoldLabel, GUILayout.Width(180));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.colRate"), EditorStyles.miniBoldLabel, GUILayout.Width(50));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.colChanged"), EditorStyles.miniBoldLabel, GUILayout.Width(50));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.colSize"), EditorStyles.miniBoldLabel, GUILayout.Width(40));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.colAge"), EditorStyles.miniBoldLabel, GUILayout.Width(40));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.additionalData.colPayload"), EditorStyles.miniBoldLabel);
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
            DrawStatus(BasisEditorLocalization.Get("editor.additionalData.payloadFrozen"), WarnColor);
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
