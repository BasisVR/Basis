using System.Collections;
using System.Collections.Generic;
using Basis.Scripts.Device_Management;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;

namespace Basis.Scripts.UI
{
    public class BasisTrackerVisibilityHandler : MonoBehaviour
    {
        private bool wasVisible;

        private void OnEnable()
        {
            BasisUINeedsVisibleTrackers.sceneInstance = this;
        }
        
        private void OnDisable()
        {
            BasisUINeedsVisibleTrackers.sceneInstance = null;
        }

        public void VerifyAtEndOfFrame()
        {
            // We need to delay showing and hiding trackers, so that we don't hide-then-show within the same frame.
            StartCoroutine(CheckCoroutine());
        }

        private IEnumerator CheckCoroutine()
        {
            yield return new WaitForEndOfFrame();

            var shouldBeVisible = BasisUINeedsVisibleTrackers.Instance.ShouldBeVisible;
            if (shouldBeVisible != wasVisible)
            {
                wasVisible = shouldBeVisible;
                if (shouldBeVisible)
                {
                    BasisDeviceManagement.ShowTrackers();
                }
                else
                {
                    BasisDeviceManagement.HideTrackers();
                }
            }
        }
    }
    
    public class BasisUINeedsVisibleTrackers
    {
        public static BasisTrackerVisibilityHandler sceneInstance;
        
        public static BasisUINeedsVisibleTrackers Instance
        {
            get { return instance ??= new BasisUINeedsVisibleTrackers(); }
        }
        private static BasisUINeedsVisibleTrackers instance;
        private HashSet<BasisUIBase> requesters = new();
        
        public bool ShouldBeVisible => requesters.Count > 0;

        public void Add(BasisUIBase requester)
        {
            requesters.Add(requester);
            if (sceneInstance) sceneInstance.VerifyAtEndOfFrame();
        }

        public void Remove(BasisUIBase requester)
        {
            requesters.Remove(requester);
            if (sceneInstance) sceneInstance.VerifyAtEndOfFrame();
        }
    }
}