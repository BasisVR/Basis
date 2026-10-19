using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
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
        BuildResponseSection(basisAvatarSDKInspector);
        if (basisAvatarSDKInspector.Avatar.FaceVisemeMesh != null)
        {
            if (basisAvatarSDKInspector.Avatar.FaceVisemeMovement == null || basisAvatarSDKInspector.Avatar.FaceVisemeMovement.Length != 15)
            {
                basisAvatarSDKInspector.Avatar.FaceVisemeMovement = new int[] { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
            }
            // Get the list of blend shape names from the Avatar
            List<string> MouthNames = AvatarHelper.FindAllNames(basisAvatarSDKInspector.Avatar.FaceVisemeMesh);
            // Add "None" to the list of names to represent the -1 case
            MouthNames.Insert(0, BasisEditorLocalization.Get("editor.common.none"));

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
                        MarkDirty(basisAvatarSDKInspector.Avatar);
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
            BlinkNames.Insert(0, BasisEditorLocalization.Get("editor.common.none"));

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
                    MarkDirty(basisAvatarSDKInspector.Avatar);
                });

                // Add the label and dropdown to the horizontal container
                rowContainer.Add(visemeLabel);
                rowContainer.Add(dropdownField);

                // Add the row to the main visual element
                manualassignBlinkDetection.Add(rowContainer);
            }
        }
    }

    /// <summary>
    /// Brings <c>FaceVisemeProfiles</c> up to one entry per viseme, filling new slots with the
    /// pass-through default so an avatar that never opens this section keeps its old response.
    /// Entirely blank slots are rebuilt on the default too, since shipping one mutes that viseme
    /// at runtime. A slot the creator switched off deliberately is left exactly as authored.
    /// </summary>
    public static void EnsureProfiles(BasisAvatar avatar)
    {
        if (avatar.FaceVisemeDrive == null || avatar.FaceVisemeDrive.IsUnset)
        {
            avatar.FaceVisemeDrive = new BasisVisemeDriveConfig();
        }

        int count = BasisVisemeDriveConfig.VisemeCount;
        bool rightSize = avatar.FaceVisemeProfiles != null && avatar.FaceVisemeProfiles.Length == count;
        if (rightSize && !AnyUnset(avatar.FaceVisemeProfiles))
        {
            return;
        }

        BasisVisemeProfile[] resized = new BasisVisemeProfile[count];
        for (int Index = 0; Index < count; Index++)
        {
            // Only entirely blank slots are rebuilt. Zeroed gain or a collapsed output range is
            // how a creator switches a viseme off, and that has to survive untouched.
            bool carryOver = avatar.FaceVisemeProfiles != null
                && Index < avatar.FaceVisemeProfiles.Length
                && !avatar.FaceVisemeProfiles[Index].IsUnset;

            resized[Index] = carryOver ? avatar.FaceVisemeProfiles[Index] : BasisVisemeProfile.Default;
        }
        avatar.FaceVisemeProfiles = resized;
    }

    private static bool AnyUnset(BasisVisemeProfile[] profiles)
    {
        for (int Index = 0; Index < profiles.Length; Index++)
        {
            if (profiles[Index].IsUnset)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Builds the Advanced &gt; Viseme Response block. Only the drive mode sits at the top level;
    /// everything else is tuning that the defaults already cover, so it stays folded away.
    /// </summary>
    private void BuildResponseSection(BasisAvatarSDKInspector inspector)
    {
        VisualElement parent = inspector.rootElement.Q<VisualElement>("visemeresponse");
        if (parent == null)
        {
            return;
        }

        parent.Clear();
        BasisAvatar avatar = inspector.Avatar;

        Label header = inspector.rootElement.Q<Label>("VisemeResponseHeader");
        DisplayStyle visibility = avatar.FaceVisemeMesh != null ? DisplayStyle.Flex : DisplayStyle.None;
        parent.style.display = visibility;
        if (header != null)
        {
            header.style.display = visibility;
        }
        if (avatar.FaceVisemeMesh == null)
        {
            return;
        }

        EnsureProfiles(avatar);

        Foldout tuning = new Foldout
        {
            text = BasisEditorLocalization.Get("sdk.visemes.tuning.header"),
            value = false
        };

        Foldout perViseme = new Foldout
        {
            text = BasisEditorLocalization.Get("sdk.visemes.perViseme.header"),
            value = false
        };
        for (int Index = 0; Index < VisibleKeysMouth.Count && Index < avatar.FaceVisemeProfiles.Length; Index++)
        {
            perViseme.Add(BuildProfileFoldout(avatar, Index));
        }

        VisualElement winnerFields = new VisualElement();

        PopupField<BasisVisemeDriveMode> modeField = new PopupField<BasisVisemeDriveMode>(
            BasisEditorLocalization.Get("sdk.visemes.mode"),
            new List<BasisVisemeDriveMode>((BasisVisemeDriveMode[])System.Enum.GetValues(typeof(BasisVisemeDriveMode))),
            avatar.FaceVisemeDrive.Mode,
            DriveModeName,
            DriveModeName);
        modeField.tooltip = BasisEditorLocalization.Get("sdk.visemes.mode.tooltip");

        Slider smoothing = new Slider(BasisEditorLocalization.Get("sdk.visemes.backendSmoothing"), 0f, 100f)
        {
            value = avatar.FaceVisemeDrive.BackendSmoothing,
            showInputField = true
        };
        smoothing.tooltip = BasisEditorLocalization.Get("sdk.visemes.backendSmoothing.tooltip");
        smoothing.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeDrive.BackendSmoothing = Mathf.RoundToInt(evt.newValue);
            MarkDirty(avatar);
        });

        modeField.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeDrive.Mode = (BasisVisemeDriveMode)evt.newValue;
            avatar.FaceVisemeDrive.BackendSmoothing = avatar.FaceVisemeDrive.Mode == BasisVisemeDriveMode.WinnerTakeAll
                ? BasisVisemeDriveConfig.WinnerTakeAllBackendSmoothing
                : BasisVisemeDriveConfig.DefaultBackendSmoothing;
            smoothing.SetValueWithoutNotify(avatar.FaceVisemeDrive.BackendSmoothing);
            winnerFields.style.display = avatar.FaceVisemeDrive.Mode == BasisVisemeDriveMode.WinnerTakeAll
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            MarkDirty(avatar);
        });

        Slider margin = new Slider(BasisEditorLocalization.Get("sdk.visemes.winnerMargin"), 0f, 0.5f)
        {
            value = avatar.FaceVisemeDrive.WinnerMargin,
            showInputField = true
        };
        margin.tooltip = BasisEditorLocalization.Get("sdk.visemes.winnerMargin.tooltip");
        margin.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeDrive.WinnerMargin = evt.newValue;
            MarkDirty(avatar);
        });

        Slider hold = new Slider(BasisEditorLocalization.Get("sdk.visemes.winnerHold"), 0f, 0.25f)
        {
            value = avatar.FaceVisemeDrive.WinnerHoldSeconds,
            showInputField = true
        };
        hold.tooltip = BasisEditorLocalization.Get("sdk.visemes.winnerHold.tooltip");
        hold.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeDrive.WinnerHoldSeconds = evt.newValue;
            MarkDirty(avatar);
        });

        Slider floor = new Slider(BasisEditorLocalization.Get("sdk.visemes.silenceFloor"), 0f, 1f)
        {
            value = avatar.FaceVisemeDrive.SilenceFloor,
            showInputField = true
        };
        floor.tooltip = BasisEditorLocalization.Get("sdk.visemes.silenceFloor.tooltip");
        floor.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeDrive.SilenceFloor = evt.newValue;
            MarkDirty(avatar);
        });

        Toggle silRest = new Toggle(BasisEditorLocalization.Get("sdk.visemes.silIsRest")) { value = avatar.FaceVisemeDrive.SilIsRest };
        silRest.tooltip = BasisEditorLocalization.Get("sdk.visemes.silIsRest.tooltip");
        silRest.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeDrive.SilIsRest = evt.newValue;
            MarkDirty(avatar);
        });

        winnerFields.Add(margin);
        winnerFields.Add(hold);
        winnerFields.Add(floor);
        winnerFields.Add(silRest);
        winnerFields.style.display = avatar.FaceVisemeDrive.Mode == BasisVisemeDriveMode.WinnerTakeAll
            ? DisplayStyle.Flex
            : DisplayStyle.None;

        Button reset = new Button(() =>
        {
            avatar.FaceVisemeDrive = new BasisVisemeDriveConfig();
            for (int Index = 0; Index < avatar.FaceVisemeProfiles.Length; Index++)
            {
                avatar.FaceVisemeProfiles[Index] = BasisVisemeProfile.Default;
            }
            MarkDirty(avatar);
            Initialize(BasisAvatarSDKInspector);
        })
        {
            text = BasisEditorLocalization.Get("sdk.visemes.resetAll")
        };

        tuning.Add(smoothing);
        tuning.Add(winnerFields);
        tuning.Add(reset);

        parent.Add(modeField);
        parent.Add(tuning);
        parent.Add(perViseme);
    }

    private VisualElement BuildProfileFoldout(BasisAvatar avatar, int index)
    {
        Foldout foldout = new Foldout
        {
            text = index < VisibleKeysMouth.Count ? VisibleKeysMouth[index] : BasisEditorLocalization.Get("sdk.visemes.profile.header"),
            value = false
        };

        Slider gain = new Slider(BasisEditorLocalization.Get("sdk.visemes.profile.gain"), 0f, 4f)
        {
            value = avatar.FaceVisemeProfiles[index].Gain,
            showInputField = true
        };
        gain.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeProfiles[index].Gain = evt.newValue;
            MarkDirty(avatar);
        });

        Slider threshold = new Slider(BasisEditorLocalization.Get("sdk.visemes.profile.threshold"), 0f, 0.99f)
        {
            value = avatar.FaceVisemeProfiles[index].Threshold,
            showInputField = true
        };
        threshold.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeProfiles[index].Threshold = evt.newValue;
            MarkDirty(avatar);
        });

        Slider outMin = new Slider(BasisEditorLocalization.Get("sdk.visemes.profile.outMin"), 0f, 100f)
        {
            value = avatar.FaceVisemeProfiles[index].OutMin,
            showInputField = true
        };
        outMin.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeProfiles[index].OutMin = evt.newValue;
            MarkDirty(avatar);
        });

        Slider outMax = new Slider(BasisEditorLocalization.Get("sdk.visemes.profile.outMax"), 0f, 100f)
        {
            value = avatar.FaceVisemeProfiles[index].OutMax,
            showInputField = true
        };
        outMax.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeProfiles[index].OutMax = evt.newValue;
            MarkDirty(avatar);
        });

        Slider attack = new Slider(BasisEditorLocalization.Get("sdk.visemes.profile.attack"), 0f, 0.5f)
        {
            value = avatar.FaceVisemeProfiles[index].AttackSeconds,
            showInputField = true
        };
        attack.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeProfiles[index].AttackSeconds = evt.newValue;
            MarkDirty(avatar);
        });

        Slider release = new Slider(BasisEditorLocalization.Get("sdk.visemes.profile.release"), 0f, 0.5f)
        {
            value = avatar.FaceVisemeProfiles[index].ReleaseSeconds,
            showInputField = true
        };
        release.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeProfiles[index].ReleaseSeconds = evt.newValue;
            MarkDirty(avatar);
        });

        Toggle binary = new Toggle(BasisEditorLocalization.Get("sdk.visemes.profile.binary")) { value = avatar.FaceVisemeProfiles[index].Binary };
        binary.RegisterValueChangedCallback(evt =>
        {
            avatar.FaceVisemeProfiles[index].Binary = evt.newValue;
            MarkDirty(avatar);
        });

        Button copyToAll = new Button(() =>
        {
            BasisVisemeProfile source = avatar.FaceVisemeProfiles[index];
            for (int Index = 0; Index < avatar.FaceVisemeProfiles.Length; Index++)
            {
                avatar.FaceVisemeProfiles[Index] = source;
            }
            MarkDirty(avatar);
            Initialize(BasisAvatarSDKInspector);
        })
        {
            text = BasisEditorLocalization.Get("sdk.visemes.profile.copyToAll")
        };

        foldout.Add(gain);
        foldout.Add(threshold);
        foldout.Add(outMin);
        foldout.Add(outMax);
        foldout.Add(attack);
        foldout.Add(release);
        foldout.Add(binary);
        foldout.Add(copyToAll);
        return foldout;
    }

    private static string DriveModeName(BasisVisemeDriveMode mode)
    {
        switch (mode)
        {
            case BasisVisemeDriveMode.Continuous:
                return BasisEditorLocalization.Get("sdk.visemes.mode.continuous");
            case BasisVisemeDriveMode.WinnerTakeAll:
                return BasisEditorLocalization.Get("sdk.visemes.mode.winnerTakeAll");
            default:
                return mode.ToString();
        }
    }

    private static void MarkDirty(BasisAvatar avatar)
    {
        EditorUtility.SetDirty(avatar);
    }
}
