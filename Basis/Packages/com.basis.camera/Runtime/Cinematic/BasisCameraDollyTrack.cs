using System.Collections.Generic;
using UnityEngine;

namespace Basis.Cinematics
{
    /// <summary>
    /// The ordered chain of placed waypoints a Dolly shot rides. Spawning a point appends it to the
    /// end of the queue; its slot can be changed afterwards, which reshapes the path without moving
    /// anything in the world.
    /// </summary>
    public class BasisCameraDollyTrack
    {
        private const int SamplesPerSegment = 12;
        private const float LineWidth = 0.012f;
        private const string GizmoName = "CameraDollyTrack";

        /// <summary>Not looked up yet. A real layer index is 0-31 and -1 means the project has none.</summary>
        private const int LayerNotResolved = -2;

        private static int markerLayer = LayerNotResolved;

        /// <summary>
        /// Layer the path and its numbered labels draw on. The track is an authoring aid — it is
        /// laid out to frame a shot, so it must not appear in one. OverlayUI is what the capture
        /// camera culls and what the waypoint markers already use, so the player sees the whole
        /// track while a photo, a 360 or the video feed sees none of it.
        ///
        /// <para>A project without the layer hands back -1, which SetGizmoLayer reads as "stay on
        /// the shared gizmo layer" — still a usable track, just visible to the shot.</para>
        /// </summary>
        private static int MarkerLayer
        {
            get
            {
                if (markerLayer == LayerNotResolved)
                {
                    markerLayer = LayerMask.NameToLayer("OverlayUI");
                }
                return markerLayer;
            }
        }

        private readonly List<BasisCameraDollyWaypoint> waypoints = new List<BasisCameraDollyWaypoint>();
        private readonly List<Vector3> points = new List<Vector3>();
        private readonly List<int> labelIds = new List<int>();

        private Vector3[] lineBuffer = System.Array.Empty<Vector3>();
        private Color32[] lineColors = System.Array.Empty<Color32>();
        private int lineId = -1;
        private bool lineCreated;
        private bool lineVisible;
        private bool lineColored;

        /// <summary>
        /// Where the track's own options start, and so where a reset returns them to. Named rather
        /// than left in the field initializers alone, so the panel's defaults cannot drift off them.
        /// </summary>
        public const bool DefaultLooped = false;
        public const bool DefaultVisible = true;
        public const bool DefaultColorBySpeed = true;
        public const bool DefaultGridSnap = false;
        public const float DefaultGridSize = 0.25f;

        public bool Looped = DefaultLooped;
        public bool Visible = DefaultVisible;

        /// <summary>
        /// Paint the path by how fast the camera passes through each part of it rather than one
        /// flat colour. What the move is doing is otherwise invisible until it is played: a track
        /// laid out with points far apart is covered faster there, and the ease settings decide
        /// where it is still getting up to speed and where it is already coming off it.
        /// </summary>
        public bool ColorBySpeed = DefaultColorBySpeed;

        /// <summary>
        /// The move the speed colouring is drawn from. Set every frame from the fitted stack, so
        /// the path answers a speed or an ease change as it is made.
        /// </summary>
        public BasisCameraDollySettings Motion = BasisCameraDollySettings.Default;

        /// <summary>Whether a move is actually fitted. Manual and Follow have no speed to show.</summary>
        public bool MotionActive;

        public float MotionScale;

        public bool PaintsBySpeed => ColorBySpeed && MotionActive && Motion.mode == BasisCameraDollyMode.Play;

        /// <summary>
        /// How the track is shared. Read every refresh rather than cached, so a change of mode
        /// reaches the markers on the next frame without a separate apply path to keep in step.
        /// </summary>
        public BasisCameraDollySync SyncMode = BasisCameraDollySync.LocalOnly;

        /// <summary>
        /// Whether this client authored the track, as opposed to mirroring somebody else's. Only
        /// the author may reshape a locked track.
        /// </summary>
        public bool IsAuthor = true;
        public bool GridSnap = DefaultGridSnap;
        public float GridSize = DefaultGridSize;

        public IReadOnlyList<BasisCameraDollyWaypoint> Waypoints => waypoints;

        /// <summary>World positions of the queue, rebuilt by <see cref="Refresh"/>.</summary>
        public IReadOnlyList<Vector3> Points => points;

        public int Count => waypoints.Count;

        /// <summary>
        /// Whether the points on this client may be picked up. A locked track is readable by
        /// everyone and reshapeable only by whoever authored it; the other two modes leave the
        /// points alone, so a track you can see is a track you can move unless it says otherwise.
        /// </summary>
        public bool CanMovePoints => SyncMode != BasisCameraDollySync.NetworkedLocked || IsAuthor;

        /// <summary>A Dolly shot needs at least two points before there is a path to ride.</summary>
        public bool HasPath => waypoints.Count >= 2;

        /// <summary>Spawns a waypoint and appends it to the end of the queue.</summary>
        public BasisCameraDollyWaypoint AddWaypoint(Vector3 position, Quaternion rotation, float scale)
        {
            BasisCameraDollyWaypoint waypoint = BasisCameraDollyWaypoint.Spawn(position, rotation, scale, GridSnap, GridSize);
            waypoints.Add(waypoint);
            RebuildIndices();
            return waypoint;
        }

        public bool RemoveWaypointAt(int index)
        {
            if (index < 0 || index >= waypoints.Count)
            {
                return false;
            }

            BasisCameraDollyWaypoint waypoint = waypoints[index];
            waypoints.RemoveAt(index);
            if (waypoint != null)
            {
                Object.Destroy(waypoint.gameObject);
            }

            ReleaseLabel(waypoints.Count);
            RebuildIndices();
            return true;
        }

        /// <summary>
        /// Changes a waypoint's slot in the queue. This is the reorder behind the panel's position
        /// field: the marker stays exactly where it was placed, only its place in the path changes.
        /// </summary>
        public bool MoveWaypoint(int fromIndex, int toIndex)
        {
            if (!MoveInList(waypoints, fromIndex, toIndex))
            {
                return false;
            }
            RebuildIndices();
            return true;
        }

        /// <summary>
        /// Pure list reorder shared by the track and the shot list. Clamps the destination rather
        /// than rejecting it, so "move to slot 99" lands at the end instead of doing nothing.
        /// </summary>
        public static bool MoveInList<T>(IList<T> list, int fromIndex, int toIndex)
        {
            if (list == null || list.Count == 0)
            {
                return false;
            }
            if (fromIndex < 0 || fromIndex >= list.Count)
            {
                return false;
            }

            toIndex = Mathf.Clamp(toIndex, 0, list.Count - 1);
            if (toIndex == fromIndex)
            {
                return false;
            }

            T item = list[fromIndex];
            list.RemoveAt(fromIndex);
            list.Insert(toIndex, item);
            return true;
        }

        public void Clear()
        {
            for (int Index = 0; Index < waypoints.Count; Index++)
            {
                if (waypoints[Index] != null)
                {
                    Object.Destroy(waypoints[Index].gameObject);
                }
            }
            waypoints.Clear();
            points.Clear();
            ReleaseAllLabels();
            HideLine();
        }

        public int IndexOf(BasisCameraDollyWaypoint waypoint) => waypoints.IndexOf(waypoint);

        public BasisCameraDollyWaypoint GetWaypoint(int index)
            => index >= 0 && index < waypoints.Count ? waypoints[index] : null;

        /// <summary>
        /// Rebuilds the point cache from the live marker transforms and redraws the path. Called
        /// every frame because a grabbed waypoint moves the path under the shot as it is carried.
        /// </summary>
        public void Refresh(float scale, Quaternion labelFacing)
        {
            PruneDestroyed();

            points.Clear();
            for (int Index = 0; Index < waypoints.Count; Index++)
            {
                points.Add(waypoints[Index].Position);
            }

            if (!Visible)
            {
                HideVisuals();
                return;
            }

            for (int Index = 0; Index < waypoints.Count; Index++)
            {
                BasisCameraDollyWaypoint waypoint = waypoints[Index];
                waypoint.SetVisible(true);
                waypoint.SetMovable(CanMovePoints);
                waypoint.SetScale(scale);
                waypoint.SetColor(ColorForIndex(Index, waypoints.Count));
            }

            UpdateLine(scale);
            UpdateLabels(scale, labelFacing);
        }

        public void SetVisible(bool visible)
        {
            Visible = visible;
            if (!visible)
            {
                HideVisuals();
            }
        }

        public void SetGridSnap(bool enabled, float size)
        {
            GridSnap = enabled;
            GridSize = size;
            for (int Index = 0; Index < waypoints.Count; Index++)
            {
                if (waypoints[Index] != null)
                {
                    waypoints[Index].SetGridSnap(enabled, size);
                }
            }
        }

        /// <summary>The path's own colour, for a track with no move fitted to read a speed off.</summary>
        public static readonly Color RestingColor = new Color(0.4f, 0.85f, 1f);

        /// <summary>Direction of travel, green at the head of the queue through red at the tail.</summary>
        public static Color ColorForIndex(int index, int count)
        {
            if (count <= 1)
            {
                return new Color(0.3f, 1f, 0.45f);
            }
            float t = index / (float)(count - 1);
            return Color.Lerp(new Color(0.3f, 1f, 0.45f), new Color(1f, 0.35f, 0.3f), t);
        }

        private void PruneDestroyed()
        {
            bool removed = false;
            for (int Index = waypoints.Count - 1; Index >= 0; Index--)
            {
                if (waypoints[Index] == null)
                {
                    waypoints.RemoveAt(Index);
                    removed = true;
                }
            }

            if (removed)
            {
                while (labelIds.Count > waypoints.Count)
                {
                    ReleaseLabel(labelIds.Count - 1);
                }
                RebuildIndices();
            }
        }

        private void RebuildIndices()
        {
            for (int Index = 0; Index < waypoints.Count; Index++)
            {
                if (waypoints[Index] != null)
                {
                    waypoints[Index].DisplayIndex = Index + 1;
                }
            }
        }

        private void UpdateLine(float scale)
        {
            if (!HasPath)
            {
                HideLine();
                return;
            }

            float max = BasisCameraSpline.MaxPosition(points.Count, Looped);
            int samples = Mathf.Max(2, Mathf.CeilToInt(max * SamplesPerSegment)) + 1;

            if (lineBuffer.Length != samples)
            {
                lineBuffer = new Vector3[samples];
            }

            float step = max / (samples - 1);
            for (int Index = 0; Index < samples; Index++)
            {
                lineBuffer[Index] = BasisCameraSpline.Evaluate(points, Index * step, Looped);
            }

            if (!lineCreated)
            {
                BasisGizmoManager.CreateLineGizmo(GizmoName, out lineId, lineBuffer, LineWidth, RestingColor, Looped);
                BasisGizmoManager.SetGizmoLayer(lineId, MarkerLayer);
                lineCreated = true;
                lineVisible = true;
                lineColored = false;
            }
            else
            {
                if (!lineVisible)
                {
                    BasisGizmoManager.SetGizmoActive(lineId, true);
                    lineVisible = true;
                }
                BasisGizmoManager.UpdateLineGizmo(lineId, lineBuffer);
            }

            ColorLine(samples, scale);
        }

        /// <summary>
        /// Writes a colour per sample from the speed the camera will pass through it at.
        ///
        /// <para>The distances come out of the sample buffer that was just built rather than being
        /// measured off the spline again: the playhead advances in waypoints at one rate, so how
        /// fast a stretch is covered in the world is exactly how far apart its samples landed
        /// against the average.</para>
        /// </summary>
        private void ColorLine(int samples, float scale)
        {
            if (!PaintsBySpeed)
            {
                if (lineColored)
                {
                    BasisGizmoManager.UpdateGizmoColor(lineId, RestingColor);
                    lineColored = false;
                }
                return;
            }

            if (lineColors.Length != samples)
            {
                lineColors = new Color32[samples];
            }

            float total = 0f;
            for (int Index = 1; Index < samples; Index++)
            {
                total += Vector3.Distance(lineBuffer[Index - 1], lineBuffer[Index]);
            }

            int spans = samples - 1;
            float mean = spans > 0 ? total / spans : 0f;
            float denominator = spans > 0 ? spans : 1f;

            for (int Index = 0; Index < samples; Index++)
            {
                int span = Mathf.Min(Index, spans - 1);
                float stretch = mean > 1e-5f
                    ? Vector3.Distance(lineBuffer[span], lineBuffer[span + 1]) / mean
                    : 1f;

                float metresPerSecond = BasisCameraDollySpeed.MetresPerSecond(
                    Motion, Index / denominator, Looped, stretch);
                lineColors[Index] = BasisCameraDollySpeed.Sample(metresPerSecond, MotionScale > 0f ? MotionScale : scale);
            }

            BasisGizmoManager.SetLineGizmoColors(lineId, lineColors, samples);
            lineColored = true;
        }

        private void UpdateLabels(float scale, Quaternion facing)
        {
            float labelScale = Mathf.Max(0.02f, 0.05f * scale);
            Vector3 lift = Vector3.up * (0.12f * scale);

            for (int Index = 0; Index < waypoints.Count; Index++)
            {
                Vector3 position = waypoints[Index].Position + lift;
                string text = (Index + 1).ToString();
                Color color = ColorForIndex(Index, waypoints.Count);

                if (Index >= labelIds.Count)
                {
                    BasisGizmoManager.CreateTextGizmo(GizmoName, out int newId, position, text, color);

                    // After the create, not before: a label rented back out of the pool starts on
                    // the container's layer, so this is the point at which the layer sticks.
                    BasisGizmoManager.SetGizmoLayer(newId, MarkerLayer);
                    labelIds.Add(newId);
                    continue;
                }

                BasisGizmoManager.SetGizmoActive(labelIds[Index], true);
                BasisGizmoManager.UpdateTextGizmo(labelIds[Index], position, facing, labelScale, text, color);
            }

            for (int Index = waypoints.Count; Index < labelIds.Count; Index++)
            {
                BasisGizmoManager.SetGizmoActive(labelIds[Index], false);
            }
        }

        private void HideVisuals()
        {
            for (int Index = 0; Index < waypoints.Count; Index++)
            {
                if (waypoints[Index] != null)
                {
                    waypoints[Index].SetVisible(false);
                }
            }
            HideLine();
            for (int Index = 0; Index < labelIds.Count; Index++)
            {
                BasisGizmoManager.SetGizmoActive(labelIds[Index], false);
            }
        }

        private void HideLine()
        {
            if (lineCreated && lineVisible)
            {
                BasisGizmoManager.SetGizmoActive(lineId, false);
                lineVisible = false;
            }
        }

        private void ReleaseLabel(int index)
        {
            if (index < 0 || index >= labelIds.Count)
            {
                return;
            }
            BasisGizmoManager.DestroyGizmo(labelIds[index]);
            labelIds.RemoveAt(index);
        }

        private void ReleaseAllLabels()
        {
            for (int Index = 0; Index < labelIds.Count; Index++)
            {
                BasisGizmoManager.DestroyGizmo(labelIds[Index]);
            }
            labelIds.Clear();
        }

        /// <summary>Destroys every marker and every gizmo the track owns. Called from camera teardown.</summary>
        public void Dispose()
        {
            Clear();
            if (lineCreated)
            {
                BasisGizmoManager.DestroyGizmo(lineId);
                lineCreated = false;
            }
        }
    }
}
