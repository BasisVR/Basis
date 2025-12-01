using System;
using UnityEngine;

namespace Basis.BasisUI
{
    [Obsolete]
    [System.Serializable]
    public struct LayoutContainerOptions
    {
        public TextAnchor Alignment;
        /// <summary>
        /// Will shrink the container down as far as possible while fitting all child elements within.
        /// </summary>
        public bool Constrained;
        [Header("Stretches elements to fill container.")]
        public bool StretchItemWidth;
        public bool StretchItemHeight;
        [Header("Adds empty space between elements to fill container.")]
        public bool SpreadItemWidth;
        public bool SpreadItemHeight;
    }
}
