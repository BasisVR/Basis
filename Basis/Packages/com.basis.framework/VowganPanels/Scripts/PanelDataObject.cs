using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.VowganUI
{
    public enum PanelPlacementDirection
    {
        Left,
        Up,
        Right,
        Down,
        Front,
        Behind,
    }

    [Serializable]
    public struct PanelData
    {
        public string Name;
        public Vector2 PanelSize;
        public float Scale;
    }

    [CreateAssetMenu(fileName = "Panel Data", menuName = "Basis/UI/Panel Data")]
    public class PanelDataObject : ScriptableObject
    {
        [Serializable]
        public struct PanelDataChild
        {
            public PanelDataObject Child;
            public float Margin;
            public PanelPlacementDirection Direction;
        }

        public PanelData Data;
        public List<PanelDataChild> Children = new();

    }
}
