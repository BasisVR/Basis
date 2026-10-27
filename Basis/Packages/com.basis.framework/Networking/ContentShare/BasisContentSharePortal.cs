using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using static SerializableBasis;

[AutoStaticsCleanup]
public static partial class BasisContentSharePortal
{
    public const string PortalEntryId = "basis-portal";
    public const int ProbeTimeoutMs = 3000;
    private static CancellationTokenSource probeCts;

    internal static void Present(BasisContentSphere sphere, string title, string sharerLine)
    {
        string sphereId = sphere.SphereNetID;
        bool canRemove = sphere.CanLocalPlayerRemove;
        bool readable = BasisConnectionService.TryBuildEntry(sphere.ContentURL, PortalEntryId, out ServerDirectoryEntry entry);
        ContentSharePortal.TryReadLifetime(sphere.UnlockPassword, out int lifetime);

        BasisMainMenu.Open();
        BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
        if (menu == null || menu.Dialogue != null) return;

        if (!readable)
        {
            menu.OpenDialogue(title, $"{sharerLine}\n\n{BasisLocalization.Get("content.share.portal.invalid")}", BasisLocalization.Get("ui.ok"), _ => { }, category: BasisNotificationCategory.Content);
            if (menu.Dialogue != null)
            {
                menu.Dialogue.CaptureOnClose = false;
                if (canRemove) menu.Dialogue.EnableAlternate(BasisLocalization.Get("library.delete"), () => BasisContentShareManager.RequestRemoveSphere(sphereId));
            }
            return;
        }

        string description = $"{sharerLine}\n\n{BasisLocalization.Get("content.share.portal.question")}";
        menu.OpenDialogue(title, description, BasisLocalization.Get("content.share.portal.go"), BasisLocalization.Get("ui.cancel"), accepted =>
        {
            probeCts?.Cancel();
            if (accepted) Go(entry);
        }, category: BasisNotificationCategory.Content);
        BasisMenuDialoguePanel dialogue = menu.Dialogue;
        if (dialogue == null) return;
        dialogue.CaptureOnClose = false;
        if (canRemove)
        {
            dialogue.EnableAlternate(BasisLocalization.Get("library.delete"), () => BasisContentShareManager.RequestRemoveSphere(sphereId));
        }

        string destination = Destination(entry);
        string lifetimeText = lifetime == ContentSharePortal.Persistent
            ? BasisLocalization.Get("content.share.portal.persistent")
            : BasisLocalization.Get("content.share.portal.timed", lifetime.ToString(CultureInfo.InvariantCulture));
        dialogue.ShowDetails(Rows(destination, lifetimeText, null, true));
        _ = ProbeAsync(dialogue, entry, destination, lifetimeText);
    }

    private static async Task ProbeAsync(BasisMenuDialoguePanel dialogue, ServerDirectoryEntry entry, string destination, string lifetimeText)
    {
        probeCts?.Cancel();
        CancellationTokenSource cts = new CancellationTokenSource();
        probeCts = cts;
        ServerProbeResult result;
        try
        {
            result = await BasisNetworkStackRegistry.ProbeAsync(entry.Target, ProbeTimeoutMs, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"Portal health check failed: {ex.Message}", BasisDebug.LogTag.Networking);
            result = null;
        }
        if (cts.IsCancellationRequested || dialogue == null || BasisMainMenu.Instance == null || BasisMainMenu.Instance.Dialogue != dialogue) return;
        dialogue.ShowDetails(Rows(destination, lifetimeText, result, false));
    }

    private static List<BasisMenuDialoguePanel.DetailRow> Rows(string destination, string lifetimeText, ServerProbeResult probe, bool checking)
    {
        List<BasisMenuDialoguePanel.DetailRow> rows = new List<BasisMenuDialoguePanel.DetailRow>(5)
        {
            new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("content.share.portal.destination"), destination, plainValue: true),
            new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("content.share.portal.lifetime"), lifetimeText),
        };
        string healthTitle = BasisLocalization.Get("content.share.portal.health");
        if (checking)
        {
            rows.Add(new BasisMenuDialoguePanel.DetailRow(healthTitle, BasisLocalization.Get("content.share.portal.checking")));
            return rows;
        }
        if (probe == null || !probe.Reachable)
        {
            rows.Add(new BasisMenuDialoguePanel.DetailRow(healthTitle, BasisLocalization.Get("content.share.portal.offline")));
            return rows;
        }
        rows.Add(new BasisMenuDialoguePanel.DetailRow(healthTitle, BasisLocalization.Get("content.share.portal.online", probe.Online.ToString(CultureInfo.InvariantCulture), probe.Max.ToString(CultureInfo.InvariantCulture), probe.RoundTripMs.ToString(CultureInfo.InvariantCulture))));
        if (!string.IsNullOrWhiteSpace(probe.Name))
        {
            rows.Add(new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("content.share.portal.name"), BasisContentShareText.Normalize(probe.Name), plainValue: true));
        }
        if (!string.IsNullOrWhiteSpace(probe.Motd))
        {
            rows.Add(new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("content.share.portal.motd"), BasisContentShareText.Normalize(probe.Motd), plainValue: true, wrap: true));
        }
        return rows;
    }

    private static string Destination(ServerDirectoryEntry entry)
    {
        string address = entry.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
        string port = entry.Target?.Get(ConnectionTarget.Keys.Port) ?? string.Empty;
        return string.IsNullOrEmpty(port) ? address : $"{address}:{port}";
    }

    private static void Go(ServerDirectoryEntry entry)
    {
        string userName = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.DisplayName : null;
        if (string.IsNullOrWhiteSpace(userName)) userName = BasisDataStore.LoadString(BasisConnectionService.UsernameFileName, string.Empty);
        if (string.IsNullOrWhiteSpace(userName))
        {
            BasisDebug.LogWarning("Portal needs a display name before it can connect.", BasisDebug.LogTag.Networking);
            return;
        }
        BasisDebug.Log($"Taking portal to {Destination(entry)}", BasisDebug.LogTag.Networking);
        BasisMainMenu.Close();
        _ = BasisConnectionService.ConnectAsync(entry, userName);
    }
}
