using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

public class AvatarSDKVisemes
{
    public BasisAvatarSDKInspector BasisAvatarSDKInspector;
    public VisualElement rowContainer;
    public List<string> VisibleKeysMouth = new List<string>()
    {
     //   "None", // Should map to -1
        "sil",
        "PP",
        "FF",
        "TH",
        "DD",
        "kk",
        "CH",
        "SS",
        "nn",
        "RR",
        "aa",
        "E",
        "ih",
        "oh",
        "ou",
    };
    public List<string> VisibleKeysBlink = new List<string>()
    {
     //   "None", // Should map to -1
        "blink"
    };

    public void Initialize(BasisAvatarSDKInspector basisAvatarSDKInspector)
    {
        VisualElement ManualAvatarVisemesvisualElement = basisAvatarSDKInspector.rootElement.Q<VisualElement>("manualassignavatarvisemes");
        this.BasisAvatarSDKInspector = basisAvatarSDKInspector;


        ManualAvatarVisemesvisualElement.Clear();
        if (basisAvatarSDKInspector.Avatar.FaceVisemeMesh != null)
        {
            if (basisAvatarSDKInspector.Avatar.FaceVisemeMovement == null || basisAvatarSDKInspector.Avatar.FaceVisemeMovement.Length != 15)
            {
                basisAvatarSDKInspector.Avatar.FaceVisemeMovement = new int[] { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
            }
            // Get the list of blend shape names from the Avatar
            List<string> MouthNames = AvatarHelper.FindAllNames(basisAvatarSDKInspector.Avatar.FaceVisemeMesh);
            // Add "None" to the list of names to represent the -1 case
            MouthNames.Insert(0, "None");

            for (int index = 0; index < VisibleKeysMouth.Count; index++)
            {
                // Create a horizontal container to hold both the label and the dropdown
                rowContainer = new VisualElement();
                rowContainer.style.flexDirection = FlexDirection.Row; // Horizontal layout

                // Create a label for the viseme name (assignable on the left)
                Label visemeLabel = new Label(VisibleKeysMouth[index]);
                visemeLabel.style.width = 150; // Adjust the width for alignment
                visemeLabel.style.unityTextAlign = TextAnchor.MiddleLeft;

                // Check if the index is within the bounds of FaceVisemeMovement
                if (index >= 0 && index < basisAvatarSDKInspector.Avatar.FaceVisemeMovement.Length)
                {
                    // Determine which item to select in the dropdown
                    int faceVisemeMovement = basisAvatarSDKInspector.Avatar.FaceVisemeMovement[index];
                    int selectedIndex = (faceVisemeMovement == -1) ? 0 : faceVisemeMovement + 1;

                    // Create the dropdown field (dropdown on the right)
                    DropdownField dropdownField = new DropdownField(MouthNames, selectedIndex);
                    dropdownField.style.flexGrow = 1; // Make dropdown take the remaining space

                    // Register callback for when the value changes
                    int currentIndex = index; // Capture the current index in a local variable
                    dropdownField.RegisterValueChangedCallback(evt =>
                    {
                        // Get the index of the new value in the Names list
                        int newIndex = MouthNames.IndexOf(evt.newValue);

                        // If "None" is selected, map it to -1, otherwise map to the corresponding index
                        basisAvatarSDKInspector.Avatar.FaceVisemeMovement[currentIndex] = (newIndex == 0) ? -1 : newIndex - 1;
                    });

                    // Add the label and dropdown to the horizontal container
                    rowContainer.Add(visemeLabel);
                    rowContainer.Add(dropdownField);

                    // Add the row to the main visual element
                    ManualAvatarVisemesvisualElement.Add(rowContainer);
                }
                else
                {
                    // Log a warning if the index is out of bounds
                    Debug.LogWarning($"Index {index} is out of bounds for FaceVisemeMovement.");
                }
            }
        }
        if (basisAvatarSDKInspector.Avatar.FaceBlinkMesh != null)
        {
            if (basisAvatarSDKInspector.Avatar.BlinkViseme == null || basisAvatarSDKInspector.Avatar.BlinkViseme.Length == 0)
            {
                basisAvatarSDKInspector.Avatar.BlinkViseme = new int[1] { -1 };
            }
            VisualElement manualassignBlinkDetection = basisAvatarSDKInspector.rootElement.Q<VisualElement>("manualassignBlinkDetection");

            manualassignBlinkDetection.Clear();
            // Get the list of blend shape names from the Avatar
            List<string> BlinkNames = AvatarHelper.FindAllNames(basisAvatarSDKInspector.Avatar.FaceBlinkMesh);
            // Add "None" to the list of names to represent the -1 case
            BlinkNames.Insert(0, "None");

            for (int index = 0; index < VisibleKeysBlink.Count; index++)
            {
                // Create a horizontal container to hold both the label and the dropdown
                rowContainer = new VisualElement();
                rowContainer.style.flexDirection = FlexDirection.Row; // Horizontal layout

                // Create a label for the viseme name (assignable on the left)
                Label visemeLabel = new Label(VisibleKeysBlink[index]);
                visemeLabel.style.width = 150; // Adjust the width for alignment
                visemeLabel.style.unityTextAlign = TextAnchor.MiddleLeft;

                // Determine which item to select in the dropdown
                int faceVisemeMovement = basisAvatarSDKInspector.Avatar.BlinkViseme[index];
                int selectedIndex = (faceVisemeMovement == -1) ? 0 : faceVisemeMovement + 1;

                // Create the dropdown field (dropdown on the right)
                DropdownField dropdownField = new DropdownField(BlinkNames, selectedIndex);
                dropdownField.style.flexGrow = 1; // Make dropdown take the remaining space

                // Register callback for when the value changes
                int currentIndex = index; // Capture the current index in a local variable
                dropdownField.RegisterValueChangedCallback(evt =>
                {
                    // Get the index of the new value in the Names list
                    int newIndex = BlinkNames.IndexOf(evt.newValue);

                    // If "None" is selected, map it to -1, otherwise map to the corresponding index
                    basisAvatarSDKInspector.Avatar.BlinkViseme[currentIndex] = (newIndex == 0) ? -1 : newIndex - 1;
                });

                // Add the label and dropdown to the horizontal container
                rowContainer.Add(visemeLabel);
                rowContainer.Add(dropdownField);

                // Add the row to the main visual element
                manualassignBlinkDetection.Add(rowContainer);
            }
        }
    }
}
