using System;
using UnityEngine;

namespace Basis.VowganUI
{
    public abstract class BasisMenuActionProvider : IComparable<BasisMenuActionProvider>
    {

        public int CompareTo(BasisMenuActionProvider target)
        {
            if (Order < target.Order) return -1;
            if (Order > target.Order) return 1;
            return string.CompareOrdinal(Title, target.Title);
        }

        public abstract string Title { get; }
        public abstract Sprite Icon { get; }
        public abstract int Order { get; }
        public abstract void RunAction();

        public virtual void BindToButton(BasisMenuBase menu, PanelButton button)
        {
            button.OnClicked.AddListener(() =>
            {
                if (!menu.Dialogue || !menu.Dialogue.BlocksOtherActions) RunAction();
            });
        }
    }
}
