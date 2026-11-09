using Basis.Scripts.Device_Management;
using System;
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Asks the local player before content someone else owns (a prop another player spawned, or another
    /// player's avatar) may lock their movement, view or crouching. One prompt at a time, the accept button
    /// arms after a second, and declines are counted per player: a cooldown after each, muted for the
    /// session after a few, so spawning more content does not get around it.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisPlayerLockRequests
    {
        public enum Result
        {
            Accepted = 0,
            Declined = 1,
            Blocked = 2,
            Busy = 3,
            Unsupported = 4,
        }

        [Flags]
        public enum Kinds
        {
            None = 0,
            Movement = 1,
            LookRotation = 2,
            Crouching = 4,
        }

        public const float DeclineCooldownSeconds = 30f;
        public const int DeclinesBeforeMute = 3;

        private sealed class Strike
        {
            public int Count;
            public float Until;
        }

        private sealed class Ask
        {
            public string OwnerId;
            public string Title;
            public string Body;
            public Action<Result> OnResult;
            public bool Done;
        }

        private static readonly Dictionary<string, Strike> strikes = new Dictionary<string, Strike>();
        private static Ask current;

        public static bool IsAsking => current != null;

        public static bool IsMuted(string ownerId)
        {
            return !string.IsNullOrEmpty(ownerId) && strikes.TryGetValue(ownerId, out Strike strike) && (strike.Count >= DeclinesBeforeMute || Time.realtimeSinceStartup < strike.Until);
        }

        public static void Request(string ownerId, string ownerName, string contentName, bool avatar, Kinds kinds, Action<Result> onResult)
        {
#if UNITY_SERVER
            Deliver(onResult, Result.Unsupported);
#else
            if (string.IsNullOrEmpty(ownerId) || kinds == Kinds.None || IsMuted(ownerId))
            {
                Deliver(onResult, Result.Blocked);
                return;
            }
            if (current != null)
            {
                Deliver(onResult, Result.Busy);
                return;
            }
            string who = BasisExternalLinks.CleanName(ownerName);
            if (who.Length == 0) who = BasisLocalization.Get("externalLinks.unnamed");
            string what = BasisExternalLinks.CleanName(contentName);
            if (what.Length == 0) what = BasisLocalization.Get("externalLinks.unnamed");
            string kind = BasisLocalization.Get(avatar ? "playerLocks.kind.avatar" : "externalLinks.kind.prop");
            Ask ask = new Ask
            {
                OwnerId = ownerId,
                Title = BasisLocalization.Get("playerLocks.title"),
                Body = BasisLocalization.Get("playerLocks.body", what, kind, who, Describe(kinds)),
                OnResult = onResult,
            };
            current = ask;
            try
            {
                Show(ask);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[PlayerLocks] Could not ask about a lock: {e}");
                Finish(ask, Result.Unsupported);
            }
#endif
        }

        /// <summary>Drops the open question, if any, as declined without a strike. Used when the player respawns.</summary>
        public static void Cancel()
        {
            Ask ask = current;
            if (ask == null) return;
            ask.Done = true;
            current = null;
            Deliver(ask.OnResult, Result.Declined);
        }

        public static string Describe(Kinds kinds)
        {
            List<string> parts = new List<string>(3);
            if ((kinds & Kinds.Movement) != 0) parts.Add(BasisLocalization.Get("playerLocks.movement"));
            if ((kinds & Kinds.LookRotation) != 0) parts.Add(BasisLocalization.Get("playerLocks.look"));
            if ((kinds & Kinds.Crouching) != 0) parts.Add(BasisLocalization.Get("playerLocks.crouch"));
            return string.Join(", ", parts);
        }

        private static void Show(Ask ask)
        {
            if (ask.Done) return;
            if (BasisNotificationCenter.RouteToNotifications)
            {
                Defer(ask);
                return;
            }
            if (!BasisMainMenu.Instance) BasisMainMenu.Open();
            if (!BasisMainMenu.Instance)
            {
                Defer(ask);
                return;
            }
            if (BasisMainMenu.Instance.Dialogue && !BasisMainMenu.Instance.Dialogue.IsReleased)
            {
                BasisMainMenu.Instance.Dialogue.OnInstanceReleased += () => BasisDeviceManagement.EnqueueOnMainThread(() => Show(ask));
                return;
            }

            bool answered = false;
            BasisMenuDialoguePanel panel = BasisMenuDialoguePanel.CreateNew(
                ask.Title,
                ask.Body,
                BasisLocalization.Get("playerLocks.accept"),
                BasisLocalization.Get("ui.cancel"),
                accepted =>
                {
                    answered = true;
                    Finish(ask, accepted ? Result.Accepted : Result.Declined);
                },
                false,
                BasisPanelSeverity.Caution,
                BasisNotificationCategory.Content);
            if (panel == null)
            {
                Defer(ask);
                return;
            }

            BasisMainMenu.Instance.Dialogue = panel;
            panel.CaptureOnClose = false;
            BasisPromptArming.Arm(panel.AcceptButton);
            panel.OnInstanceReleased += () =>
            {
                if (!answered) Defer(ask);
            };
        }

        private static void Defer(Ask ask)
        {
            if (ask.Done) return;
            BasisNotificationCenter.AddPending(
                ask.Title,
                ask.Body,
                AddressableAssets.Sprites.Locked,
                reopen: () => Show(ask),
                onDismiss: () => Finish(ask, Result.Declined),
                category: BasisNotificationCategory.Content);
        }

        private static void Finish(Ask ask, Result result)
        {
            if (ask.Done) return;
            ask.Done = true;
            if (ReferenceEquals(current, ask)) current = null;
            if (result == Result.Declined) StrikeOut(ask.OwnerId);
            else if (result == Result.Accepted) strikes.Remove(ask.OwnerId);
            Deliver(ask.OnResult, result);
        }

        private static void StrikeOut(string ownerId)
        {
            if (string.IsNullOrEmpty(ownerId)) return;
            if (!strikes.TryGetValue(ownerId, out Strike strike))
            {
                strike = new Strike();
                strikes[ownerId] = strike;
            }
            strike.Count++;
            strike.Until = Time.realtimeSinceStartup + DeclineCooldownSeconds;
        }

        private static void Deliver(Action<Result> onResult, Result result)
        {
            if (onResult == null) return;
            try
            {
                onResult(result);
            }
            catch (Exception e)
            {
                BasisDebug.LogErrorUnreported($"[PlayerLocks] A lock request callback threw: {e}");
            }
        }
    }
}
