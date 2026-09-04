using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using Unity.Mathematics;

namespace Basis.BasisUI
{
    public static class BasisVoiceRangePanelUpdater
    {
        private const int RefreshIntervalTicks = 6;
        private const int MaxRows = 50;

        private static PanelElementDescriptor _statusField;
        private static readonly StringBuilder _builder = new StringBuilder(512);
        private static string _lastText;
        private static int _tickCounter;
        private static bool _subscribed;

        public static void Attach(PanelElementDescriptor statusField)
        {
            _statusField = statusField;
            if (_statusField != null) _statusField.DisableRichText();
            _lastText = null;
            _tickCounter = 0;

            if (!_subscribed)
            {
                BasisFrameClock.OnTick += OnTick;
                BasisFrameClock.AddRequest();
                _subscribed = true;
            }

            Refresh();
        }

        public static void Detach()
        {
            if (_subscribed)
            {
                BasisFrameClock.OnTick -= OnTick;
                BasisFrameClock.RemoveRequest();
                _subscribed = false;
            }
            _statusField = null;
            _lastText = null;
            _tickCounter = 0;
        }

        private static void OnTick()
        {
            if (_statusField == null)
            {
                BasisFrameClock.OnTick -= OnTick;
                BasisFrameClock.RemoveRequest();
                _subscribed = false;
                return;
            }

            if (!_statusField.gameObject.activeInHierarchy) return;

            if (++_tickCounter < RefreshIntervalTicks) return;
            _tickCounter = 0;
            Refresh();
        }

        private static void Refresh()
        {
            if (_statusField == null) return;

            BasisNetworkReceiver[] snapshot = BasisNetworkPlayers.ReceiversSnapshot;
            int count = BasisNetworkPlayers.ReceiverCount;

            float hearingSq = SMModuleDistanceBasedReductions.HearingRange;
            float shoutHearingSq = hearingSq * BasisShout.RangeMultiplierSquared;
            float micSq = SMModuleDistanceBasedReductions.MicrophoneRange
                * (BasisTalkModeManager.LocalIsShouting ? BasisShout.RangeMultiplierSquared : 1f);
            float3 head = BasisLocalCameraDriver.HeadPosition;

            int players = 0;
            int hearCount = 0;
            int micCount = 0;
            int shown = 0;

            _builder.Clear();

            for (int i = 0; i < count && i < snapshot.Length; i++)
            {
                BasisNetworkReceiver receiver = snapshot[i];
                if (receiver == null) continue;
                BasisRemotePlayer remote = receiver.RemotePlayer;
                if (remote == null) continue;

                players++;

                bool hasPos = RemoteBoneJobSystem.GetOutGoingMouth(receiver.playerId, out float3 mouth);
                bool inHearing = false;
                bool inMic = false;
                float meters = -1f;
                if (hasPos)
                {
                    float d2 = math.lengthsq(mouth - head);
                    meters = math.sqrt(d2);
                    inHearing = d2 < (remote.TalkMode == BasisTalkMode.Shout ? shoutHearingSq : hearingSq);
                    inMic = d2 < micSq;
                }

                if (inHearing) hearCount++;
                if (inMic) micCount++;

                if (shown < MaxRows)
                {
                    shown++;
                    string name = remote.SafeDisplayName;
                    if (string.IsNullOrEmpty(name)) name = "Player " + receiver.playerId;
                    _builder.Append(name);
                    if (meters >= 0f) _builder.Append("  ").Append(meters.ToString("0.0")).Append('m');
                    else _builder.Append("  (no data)");
                    _builder.Append("  hear:").Append(inHearing ? 'Y' : 'N');
                    _builder.Append("  mic:").Append(inMic ? 'Y' : 'N');
                    _builder.Append('\n');
                }
            }

            string text;
            if (players == 0)
            {
                text = BasisLocalization.Get("settings.developer.voiceRange.empty");
            }
            else
            {
                if (shown < players)
                {
                    _builder.Append(BasisLocalization.Get("settings.developer.voiceRange.more", players - shown));
                    _builder.Append('\n');
                }
                text = BasisLocalization.Get("settings.developer.voiceRange.summary", hearCount, micCount, players)
                    + "\n\n" + _builder.ToString();
            }

            if (!string.Equals(text, _lastText))
            {
                _lastText = text;
                _statusField.SetDescription(text);
                _statusField.ForceRebuild();
            }
        }
    }
}
