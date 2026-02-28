
using System.Threading.Tasks;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using static Basis.BasisUI.LibraryProvider;

namespace Basis.BasisUI
{
    public class LibraryProviderDialogLoading
    {
        #region Loading Dialog

        public static async Task<bool> PromptUserLoadingInProgress(
            BasisMenuPanel panel,
            BasisDataStoreItemKeys.ItemKey item,
            BundledContentHolder.NetworkType networkType = BundledContentHolder.NetworkType.Local,
            bool persistence = false,
            bool modifyScale = false
        )
        {
            DialogBox<bool> contentLoadingDialogBox = DialogBox<bool>.Create(panel, new Vector2(830, 120),
                "Please wait",
                "Your content is currently loading standby...",
                AddressableAssets.Sprites.HourGlass,
                true
            );

            await LoadSelectedItem(item, networkType, persistence, modifyScale);

            // close with true to indicate success
            contentLoadingDialogBox.CloseWithResult(true);

            return await contentLoadingDialogBox.WaitAsync();

        }

        #endregion
    }

}