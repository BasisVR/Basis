using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.VowganUI
{
    /// <summary>
    /// This is the backing data that supports and manages the MenuInstance in the scene.
    /// </summary>
    [Serializable]
    public abstract class BasisMenuBase
    {

        public BasisMenuInstance MenuInstance = BasisMenuInstance.CreateNew();

        public static implicit operator bool(BasisMenuBase obj) => obj != null;

        public BasisMenuPanel ActiveMenu;
        public BasisMenuDialoguePanel Dialogue;


        public virtual void Release()
        {
            if (MenuInstance) MenuInstance.ReleaseInstance();
        }

        public void OpenDialogue(
            string title,
            string description,
            string accept,
            string deny,
            Action<bool> callback)
        {
            if (Dialogue)
            {
                Debug.LogWarning("An existing Dialogue window is already active.");
                return;
            }

            Dialogue = BasisMenuDialoguePanel.CreateNew(title,
                description,
                accept,
                deny,
                callback);
        }

        public void OpenDialogue(
            string title,
            string description,
            string accept,
            Action<bool> callback)
        {
            if (Dialogue)
            {
                Debug.LogWarning("An existing Dialogue window is already active.");
                return;
            }

            Dialogue = BasisMenuDialoguePanel.CreateNew(title,
                description,
                accept,
                callback);
        }
    }
}
