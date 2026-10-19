using System;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using static Basis.BasisUI.PanelButton;

namespace Basis.BasisUI
{
    /// <summary>
    /// User-driven cache invalidation for content published to a STATIC url.
    ///
    /// <para>Cache identity is the url, so re-uploading a bee to the same address is invisible to a
    /// client that already has it — the load path never touches the network again. This asks the
    /// host whether the bytes changed (one small conditional request, no payload), and on a change
    /// evicts the cached copy so the next load fetches fresh.</para>
    ///
    /// <para>Only the person who published the content knows they changed it, which is why this is
    /// a button rather than something automatic: the check is cheap but not free, and doing it on
    /// every load would add a round trip to every avatar in the instance.</para>
    /// </summary>
    public class LibraryProviderDialogCheckForUpdate
    {
        /// <summary>
        /// True when an update check is meaningful for this item. Embedded items ship inside the
        /// build and local bee files are read straight off disk — neither is cached by url, so
        /// there is nothing to invalidate.
        /// </summary>
        public static bool IsSupported(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item == null || item.EmbeddedSettings.IsEmbedded || string.IsNullOrWhiteSpace(item.Url))
            {
                return false;
            }
            if (BasisIOManagement.IsLocalBeeUrl(item.Url))
            {
                return false;
            }
            return Uri.TryCreate(item.Url, UriKind.Absolute, out Uri uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// Runs the check and, when the content changed, refreshes it. Returns true when something
        /// was actually invalidated, so the caller can rebuild the library card.
        /// </summary>
        public static async Task<bool> PromptUserForUpdateCheck(BasisMenuPanel panel, BasisDataStoreItemKeys.ItemKey item, BasisBundleDescription description)
        {
            string title = description?.AssetBundleName ?? item.Url;

            DialogBox<bool> checkingDialog = DialogBox<bool>.Create(panel, new Vector2(830, 150),
                BasisLocalization.Get("library.dialog.checkForUpdate.title", title),
                BasisLocalization.Get("library.dialog.checkForUpdate.checking"),
                AddressableAssets.Sprites.HourGlass,
                true
            );

            BasisContentVersion.UpdateCheckResult result;
            try
            {
                result = await BasisContentVersion.CheckForUpdateAsync(item.Url);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Update check failed for {item.Url}: {ex}");
                result = new BasisContentVersion.UpdateCheckResult(false, false, false, null, ex.Message);
            }

            checkingDialog.CloseWithResult(true);

            if (!result.Succeeded)
            {
                await ShowNotice(panel,
                    BasisLocalization.Get("library.dialog.checkForUpdate.failed.title"),
                    BasisLocalization.Get("library.dialog.checkForUpdate.failed.body", result.Error ?? string.Empty));
                return false;
            }

            // Host publishes neither ETag nor Last-Modified, so a change is undetectable here. Offer
            // the blunt instrument instead of silently claiming the copy is current.
            if (result.VersioningUnavailable)
            {
                bool forceRefresh = await PromptYesNo(panel,
                    BasisLocalization.Get("library.dialog.checkForUpdate.unsupported.title"),
                    BasisLocalization.Get("library.dialog.checkForUpdate.unsupported.body"));

                if (!forceRefresh)
                {
                    return false;
                }

                return await ApplyRefresh(panel, item, title, result.ObservedTag);
            }

            // First ever check on this entry: there was no recorded version, so "unchanged" is an
            // assumption rather than a comparison. Say that plainly and let the user force a refresh,
            // because a re-upload made before this check looks identical to no change at all.
            if (result.BaselineEstablished)
            {
                bool forceRefresh = await PromptYesNo(panel,
                    BasisLocalization.Get("library.dialog.checkForUpdate.baseline.title"),
                    BasisLocalization.Get("library.dialog.checkForUpdate.baseline.body", title));

                if (!forceRefresh)
                {
                    return false;
                }

                return await ApplyRefresh(panel, item, title, result.ObservedTag);
            }

            if (!result.HasUpdate)
            {
                await ShowNotice(panel,
                    BasisLocalization.Get("library.dialog.checkForUpdate.upToDate.title"),
                    BasisLocalization.Get("library.dialog.checkForUpdate.upToDate.body", title));
                return false;
            }

            bool confirmed = await PromptYesNo(panel,
                BasisLocalization.Get("library.dialog.checkForUpdate.available.title"),
                BasisLocalization.Get("library.dialog.checkForUpdate.available.body", title));

            if (!confirmed)
            {
                return false;
            }

            return await ApplyRefresh(panel, item, title, result.ObservedTag);
        }

        /// <summary>
        /// Evicts the cached copy and rebuilds the library's view of the item. When the item is the
        /// avatar currently being worn it is re-equipped, which also rebroadcasts the new version to
        /// everyone else (OnAvatarSwitched drives SendOutAvatarChange) so their caches invalidate too.
        /// </summary>
        private static async Task<bool> ApplyRefresh(BasisMenuPanel panel, BasisDataStoreItemKeys.ItemKey item, string title, string checkedVersionTag)
        {
            bool wasWorn = IsCurrentlyWornAvatar(item);

            if (!BasisContentVersion.Invalidate(item.Url))
            {
                BasisDebug.LogWarning($"Nothing cached to invalidate for {item.Url}; it will be fetched on next load anyway.", BasisDebug.LogTag.Event);
            }

            // The library keeps its own parsed copy of the connector (name, thumbnail, date), which
            // still describes the old bytes. Drop it so the card is rebuilt from the fresh download.
            CachedMetaData.RemoveMetaData(item.Url);

            DialogBox<bool> refreshingDialog = DialogBox<bool>.Create(panel, new Vector2(830, 150),
                BasisLocalization.Get("library.dialog.checkForUpdate.refreshing.title"),
                BasisLocalization.Get("library.dialog.checkForUpdate.refreshing.body", title),
                AddressableAssets.Sprites.HourGlass,
                true
            );

            try
            {
                await CachedMetaData.PreloadMetaDataForItem(item);

                // A host can expose Last-Modified on the HEAD used by the update check but omit it
                // from the ranged GET used to download the connector. In that case the freshly
                // rebuilt cache has no validator unless we carry forward the one the host reported
                // moments ago. Prefer a tag observed by the download when it has one: the remote
                // may have changed between the check and the download, so the downloaded bytes'
                // own validator is the authoritative value.
                string refreshedVersionTag = await BasisContentVersion.GetCachedTagAsync(item.Url);
                if (string.IsNullOrWhiteSpace(refreshedVersionTag) && !string.IsNullOrWhiteSpace(checkedVersionTag))
                {
                    refreshedVersionTag = checkedVersionTag.Trim();
                    await BasisContentVersion.MarkValidatedAsync(item.Url, refreshedVersionTag);
                }

                // PreloadMetaDataForItem creates its in-memory wrapper before the fallback above is
                // persisted. Keep that wrapper in sync as well, otherwise wearing/sharing the item
                // in this session would still advertise an empty version tag.
                if (!string.IsNullOrWhiteSpace(refreshedVersionTag) &&
                    CachedMetaData.TryGetMeta(item.Url, out CachedMetaData.CachedContent refreshedMeta) &&
                    refreshedMeta?.BasisLoadableBundle?.BasisRemoteBundleEncrypted != null)
                {
                    refreshedMeta.BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteVersionTag = refreshedVersionTag;
                }

                if (wasWorn)
                {
                    await LibraryProvider.LoadSelectedItem(item);
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Refresh after update check failed for {item.Url}: {ex}");
            }
            finally
            {
                refreshingDialog.CloseWithResult(true);
            }

            return true;
        }

        private static bool IsCurrentlyWornAvatar(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item.Mode != BundledContentHolder.Mode.Avatar || BasisLocalPlayer.Instance == null)
            {
                return false;
            }

            string worn = BasisLocalPlayer.Instance.AvatarMetaData?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation;
            return !string.IsNullOrEmpty(worn) &&
                   string.Equals(BasisIOManagement.CanonicalizeRemoteUrl(worn), BasisIOManagement.CanonicalizeRemoteUrl(item.Url), StringComparison.Ordinal);
        }

        /// <summary>
        /// A dialog box is a fixed size, but these bodies run to several sentences over two
        /// paragraphs — at a flat 200 the text ran straight through the yes/no row below it. Grows
        /// the box with the body instead of trusting one height to fit every message (and every
        /// translation of it): roughly 68 characters fit on a line at the description font size,
        /// and each explicit line break starts a new one.
        /// </summary>
        private static Vector2 SizeForBody(string body)
        {
            const float Width = 830f;
            const float CharactersPerLine = 68f;
            const float LineHeight = 28f;

            int lines = 0;
            string[] paragraphs = (body ?? string.Empty).Split('\n');
            for (int Index = 0; Index < paragraphs.Length; Index++)
            {
                lines += Mathf.Max(1, Mathf.CeilToInt(paragraphs[Index].Length / CharactersPerLine));
            }

            return new Vector2(Width, Mathf.Clamp(150f + lines * LineHeight, 200f, 600f));
        }

        private static async Task ShowNotice(BasisMenuPanel panel, string title, string body)
        {
            DialogBox<bool> noticeDialog = DialogBox<bool>.Create(panel, SizeForBody(body), title, body, AddressableAssets.Sprites.Information);

            if (noticeDialog.Descriptor != null)
            {
                PanelButton closeButton = PanelButton.CreateNew(ButtonStyles.AcceptButton, noticeDialog.Descriptor.ContentParent);
                closeButton.Descriptor.SetTitle(BasisLocalization.Get("ui.ok"));
                closeButton.Descriptor.SetWidth(200);
                closeButton.Descriptor.SetHeight(60);
                closeButton.OnClicked += () => noticeDialog.CloseWithResult(true);
            }

            await noticeDialog.WaitAsync();
        }

        private static async Task<bool> PromptYesNo(BasisMenuPanel panel, string title, string body)
        {
            DialogBox<bool> dialog = DialogBox<bool>.Create(panel, SizeForBody(body), title, body, AddressableAssets.Sprites.Information, true);

            if (dialog.Descriptor != null)
            {
                PanelTabGroup actionsPanel = PanelTabGroup.CreateNew(dialog.Descriptor.ContentParent, LayoutDirection.HorizontalNoBackground);
                actionsPanel.Descriptor.SetHeight(60);

                PanelButton noButton = PanelButton.CreateNew(ButtonStyles.CancelButton, actionsPanel.TabButtonParent);
                noButton.Descriptor.SetTitle(BasisLocalization.Get("ui.no"));
                noButton.Descriptor.SetWidth(200);
                noButton.Descriptor.SetHeight(60);
                noButton.OnClicked += () =>
                {
                    if (dialog.IsBusy) return;
                    dialog.IsBusy = true;
                    dialog.CloseWithResult(false);
                };

                PanelButton yesButton = PanelButton.CreateNew(ButtonStyles.AcceptButton, actionsPanel.TabButtonParent);
                yesButton.Descriptor.SetTitle(BasisLocalization.Get("ui.yes"));
                yesButton.Descriptor.SetWidth(200);
                yesButton.Descriptor.SetHeight(60);
                yesButton.OnClicked += () =>
                {
                    if (dialog.IsBusy) return;
                    dialog.IsBusy = true;
                    dialog.CloseWithResult(true);
                };
            }

            return await dialog.WaitAsync();
        }
    }
}
