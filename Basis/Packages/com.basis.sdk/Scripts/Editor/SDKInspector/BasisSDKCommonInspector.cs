using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

public static class BasisSDKCommonInspector
{
    // Source of truth for preset labels lives in BasisContentTagPresets so the client
    // filter UI surfaces the same options creators see in this inspector.
    private static string[] ContentTagPresets => BasisContentTagPresets.All;

    public static Button DocumentationButton(VisualElement rootElement, string Text)
    {
        Button fixMeButton = new Button();
        fixMeButton.text = Text;

        Color backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        fixMeButton.style.backgroundColor = new StyleColor(backgroundColor);
        fixMeButton.style.color = new StyleColor(Color.white);
        fixMeButton.style.fontSize = 14;
        fixMeButton.style.unityFontStyleAndWeight = FontStyle.Bold;
        fixMeButton.style.paddingTop = 6;
        fixMeButton.style.paddingBottom = 6;
        fixMeButton.style.paddingLeft = 12;
        fixMeButton.style.paddingRight = 12;
        fixMeButton.style.marginBottom = 10;
        fixMeButton.style.borderTopLeftRadius = 8;
        fixMeButton.style.borderTopRightRadius = 8;
        fixMeButton.style.borderBottomLeftRadius = 8;
        fixMeButton.style.borderBottomRightRadius = 8;
        fixMeButton.style.borderLeftWidth = 0;
        fixMeButton.style.borderRightWidth = 0;
        fixMeButton.style.borderTopWidth = 0;
        fixMeButton.style.borderBottomWidth = 3;
        fixMeButton.style.unityTextAlign = TextAnchor.MiddleCenter;
        fixMeButton.style.alignSelf = Align.Auto;

        fixMeButton.RegisterCallback<MouseEnterEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(new Color(0.4f, 0.4f, 0.4f, 1f));
        });
        fixMeButton.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(backgroundColor);
        });

        rootElement.Add(fixMeButton);
        return fixMeButton;
    }

    /// <summary>
    /// Resolves the container that code-built inspector sections should be parented into: the field
    /// area of the UXML "Settings" foldout, so they inherit its styling. Falls back to the inspector
    /// root if a content UXML has no such foldout.
    /// </summary>
    public static VisualElement ResolveSettingsContainer(VisualElement uiElementsRoot)
    {
        if (uiElementsRoot == null) return null;

        Foldout settings = uiElementsRoot.Q<Foldout>(BasisSDKConstants.SettingsFoldout);
        if (settings == null) return uiElementsRoot;

        return settings.Q<VisualElement>(BasisSDKConstants.SettingsFields) ?? settings;
    }

    /// <summary>
    /// Resolves the container for the build target selection, which sits in its own block just
    /// above the bundle options. Falls back to the inspector root if a content UXML has no such
    /// block.
    /// </summary>
    public static VisualElement ResolveBuildTargetsContainer(VisualElement uiElementsRoot)
    {
        return ResolveSectionContainer(uiElementsRoot, BasisSDKConstants.BuildTargetsSection, BasisSDKConstants.BuildTargetsFields);
    }

    /// <summary>
    /// Resolves the container for the bundle build options, which sit in their own block inside the
    /// "Advanced" foldout. Falls back to the inspector root if a content UXML has no such block.
    /// </summary>
    public static VisualElement ResolveBuildContainer(VisualElement uiElementsRoot)
    {
        return ResolveSectionContainer(uiElementsRoot, BasisSDKConstants.BuildOptionsSection, BasisSDKConstants.BuildOptionsFields);
    }

    /// <summary>
    /// Resolves the container for the content tags, which sit in their own block inside the
    /// "Advanced" foldout. Falls back to the inspector root if a content UXML has no such block.
    /// </summary>
    public static VisualElement ResolveContentTagsContainer(VisualElement uiElementsRoot)
    {
        return ResolveSectionContainer(uiElementsRoot, BasisSDKConstants.ContentTagsSection, BasisSDKConstants.ContentTagsFields);
    }

    private static VisualElement ResolveSectionContainer(VisualElement uiElementsRoot, string sectionName, string fieldsName)
    {
        if (uiElementsRoot == null) return null;

        VisualElement section = uiElementsRoot.Q<VisualElement>(sectionName);
        if (section == null) return uiElementsRoot;

        return section.Q<VisualElement>(fieldsName) ?? section;
    }

    // The Basis accent, matching the title text at the top of every content inspector.
    private static readonly Color BuildAccent = new Color(239f / 255f, 40f / 255f, 90f / 255f, 1f);
    private static readonly Color BuildAccentHover = new Color(255f / 255f, 74f / 255f, 120f / 255f, 1f);
    private static readonly Color BuildAccentPressed = new Color(190f / 255f, 26f / 255f, 67f / 255f, 1f);
    private static readonly Color BuildAccentShadow = new Color(150f / 255f, 20f / 255f, 53f / 255f, 1f);

    /// <summary>
    /// Dresses the "create .bee file" button as the primary action of the inspector: full-width
    /// accent fill, raised edge, and hover/press feedback.
    /// </summary>
    public static void StyleBuildButton(Button buildButton)
    {
        if (buildButton == null) return;

        IStyle style = buildButton.style;
        style.backgroundColor = new StyleColor(BuildAccent);
        style.color = new StyleColor(Color.white);
        style.fontSize = 16;
        style.unityFontStyleAndWeight = FontStyle.Bold;
        style.unityTextAlign = TextAnchor.MiddleCenter;
        style.height = 44;
        style.flexGrow = 1;
        style.marginTop = 2;
        style.marginBottom = 2;
        style.marginLeft = 4;
        style.marginRight = 4;
        style.paddingTop = 6;
        style.paddingBottom = 6;
        style.paddingLeft = 12;
        style.paddingRight = 12;
        style.borderTopLeftRadius = 8;
        style.borderTopRightRadius = 8;
        style.borderBottomLeftRadius = 8;
        style.borderBottomRightRadius = 8;
        style.borderLeftWidth = 0;
        style.borderRightWidth = 0;
        style.borderTopWidth = 0;
        style.borderBottomWidth = 3;
        style.borderBottomColor = new StyleColor(BuildAccentShadow);
        style.backgroundImage = null;

        buildButton.RegisterCallback<MouseEnterEvent>(evt =>
        {
            buildButton.style.backgroundColor = new StyleColor(BuildAccentHover);
        });
        buildButton.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            buildButton.style.backgroundColor = new StyleColor(BuildAccent);
            buildButton.style.borderBottomWidth = 3;
            buildButton.style.marginTop = 2;
        });
        // Sink the button by the height of its raised edge while held, so a click reads as a press.
        buildButton.RegisterCallback<MouseDownEvent>(evt =>
        {
            buildButton.style.backgroundColor = new StyleColor(BuildAccentPressed);
            buildButton.style.borderBottomWidth = 0;
            buildButton.style.marginTop = 5;
        });
        buildButton.RegisterCallback<MouseUpEvent>(evt =>
        {
            buildButton.style.backgroundColor = new StyleColor(BuildAccentHover);
            buildButton.style.borderBottomWidth = 3;
            buildButton.style.marginTop = 2;
        });
    }

    /// <summary>
    /// Binds the icon ObjectField to the serialized property and adds the capture buttons under it.
    ///
    /// <para>The binding is what makes Ctrl+Z work: an assignment written straight to the field —
    /// which is what these inspectors used to do — never reaches the undo stack, and the field
    /// keeps showing the old value when the user undoes something else.</para>
    /// </summary>
    /// <param name="primaryLabel">Label for the content-specific capture, e.g. "Generate Icon".</param>
    /// <param name="primaryCapture">Renders the content. Returns null when there is nothing to shoot.</param>
    public static void CreateIconTools(ObjectField iconField, SerializedObject serializedObject, BasisContentBase content, string primaryLabel, string primaryTooltip, Func<Texture2D> primaryCapture)
    {
        if (iconField == null || content == null) return;
        if (content.BasisBundleDescription == null)
        {
            content.BasisBundleDescription = new BasisBundleDescription();
        }

        SerializedProperty iconProperty = serializedObject?.FindProperty(BasisIconCapture.IconPropertyPath);
        if (iconProperty != null)
        {
            iconField.BindProperty(iconProperty);
        }
        else
        {
            iconField.value = content.BasisBundleDescription.AssetBundleIcon;
        }

        VisualElement row = new VisualElement
        {
            style = { flexDirection = FlexDirection.Row, marginTop = 2, marginBottom = 2 },
        };

        Label status = new Label
        {
            style =
            {
                whiteSpace = WhiteSpace.Normal,
                unityFontStyleAndWeight = FontStyle.Normal,
                unityTextAlign = TextAnchor.MiddleLeft,
                opacity = 0.85f,
                marginBottom = 4,
                display = DisplayStyle.None,
            },
        };

        // Both buttons share one undo label — which capture produced the icon is not what the
        // author is looking for in the Edit menu, only that undoing puts the old icon back.
        string undoName = BasisEditorLocalization.Get("sdk.commonInspector.icon.undo");

        Button primary = new Button
        {
            text = primaryLabel,
            tooltip = primaryTooltip,
        };
        primary.clicked += () => RunIconCapture(serializedObject, content, primaryCapture, undoName, status);

        Button fromSceneView = new Button
        {
            text = BasisEditorLocalization.Get("sdk.commonInspector.icon.fromSceneView"),
            tooltip = BasisEditorLocalization.Get("sdk.commonInspector.icon.fromSceneView.tooltip"),
        };
        fromSceneView.clicked += () => RunIconCapture(serializedObject, content, BasisIconCapture.CaptureFromSceneView, undoName, status);

        StyleIconButton(primary);
        StyleIconButton(fromSceneView);
        row.Add(primary);
        row.Add(fromSceneView);

        // Sit directly under the icon field rather than at the end of the inspector, so the button
        // and the field it fills in read as one control.
        VisualElement parent = iconField.parent;
        if (parent == null)
        {
            return;
        }
        int index = parent.IndexOf(iconField);
        if (index < 0)
        {
            parent.Add(row);
            parent.Add(status);
            return;
        }
        parent.Insert(index + 1, row);
        parent.Insert(index + 2, status);
    }

    private static void RunIconCapture(SerializedObject serializedObject, BasisContentBase content, Func<Texture2D> capture, string undoName, Label status)
    {
        (bool success, string message) = BasisIconCapture.GenerateAndAssign(serializedObject, content, capture, undoName);
        if (string.IsNullOrEmpty(message))
        {
            status.style.display = DisplayStyle.None;
            return;
        }

        status.text = message;
        status.style.color = new StyleColor(success ? new Color(0.55f, 0.85f, 0.55f, 1f) : new Color(0.95f, 0.55f, 0.55f, 1f));
        status.style.display = DisplayStyle.Flex;
    }

    private static void StyleIconButton(Button button)
    {
        IStyle style = button.style;
        style.flexGrow = 1;
        style.height = 22;
        style.marginLeft = 2;
        style.marginRight = 2;
        style.unityFontStyleAndWeight = FontStyle.Normal;
        style.borderTopLeftRadius = 4;
        style.borderTopRightRadius = 4;
        style.borderBottomLeftRadius = 4;
        style.borderBottomRightRadius = 4;
    }

    /// <summary>
    /// The Settings foldout is bold and centre-aligned, and both properties inherit, so paragraph
    /// text parented into it has to opt back out.
    /// </summary>
    private static Label BodyLabel(string text)
    {
        return new Label(text)
        {
            style =
            {
                whiteSpace = WhiteSpace.Normal,
                unityFontStyleAndWeight = FontStyle.Normal,
                unityTextAlign = TextAnchor.MiddleLeft,
            },
        };
    }

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
            text = BasisEditorLocalization.Get("sdk.commonInspector.contentTags.foldout"),
            value = false,
        };
        parent.Add(foldout);

        Label help = BodyLabel(BasisEditorLocalization.Get("sdk.commonInspector.contentTags.help"));
        help.style.marginBottom = 6;
        help.style.opacity = 0.85f;
        foldout.Add(help);

        Label presetLabel = new Label(BasisEditorLocalization.Get("sdk.commonInspector.contentTags.presets")) { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 4 } };
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

        Label customLabel = new Label(BasisEditorLocalization.Get("sdk.commonInspector.contentTags.custom")) { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 8 } };
        foldout.Add(customLabel);

        VisualElement addRow = new VisualElement
        {
            style = { flexDirection = FlexDirection.Row, marginBottom = 4 },
        };
        TextField customField = new TextField
        {
            style = { flexGrow = 1, marginRight = 4 },
        };
        customField.textEdition.placeholder = BasisEditorLocalization.Get("sdk.commonInspector.contentTags.placeholder");
        Button addButton = new Button { text = BasisEditorLocalization.Get("sdk.commonInspector.contentTags.add") };

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

    /// <summary>
    /// Builds the "Content Group ID" block: the stable identity that stacks every upload of this
    /// content into one library card. Same id ⇒ same stack; a fresh id lists future uploads as a
    /// separate item. Empty is fine — the build generates and persists one automatically.
    /// </summary>
    public static void CreateContentGroupIdFoldout(VisualElement parent, BasisContentBase content)
    {
        if (content == null) return;
        if (content.BasisBundleDescription == null)
        {
            content.BasisBundleDescription = new BasisBundleDescription();
        }

        Foldout foldout = new Foldout
        {
            text = BasisEditorLocalization.Get("sdk.commonInspector.contentGroupId.foldout"),
            value = false,
        };
        parent.Add(foldout);

        Label help = BodyLabel(BasisEditorLocalization.Get("sdk.commonInspector.contentGroupId.help"));
        help.style.marginBottom = 6;
        help.style.opacity = 0.85f;
        foldout.Add(help);

        TextField idField = new TextField
        {
            value = content.BasisBundleDescription.ContentGroupId ?? string.Empty,
            isDelayed = true,
            style = { flexGrow = 1 },
        };
        idField.textEdition.placeholder = BasisEditorLocalization.Get("sdk.commonInspector.contentGroupId.placeholder");
        idField.RegisterValueChangedCallback(evt =>
        {
            Undo.RecordObject(content, "Change Content Group ID");
            content.BasisBundleDescription.ContentGroupId = evt.newValue == null ? string.Empty : evt.newValue.Trim();
            MarkContentDirty(content);
        });
        foldout.Add(idField);

        VisualElement buttonRow = new VisualElement
        {
            style = { flexDirection = FlexDirection.Row, marginTop = 4 },
        };

        Button generate = new Button
        {
            text = BasisEditorLocalization.Get("sdk.commonInspector.contentGroupId.generate"),
            tooltip = BasisEditorLocalization.Get("sdk.commonInspector.contentGroupId.generate.tooltip"),
        };
        generate.clicked += () =>
        {
            Undo.RecordObject(content, "Change Content Group ID");
            content.BasisBundleDescription.ContentGroupId = BasisGenerateUniqueID.GenerateUniqueID();
            MarkContentDirty(content);
            idField.SetValueWithoutNotify(content.BasisBundleDescription.ContentGroupId);
        };

        Button copy = new Button
        {
            text = BasisEditorLocalization.Get("sdk.commonInspector.contentGroupId.copy"),
            tooltip = BasisEditorLocalization.Get("sdk.commonInspector.contentGroupId.copy.tooltip"),
        };
        copy.clicked += () =>
        {
            EditorGUIUtility.systemCopyBuffer = content.BasisBundleDescription.ContentGroupId ?? string.Empty;
        };

        StyleIconButton(generate);
        StyleIconButton(copy);
        buttonRow.Add(generate);
        buttonRow.Add(copy);
        foldout.Add(buttonRow);
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

    /// <summary>
    /// Builds the "Spawn Placement" section for a prop: how the prop asks to arrive in the world.
    /// Rows that do not apply to the chosen placement are hidden rather than disabled, so the
    /// section only ever shows knobs that actually do something.
    /// </summary>
    public static void CreatePropSpawnFoldout(VisualElement parent, BasisProp prop)
    {
        if (prop == null) return;

        Foldout foldout = new Foldout
        {
            text = BasisEditorLocalization.Get("sdk.propInspector.spawn.foldout"),
            value = false,
        };
        parent.Add(foldout);

        Label help = BodyLabel(BasisEditorLocalization.Get("sdk.propInspector.spawn.help"));
        help.style.marginBottom = 6;
        help.style.opacity = 0.85f;
        foldout.Add(help);

        List<BasisPropSpawnPlacement> placementChoices = new List<BasisPropSpawnPlacement>();
        foreach (BasisPropSpawnPlacement value in Enum.GetValues(typeof(BasisPropSpawnPlacement)))
        {
            if (value != BasisPropSpawnPlacement.AtAnchor || prop.SpawnMetaData.Placement == BasisPropSpawnPlacement.AtAnchor)
            {
                placementChoices.Add(value);
            }
        }
        PopupField<BasisPropSpawnPlacement> placementField = new PopupField<BasisPropSpawnPlacement>(BasisEditorLocalization.Get("sdk.propInspector.spawn.placement"), placementChoices, prop.SpawnMetaData.Placement, value => ObjectNames.NicifyVariableName(value.ToString()), value => ObjectNames.NicifyVariableName(value.ToString()));
        foldout.Add(placementField);

        Label placementHelp = BodyLabel(string.Empty);
        placementHelp.style.marginBottom = 6;
        placementHelp.style.marginLeft = 4;
        placementHelp.style.opacity = 0.7f;
        foldout.Add(placementHelp);

        EnumField handField = new EnumField(BasisEditorLocalization.Get("sdk.propInspector.spawn.hand"), prop.SpawnMetaData.Hand);
        foldout.Add(handField);

        Toggle distanceToggle = new Toggle(BasisEditorLocalization.Get("sdk.propInspector.spawn.overrideDistance"))
        {
            value = prop.SpawnMetaData.HasCustomDistance,
        };
        foldout.Add(distanceToggle);

        FloatField distanceField = new FloatField(BasisEditorLocalization.Get("sdk.propInspector.spawn.distance"))
        {
            value = prop.SpawnMetaData.HasCustomDistance ? prop.SpawnMetaData.Distance : BasisPropSpawnMetaData.FallbackDistance,
        };
        foldout.Add(distanceField);

        Toggle alignField = new Toggle(BasisEditorLocalization.Get("sdk.propInspector.spawn.alignToSurface"))
        {
            value = prop.SpawnMetaData.AlignToSurface,
        };
        foldout.Add(alignField);

        Toggle faceField = new Toggle(BasisEditorLocalization.Get("sdk.propInspector.spawn.faceThePlayer"))
        {
            value = prop.SpawnMetaData.FaceThePlayer,
        };
        foldout.Add(faceField);

        Toggle scaleToggle = new Toggle(BasisEditorLocalization.Get("sdk.propInspector.spawn.overrideScale"))
        {
            value = prop.SpawnMetaData.HasCustomScale,
        };
        foldout.Add(scaleToggle);

        FloatField scaleField = new FloatField(BasisEditorLocalization.Get("sdk.propInspector.spawn.uniformScale"))
        {
            value = prop.SpawnMetaData.HasCustomScale && prop.SpawnMetaData.UniformScale > 0f ? prop.SpawnMetaData.UniformScale : 1f,
        };
        foldout.Add(scaleField);

        void RefreshRows()
        {
            BasisPropSpawnPlacement placement = prop.SpawnMetaData.Placement;
            bool inHand = placement == BasisPropSpawnPlacement.InHand;
            bool onGround = placement == BasisPropSpawnPlacement.OnGround;
            bool usesDistance = inHand || onGround || placement == BasisPropSpawnPlacement.InAirAtDistance;
            bool usesFacing = onGround
                || placement == BasisPropSpawnPlacement.InAirAtDistance
                || placement == BasisPropSpawnPlacement.InFrontOfPlayer
                || placement == BasisPropSpawnPlacement.AtPlayerOrigin;

            placementHelp.text = SpawnPlacementHelp(placement);

            SetRowVisible(handField, inHand);
            SetRowVisible(distanceToggle, usesDistance);
            SetRowVisible(distanceField, usesDistance && prop.SpawnMetaData.HasCustomDistance);
            SetRowVisible(alignField, onGround);
            SetRowVisible(faceField, usesFacing);
            SetRowVisible(scaleToggle, true);
            SetRowVisible(scaleField, prop.SpawnMetaData.HasCustomScale);
        }

        void Apply(Action mutate, string undoName)
        {
            Undo.RecordObject(prop, undoName);
            mutate();
            MarkContentDirty(prop);
            RefreshRows();
        }

        placementField.RegisterValueChangedCallback(evt =>
            Apply(() => prop.SpawnMetaData.Placement = (BasisPropSpawnPlacement)evt.newValue, "Change Prop Spawn Placement"));

        handField.RegisterValueChangedCallback(evt =>
            Apply(() => prop.SpawnMetaData.Hand = (BasisPropSpawnHand)evt.newValue, "Change Prop Spawn Hand"));

        distanceToggle.RegisterValueChangedCallback(evt => Apply(() =>
        {
            prop.SpawnMetaData.HasCustomDistance = evt.newValue;
            if (evt.newValue && prop.SpawnMetaData.Distance <= 0f)
            {
                prop.SpawnMetaData.Distance = BasisPropSpawnMetaData.FallbackDistance;
                distanceField.SetValueWithoutNotify(prop.SpawnMetaData.Distance);
            }
        }, "Toggle Prop Spawn Distance Override"));

        distanceField.RegisterValueChangedCallback(evt => Apply(() =>
        {
            float clamped = Mathf.Max(evt.newValue, 0.05f);
            prop.SpawnMetaData.Distance = clamped;
            if (!Mathf.Approximately(clamped, evt.newValue))
            {
                distanceField.SetValueWithoutNotify(clamped);
            }
        }, "Change Prop Spawn Distance"));

        alignField.RegisterValueChangedCallback(evt =>
            Apply(() => prop.SpawnMetaData.AlignToSurface = evt.newValue, "Toggle Prop Align To Surface"));

        faceField.RegisterValueChangedCallback(evt =>
            Apply(() => prop.SpawnMetaData.FaceThePlayer = evt.newValue, "Toggle Prop Face The Player"));

        scaleToggle.RegisterValueChangedCallback(evt => Apply(() =>
        {
            prop.SpawnMetaData.HasCustomScale = evt.newValue;
            if (evt.newValue && prop.SpawnMetaData.UniformScale <= 0f)
            {
                prop.SpawnMetaData.UniformScale = 1f;
                scaleField.SetValueWithoutNotify(prop.SpawnMetaData.UniformScale);
            }
        }, "Toggle Prop Spawn Scale Override"));

        scaleField.RegisterValueChangedCallback(evt => Apply(() =>
        {
            float clamped = Mathf.Max(evt.newValue, 0.001f);
            prop.SpawnMetaData.UniformScale = clamped;
            if (!Mathf.Approximately(clamped, evt.newValue))
            {
                scaleField.SetValueWithoutNotify(clamped);
            }
        }, "Change Prop Spawn Scale"));

        RefreshRows();
    }

    private static void SetRowVisible(VisualElement row, bool visible)
    {
        row.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private static string SpawnPlacementHelp(BasisPropSpawnPlacement placement)
    {
        switch (placement)
        {
            case BasisPropSpawnPlacement.Raycast:
                return BasisEditorLocalization.Get("sdk.propInspector.spawn.placement.raycast");
            case BasisPropSpawnPlacement.InFrontOfPlayer:
                return BasisEditorLocalization.Get("sdk.propInspector.spawn.placement.inFront");
            case BasisPropSpawnPlacement.AtPlayerOrigin:
                return BasisEditorLocalization.Get("sdk.propInspector.spawn.placement.playerOrigin");
            case BasisPropSpawnPlacement.InAirAtDistance:
                return BasisEditorLocalization.Get("sdk.propInspector.spawn.placement.inAir");
            case BasisPropSpawnPlacement.OnGround:
                return BasisEditorLocalization.Get("sdk.propInspector.spawn.placement.onGround");
            case BasisPropSpawnPlacement.InHand:
                return BasisEditorLocalization.Get("sdk.propInspector.spawn.placement.inHand");
            case BasisPropSpawnPlacement.AtAnchor:
                return BasisEditorLocalization.Get("sdk.propInspector.spawn.placement.atAnchor");
            default:
                return BasisEditorLocalization.Get("sdk.propInspector.spawn.placement.unspecified");
        }
    }

    public static void CreateBuildOptionsDropdown(VisualElement parent, SerializedObject serializedObject)
    {
        BasisAssetBundleObject assetBundleObject =
            AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(
                BasisAssetBundleObject.AssetBundleObject);

        Foldout foldout = new Foldout
        {
            text = BasisEditorLocalization.Get("sdk.commonInspector.buildOptions.foldout"),
            value = false
        };
        parent.Add(foldout);

        SerializedProperty useCustomPassword = serializedObject.FindProperty(nameof(BasisContentBase.UseCustomPassword));
        SerializedProperty userSelectedPassword = serializedObject.FindProperty(nameof(BasisContentBase.UserSelectedPassword));

        Toggle toggle = new Toggle(BasisEditorLocalization.Get("sdk.commonInspector.useCustomPassword"))
        {
            value = useCustomPassword.boolValue
        };

        TextField passwordField = new TextField(BasisEditorLocalization.Get("sdk.commonInspector.password"))
        {
            value = userSelectedPassword.stringValue,
            isPasswordField = true // masks input
        };

        // Initial state
        passwordField.SetEnabled(toggle.value);
        passwordField.style.display = toggle.value
            ? DisplayStyle.Flex
            : DisplayStyle.None;

        toggle.BindProperty(useCustomPassword);
        passwordField.BindProperty(userSelectedPassword);

        toggle.RegisterValueChangedCallback(evt =>
        {
            passwordField.SetEnabled(evt.newValue);
            passwordField.style.display = evt.newValue
                ? DisplayStyle.Flex
                : DisplayStyle.None;

            if (!evt.newValue && !string.IsNullOrEmpty(userSelectedPassword.stringValue))
            {
                userSelectedPassword.stringValue = "";
                serializedObject.ApplyModifiedProperties();
            }
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
        Foldout buildTargetFoldout = new Foldout { text = BasisEditorLocalization.Get("sdk.commonInspector.buildTargets.foldout"), value = false }; // Expanded by default
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
