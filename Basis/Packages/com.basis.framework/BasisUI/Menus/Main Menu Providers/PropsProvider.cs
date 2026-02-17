using Basis.BasisUI;
using UnityEngine;

public class PropsProvider : BasisMenuActionProvider<BasisMainMenu>
{

    [RuntimeInitializeOnLoadMethod]
    public static void AddToMenu()
    {
     //  BasisMenuBase<BasisMainMenu>.AddProvider(new PropsProvider());
    }

    public override string Title => "Props";
    public override string IconAddress => AddressableAssets.Sprites.Settings;
    public override int Order => 3;

    public override bool Hidden => false;

    public override void RunAction()
    {
        if (BasisMainMenu.ActiveMenuTitle == Title)
        {
            BasisMainMenu.Instance.ActiveMenu.ReleaseInstance();
        }

        BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
            BasisMenuPanel.PanelData.Standard(Title),
            BasisMenuPanel.PanelStyles.Page);
        BoundButton?.BindActiveStateToAddressablesInstance(panel);

        PanelPropsList proplist = PanelPropsList.CreateNew(panel.Descriptor.ContentParent);
    }
}
