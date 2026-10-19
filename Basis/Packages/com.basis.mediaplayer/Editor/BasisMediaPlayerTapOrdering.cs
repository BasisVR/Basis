using Basis.Editor.Localization;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// An output plays through a <see cref="BasisMediaPlayerAudioTap"/>, which
/// generates the stream into the AudioSource's DSP block. Unity runs filters
/// in component order, so a tap below them (or a missing one, which is appended
/// when added at runtime) leaves every filter above it processing silence. Shared by
/// the audio and tap inspectors, which both offer to put the tap back on top.
/// </summary>
internal static class BasisMediaPlayerTapOrdering
{
    // An analysis output plays a written clip rather than generating into the
    // DSP block, so its filters run in the normal order and there is nothing
    // to raise.
    public static bool NeedsRaise(AudioSource src) =>
        src != null &&
        !(src.TryGetComponent(out BasisMediaAudioChannel channel) && channel.AnalysisFeed) &&
        BasisMediaPlayerAudioTap.FirstBypassedFilter(src) != null;

    // Adds the tap if absent and gets it above the filters, by raising the tap
    // or, failing that, lowering the filters past it. Returns false when
    // neither move is allowed.
    public static bool Fix(AudioSource src)
    {
        if (src == null) return false;

        // ComponentUtility's reorder calls are native, and whether they
        // register undo of their own isn't something the caller can see.
        // Record the object up front and collapse everything into one group,
        // so a single undo takes back the whole fix however many steps it
        // took, including a run that gave up part-way.
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Fix audio filter order");
        Undo.RegisterCompleteObjectUndo(src.gameObject, "Fix audio filter order");

        if (!src.TryGetComponent(out BasisMediaPlayerAudioTap tap))
        {
            tap = Undo.AddComponent<BasisMediaPlayerAudioTap>(src.gameObject);
        }

        bool fixedOrder = RaiseTap(src, tap) || LowerFilters(src);
        EditorUtility.SetDirty(src.gameObject);
        Undo.CollapseUndoOperations(group);
        return fixedOrder;
    }

    // One move, and it leaves the filters in the order they were written in.
    private static bool RaiseTap(AudioSource src, BasisMediaPlayerAudioTap tap)
    {
        while (NeedsRaise(src))
        {
            if (!ComponentUtility.MoveComponentUp(tap)) return false;
        }
        return true;
    }

    // A prefab instance refuses to move a component that came from the asset,
    // but the filters added on the instance still move. Lowest offender first:
    // taking the top one down would drop it past its neighbours and reverse
    // the chain they run in.
    private static bool LowerFilters(AudioSource src)
    {
        for (int guard = 0; guard < 256 && NeedsRaise(src); guard++)
        {
            Component filter = LowestFilterAboveTap(src);
            if (filter == null || !ComponentUtility.MoveComponentDown(filter)) return false;
        }
        return !NeedsRaise(src);
    }

    private static Component LowestFilterAboveTap(AudioSource src)
    {
        Component[] comps = src.GetComponents<Component>();
        int tap = -1;
        for (int i = 0; i < comps.Length; i++)
        {
            if (comps[i] is BasisMediaPlayerAudioTap) { tap = i; break; }
        }
        for (int i = tap - 1; i >= 0; i--)
        {
            if (comps[i] != null && BasisMediaPlayerAudioTap.IsAudioFilter(comps[i])) return comps[i];
        }
        return null;
    }

    // The warning and its fix button, shared by the audio and tap inspectors so
    // a change to either lands on both. `resolve` is called fresh on every poll
    // and on every click: the offending sources are never held between the two,
    // since a source swapped for a same-named one leaves the message identical
    // and so the notice can outlive what it named.
    internal sealed class Notice : VisualElement
    {
        private readonly Func<AudioSource[]> resolve;
        private readonly Func<List<string>, string> describe;
        private string signature;
        private bool blocked;

        public Notice(Func<AudioSource[]> resolveSources, Func<List<string>, string> describeOffenders)
        {
            resolve = resolveSources;
            describe = describeOffenders;
            Refresh();
            // The edit lands on components, not on a serialized property either
            // inspector could track, so poll. The scheduler stops on its own
            // once this element leaves the panel.
            schedule.Execute(Refresh).Every(500);
        }

        private List<AudioSource> Offenders()
        {
            var pending = new List<AudioSource>();
            AudioSource[] sources = resolve();
            if (sources == null) return pending;
            foreach (AudioSource src in sources)
            {
                if (NeedsRaise(src)) pending.Add(src);
            }
            return pending;
        }

        private void Refresh()
        {
            List<AudioSource> pending = Offenders();
            if (pending.Count == 0)
            {
                if (signature != null) { Clear(); signature = null; blocked = false; }
                return;
            }

            var names = new List<string>(pending.Count);
            foreach (AudioSource src in pending) names.Add(src.name);
            string message = describe(names);

            // Redraw only when what the notice says changes, so the poll can't
            // rebuild the button out from under a click.
            string want = blocked ? message + " [blocked]" : message;
            if (signature == want) return;
            signature = want;

            Clear();
            Add(new HelpBox(message, HelpBoxMessageType.Warning));
            Add(new Button(Apply) { text = BasisEditorLocalization.Get("editor.mediaPlayer.tapOrdering.fix") });
            if (blocked) Add(new HelpBox(BasisEditorLocalization.Get("editor.mediaPlayer.tapOrdering.reorderRefused"), HelpBoxMessageType.Info));
        }

        private void Apply()
        {
            blocked = false;
            foreach (AudioSource src in Offenders())
            {
                if (!Fix(src)) blocked = true;
            }
            Refresh();
        }
    }
}
