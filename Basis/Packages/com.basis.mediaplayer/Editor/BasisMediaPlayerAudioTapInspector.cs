using Basis.Editor.Localization;
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The tap has no serialized fields, so this draws the filter-ordering note
/// and, when filters have ended up above the tap, the offer to put it back on
/// top. It also has to be a UIElements inspector: Unity's default
/// MonoBehaviour inspector draws a level meter for any script with an
/// OnAudioFilterRead, and that IMGUI path dereferences GUIView.current without
/// a null check, which throws whenever the inspector redraws outside a GUIView
/// repaint (adding a component, for instance).
/// </summary>
[CustomEditor(typeof(BasisMediaPlayerAudioTap))]
public class BasisMediaPlayerAudioTapInspector : Editor
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();
        root.Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.tap.note"), HelpBoxMessageType.Info));
        root.Add(new BasisMediaPlayerTapOrdering.Notice(Source, _ => BasisEditorLocalization.Get("editor.mediaPlayer.tap.warning")));
        return root;
    }

    // Resolved per call, never held: the AudioSource can be replaced on this
    // object while the notice is up.
    private AudioSource[] Source()
    {
        var tap = target as BasisMediaPlayerAudioTap;
        AudioSource src = tap != null ? tap.GetComponent<AudioSource>() : null;
        return src != null ? new[] { src } : Array.Empty<AudioSource>();
    }
}
