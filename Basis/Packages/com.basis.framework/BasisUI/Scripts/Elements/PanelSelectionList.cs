using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelSelectionList : PanelElement
    {

        [Serializable]
        public class SelectionEntry
        {
            public string Title;
            public Texture2D Icon;
            public Action<bool> OnSelect;
        }

        public List<SelectionEntry> Entries = new();

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();

        }
    }
}
