using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.UI;
using UnityEngine;

/// <summary>
/// Runtime visualisation of each pointer's physics raycast (the line from the raycast
/// origin out to EffectiveMaxDistance) plus the oriented placement box while a raycaster
/// is in Placement mode. Replaces BasisPointRaycaster.OnDrawGizmosSelected so the ray
/// renders in-game rather than only in the editor scene view. Driven from
/// SMModuleDebugOptions under the GizmoPointerRay toggle.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisPointerRayGizmos
{
    public static bool Show;

    private const float RayBaseWidth = 0.004f;
    private const float BoxBaseWidth = 0.004f;
    private static readonly Color RayColor = Color.cyan;
    private static readonly Color BoxColor = Color.yellow;

    private struct RayGizmo
    {
        public int RayLine;
        public int TopLoop;
        public int BottomLoop;
        public int Vert0;
        public int Vert1;
        public int Vert2;
        public int Vert3;
        public bool BoxCreated;
        public bool BoxVisible;
    }

    private static readonly Dictionary<BasisPointRaycaster, RayGizmo> _rays = new Dictionary<BasisPointRaycaster, RayGizmo>();
    private static readonly HashSet<BasisPointRaycaster> _seen = new HashSet<BasisPointRaycaster>();
    private static readonly List<BasisPointRaycaster> _stale = new List<BasisPointRaycaster>();
    private static readonly Vector3[] _ring = new Vector3[4];
    private static bool _hooked;

    public static void Tick(float scale)
    {
        EnsureMasterHook();

        if (!Show)
        {
            Shutdown();
            return;
        }

        BasisDeviceManagement manager = BasisDeviceManagement.Instance;
        if (manager == null)
        {
            Shutdown();
            return;
        }

        if (scale <= 0f)
        {
            scale = 1f;
        }
        float rayWidth = RayBaseWidth * scale;
        float boxWidth = BoxBaseWidth * scale;

        _seen.Clear();
        BasisObservableList<BasisInput> devices = manager.AllInputDevices;
        int count = devices.Count;
        for (int i = 0; i < count; i++)
        {
            BasisInput input = devices[i];
            if (input == null)
            {
                continue;
            }
            BasisPointRaycaster caster = input.BasisPointRaycaster;
            if (caster == null)
            {
                continue;
            }

            _seen.Add(caster);
            _rays.TryGetValue(caster, out RayGizmo g);

            Ray ray = caster.ray;
            Vector3 start = ray.origin;
            Vector3 end = ray.origin + ray.direction * caster.EffectiveMaxDistance;

            if (g.RayLine <= 0)
            {
                BasisGizmoManager.CreateLineGizmo("PointerRay", out g.RayLine, start, end, rayWidth, RayColor);
            }
            else
            {
                BasisGizmoManager.UpdateLineGizmo(g.RayLine, start, end);
            }

            bool placing = caster.Mode == BasisPointRaycaster.ControlMode.Placement && caster.CurrentPlacement.HasHit;
            if (placing)
            {
                UpdateBox(ref g, caster.CurrentPlacement, boxWidth);
            }
            else if (g.BoxCreated && g.BoxVisible)
            {
                SetBoxActive(ref g, false);
            }

            _rays[caster] = g;
        }

        Prune();
    }

    public static void Shutdown()
    {
        if (_rays.Count == 0)
        {
            return;
        }
        foreach (KeyValuePair<BasisPointRaycaster, RayGizmo> kvp in _rays)
        {
            Destroy(kvp.Value);
        }
        _rays.Clear();
    }

    private static void UpdateBox(ref RayGizmo g, in BasisPointRaycaster.PlacementResult p, float width)
    {
        Vector3 c = p.Center;
        Quaternion r = p.Rotation;
        Vector3 e = p.Extents;

        Vector3 rx = r * new Vector3(e.x, 0f, 0f);
        Vector3 ry = r * new Vector3(0f, e.y, 0f);
        Vector3 rz = r * new Vector3(0f, 0f, e.z);

        Vector3 t0 = c + ry + rx + rz;
        Vector3 t1 = c + ry + rx - rz;
        Vector3 t2 = c + ry - rx - rz;
        Vector3 t3 = c + ry - rx + rz;
        Vector3 b0 = c - ry + rx + rz;
        Vector3 b1 = c - ry + rx - rz;
        Vector3 b2 = c - ry - rx - rz;
        Vector3 b3 = c - ry - rx + rz;

        if (!g.BoxCreated)
        {
            _ring[0] = t0; _ring[1] = t1; _ring[2] = t2; _ring[3] = t3;
            BasisGizmoManager.CreateLineGizmo("PointerBox_Top", out g.TopLoop, _ring, width, BoxColor, loop: true);
            _ring[0] = b0; _ring[1] = b1; _ring[2] = b2; _ring[3] = b3;
            BasisGizmoManager.CreateLineGizmo("PointerBox_Bottom", out g.BottomLoop, _ring, width, BoxColor, loop: true);
            BasisGizmoManager.CreateLineGizmo("PointerBox_V0", out g.Vert0, t0, b0, width, BoxColor);
            BasisGizmoManager.CreateLineGizmo("PointerBox_V1", out g.Vert1, t1, b1, width, BoxColor);
            BasisGizmoManager.CreateLineGizmo("PointerBox_V2", out g.Vert2, t2, b2, width, BoxColor);
            BasisGizmoManager.CreateLineGizmo("PointerBox_V3", out g.Vert3, t3, b3, width, BoxColor);
            g.BoxCreated = true;
            g.BoxVisible = true;
            return;
        }

        _ring[0] = t0; _ring[1] = t1; _ring[2] = t2; _ring[3] = t3;
        BasisGizmoManager.UpdateLineGizmo(g.TopLoop, _ring);
        _ring[0] = b0; _ring[1] = b1; _ring[2] = b2; _ring[3] = b3;
        BasisGizmoManager.UpdateLineGizmo(g.BottomLoop, _ring);
        BasisGizmoManager.UpdateLineGizmo(g.Vert0, t0, b0);
        BasisGizmoManager.UpdateLineGizmo(g.Vert1, t1, b1);
        BasisGizmoManager.UpdateLineGizmo(g.Vert2, t2, b2);
        BasisGizmoManager.UpdateLineGizmo(g.Vert3, t3, b3);
        SetBoxActive(ref g, true);
    }

    private static void SetBoxActive(ref RayGizmo g, bool active)
    {
        if (!g.BoxCreated || g.BoxVisible == active)
        {
            return;
        }
        BasisGizmoManager.SetGizmoActive(g.TopLoop, active);
        BasisGizmoManager.SetGizmoActive(g.BottomLoop, active);
        BasisGizmoManager.SetGizmoActive(g.Vert0, active);
        BasisGizmoManager.SetGizmoActive(g.Vert1, active);
        BasisGizmoManager.SetGizmoActive(g.Vert2, active);
        BasisGizmoManager.SetGizmoActive(g.Vert3, active);
        g.BoxVisible = active;
    }

    private static void Destroy(RayGizmo g)
    {
        if (g.RayLine > 0) BasisGizmoManager.DestroyGizmo(g.RayLine);
        if (g.TopLoop > 0) BasisGizmoManager.DestroyGizmo(g.TopLoop);
        if (g.BottomLoop > 0) BasisGizmoManager.DestroyGizmo(g.BottomLoop);
        if (g.Vert0 > 0) BasisGizmoManager.DestroyGizmo(g.Vert0);
        if (g.Vert1 > 0) BasisGizmoManager.DestroyGizmo(g.Vert1);
        if (g.Vert2 > 0) BasisGizmoManager.DestroyGizmo(g.Vert2);
        if (g.Vert3 > 0) BasisGizmoManager.DestroyGizmo(g.Vert3);
    }

    private static void Prune()
    {
        if (_rays.Count == _seen.Count)
        {
            return;
        }
        _stale.Clear();
        foreach (KeyValuePair<BasisPointRaycaster, RayGizmo> kvp in _rays)
        {
            if (!_seen.Contains(kvp.Key))
            {
                _stale.Add(kvp.Key);
            }
        }
        for (int i = 0; i < _stale.Count; i++)
        {
            if (_rays.TryGetValue(_stale[i], out RayGizmo g))
            {
                Destroy(g);
                _rays.Remove(_stale[i]);
            }
        }
    }

    private static void EnsureMasterHook()
    {
        if (_hooked)
        {
            return;
        }
        BasisGizmoManager.OnUseGizmosChanged += OnMasterToggleChanged;
        _hooked = true;
    }

    // Master toggle going off destroys BasisGizmoManager's dictionaries, so our cached
    // IDs are stale — forget them so the next Tick re-creates cleanly.
    private static void OnMasterToggleChanged(bool state)
    {
        if (!state)
        {
            _rays.Clear();
        }
    }
}
