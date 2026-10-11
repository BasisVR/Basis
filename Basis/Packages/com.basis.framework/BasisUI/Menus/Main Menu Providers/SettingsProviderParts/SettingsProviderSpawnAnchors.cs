using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderSpawnAnchors
    {
        private const string NoneEntry = "none";

        public static void Build(RectTransform container, PanelElementDescriptor descriptor)
        {
            PanelSectionToggle section = PanelSectionToggle.CreateNewEntry(container);
            section.SetTitle(BasisLocalization.Get("settings.developer.group.spawnAnchors"));
            int start = container.childCount;

            PanelDropdown target = PanelDropdown.CreateNewEntry(container);
            target.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.target"));
            target.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.target.tooltip"));

            PanelToggle handlesToggle = PanelToggle.CreateNewEntry(container);
            handlesToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.handles"));
            handlesToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.handles.tooltip"));
            handlesToggle.AssignBinding(BasisSettingsDefaults.SpawnAnchorHandles);

            PanelToggle seatToggle = PanelToggle.CreateNewEntry(container);
            seatToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.seat"));
            seatToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.seat.tooltip"));
            seatToggle.AssignBinding(BasisSettingsDefaults.SpawnAnchorSeatOnSurface);

            PanelToggle positionSnapToggle = PanelToggle.CreateNewEntry(container);
            positionSnapToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.positionSnap"));
            positionSnapToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.positionSnap.tooltip"));
            positionSnapToggle.AssignBinding(BasisSettingsDefaults.SpawnAnchorPositionSnap);

            PanelSlider positionSnapSlider = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.developer.spawnAnchors.positionSnapSize"),
                    BasisLocalization.Get("settings.developer.spawnAnchors.positionSnapSize.description"),
                    0.05f, 2f, false, 2, ValueDisplayMode.Meters),
                BasisSettingsDefaults.SpawnAnchorPositionSnapSize);

            PanelToggle rotationSnapToggle = PanelToggle.CreateNewEntry(container);
            rotationSnapToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.rotationSnap"));
            rotationSnapToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.rotationSnap.tooltip"));
            rotationSnapToggle.AssignBinding(BasisSettingsDefaults.SpawnAnchorRotationSnap);

            PanelSlider rotationSnapSlider = PanelSlider.CreateEntryAndBind(
                container,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.developer.spawnAnchors.rotationSnapDegrees"),
                    BasisLocalization.Get("settings.developer.spawnAnchors.rotationSnapDegrees.description"),
                    5f, 90f, true, 0, ValueDisplayMode.Degrees),
                BasisSettingsDefaults.SpawnAnchorRotationSnapDegrees);

            PanelElementDescriptor sectionBox = null;
            void RebuildRows()
            {
                if (sectionBox != null) PanelElementDescriptor.RebuildLayoutChain(sectionBox.ContentParent, container);
            }
            void ApplySnapVisibility()
            {
                positionSnapSlider.Descriptor.SetActive(positionSnapToggle.Value);
                rotationSnapSlider.Descriptor.SetActive(rotationSnapToggle.Value);
            }
            ApplySnapVisibility();
            positionSnapToggle.OnValueChanged += _ =>
            {
                ApplySnapVisibility();
                RebuildRows();
            };
            rotationSnapToggle.OnValueChanged += _ =>
            {
                ApplySnapVisibility();
                RebuildRows();
            };

            PanelTextField nameField = PanelTextField.CreateNewEntry(container);
            nameField.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.anchorName"));
            nameField.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.anchorName.tooltip"));
            TMP_InputField nameInput = nameField._inputField;
            if (nameInput != null)
            {
                nameInput.contentType = TMP_InputField.ContentType.Standard;
                nameInput.lineType = TMP_InputField.LineType.SingleLine;
                nameInput.characterLimit = 40;
            }

            PanelTextField positionX = NumberField(container, "settings.developer.spawnAnchors.positionX");
            PanelTextField positionY = NumberField(container, "settings.developer.spawnAnchors.positionY");
            PanelTextField positionZ = NumberField(container, "settings.developer.spawnAnchors.positionZ");
            PanelTextField rotationX = NumberField(container, "settings.developer.spawnAnchors.rotationX");
            PanelTextField rotationY = NumberField(container, "settings.developer.spawnAnchors.rotationY");
            PanelTextField rotationZ = NumberField(container, "settings.developer.spawnAnchors.rotationZ");

            PanelToggle scaleOverrideToggle = PanelToggle.CreateNewEntry(container);
            scaleOverrideToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.scaleOverride"));
            scaleOverrideToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.scaleOverride.tooltip"));

            PanelTextField scaleField = NumberField(container, "settings.developer.spawnAnchors.scale");
            scaleField.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.scale.tooltip"));

            PanelButton placeButton = PanelButton.CreateNew(container);
            placeButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.place"));
            placeButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.place.tooltip"));
            placeButton.OnClicked += () => _ = BasisSpawnAnchors.PlaceWithRaycast();

            PanelButton addAtPlayerButton = PanelButton.CreateNew(container);
            addAtPlayerButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.addAtPlayer"));
            addAtPlayerButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.addAtPlayer.tooltip"));
            addAtPlayerButton.OnClicked += () => BasisSpawnAnchors.AddAtPlayer();

            PanelButton removeButton = PanelButton.CreateNew(container);
            removeButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.remove"));
            removeButton.OnClicked += BasisSpawnAnchors.RemoveSelected;

            PanelButton clearButton = PanelButton.CreateNew(container);
            clearButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.clear"));
            clearButton.OnClicked += BasisSpawnAnchors.Clear;

            PanelTextField fileField = PanelTextField.CreateNewEntry(container);
            fileField.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.file"));
            fileField.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.spawnAnchors.file.tooltip"));
            fileField.SetValueWithoutNotify(BasisSpawnAnchors.DefaultFilePath);
            TMP_InputField fileInput = fileField._inputField;
            if (fileInput != null)
            {
                fileInput.contentType = TMP_InputField.ContentType.Standard;
                fileInput.lineType = TMP_InputField.LineType.SingleLine;
                fileInput.characterLimit = 0;
            }

            PanelButton saveButton = PanelButton.CreateNew(container);
            saveButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.save"));
            saveButton.OnClicked += () => BasisSpawnAnchors.Save(fileField.Value);

            PanelButton loadButton = PanelButton.CreateNew(container);
            loadButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.load"));
            loadButton.OnClicked += () => BasisSpawnAnchors.Load(fileField.Value);

            PanelButton folderButton = PanelButton.CreateNew(container);
            folderButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.spawnAnchors.openFolder"));
            folderButton.OnClicked += () => RevealFolder(fileField.Value);

            bool syncing = false;
            bool lastSelection = false;
            bool lastScaleVisible = false;

            void Refresh(bool structural)
            {
                if (target == null)
                {
                    BasisSpawnAnchors.OnChanged -= Refresh;
                    return;
                }
                syncing = true;
                AssignEntries(target);
                target.SetValueWithoutNotify(BasisSpawnAnchors.SelectedIndex >= 0 ? BasisSpawnAnchors.SelectedIndex.ToString() : NoneEntry);
                bool hasSelection = BasisSpawnAnchors.TryGetSelected(out BasisSpawnAnchors.SpawnAnchor anchor);
                bool scaleVisible = hasSelection && anchor.OverrideScale;
                removeButton.SetInteractable(hasSelection);
                SetFieldsActive(hasSelection, nameField, positionX, positionY, positionZ, rotationX, rotationY, rotationZ);
                scaleOverrideToggle.Descriptor.SetActive(hasSelection);
                scaleOverrideToggle.SetValueWithoutNotify(scaleVisible);
                scaleField.Descriptor.SetActive(scaleVisible);
                if (hasSelection)
                {
                    Vector3 euler = anchor.Rotation.eulerAngles;
                    ShowText(nameField, anchor.Name);
                    Show(positionX, anchor.Position.x, "0.###");
                    Show(positionY, anchor.Position.y, "0.###");
                    Show(positionZ, anchor.Position.z, "0.###");
                    Show(rotationX, euler.x, "0.#");
                    Show(rotationY, euler.y, "0.#");
                    Show(rotationZ, euler.z, "0.#");
                    Show(scaleField, anchor.Scale, "0.###");
                }
                syncing = false;
                if (structural || hasSelection != lastSelection || scaleVisible != lastScaleVisible)
                {
                    lastSelection = hasSelection;
                    lastScaleVisible = scaleVisible;
                    RebuildRows();
                }
            }

            target.OnValueChanged += value =>
            {
                if (syncing)
                {
                    return;
                }
                BasisSpawnAnchors.Select(int.TryParse(value, out int index) ? index : -1);
            };
            scaleOverrideToggle.OnValueChanged += enabled =>
            {
                if (!syncing && BasisSpawnAnchors.TryGetSelected(out BasisSpawnAnchors.SpawnAnchor anchor))
                {
                    BasisSpawnAnchors.SetScaleOverride(anchor, enabled, anchor.Scale);
                }
            };
            scaleField.OnValueChanged += text =>
            {
                if (!syncing && TryParse(text, out float value) && BasisSpawnAnchors.TryGetSelected(out BasisSpawnAnchors.SpawnAnchor anchor))
                {
                    BasisSpawnAnchors.SetScaleOverride(anchor, anchor.OverrideScale, value);
                }
            };

            nameField.OnValueChanged += text =>
            {
                if (!syncing && BasisSpawnAnchors.TryGetSelected(out BasisSpawnAnchors.SpawnAnchor anchor))
                {
                    BasisSpawnAnchors.SetName(anchor, text);
                    nameField.SetValueWithoutNotify(anchor.Name);
                }
            };

            void ApplyPosition(int axis, string text)
            {
                if (!syncing && TryParse(text, out float value) && BasisSpawnAnchors.TryGetSelected(out BasisSpawnAnchors.SpawnAnchor anchor))
                {
                    Vector3 position = anchor.Position;
                    position[axis] = value;
                    BasisSpawnAnchors.SetPose(anchor, position, anchor.Rotation);
                }
            }

            void ApplyRotation(int axis, string text)
            {
                if (!syncing && TryParse(text, out float value) && BasisSpawnAnchors.TryGetSelected(out BasisSpawnAnchors.SpawnAnchor anchor))
                {
                    Vector3 euler = anchor.Rotation.eulerAngles;
                    euler[axis] = value;
                    BasisSpawnAnchors.SetPose(anchor, anchor.Position, Quaternion.Euler(euler));
                }
            }

            positionX.OnValueChanged += text => ApplyPosition(0, text);
            positionY.OnValueChanged += text => ApplyPosition(1, text);
            positionZ.OnValueChanged += text => ApplyPosition(2, text);
            rotationX.OnValueChanged += text => ApplyRotation(0, text);
            rotationY.OnValueChanged += text => ApplyRotation(1, text);
            rotationZ.OnValueChanged += text => ApplyRotation(2, text);

            Refresh(true);
            BasisSpawnAnchors.OnChanged += Refresh;

            sectionBox = PanelSectionToggleHelpers.FinalizeBoxedSectionFromIndex(section, container, start, false, visible =>
            {
                if (visible)
                {
                    ApplySnapVisibility();
                    Refresh(false);
                }
                RebuildRows();
            });
        }

        private static PanelTextField NumberField(RectTransform container, string titleKey)
        {
            PanelTextField field = PanelTextField.CreateNewEntry(container);
            field.Descriptor.SetTitle(BasisLocalization.Get(titleKey));
            TMP_InputField input = field._inputField;
            if (input != null)
            {
                input.contentType = TMP_InputField.ContentType.DecimalNumber;
                input.lineType = TMP_InputField.LineType.SingleLine;
                input.characterLimit = 12;
            }
            return field;
        }

        private static void Show(PanelTextField field, float value, string format)
        {
            if (field._inputField != null && field._inputField.isFocused)
            {
                return;
            }
            field.SetValueWithoutNotify(value.ToString(format, CultureInfo.InvariantCulture));
        }

        private static void ShowText(PanelTextField field, string value)
        {
            if (field._inputField != null && field._inputField.isFocused)
            {
                return;
            }
            field.SetValueWithoutNotify(value);
        }

        private static bool TryParse(string text, out float value)
        {
            return float.TryParse((text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void SetFieldsActive(bool active, params PanelTextField[] fields)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i].Descriptor.SetActive(active);
            }
        }

        private static void AssignEntries(PanelDropdown target)
        {
            List<string> entries = new List<string> { NoneEntry };
            List<string> labels = new List<string> { BasisLocalization.Get("settings.developer.spawnAnchors.none") };
            int count = BasisSpawnAnchors.Count;
            for (int i = 0; i < count; i++)
            {
                entries.Add(i.ToString());
                labels.Add(Describe(BasisSpawnAnchors.Anchors[i]));
            }
            target.AssignEntries(entries, labels);
        }

        public static string Describe(BasisSpawnAnchors.SpawnAnchor anchor)
        {
            Vector3 p = anchor.Position;
            string label = $"{anchor.Name}  ({p.x:0.0}, {p.y:0.0}, {p.z:0.0})";
            return anchor.OverrideScale ? $"{label}  ×{anchor.Scale:0.00}" : label;
        }

        private static void RevealFolder(string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(BasisSpawnAnchors.ResolvePath(path));
                if (string.IsNullOrEmpty(folder))
                {
                    return;
                }
                Directory.CreateDirectory(folder);
                BasisFileBrowserUtility.Reveal(folder);
            }
            catch (System.Exception e)
            {
                BasisDebug.LogWarning($"Could not open the spawn anchors folder: {e.Message}");
            }
        }
    }
}
