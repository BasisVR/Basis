using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One visualiser layer's gizmo handles. Slots are handed out by draw order each frame and reused,
/// so a layer whose element count varies between frames parks the unused tail with
/// <see cref="BasisGizmoManager.SetGizmoActive"/> instead of leaking or misaligning it.
/// <para>
/// Wrap a frame's drawing in <see cref="Begin"/> / <see cref="End"/>. <see cref="Clear"/> destroys
/// every handle; <see cref="Forget"/> drops them without destroying, which is what a
/// <see cref="BasisGizmoManager.OnUseGizmosChanged"/> master wipe needs — the manager already
/// dropped those slots, so destroying them again only logs warnings.
/// </para>
/// </summary>
public sealed class BasisGizmoSet
{
    private readonly string _name;
    private readonly List<int> _spheres = new List<int>();
    private readonly List<int> _lines = new List<int>();
    private readonly List<int> _labels = new List<int>();
    private int _sphereCursor;
    private int _lineCursor;
    private int _labelCursor;

    /// <summary>
    /// Unity layer every gizmo in the set is put on, or -1 to follow
    /// <see cref="BasisGizmoManager.RenderLayer"/>. Set it before the first draw; it is applied
    /// when a handle is created, not when it is reused.
    /// </summary>
    public int Layer = -1;

    public BasisGizmoSet(string name)
    {
        _name = name;
    }

    public BasisGizmoSet(string name, int layer)
    {
        _name = name;
        Layer = layer;
    }

    public void Begin()
    {
        _sphereCursor = 0;
        _lineCursor = 0;
        _labelCursor = 0;
    }

    public void Sphere(Vector3 position, float size, Color color)
    {
        if (_sphereCursor == _spheres.Count)
        {
            BasisGizmoManager.CreateSphereGizmo(_name, out int created, position, size, color);
            ApplyLayer(created);
            _spheres.Add(created);
            _sphereCursor++;
            return;
        }

        int id = _spheres[_sphereCursor++];
        BasisGizmoManager.SetGizmoActive(id, true);
        BasisGizmoManager.UpdateSphereGizmo(id, position, Vector3.one * size);
        BasisGizmoManager.UpdateGizmoColor(id, color);
    }

    public void Line(Vector3 start, Vector3 end, Color color, float width)
    {
        if (_lineCursor == _lines.Count)
        {
            BasisGizmoManager.CreateLineGizmo(_name, out int created, start, end, width, color);
            ApplyLayer(created);
            _lines.Add(created);
            _lineCursor++;
            return;
        }

        int id = _lines[_lineCursor++];
        BasisGizmoManager.SetGizmoActive(id, true);
        BasisGizmoManager.UpdateLineGizmo(id, start, end);
        BasisGizmoManager.UpdateGizmoColor(id, color);
    }

    public void Poly(Vector3[] points, Color color, bool loop, float width)
    {
        if (_lineCursor == _lines.Count)
        {
            BasisGizmoManager.CreateLineGizmo(_name, out int created, points, width, color, loop);
            ApplyLayer(created);
            _lines.Add(created);
            _lineCursor++;
            return;
        }

        int id = _lines[_lineCursor++];
        BasisGizmoManager.SetGizmoActive(id, true);
        BasisGizmoManager.UpdateLineGizmo(id, points);
        BasisGizmoManager.UpdateGizmoColor(id, color);
    }

    public void Label(Vector3 position, string text, Color color, Vector3 viewer, float scale)
    {
        int id;
        if (_labelCursor == _labels.Count)
        {
            BasisGizmoManager.CreateTextGizmo(_name, out id, position, text, color);

            // A label rented back out of the pool starts on the container's layer, so this is the
            // point at which the layer sticks.
            ApplyLayer(id);
            _labels.Add(id);
            _labelCursor++;
        }
        else
        {
            id = _labels[_labelCursor++];
            BasisGizmoManager.SetGizmoActive(id, true);
        }
        BasisGizmoManager.UpdateTextGizmo(id, position, BasisGizmoManager.BillboardRotation(position, viewer), scale, text, color);
    }

    public void End()
    {
        for (int Index = _sphereCursor; Index < _spheres.Count; Index++)
        {
            BasisGizmoManager.SetGizmoActive(_spheres[Index], false);
        }
        for (int Index = _lineCursor; Index < _lines.Count; Index++)
        {
            BasisGizmoManager.SetGizmoActive(_lines[Index], false);
        }
        for (int Index = _labelCursor; Index < _labels.Count; Index++)
        {
            BasisGizmoManager.SetGizmoActive(_labels[Index], false);
        }
    }

    public void Clear()
    {
        for (int Index = 0; Index < _spheres.Count; Index++)
        {
            BasisGizmoManager.DestroyGizmo(_spheres[Index]);
        }
        for (int Index = 0; Index < _lines.Count; Index++)
        {
            BasisGizmoManager.DestroyGizmo(_lines[Index]);
        }
        for (int Index = 0; Index < _labels.Count; Index++)
        {
            BasisGizmoManager.DestroyGizmo(_labels[Index]);
        }
        Forget();
    }

    public void Forget()
    {
        _spheres.Clear();
        _lines.Clear();
        _labels.Clear();
        Begin();
    }

    private void ApplyLayer(int linkedID)
    {
        if (Layer >= 0)
        {
            BasisGizmoManager.SetGizmoLayer(linkedID, Layer);
        }
    }
}
