namespace Basis.BasisUI
{
    public abstract class BasisToggleMenuActionProvider<TMenu> :
        BasisMenuActionProvider<TMenu>
        where TMenu : BasisMenuBase<TMenu>
    {

        public bool IsActive => _isActive;
        protected bool _isActive;


        /// <summary>
        /// Toggle the Action's state between Active and Inactive.
        /// </summary>
        public void ToggleAction()
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

        public virtual void OnActionEnabled()
        {
        }

        public virtual void OnActionDisabled()
        {
        }
    }
}
