using System;
using System.Collections.Generic;
using UnityEngine;

namespace JigglePhysics
{
    [DisallowMultipleComponent]
    public abstract class JiggleRigLOD : MonoBehaviour
    {
        protected IJiggleBlendable[] jiggles;
        protected List<IJiggleBlendable> JigglesList;
        protected int JiggleCount;
        protected bool wasActive;

        protected virtual void Awake()
        {
            JigglesList = new List<IJiggleBlendable>();
            jiggles = null;
        }

        public virtual void AddTrackedJiggleRig(IJiggleBlendable blendable)
        {
            if (JigglesList.Contains(blendable)) return;
            JigglesList.Add(blendable);
            jiggles = JigglesList.ToArray();
            JiggleCount = jiggles.Length;
        }
        public virtual void RemoveTrackedJiggleRig(IJiggleBlendable blendable)
        {
            if (!JigglesList.Contains(blendable)) return;
            JigglesList.Remove(blendable);
            jiggles = JigglesList.ToArray();
            JiggleCount = jiggles.Length;
        }

        public void Simulate()
        {
            if (!CheckActive())
            {
                if (wasActive)
                {
                    for (int Index = 0; Index < JiggleCount; Index++)
                    {
                        IJiggleBlendable jiggle = jiggles[Index];
                        jiggle.enabled = false;
                    }
                }
                wasActive = false;
                return;
            }
            if (!wasActive)
            {
                for (int Index = 0; Index < JiggleCount; Index++)
                {
                    IJiggleBlendable jiggle = jiggles[Index];
                    jiggle.enabled = true;
                }
            }
            wasActive = true;
        }

        protected abstract bool CheckActive();

        private void OnDisable()
        {
            for (int Index = 0; Index < JiggleCount; Index++)
            {
                IJiggleBlendable jiggle = jiggles[Index];
                jiggle.enabled = false;
            }
        }
    }
}
