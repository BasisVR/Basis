using System.Collections.Generic;
using System.Globalization;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.BasisUI
{
    public partial class IndividualPlayerProvider
    {
        internal sealed class ChatHistoryView
        {
            public PanelElementDescriptor Group;
            public PanelElementDescriptor Status;
            public RectTransform RebuildStopAt;
            public readonly List<PanelElementDescriptor> Rows = new List<PanelElementDescriptor>(BasisChatHistory.Capacity);
            public int PaintedVersion = -1;
            public string PaintedStatusKey;
        }

        private static ChatHistoryView BuildChatHistory(RectTransform pageContent)
        {
            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, pageContent);
            group.SetTitle(BasisLocalization.Get("menu.individualPlayer.chatHistory"));
            group.SetDescription(BasisLocalization.Get("menu.individualPlayer.chatHistory.description", BasisChatHistory.Capacity));

            PanelElementDescriptor status = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
            status.SetTitle(string.Empty);

            return new ChatHistoryView { Group = group, Status = status, RebuildStopAt = pageContent };
        }

        private static string ChatHistoryStatusKey(BasisRemotePlayer player)
        {
            if (BasisSettingsDefaults.ChatDisabled.RawValue)
            {
                return "menu.individualPlayer.chatHistory.disabled";
            }

            if (player == null)
            {
                return "menu.individualPlayer.chatHistory.empty";
            }

            if (player.IsEffectivelyBlocked)
            {
                return "menu.individualPlayer.chatHistory.blocked";
            }

            if (BasisPlayerSettingsManager.TryGetCached(player.UUID, out BasisPlayerSettingsData settings) && !settings.ChatVisible)
            {
                return "menu.individualPlayer.chatHistory.hidden";
            }

            return player.ChatHistory.Count == 0 ? "menu.individualPlayer.chatHistory.empty" : null;
        }

        internal static void PaintChatHistory(BasisRemotePlayer player, ChatHistoryView view)
        {
            if (view == null || view.Group == null)
            {
                return;
            }

            string statusKey = ChatHistoryStatusKey(player);
            int version = player != null ? player.ChatHistory.Version : 0;
            if (version == view.PaintedVersion && statusKey == view.PaintedStatusKey)
            {
                return;
            }

            view.PaintedVersion = version;
            view.PaintedStatusKey = statusKey;

            if (statusKey != null)
            {
                view.Status.SetDescription(BasisLocalization.Get(statusKey));
            }
            view.Status.SetActive(statusKey != null);

            BasisChatHistory history = statusKey == null ? player.ChatHistory : null;
            int shown = history != null ? history.Count : 0;
            for (int i = 0; i < shown; i++)
            {
                if (i == view.Rows.Count)
                {
                    PanelElementDescriptor created = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, view.Group.ContentParent);
                    created.DisableRichText();
                    view.Rows.Add(created);
                }

                BasisChatHistory.Entry entry = history[history.Count - 1 - i];
                PanelElementDescriptor row = view.Rows[i];
                row.SetTitle(entry.ReceivedUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
                row.SetDescription(entry.Message.Replace("\r", string.Empty).Replace('\n', ' '));
                row.SetActive(true);
            }

            for (int i = shown; i < view.Rows.Count; i++)
            {
                view.Rows[i].SetActive(false);
            }

            PanelElementDescriptor.RebuildLayoutChain(view.Group.ContentParent, view.RebuildStopAt);
        }
    }
}
