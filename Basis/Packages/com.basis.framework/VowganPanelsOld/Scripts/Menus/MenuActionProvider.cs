using System;
using Basis.VowganUI;
using UnityEngine;

namespace Basis.VowganUIOld
{
    public abstract class MenuActionProvider : IComparable<MenuActionProvider>
    {

        public int CompareTo(MenuActionProvider target)
        {
            if (Order < target.Order) return -1;
            if (Order > target.Order) return 1;
            return string.CompareOrdinal(Title, target.Title);
        }


        public abstract string Title { get; }
        public abstract Sprite Icon { get; }
        public abstract int Order { get; }
        public abstract void RunAction();

        public bool IsActive => _isActive;
        protected bool _isActive;


        /// <summary>
        /// Toggle the Action's state between Active and Inactive.
        /// </summary>
        public void ToggleActive()
        {
            if (_isActive) DisableAction();
            else EnableAction();
        }

        /// <summary>
        /// Sets the Action's state to Active, with callbacks.
        /// </summary>
        public void EnableAction()
        {
            if (_isActive) return;
            _isActive = true;
            OnActionEnabled();
        }

        /// <summary>
        /// Sets the Action's state to Inactive, with callbacks.
        /// </summary>
        public void DisableAction()
        {
            if (!_isActive) return;
            _isActive = false;
            OnActionDisabled();
        }

        public virtual void OnActionEnabled(){}
        public virtual void OnActionDisabled(){}


        /// <summary>
        /// Bind this action to an PanelButton Instance.
        /// </summary>
        public void BindToButton(PanelButton button)
        {
            button.OnClicked.AddListener(RunAction);
        }

        public void ReleasePanelForAction()
        {
            foreach (Panel panel in HomeRowMenu.Instance.Group.MovementPanels)
            {
                if (!panel ||
                    panel.IsReleased ||
                    panel.Data.Title != this.Title) continue;
                panel.ReleaseInstance();
                break;
            }
        }
    }
}
