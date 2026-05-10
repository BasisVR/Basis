using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Editor.Localization;
using Basis.Scripts.Behaviour;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Partial extension of BasisAvatarSDKInspector that populates the
/// "Network Behaviours" UXML foldout with discovered BasisAvatarMonoBehaviour types.
/// Shows attached components with Select/Remove, and available types with Add buttons.
/// Adding a component calls OnEditorSetup for auto-configuration.
/// Types can hide from this menu by shadowing: <c>new public static bool VisibleInAvatarMenu = false;</c>
/// </summary>
public partial class BasisAvatarSDKInspector
{
    private VisualElement _attachedContainer;
    private VisualElement _availableContainer;

    /// <summary>
    /// Queries the UXML containers and populates them with discovered behaviours.
    /// Call from CreateInspectorGUI after the UXML tree is cloned.
    /// </summary>
    public void SetupNetworkBehaviours()
    {
        _attachedContainer = uiElementsRoot.Q<VisualElement>(BasisSDKConstants.NetworkBehavioursAttached);
        _availableContainer = uiElementsRoot.Q<VisualElement>(BasisSDKConstants.NetworkBehavioursAvailable);

        if (_attachedContainer == null || _availableContainer == null)
        {
            Debug.LogError("Network Behaviours UXML containers not found.");
            return;
        }

        RefreshNetworkBehaviours();
    }

    /// <summary>
    /// Reads the static VisibleInAvatarMenu field on a BasisAvatarMonoBehaviour subclass.
    /// Uses reflection so subclasses can shadow the base field with their own value.
    /// Returns true if the field is missing (visible by default).
    /// </summary>
    private static bool IsVisibleInAvatarMenu(Type type)
    {
        FieldInfo field = type.GetField("VisibleInAvatarMenu", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (field != null && field.FieldType == typeof(bool))
        {
            return (bool)field.GetValue(null);
        }
        return true;
    }

    private void RefreshNetworkBehaviours()
    {
        _attachedContainer.Clear();
        _availableContainer.Clear();

        // ---- Discover all concrete subclasses that are visible in the menu ----
        var availableTypes = new List<Type>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch { continue; }

            foreach (var type in types)
            {
                if (!type.IsAbstract
                    && type.IsSubclassOf(typeof(BasisAvatarMonoBehaviour))
                    && IsVisibleInAvatarMenu(type))
                {
                    availableTypes.Add(type);
                }
            }
        }

        // ---- Attached components ----
        var existing = Avatar.GetComponentsInChildren<BasisAvatarMonoBehaviour>(true);
        var existingTypes = new HashSet<Type>();

        foreach (var comp in existing)
        {
            existingTypes.Add(comp.GetType());

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 2;

            var label = new Label(comp.GetType().Name);
            label.style.flexGrow = 1;
            row.Add(label);

            var selectBtn = new Button(() =>
            {
                Selection.activeGameObject = comp.gameObject;
                EditorGUIUtility.PingObject(comp);
            });
            selectBtn.text = BasisEditorLocalization.Get("sdk.networkBehaviours.select");
            selectBtn.style.width = 50;
            row.Add(selectBtn);

            var capturedComp = comp;
            var removeBtn = new Button(() =>
            {
                Undo.DestroyObjectImmediate(capturedComp);
                EditorUtility.SetDirty(Avatar);
                RefreshNetworkBehaviours();
            });
            removeBtn.text = BasisEditorLocalization.Get("sdk.networkBehaviours.remove");
            removeBtn.style.width = 60;
            row.Add(removeBtn);

            _attachedContainer.Add(row);
        }

        if (existing.Length == 0)
        {
            var noneLabel = new Label(BasisEditorLocalization.Get("sdk.networkBehaviours.noneAttached"));
            noneLabel.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
            _attachedContainer.Add(noneLabel);
        }

        // ---- Available to add ----
        foreach (var type in availableTypes)
        {
            if (existingTypes.Contains(type))
                continue;

            var capturedType = type;
            var addBtn = new Button(() =>
            {
                var comp = Undo.AddComponent(Avatar.gameObject, capturedType);
                if (comp is BasisAvatarMonoBehaviour basisComp)
                {
                    basisComp.OnEditorSetup(Avatar.gameObject, Avatar);
                }
                EditorUtility.SetDirty(Avatar);
                RefreshNetworkBehaviours();
            });
            addBtn.text = BasisEditorLocalization.Get("sdk.networkBehaviours.add", type.Name);
            addBtn.style.marginBottom = 2;
            _availableContainer.Add(addBtn);
        }

        if (_availableContainer.childCount == 0)
        {
            var allLabel = new Label(BasisEditorLocalization.Get("sdk.networkBehaviours.allAttached"));
            allLabel.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
            _availableContainer.Add(allLabel);
        }
    }
}
