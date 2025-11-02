using System;
using Basis.BasisUI.Styling;
using UnityEngine;

namespace Basis.BasisUI
{
    public abstract class BasisMenuActionProvider<TMenu> :
        IComparable<BasisMenuActionProvider<TMenu>>
        where TMenu : BasisMenuBase<TMenu>
    {
        public int CompareTo(BasisMenuActionProvider<TMenu> target)
        {
            if (Order < target.Order) return -1;
            if (Order > target.Order) return 1;
            return string.CompareOrdinal(Title, target.Title);
        }

        public abstract string Title { get; }
        public abstract Sprite Icon { get; }
        public abstract bool IconIsAddressable { get; }
        public abstract int Order { get; }
        public abstract void RunAction();


        public virtual PaletteStyle NormalStyle => PaletteStyle.FontColor1;
        public virtual PaletteStyle ActiveStyle => PaletteStyle.WhiteColor;


        public BasisMenuBase<TMenu> BoundMenu;
        public PanelButton BoundButton;

        public virtual void BindToButton(BasisMenuBase<TMenu> menu, PanelButton button)
        {
            BoundMenu = menu;
            BoundButton = button;

            BoundButton.IconStyling.NormalStyle = NormalStyle;
            BoundButton.LabelStyling.NormalStyle = NormalStyle;
            BoundButton.IconStyling.ActiveStyle = ActiveStyle;
            BoundButton.LabelStyling.ActiveStyle = ActiveStyle;
            BoundButton.UseActiveStyle(false);

            BoundButton.OnClicked.AddListener(() =>
            {
                if (!BoundMenu.Dialogue || !BoundMenu.Dialogue.BlocksOtherActions) RunAction();
            });
        }
    }
}
