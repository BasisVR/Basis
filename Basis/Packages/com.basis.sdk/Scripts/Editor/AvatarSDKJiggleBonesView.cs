using Basis.Scripts.BasisSdk;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

public class AvatarSDKJiggleBonesView
{
    public ListView JiggleStrainsList;
    public BasisJiggleBonesComponentInspector Inspector;
    public SerializedProperty BasisJiggleStrainProperty;
     public VisualElement AddButtonhere;
     public static string JiggleStrain = "JiggleStrains";
    public void Initialize(BasisJiggleBonesComponentInspector BasisJiggleBonesComponentInspector)
    {
        Inspector = BasisJiggleBonesComponentInspector;
        BasisJiggleStrainProperty = Inspector.serializedObject.FindProperty(JiggleStrain);
        AddButtonhere = BasisJiggleBonesComponentInspector.rootElement;
        //AddButtonhere = Inspector.rootElement.Q<VisualElement>(JiggleStrain);
        // Add button to add new jiggle strain
        Button addButton = new Button(AddNewBasisJiggleStrain)
        {
            text = "Add New Basis Jiggle Strain"
        };
        AddButtonhere.Add(addButton);
        CreateJiggleStrain();
        RefreshListView();
    }

    public void CreateJiggleStrain()
    {
        // Initialize and configure ListView
        JiggleStrainsList = new ListView
        {
            bindingPath = "basisJiggleStrains",
            virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
            makeItem = MakeListItem,
            bindItem = BindListItem,
            itemsSource = new List<BasisJiggleStrain>(),
        };
        AddButtonhere.Add(JiggleStrainsList);
    }
    private Foldout MakeListItem()
    {
        var container = new VisualElement
        {
            style = {
                flexDirection = FlexDirection.Column,
            }
        };

        // Add a foldout for each JiggleStrain
        Foldout foldout = new Foldout
        {
            text = "Jiggle Bone",
            value = false, // Set the initial value to expanded
            style = {
                marginLeft = 10,
            }
        };
        foldout.Add(container);

        // Inside the foldout, you can add fields specific to each JiggleStrain if needed

        return foldout;
    }
    private void AddNewBasisJiggleStrain()
    {
        Inspector.serializedObject.Update(); // Ensure serialized object is up-to-date
        BasisJiggleStrainProperty.arraySize++; // Increase array size
        Inspector.serializedObject.ApplyModifiedProperties(); // Apply changes
        RefreshListView(); // Refresh the ListView
        // Check if the array size is greater than 0
        if (BasisJiggleStrainProperty.arraySize > 0)
        {
            // Get the newly added BasisJiggleStrain
            var newJiggleStrain = Inspector.BonesComponent.JiggleStrains.Last();

            // Set default values
            newJiggleStrain.GravityMultiplier = 0.1f;
            newJiggleStrain.Friction = 0.3f;
            newJiggleStrain.AngleElasticity = 0.6f;
            newJiggleStrain.Blend = 1f;
            newJiggleStrain.AirDrag = 0.01f;
            newJiggleStrain.LengthElasticity = 0.4f;
            newJiggleStrain.ElasticitySoften = 0.2f;
            newJiggleStrain.RadiusMultiplier = 0.01f;
            newJiggleStrain.RootTransform = null; // You may want to set a default value here if applicable
            newJiggleStrain.IgnoredTransforms = null; // Clear the list
            newJiggleStrain.Colliders = null; // Clear the list

            Inspector.serializedObject.ApplyModifiedProperties(); // Apply changes
            RefreshListView(); // Refresh the ListView
        }
        else
        {
            Debug.LogError("Array size is 0. Unable to add new BasisJiggleStrain.");
        }
    }
    private void BindListItem(VisualElement element, int index)
    {
        if (index < 0 || index >= BasisJiggleStrainProperty.arraySize)
        {
            Debug.LogWarning("Invalid index for binding");
            return;
        }

        var itemProperty = BasisJiggleStrainProperty.GetArrayElementAtIndex(index);
        element.Clear();

        var fields = new[]
        {
            ("GravityMultiplier", "Gravity Multiplier"),
            ("Friction", "Friction"),
            ("AngleElasticity", "Angle Elasticity"),
            ("Blend", "Blend"),
            ("AirDrag", "Air Drag"),
            ("LengthElasticity", "Length Elasticity"),
            ("ElasticitySoften", "Elasticity Soften"),
            ("RadiusMultiplier", "Radius Multiplier")
        };

        foreach (var (propertyName, label) in fields)
        {
            AddPropertyField(element, itemProperty, propertyName, label);
        }

        DrawSingleObjectField(element, itemProperty, "RootTransform", "Root Transform", typeof(Transform));
        AddObjectField(element, itemProperty, "IgnoredTransforms", "Ignored Transforms", typeof(Transform));
        AddObjectField(element, itemProperty, "Colliders", "Colliders", typeof(Collider));

        Button removeButton = new Button(() => RemoveBasisJiggleStrain(index)) { text = "Remove" };
        element.Add(removeButton);

        element.style.height = StyleKeyword.Auto;
    }

    private void AddPropertyField(VisualElement root, SerializedProperty itemProperty, string propertyName, string label)
    {
        var property = itemProperty.FindPropertyRelative(propertyName);
        if (property != null)
        {
            var propertyField = new PropertyField(property, label);
            propertyField.Bind(Inspector.serializedObject);
            root.Add(propertyField);
        }
        else
        {
            Debug.LogWarning($"Property {propertyName} not found in serialized object.");
        }
    }
    private void AddObjectField(VisualElement root, SerializedProperty itemProperty, string propertyName, string label, System.Type objectType)
    {
        var property = itemProperty.FindPropertyRelative(propertyName);
        if (property != null)
        {
            // Create a horizontal containers
            var container = CreateHorizontalElement();
            root.Add(container);
            property.isExpanded = true;
            Foldout foldout = new Foldout { text = label, value = property.isExpanded };
            container.Add(foldout);

            for (int Index = 0; Index < property.arraySize; Index++)
            {
                BindArrayElement(foldout, property, Index, objectType);
            }

            Button addButton = new Button(() =>
            {
                property.arraySize++;
                Inspector.serializedObject.ApplyModifiedProperties();
                BindArrayElement(foldout, property, property.arraySize - 1, objectType);
            })
            {
                text = "Add " + objectType.Name
            };

            // Add the button to the right of the foldout
            container.Add(addButton);
        }
        else
        {
            Debug.LogWarning($"Property {propertyName} not found in serialized object.");
        }
    }
    public void DrawSingleObjectField(VisualElement root, SerializedProperty itemProperty, string propertyName, string label, System.Type objectType)
    {
        var property = itemProperty.FindPropertyRelative(propertyName);
        if (property != null)
        {
            ObjectField objectField = new ObjectField(label)
            {
                objectType = objectType,
                bindingPath = property.propertyPath
            };
            objectField.BindProperty(property);
            root.Add(objectField);
        }
    }
    public VisualElement CreateHorizontalElement()
    {
        // Create a horizontal container
        var container = new VisualElement
        {
            style =
        {
            flexDirection = FlexDirection.Row,
            alignItems = Align.Center // Align items vertically center
        }
        };
        return container;
    }

    private void BindArrayElement(VisualElement parent, SerializedProperty arrayProperty, int index, System.Type objectType)
    {
        var elementProperty = arrayProperty.GetArrayElementAtIndex(index);

        // Create a horizontal container
        var container = CreateHorizontalElement();
        // Create the object field
        var objectField = new ObjectField
        {
            objectType = objectType,
            value = elementProperty.objectReferenceValue,
            bindingPath = elementProperty.propertyPath
        };
        objectField.RegisterValueChangedCallback(evt =>
        {
            elementProperty.objectReferenceValue = evt.newValue;
            Inspector.serializedObject.ApplyModifiedProperties();
        });

        // Add object field to the container
        container.Add(objectField);

        // Create the remove button
        var removeButton = new Button(() =>
        {
            arrayProperty.DeleteArrayElementAtIndex(index);
            Inspector.serializedObject.ApplyModifiedProperties();
            RefreshListView(); // Refresh the ListView after removal
        })
        { text = "Remove" };

        // Add remove button to the container
        container.Add(removeButton);

        // Add the container to the parent
        parent.Add(container);
    }

    private void RefreshListView()
    {
        EditorUtility.SetDirty(Inspector.BonesComponent);
        Inspector.serializedObject.ApplyModifiedProperties();
        JiggleStrainsList.itemsSource = Inspector.BonesComponent.JiggleStrains;
        JiggleStrainsList.Rebuild();
    }

    private void RemoveBasisJiggleStrain(int index)
    {
        if (index < 0 || index >= Inspector.BonesComponent.JiggleStrains.Length)
        {
            Debug.LogWarning("Invalid index for removal");
            return;
        }
        Inspector.BonesComponent.JiggleStrains = RemoveAt(Inspector.BonesComponent.JiggleStrains, index);

        Inspector.serializedObject.ApplyModifiedProperties();
        RefreshListView();
    }
    static T[] RemoveAt<T>(T[] array, int index)
    {
        if (index < 0 || index >= array.Length)
            throw new ArgumentOutOfRangeException("Index is out of range.");

        // Create a new array with a size that is one less than the original array
        T[] result = new T[array.Length - 1];

        // Copy elements before the index
        Array.Copy(array, 0, result, 0, index);

        // Copy elements after the index
        Array.Copy(array, index + 1, result, index, array.Length - index - 1);

        return result;
    }
}
