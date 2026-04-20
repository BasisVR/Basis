using System;
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
        public abstract bool Hidden { get; }
        public abstract string Title { get; }
        public abstract string IconAddress { get; }
        public abstract int Order { get; }
        public abstract void RunAction();

        public BasisMenuBase<TMenu> BoundMenu { get; private set; }
        public PanelButton BoundButton { get; private set; }

        public virtual void BindToButton(BasisMenuBase<TMenu> menu, PanelButton button)
        {
            BoundMenu = menu;
            BoundButton = button;

            BoundButton.OnClicked += () =>
            {
                if (!BoundMenu.Dialogue || !BoundMenu.Dialogue.BlocksOtherActions) RunAction();
            };
        }

        public virtual void OnButtonCreated(PanelButton button)
        {
        }

        /// <summary>
        /// Called when this provider's active menu panel is being released
        /// (e.g. when switching to another provider's tab).
        /// Override to unsubscribe from events or perform cleanup.
        /// </summary>
        public virtual void OnReleaseEvent()
        {
        }
    }
}
