using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Scripts.UI
{
    public static class BasisTextFieldCaret
    {
        private static readonly Event CaretEvent = new Event();

        private static TMP_Text overflowPatchedLabel;
        private static TextOverflowModes overflowPreviousMode;

        private static void EnsureOverflowForEditing(TMP_Text label)
        {
            if (overflowPatchedLabel == label)
            {
                return;
            }
            RestoreOverflowMode();
            if (label.overflowMode != TextOverflowModes.Overflow)
            {
                overflowPatchedLabel = label;
                overflowPreviousMode = label.overflowMode;
                label.overflowMode = TextOverflowModes.Overflow;
            }
        }

        public static void RestoreOverflowMode()
        {
            if (overflowPatchedLabel)
            {
                overflowPatchedLabel.overflowMode = overflowPreviousMode;
                RectTransform textRect = overflowPatchedLabel.rectTransform;
                Vector2 anchored = textRect.anchoredPosition;
                if (anchored.x != 0f)
                {
                    textRect.anchoredPosition = new Vector2(0f, anchored.y);
                }
            }
            overflowPatchedLabel = null;
        }

        public static void MoveCaret(TMP_InputField tmp, InputField legacy, int direction, bool shift, bool ctrl)
        {
            if (direction == 0)
            {
                return;
            }

            CaretEvent.type = EventType.KeyDown;
            CaretEvent.character = '\0';
            CaretEvent.keyCode = direction < 0 ? KeyCode.LeftArrow : KeyCode.RightArrow;

            EventModifiers modifiers = EventModifiers.None;
            if (shift)
            {
                modifiers |= EventModifiers.Shift;
            }
            if (ctrl)
            {
                modifiers |= EventModifiers.Control;
            }
            CaretEvent.modifiers = modifiers;

            if (tmp != null)
            {
                tmp.ProcessEvent(CaretEvent);
                tmp.ForceLabelUpdate();
                EnsureCaretVisible(tmp);
            }
            else if (legacy != null)
            {
                legacy.ProcessEvent(CaretEvent);
                legacy.ForceLabelUpdate();
            }
        }

        public static void EnsureCaretVisible(TMP_InputField tmp)
        {
            if (tmp == null || tmp.lineType != TMP_InputField.LineType.SingleLine)
            {
                return;
            }
            TMP_Text label = tmp.textComponent;
            if (label == null)
            {
                return;
            }
            RectTransform textRect = label.rectTransform;
            RectTransform viewport = tmp.textViewport;
            if (viewport == null)
            {
                viewport = textRect.parent as RectTransform;
                if (viewport == null)
                {
                    return;
                }
            }

            EnsureOverflowForEditing(label);
            label.ForceMeshUpdate();
            TMP_TextInfo info = label.textInfo;
            int characterCount = info.characterCount;

            Rect viewportRect = viewport.rect;
            Vector2 anchored = textRect.anchoredPosition;

            if (characterCount == 0 || label.preferredWidth <= viewportRect.width)
            {
                if (anchored.x != 0f)
                {
                    textRect.anchoredPosition = new Vector2(0f, anchored.y);
                }
                return;
            }

            int caretIndex = Mathf.Clamp(tmp.caretPosition, 0, characterCount);
            float caretX = caretIndex <= 0
                ? info.characterInfo[0].origin
                : info.characterInfo[caretIndex - 1].xAdvance;

            float caretViewportX = caretX + textRect.localPosition.x;

            float rightOffset = viewportRect.xMax - (caretViewportX + label.margin.z + tmp.caretWidth);
            if (rightOffset < 0f)
            {
                textRect.anchoredPosition += new Vector2(rightOffset, 0f);
            }

            float leftOffset = (caretViewportX - label.margin.x) - viewportRect.xMin;
            if (leftOffset < 0f)
            {
                textRect.anchoredPosition += new Vector2(-leftOffset, 0f);
            }
        }

        private static int ResolveCaret(TMP_InputField tmp, int textLength)
        {
            return Mathf.Clamp(
                Mathf.Max(tmp.selectionStringAnchorPosition, tmp.selectionStringFocusPosition),
                0,
                textLength);
        }

        private static int ResolveCaret(InputField legacy, int textLength)
        {
            return Mathf.Clamp(
                Mathf.Max(legacy.selectionAnchorPosition, legacy.selectionFocusPosition),
                0,
                textLength);
        }

        public static void InsertAtCaret(TMP_InputField tmp, InputField legacy, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (tmp != null)
            {
                string text = tmp.text ?? string.Empty;
                int position = ResolveCaret(tmp, text.Length);
                tmp.text = text.Insert(position, value);
                tmp.stringPosition = Mathf.Min(position + value.Length, tmp.text.Length);
                EnsureCaretVisible(tmp);
            }
            else if (legacy != null)
            {
                string text = legacy.text ?? string.Empty;
                int position = ResolveCaret(legacy, text.Length);
                legacy.text = text.Insert(position, value);
                legacy.caretPosition = Mathf.Min(position + value.Length, legacy.text.Length);
                legacy.ForceLabelUpdate();
            }
        }

        public static void DeleteBeforeCaret(TMP_InputField tmp, InputField legacy)
        {
            if (tmp != null)
            {
                string text = tmp.text;
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }
                int position = ResolveCaret(tmp, text.Length);
                if (position <= 0)
                {
                    return;
                }
                tmp.text = text.Remove(position - 1, 1);
                tmp.stringPosition = position - 1;
                EnsureCaretVisible(tmp);
            }
            else if (legacy != null)
            {
                string text = legacy.text;
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }
                int position = ResolveCaret(legacy, text.Length);
                if (position <= 0)
                {
                    return;
                }
                legacy.text = text.Remove(position - 1, 1);
                legacy.caretPosition = position - 1;
                legacy.ForceLabelUpdate();
            }
        }
    }
}
