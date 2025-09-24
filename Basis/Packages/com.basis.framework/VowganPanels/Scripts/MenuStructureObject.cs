using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Basis.VowganUI
{
    public enum PanelPlacementDirection
    {
        Center,
        Left,
        Up,
        Right,
        Down,
        Front,
        Behind,
    }

    //TODO: This is not implemented yet.
    // These should describe the placement of panels for the menu and elements in the panels.
    [CreateAssetMenu(fileName = "Panel Data", menuName = "Basis/UI/Panel Data")]
    public class MenuStructureObject : ScriptableObject
    {
        [Serializable]
        public struct PanelDataChild
        {
            public MenuStructureObject Child;
            public float Margin;
            public PanelPlacementDirection Direction;
        }

        public PanelData Data;
        public List<PanelDataChild> Children = new();

        public static MenuStructureObject CreateNew(string referencePath)
        {
            return Addressables.LoadAssetAsync<MenuStructureObject>(referencePath).WaitForCompletion();
        }

    }
}
