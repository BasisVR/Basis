using System;
using System.Collections.Generic;
using UnityEditor;

using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.Scripts.BasisSdk.Helpers.Editor
{
    public static class BasisHelpersGizmo
    {
        public static void PositionHandler(ref Vector3 Position, Quaternion Rotation)
        {
            Position = Handles.PositionHandle(Position, Rotation);
        }
        public static Button Button(VisualElement VisualElement, string ButttonNameIdentifier)
        {
            // Find the button by name
            Button button = VisualElement.Q<Button>(ButttonNameIdentifier);

            // Check if the button is found
            if (button != null)
            {
                // Subscribe to the button click event
                return button;
            }
            else
            {
                Debug.LogError("Button not found! " + ButttonNameIdentifier);
                return null;
            }
        }
        public static EventCallback<ChangeEvent<Vector2>> CallBackVector2Field(
            VisualElement visualElement,
            string fieldNameIdentifier,
            Vector2 initialValue,
            EventCallback<ChangeEvent<Vector2>> callback)
        {
            Vector2Field field = visualElement.Q<Vector2Field>(fieldNameIdentifier);
            if (field != null)
            {
                field.value = initialValue;
                field.RegisterCallback(callback);
                return callback;
            }
            else
            {
                return null;
            }
        }

        public static void LabelVector2Field(VisualElement visualElement, string fieldNameIdentifier, string fieldLabel, string firstComponentLabel, string secondComponentLabel)
        {
            Vector2Field field = visualElement.Q<Vector2Field>(fieldNameIdentifier);
            if (field == null)
            {
                Debug.LogError("Vector2Field not found! " + fieldNameIdentifier);
                return;
            }

            field.label = fieldLabel;

            List<FloatField> components = field.Query<FloatField>().ToList();
            if (components.Count < 2)
            {
                return;
            }

            LabelCompositeComponent(components[0], firstComponentLabel);
            LabelCompositeComponent(components[1], secondComponentLabel);
        }

        private static void LabelCompositeComponent(FloatField component, string text)
        {
            component.label = text;

            Label label = component.labelElement;
            if (label == null)
            {
                return;
            }

            label.style.minWidth = StyleKeyword.Auto;
            label.style.width = StyleKeyword.Auto;
            label.style.maxWidth = StyleKeyword.None;
            label.style.flexBasis = StyleKeyword.Auto;
            label.style.flexGrow = 0f;
            label.style.flexShrink = 0f;
            label.style.overflow = Overflow.Visible;
            label.style.textOverflow = TextOverflow.Clip;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.marginRight = 3f;

            VisualElement input = component.Q(className: BaseField<float>.inputUssClassName);
            if (input != null)
            {
                input.style.minWidth = 28f;
            }
        }

        public static void SetValueVector2Field(VisualElement visualElement, string fieldNameIdentifier, Vector2 Value)
        {
            Vector2Field Field = visualElement.Q<Vector2Field>(fieldNameIdentifier);
            if (Field != null)
            {
                Field.value = Value;
            }
        }
        public static EventCallback<ChangeEvent<Vector3>> CallBackVector3Field(VisualElement visualElement, string fieldNameIdentifier, Vector3 InitialValue)
        {
            Vector3Field Field = visualElement.Q<Vector3Field>(fieldNameIdentifier);
            if (Field != null)
            {
                Field.value = InitialValue;
                var changeEvent = new EventCallback<ChangeEvent<Vector3>>(evt => OnVector3FieldValueChanged(evt));
                Field.RegisterCallback(changeEvent);
                return changeEvent;
            }
            else
            {
                return null;
            }
        }

        private static void OnVector3FieldValueChanged(ChangeEvent<Vector3> evt)
        {
        }

        public static void SetValueVector3Field(VisualElement visualElement, string fieldNameIdentifier, Vector3 Value)
        {
            Vector3Field Field = visualElement.Q<Vector3Field>(fieldNameIdentifier);
            if (Field != null)
            {
                Field.value = Value;
            }
        }
    }
}
