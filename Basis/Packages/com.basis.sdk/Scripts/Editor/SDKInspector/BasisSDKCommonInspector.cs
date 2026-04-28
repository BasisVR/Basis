using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class BasisSDKCommonInspector
{
    // Source of truth for preset labels lives in BasisContentTagPresets so the client
    // filter UI surfaces the same options creators see in this inspector.
    private static string[] ContentTagPresets => BasisContentTagPresets.All;

    public static void CreateContentTagsFoldout(VisualElement parent, BasisContentBase content)
    {
        if (content == null) return;
        if (content.BasisBundleDescription == null)
        {
            content.BasisBundleDescription = new BasisBundleDescription();
        }
        if (content.BasisBundleDescription.Tags == null)
        {
            content.BasisBundleDescription.Tags = new string[0];
        }

        Foldout foldout = new Foldout
        {
            text = "Content Tags",
            value = false,
        };
        parent.Add(foldout);

        Label help = new Label("Tag your content so users can filter what they see. Tags are stored inside the bee bundle and travel with the asset. Honor system — accuracy is your responsibility.")
        {
            style =
            {
                whiteSpace = WhiteSpace.Normal,
                marginBottom = 6,
                opacity = 0.85f,
            },
        };
        foldout.Add(help);

        Label presetLabel = new Label("Presets") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 4 } };
        foldout.Add(presetLabel);

        for (int i = 0; i < ContentTagPresets.Length; i++)
        {
            string preset = ContentTagPresets[i];
            Toggle toggle = new Toggle(preset)
            {
                value = ContainsTag(content.BasisBundleDescription.Tags, preset),
            };
            toggle.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(content, "Toggle Content Tag");
                if (evt.newValue)
                {
                    content.BasisBundleDescription.Tags = AppendIfMissing(content.BasisBundleDescription.Tags, preset);
                }
                else
                {
                    content.BasisBundleDescription.Tags = RemoveTag(content.BasisBundleDescription.Tags, preset);
                }
                MarkContentDirty(content);
            });
            foldout.Add(toggle);
        }

        Label customLabel = new Label("Custom Tags") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 8 } };
        foldout.Add(customLabel);

        VisualElement addRow = new VisualElement
        {
            style = { flexDirection = FlexDirection.Row, marginBottom = 4 },
        };
        TextField customField = new TextField
        {
            style = { flexGrow = 1, marginRight = 4 },
        };
        customField.textEdition.placeholder = "Add a custom tag...";
        Button addButton = new Button { text = "Add" };

        VisualElement chipsRow = new VisualElement
        {
            style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap },
        };

        Action rebuildChips = null;
        rebuildChips = () =>
        {
            chipsRow.Clear();
            string[] tags = content?.BasisBundleDescription?.Tags;
            if (tags == null) return;
            for (int i = 0; i < tags.Length; i++)
            {
                string tag = tags[i];
                if (IsPreset(tag)) continue; // presets render as toggles above
                VisualElement chip = BuildTagChip(tag, () =>
                {
                    Undo.RecordObject(content, "Remove Content Tag");
                    content.BasisBundleDescription.Tags = RemoveTag(content.BasisBundleDescription.Tags, tag);
                    MarkContentDirty(content);
                    rebuildChips();
                });
                chipsRow.Add(chip);
            }
        };

        Action commitCustomTag = () =>
        {
            string raw = customField.value;
            if (string.IsNullOrWhiteSpace(raw)) return;
            string trimmed = raw.Trim();
            Undo.RecordObject(content, "Add Content Tag");
            content.BasisBundleDescription.Tags = AppendIfMissing(content.BasisBundleDescription.Tags, trimmed);
            MarkContentDirty(content);
            customField.SetValueWithoutNotify(string.Empty);
            rebuildChips();
        };

        addButton.clicked += () => commitCustomTag();
        customField.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                commitCustomTag();
                evt.StopPropagation();
            }
        });

        addRow.Add(customField);
        addRow.Add(addButton);
        foldout.Add(addRow);
        foldout.Add(chipsRow);
        rebuildChips();
    }

    private static VisualElement BuildTagChip(string label, Action onRemove)
    {
        VisualElement chip = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                alignItems = Align.Center,
                marginRight = 4,
                marginBottom = 4,
                paddingLeft = 6,
                paddingRight = 2,
                paddingTop = 2,
                paddingBottom = 2,
                backgroundColor = new StyleColor(new Color(0.27f, 0.31f, 0.40f, 1f)),
                borderTopLeftRadius = 8,
                borderTopRightRadius = 8,
                borderBottomLeftRadius = 8,
                borderBottomRightRadius = 8,
            },
        };
        Label text = new Label(label) { style = { color = Color.white, marginRight = 4 } };
        Button remove = new Button(() => onRemove?.Invoke())
        {
            text = "x",
            style =
            {
                width = 18,
                height = 18,
                paddingLeft = 0,
                paddingRight = 0,
                paddingTop = 0,
                paddingBottom = 0,
                marginLeft = 0,
                marginRight = 0,
                marginTop = 0,
                marginBottom = 0,
                fontSize = 10,
            },
        };
        chip.Add(text);
        chip.Add(remove);
        return chip;
    }

    private static bool ContainsTag(string[] tags, string candidate)
    {
        if (tags == null) return false;
        for (int i = 0; i < tags.Length; i++)
        {
            if (string.Equals(tags[i], candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string[] AppendIfMissing(string[] tags, string tag)
    {
        if (tags == null)
        {
            return new[] { tag };
        }
        if (ContainsTag(tags, tag))
        {
            return tags;
        }
        var next = new string[tags.Length + 1];
        Array.Copy(tags, next, tags.Length);
        next[tags.Length] = tag;
        return next;
    }

    private static string[] RemoveTag(string[] tags, string tag)
    {
        if (tags == null || tags.Length == 0) return tags ?? new string[0];
        int kept = 0;
        for (int i = 0; i < tags.Length; i++)
        {
            if (!string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) kept++;
        }
        if (kept == tags.Length) return tags;
        var next = new string[kept];
        int j = 0;
        for (int i = 0; i < tags.Length; i++)
        {
            if (!string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase))
            {
                next[j++] = tags[i];
            }
        }
        return next;
    }

    private static bool IsPreset(string tag)
    {
        for (int i = 0; i < ContentTagPresets.Length; i++)
        {
            if (string.Equals(ContentTagPresets[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void MarkContentDirty(BasisContentBase content)
    {
        EditorUtility.SetDirty(content);
        if (PrefabUtility.IsPartOfPrefabInstance(content))
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(content);
        }
    }

    public static void CreateBuildOptionsDropdown(VisualElement parent)
    {
        BasisAssetBundleObject assetBundleObject =
            AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(
                BasisAssetBundleObject.AssetBundleObject);

        Foldout foldout = new Foldout
        {
            text = "Build AssetBundle Options",
            value = false
        };
        parent.Add(foldout);

        Toggle toggle = new Toggle("Use Custom Password")
        {
            value = assetBundleObject.UseCustomPassword
        };

        TextField passwordField = new TextField("Password")
        {
            value = assetBundleObject.UserSelectedPassword,
            isPasswordField = true // masks input
        };

        // Initial state
        passwordField.SetEnabled(toggle.value);
        passwordField.style.display = toggle.value
            ? DisplayStyle.Flex
            : DisplayStyle.None;

        toggle.RegisterValueChangedCallback(evt =>
        {
            assetBundleObject.UseCustomPassword = evt.newValue;

            passwordField.SetEnabled(evt.newValue);
            passwordField.style.display = evt.newValue
                ? DisplayStyle.Flex
                : DisplayStyle.None;

            if (!evt.newValue)
            {
                assetBundleObject.UserSelectedPassword = "";
                passwordField.value = "";
            }

            EditorUtility.SetDirty(assetBundleObject);
        });

        passwordField.RegisterValueChangedCallback(evt =>
        {
            assetBundleObject.UserSelectedPassword = evt.newValue;
            EditorUtility.SetDirty(assetBundleObject);
        });

        foldout.Add(toggle);
        foldout.Add(passwordField);

        foreach (BuildAssetBundleOptions option in Enum.GetValues(typeof(BuildAssetBundleOptions)))
        {
            if (option == 0)
            {
                continue; // Skip "None"
            }
            // Check if the enum field has the Obsolete attribute
            FieldInfo fieldInfo = typeof(BuildAssetBundleOptions).GetField(option.ToString());
            if (fieldInfo != null && Attribute.IsDefined(fieldInfo, typeof(ObsoleteAttribute)))
            {
                continue; // Skip obsolete options from being shown

            }
            Toggle BABOTggle = new Toggle(option.ToString())
            {
                value = assetBundleObject.BuildAssetBundleOptions.HasFlag(option)
            };

            BABOTggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                {
                    assetBundleObject.BuildAssetBundleOptions |= option;
                }
                else
                {
                    assetBundleObject.BuildAssetBundleOptions &= ~option;
                }
            });

            foldout.Add(BABOTggle);
        }
        EditorUtility.SetDirty(assetBundleObject);
        AssetDatabase.SaveAssets();
    }
    public static void CreateBuildTargetOptions(VisualElement parent)
    {
        BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
        // Multi-select dropdown (Foldout with Toggles)
        Foldout buildTargetFoldout = new Foldout { text = "Select Build Targets", value = false }; // Expanded by default
        parent.Add(buildTargetFoldout);

        foreach (var target in BasisSDKConstants.allowedTargets)
        {
            // Check if the target is already selected
            bool isSelected = assetBundleObject.selectedTargets.Contains(target);

            Toggle toggle = new Toggle(BasisSDKConstants.targetDisplayNames[target])
            {
                value = isSelected // Set the toggle based on whether the target is in the selected list
            };

            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                    assetBundleObject.selectedTargets.Add(target);
                else
                    assetBundleObject.selectedTargets.Remove(target);
            });


            buildTargetFoldout.Add(toggle);
        }
        EditorUtility.SetDirty(assetBundleObject);
        AssetDatabase.SaveAssets();
    }
}
