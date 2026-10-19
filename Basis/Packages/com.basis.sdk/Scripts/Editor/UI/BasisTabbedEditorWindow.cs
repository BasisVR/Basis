using System;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One tab's worth of an editor window: its own state and its own <see cref="Draw"/>, with the host
/// owning the chrome. Lets two windows that were almost the same become two tabs of one window
/// without either one's body being rewritten.
/// </summary>
public abstract class BasisEditorTabPage
{
    /// <summary>Label on the tab strip.</summary>
    public abstract string Title { get; }

    /// <summary>Shown under the window title while this tab is selected.</summary>
    public virtual string Subtitle => null;

    /// <summary>The window hosting this tab — use it to request a repaint.</summary>
    public EditorWindow Host { get; internal set; }

    /// <summary>Called when the host window opens (not when the tab is selected).</summary>
    public virtual void OnEnable() { }

    /// <summary>Called when the host window closes.</summary>
    public virtual void OnDisable() { }

    /// <summary>Called on the editor's slow tick, only while this tab is visible.</summary>
    public virtual void OnInspectorUpdate() { }

    /// <summary>Draws the tab body. The host has already drawn the header and opened a scroll view.</summary>
    public abstract void Draw();
}

/// <summary>
/// A Basis-styled editor window made of tabs: pink-accented header, a tab strip, and a padded
/// scrolling body. Subclasses supply the title and the pages; everything else is shared, so the
/// windows built on it all look and behave the same.
/// </summary>
public abstract class BasisTabbedEditorWindow : EditorWindow
{
    private BasisEditorTabPage[] _pages;
    private int _tab;
    private Vector2 _scroll;

    /// <summary>Bold title at the top of the window.</summary>
    protected abstract string HeaderTitle { get; }

    /// <summary>One line under the title explaining what the whole window is for.</summary>
    protected virtual string HeaderSubtitle => null;

    /// <summary>Creates this window's tabs, in display order. Called once per window instance.</summary>
    protected abstract BasisEditorTabPage[] BuildPages();

    /// <summary>The tab currently on screen, or null before the window has built its pages.</summary>
    protected BasisEditorTabPage Current =>
        _pages != null && _tab >= 0 && _tab < _pages.Length ? _pages[_tab] : null;

    /// <summary>This window's first tab of the given type, or null when it has none.</summary>
    protected T Page<T>() where T : BasisEditorTabPage
    {
        if (_pages == null) return null;
        foreach (BasisEditorTabPage page in _pages)
        {
            if (page is T match) return match;
        }
        return null;
    }

    /// <summary>
    /// Brings a tab to the front, for when something outside the window opens it at a particular
    /// page. Returns false when the page does not belong to this window.
    /// </summary>
    protected bool SelectPage(BasisEditorTabPage page)
    {
        if (_pages == null || page == null) return false;

        int index = Array.IndexOf(_pages, page);
        if (index < 0) return false;

        _tab = index;
        EditorPrefs.SetInt(PrefKey, _tab);
        GUI.FocusControl(null);
        Repaint();
        return true;
    }

    protected virtual void OnEnable()
    {
        _pages = BuildPages() ?? Array.Empty<BasisEditorTabPage>();
        foreach (BasisEditorTabPage page in _pages)
        {
            page.Host = this;
            try { page.OnEnable(); }
            catch (Exception e) { Debug.LogError($"[{HeaderTitle}] tab '{page.Title}' failed to initialise: {e}"); }
        }
        _tab = Mathf.Clamp(EditorPrefs.GetInt(PrefKey, 0), 0, Mathf.Max(0, _pages.Length - 1));
    }

    protected virtual void OnDisable()
    {
        if (_pages == null) return;
        foreach (BasisEditorTabPage page in _pages)
        {
            try { page.OnDisable(); }
            catch (Exception e) { Debug.LogError($"[{HeaderTitle}] tab '{page.Title}' failed to shut down: {e}"); }
        }
    }

    protected virtual void OnInspectorUpdate() => Current?.OnInspectorUpdate();

    private string PrefKey => "Basis.Tabs." + GetType().Name;

    protected virtual void OnGUI()
    {
        if (_pages == null || _pages.Length == 0)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("sdk.editorUi.noTabs"), MessageType.Warning);
            return;
        }

        _scroll = BasisEditorUI.BeginPage(_scroll);

        BasisEditorTabPage page = Current;
        BasisEditorUI.Header(HeaderTitle, page?.Subtitle ?? HeaderSubtitle);

        if (_pages.Length > 1)
        {
            var labels = new string[_pages.Length];
            for (int i = 0; i < _pages.Length; i++) labels[i] = _pages[i].Title;

            int next = BasisEditorUI.Tabs(_tab, labels);
            if (next != _tab)
            {
                _tab = next;
                EditorPrefs.SetInt(PrefKey, _tab);
                GUI.FocusControl(null);
            }
            page = Current;
        }

        try
        {
            page?.Draw();
        }
        catch (ExitGUIException)
        {
            throw;
        }
        catch (Exception e)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("sdk.editorUi.drawFailed", page?.Title, e), MessageType.Error);
        }

        BasisEditorUI.EndPage();
    }
}
