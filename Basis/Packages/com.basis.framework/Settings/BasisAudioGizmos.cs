using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using UnityEngine;

/// <summary>
/// Runtime visualisation of the remote-voice spatial-audio pipeline. Driven each
/// frame from <see cref="SMModuleDebugOptions"/> (under the master ShowGizmos gate)
/// so it appears live in VR/desktop, not just the editor scene view.
///
/// Three sub-toggles:
///  • Ranges     — the local listener's hearing-distance sphere (wireframe), plus a
///                 per-speaker full-volume ring at the AudioSource minDistance.
///  • ListenerCone — the listener directional-dampening pattern (RAListenerConeAngle /
///                 RAListenerDampenAmount) drawn as a polar curve around the listener:
///                 full radius ahead, near-full at the sides, pinching to the rear floor.
///  • Levels       — per speaker: an arrow along the Steam Audio source-forward
///                 (directivity dipole) axis with a vertical loudness meter on its tip.
///                 The arrow + meter fill are tinted green→red by the net loudness that
///                 reaches you, so a red arrow on someone facing you is the "facing me
///                 but quiet" test. The meter fill rises to the speaker's live output
///                 ("max volume") under a fixed dim headroom up to full scale, and the
///                 label breaks every attenuation factor out as a ×multiplier —
///                 per-player volume, distance rolloff, directivity, occlusion, listener
///                 cone, and global main volume — so you can read off what makes someone
///                 quiet.
/// </summary>
public static class BasisAudioGizmos
{
    // Mirrored from settings by SMModuleDebugOptions.
    public static bool ShowRanges;
    public static bool ShowListenerCone;
    public static bool ShowLevels;
    public static bool ShowLabels;

    private const int RingSegments = 28;
    private const int DirectivitySegments = 48;
    private const float ArrowBaseLength = 0.45f;   // metres, before avatar-scale
    private const float TipBaseSize = 0.05f;
    private const float LineBaseWidth = 0.006f;
    private const float DirectivityRadius = 2.5f;   // forward reach of the listener directivity curve
    private const float LabelBaseScale = 0.025f;
    private const float LabelBaseHeight = 0.12f;    // lift above the anchor point
    private const float LevelBarHeight = 0.4f;      // metres of bar for a full-scale (1.0) peak
    private const float LevelBarBaseOffset = 0.18f; // lift the bar base above the mouth/source
    private const float LevelBarWidth = 0.02f;      // base, before avatar-scale

    private static readonly Color HearingColor = new Color(0.25f, 0.7f, 1f, 1f);
    private static readonly Color MinDistanceColor = new Color(0.3f, 0.95f, 0.4f, 1f);
    private static readonly Color ConeColor = new Color(1f, 0.6f, 0.15f, 1f);
    private static readonly Color GainLow = new Color(0.95f, 0.2f, 0.15f, 1f);
    private static readonly Color GainMid = new Color(0.95f, 0.85f, 0.2f, 1f);
    private static readonly Color GainHigh = new Color(0.3f, 0.9f, 0.35f, 1f);
    // Dim headroom segment above the level fill, up to full scale.
    private static readonly Color LevelHeadroomColor = new Color(0.35f, 0.1f, 0.1f, 1f);

    private struct SpeakerGizmos
    {
        public int MinRing;

        // Directivity arrow (mouth → source-forward tip) and its endpoint sphere.
        public int ArrowLine;
        public int TipSphere;

        // Level meter: a bright fill that rises with the speaker's level plus a fixed
        // dim headroom segment up to full scale, and the breakdown label.
        public int LevelFill;
        public int LevelHeadroom;
        public int LevelLabel;
        public int LastNetPct;  // cached so the breakdown string only rebuilds on change
        public string LevelText;
    }

    private static readonly Dictionary<ushort, SpeakerGizmos> _speakers = new Dictionary<ushort, SpeakerGizmos>();
    private static readonly List<ushort> _seen = new List<ushort>();

    // Listener-centric gizmos (created once, repositioned each frame).
    private static int _hearingRingX = -1, _hearingRingY = -1, _hearingRingZ = -1;
    private static int _dirRingH = -1, _dirRingV = -1;
    private static int _hearingLabel = -1, _coneLabel = -1;
    private static int _lastHearingMeters = int.MinValue;
    private static int _lastConeDegrees = int.MinValue;
    private static int _lastConeRearPct = int.MinValue;
    private static string _hearingLabelText = "";
    private static string _coneLabelText = "";

    // Listener camera position this frame, used to billboard labels toward the viewer.
    private static Vector3 _camPos;

    // Reused so per-frame ring/star rebuilds don't allocate.
    private static readonly Vector3[] _ringScratch = new Vector3[RingSegments];
    private static readonly Vector3[] _dirScratch = new Vector3[DirectivitySegments];

    // Reused for the (throttled) breakdown label so a rebuild doesn't allocate a builder.
    private static readonly System.Text.StringBuilder _levelText = new System.Text.StringBuilder(160);

    private static bool _hooked;

    /// <summary>Per-frame entry point. <paramref name="scale"/> is the local avatar scale.</summary>
    public static void Tick(float scale)
    {
        EnsureMasterHook();

        if (!ShowRanges && !ShowListenerCone && !ShowLevels)
        {
            Shutdown();
            return;
        }
        if (scale <= 0f)
        {
            scale = 1f;
        }

        _camPos = BasisLocalCameraDriver.Position;
        UpdateListenerGizmos(scale);
        UpdateSpeakerGizmos(scale);
    }

    public static void Shutdown()
    {
        if (_speakers.Count > 0)
        {
            foreach (KeyValuePair<ushort, SpeakerGizmos> kvp in _speakers)
            {
                DestroySpeaker(kvp.Value);
            }
            _speakers.Clear();
        }
        DestroyId(ref _hearingRingX);
        DestroyId(ref _hearingRingY);
        DestroyId(ref _hearingRingZ);
        DestroyId(ref _dirRingH);
        DestroyId(ref _dirRingV);
        DestroyId(ref _hearingLabel);
        DestroyId(ref _coneLabel);
        _lastHearingMeters = int.MinValue;
        _lastConeDegrees = int.MinValue;
        _lastConeRearPct = int.MinValue;
    }

    // ── Listener-centric: hearing sphere + dampening cone ───────────────────

    private static void UpdateListenerGizmos(float scale)
    {
        Vector3 listenerPos = BasisLocalCameraDriver.Position;
        float width = LineBaseWidth * scale;

        if (ShowRanges)
        {
            float radius = Mathf.Sqrt(Mathf.Max(0f, SMModuleDistanceBasedReductions.HearingRange));
            BuildCircle(listenerPos, Vector3.right, radius);
            EnsureRing(ref _hearingRingX, "AudioHearing_X", width, HearingColor);
            BasisGizmoManager.UpdateLineGizmo(_hearingRingX, _ringScratch);

            BuildCircle(listenerPos, Vector3.up, radius);
            EnsureRing(ref _hearingRingY, "AudioHearing_Y", width, HearingColor);
            BasisGizmoManager.UpdateLineGizmo(_hearingRingY, _ringScratch);

            BuildCircle(listenerPos, Vector3.forward, radius);
            EnsureRing(ref _hearingRingZ, "AudioHearing_Z", width, HearingColor);
            BasisGizmoManager.UpdateLineGizmo(_hearingRingZ, _ringScratch);

            if (ShowLabels)
            {
                int meters = Mathf.RoundToInt(radius);
                if (meters != _lastHearingMeters)
                {
                    _lastHearingMeters = meters;
                    _hearingLabelText = $"Hearing {meters} m";
                }
                // Anchor on the sphere's top edge, in front of the listener so it isn't
                // lost behind them.
                Vector3 anchor = listenerPos + Vector3.up * radius;
                UpdateLabel(ref _hearingLabel, "AudioHearingLabel", anchor, _hearingLabelText, HearingColor, scale);
            }
            else
            {
                DestroyId(ref _hearingLabel);
            }
        }
        else
        {
            DestroyId(ref _hearingRingX);
            DestroyId(ref _hearingRingY);
            DestroyId(ref _hearingRingZ);
            DestroyId(ref _hearingLabel);
        }

        if (ShowListenerCone)
        {
            float coneAngle = Mathf.Clamp(BasisSettingsDefaults.RAListenerConeAngle.RawValue, 0f, 360f);
            Vector3 fwd = BasisLocalCameraDriver.Forward();
            if (fwd.sqrMagnitude < 1e-6f)
            {
                fwd = Vector3.forward;
            }
            fwd.Normalize();
            // A 360° cone means "no dampening anywhere" — nothing meaningful to draw.
            if (coneAngle >= 360f)
            {
                DestroyId(ref _dirRingH);
                DestroyId(ref _dirRingV);
                DestroyId(ref _coneLabel);
            }
            else
            {
                float dampenPercent = Mathf.Clamp(BasisSettingsDefaults.RAListenerDampenAmount.RawValue, 1f, 95f);
                float minVolume = 1f - dampenPercent / 100f;
                float halfConeRad = coneAngle * 0.5f * Mathf.Deg2Rad;
                float cosHalfCone = Mathf.Cos(halfConeRad);
                float radius = DirectivityRadius * scale;

                Vector3 right = Vector3.Cross(Vector3.up, fwd);
                if (right.sqrMagnitude < 1e-5f)
                {
                    right = Vector3.Cross(Vector3.forward, fwd);
                }
                right.Normalize();
                Vector3 up = Vector3.Cross(fwd, right).normalized;

                BuildDirectivityCurve(listenerPos, fwd, right, radius, minVolume, cosHalfCone, halfConeRad);
                if (_dirRingH <= 0)
                {
                    BasisGizmoManager.CreateLineGizmo("AudioDirectivityH", out _dirRingH, _dirScratch, width, ConeColor, true);
                }
                else
                {
                    BasisGizmoManager.UpdateLineGizmo(_dirRingH, _dirScratch);
                }

                BuildDirectivityCurve(listenerPos, fwd, up, radius, minVolume, cosHalfCone, halfConeRad);
                if (_dirRingV <= 0)
                {
                    BasisGizmoManager.CreateLineGizmo("AudioDirectivityV", out _dirRingV, _dirScratch, width, ConeColor, true);
                }
                else
                {
                    BasisGizmoManager.UpdateLineGizmo(_dirRingV, _dirScratch);
                }

                if (ShowLabels)
                {
                    int deg = Mathf.RoundToInt(coneAngle);
                    int rearPct = Mathf.RoundToInt(minVolume * 100f);
                    if (deg != _lastConeDegrees || rearPct != _lastConeRearPct)
                    {
                        _lastConeDegrees = deg;
                        _lastConeRearPct = rearPct;
                        _coneLabelText = $"Voice {deg}° · rear {rearPct}%";
                    }
                    Vector3 anchor = listenerPos + fwd * radius;
                    UpdateLabel(ref _coneLabel, "AudioConeLabel", anchor, _coneLabelText, ConeColor, scale);
                }
                else
                {
                    DestroyId(ref _coneLabel);
                }
            }
        }
        else
        {
            DestroyId(ref _dirRingH);
            DestroyId(ref _dirRingV);
            DestroyId(ref _coneLabel);
        }
    }

    // ── Per-speaker: facing arrow + full-volume ring ────────────────────────

    private static void UpdateSpeakerGizmos(float scale)
    {
        _seen.Clear();

        BasisNetworkReceiver[] snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        int count = BasisNetworkPlayers.ReceiverCount;
        float arrowLen = ArrowBaseLength * scale;
        float tipSize = TipBaseSize * scale;
        float width = LineBaseWidth * scale;

        for (int i = 0; i < count && i < snapshot.Length; i++)
        {
            BasisNetworkReceiver receiver = snapshot[i];
            if (receiver == null)
            {
                continue;
            }
            BasisAudioReceiver audio = receiver.AudioReceiverModule;
            if (audio == null || !audio.HasAudioSource || audio.AudioSourceTransform == null)
            {
                continue;
            }

            ushort id = receiver.playerId;
            _seen.Add(id);

            Transform sourceT = audio.AudioSourceTransform;
            Vector3 pos = sourceT.position;
            Vector3 fwd = sourceT.forward;
            Vector3 tip = pos + fwd * arrowLen;

            _speakers.TryGetValue(id, out SpeakerGizmos g);

            if (ShowRanges)
            {
                float minDist = audio.audioSource != null ? audio.audioSource.minDistance : 0.5f;
                BuildCircle(pos, Vector3.up, minDist);
                if (g.MinRing <= 0)
                {
                    BasisGizmoManager.CreateLineGizmo($"AudioMinDist_{id}", out g.MinRing, _ringScratch, width, MinDistanceColor, true);
                }
                else
                {
                    BasisGizmoManager.UpdateLineGizmo(g.MinRing, _ringScratch);
                }
            }
            else if (g.MinRing > 0)
            {
                DestroyId(ref g.MinRing);
            }

            if (ShowLevels)
            {
                UpdateSpeakerLevel(ref g, receiver, audio, pos, tip, tipSize, width, scale);
            }
            else if (g.ArrowLine > 0 || g.LevelFill > 0 || g.LevelHeadroom > 0 || g.LevelLabel > 0)
            {
                DestroyId(ref g.ArrowLine);
                DestroyId(ref g.TipSphere);
                DestroyId(ref g.LevelFill);
                DestroyId(ref g.LevelHeadroom);
                DestroyId(ref g.LevelLabel);
            }

            _speakers[id] = g;
        }

        PruneStaleSpeakers();
    }

    /// <summary>
    /// Lazily creates / updates a billboarded world-space label. Faces the listener
    /// camera each frame; text + colour are diffed inside the gizmo so an unchanged
    /// label only pays a transform write.
    /// </summary>
    private static void UpdateLabel(ref int id, string name, Vector3 position, string text, Color color, float scale)
    {
        Quaternion rot = Billboard(position);
        float labelScale = LabelBaseScale * scale;
        if (id <= 0)
        {
            BasisGizmoManager.CreateTextGizmo(name, out id, position, text, color);
        }
        BasisGizmoManager.UpdateTextGizmo(id, position, rot, labelScale, text, color);
    }

    private static Quaternion Billboard(Vector3 worldPos)
    {
        return BasisGizmoManager.BillboardRotation(worldPos, _camPos);
    }

    // ── Per-speaker directivity arrow + level meter + breakdown ─────────────

    /// <summary>
    /// Draws the directivity arrow (mouth → source-forward tip) and, on its tip, the
    /// vertical loudness meter: a bright fill rising to the speaker's live output level
    /// under a fixed dim headroom segment. The arrow and fill share a green→red tint by
    /// the net loudness reaching you, and — when labels are on — a per-factor breakdown
    /// of everything attenuating this voice is shown above the bar.
    /// </summary>
    private static void UpdateSpeakerLevel(ref SpeakerGizmos g, BasisNetworkReceiver receiver, BasisAudioReceiver audio, Vector3 pos, Vector3 tip, float tipSize, float lineWidth, float scale)
    {
        // A disabled source stops running OnAudioFilterRead, so SourcePeak freezes —
        // treat a non-active source as silent so the meter falls to zero.
        float source = audio.IsAudioActive ? Mathf.Clamp01(audio.SourcePeak) : 0f;

        // Every multiplier that chips away at the voice between the speaker and you.
        float slider = Mathf.Clamp01(audio.PerPlayerVolume);
        float cone = Mathf.Clamp01(audio.DirectionalDampeningMultiplier);
        float main = Mathf.Clamp01(SMModuleAudio.ActiveMainVolume);
        float dist = DistanceAttenuation(audio.audioSource);
        float dir = 1f, occ = 1f;
#if STEAMAUDIO_ENABLED
        if (audio.audioSource != null && audio.audioSource.TryGetComponent<SteamAudio.SteamAudioSource>(out var sa))
        {
            dir = Mathf.Clamp01(sa.directivityValue);
            occ = Mathf.Clamp01(sa.occlusionValue);
        }
#endif
        // The listener cone and the talker's mouth directivity are now part filter,
        // part gain. Fold the shelves' speech-weighted broadband equivalent in, or the
        // meter under-reports everything that is actually reaching the listener.
        float tone = BasisVoiceAcoustics.ShelfBroadbandGain(audio.DirectivityShelfDb, audio.ConeShelfDb);
        float netGain = slider * cone * main * dist * dir * occ * tone;
        float net = source * netGain;
        Color fillColor = GainColor(netGain);

        // Directivity arrow along the source-forward axis, anchored at the tip the meter
        // rises from, so the bar sits in front of the face rather than inside the head.
        if (g.ArrowLine <= 0)
        {
            BasisGizmoManager.CreateLineGizmo($"AudioDir_{receiver.playerId}", out g.ArrowLine, pos, tip, lineWidth, fillColor);
            BasisGizmoManager.CreateSphereGizmo($"AudioDirTip_{receiver.playerId}", out g.TipSphere, tip, tipSize, fillColor);
        }
        else
        {
            BasisGizmoManager.UpdateLineGizmo(g.ArrowLine, pos, tip);
            BasisGizmoManager.UpdateGizmoColor(g.ArrowLine, fillColor);
            BasisGizmoManager.UpdateSphereGizmo(g.TipSphere, tip, Vector3.one * tipSize);
            BasisGizmoManager.UpdateGizmoColor(g.TipSphere, fillColor);
        }

        // Bright fill rises from the base to the speaker's live level; the dim headroom
        // segment fills the rest up to a FIXED full-scale ceiling. Only the junction
        // (the level) moves — the ceiling stays put, so the bar reads as a meter rather
        // than a floating background. Fill is tinted by net gain: green = reaches you,
        // red = heavily attenuated.
        float full = LevelBarHeight * scale;
        float width = LevelBarWidth * scale;
        Vector3 basePos = tip + Vector3.up * (LevelBarBaseOffset * scale);
        Vector3 levelTop = basePos + Vector3.up * (full * source);
        Vector3 fullTop = basePos + Vector3.up * full;

        if (g.LevelFill <= 0)
        {
            BasisGizmoManager.CreateLineGizmo($"AudioLevelFill_{receiver.playerId}", out g.LevelFill, basePos, levelTop, width, fillColor);
            BasisGizmoManager.CreateLineGizmo($"AudioLevelHeadroom_{receiver.playerId}", out g.LevelHeadroom, levelTop, fullTop, width * 0.5f, LevelHeadroomColor);
        }
        else
        {
            BasisGizmoManager.UpdateLineGizmo(g.LevelFill, basePos, levelTop);
            BasisGizmoManager.UpdateGizmoColor(g.LevelFill, fillColor);
            BasisGizmoManager.UpdateLineGizmo(g.LevelHeadroom, levelTop, fullTop);
        }

        if (ShowLabels)
        {
            if (g.LevelLabel <= 0 || BasisGizmoManager.IsTextVisible(g.LevelLabel))
            {
                int netPct = Mathf.RoundToInt(net * 100f);
                if (g.LevelLabel <= 0 || netPct != g.LastNetPct || g.LevelText == null)
                {
                    g.LastNetPct = netPct;
                    g.LevelText = BuildLevelText(receiver.displayName, source, net, slider, dist, dir, occ, cone, main,
                        audio.NormalizeLoudness, audio.NormalizerGainDb);
                }
            }
            // Anchor at the bar's fixed full-scale top, not the live sourceTop, so the
            // label doesn't bounce with the meter as the speaker's level changes.
            Vector3 labelPos = basePos + Vector3.up * (LevelBarHeight * scale + LabelBaseHeight * scale);
            UpdateLabel(ref g.LevelLabel, $"AudioLevelLabel_{receiver.playerId}", labelPos, g.LevelText, fillColor, scale);
        }
        else if (g.LevelLabel > 0)
        {
            DestroyId(ref g.LevelLabel);
        }
    }

    /// <summary>
    /// Approximates the Unity AudioSource 3D distance attenuation reaching the listener.
    /// The per-sample gain handles cone/main/per-player volume, but distance rolloff is
    /// applied by Unity after OnAudioFilterRead, so it isn't in SourcePeak — fold it in
    /// here so the meter and breakdown reflect what you actually hear.
    /// </summary>
    private static float DistanceAttenuation(AudioSource src)
    {
        if (src == null)
        {
            return 1f;
        }
        float min = src.minDistance;
        float max = src.maxDistance;
        float d = Vector3.Distance(src.transform.position, _camPos);
        if (d <= min || max <= min)
        {
            return 1f;
        }
        switch (src.rolloffMode)
        {
            case AudioRolloffMode.Linear:
                return Mathf.Clamp01((max - d) / (max - min));
            case AudioRolloffMode.Custom:
                float t = Mathf.Clamp01((d - min) / (max - min));
                return Mathf.Clamp01(src.GetCustomCurve(AudioSourceCurveType.CustomRolloff).Evaluate(t));
            default: // Logarithmic — Unity's default; never reaches 0.
                return Mathf.Clamp01(min / d);
        }
    }

    private static string BuildLevelText(string name, float source, float net, float slider, float dist, float dir, float occ, float cone, float main, bool normalizing, float normalizerGainDb)
    {
        _levelText.Clear();
        if (!string.IsNullOrEmpty(name))
        {
            _levelText.Append(name).Append('\n');
        }
        _levelText.Append("Src ").Append(Mathf.RoundToInt(source * 100f))
                  .Append("%  Net ").Append(Mathf.RoundToInt(net * 100f)).Append("%\n");
        AppendFactor("Slider", slider);
        AppendFactor("Dist", dist);
#if STEAMAUDIO_ENABLED
        AppendFactor("Facing", dir);
        AppendFactor("Occlude", occ);
#endif
        AppendFactor("Cone", cone);
        AppendFactor("Main", main);
        if (normalizing)
        {
            _levelText.Append("Norm ").Append(normalizerGainDb.ToString("+0.0;-0.0;0.0")).Append(" dB\n");
        }
        return _levelText.ToString();
    }

    private static void AppendFactor(string label, float value)
    {
        _levelText.Append(label).Append(" x").Append(value.ToString("0.00")).Append('\n');
    }

    // ── Geometry helpers ────────────────────────────────────────────────────

    private static void BuildCircle(Vector3 center, Vector3 normal, float radius)
    {
        normal = normal.sqrMagnitude < 1e-6f ? Vector3.up : normal.normalized;
        Vector3 tangent = Vector3.Cross(normal, Vector3.up);
        if (tangent.sqrMagnitude < 1e-4f)
        {
            tangent = Vector3.Cross(normal, Vector3.right);
        }
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(normal, tangent);

        float step = (Mathf.PI * 2f) / RingSegments;
        for (int i = 0; i < RingSegments; i++)
        {
            float a = i * step;
            _ringScratch[i] = center + (tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a)) * radius;
        }
    }

    /// <summary>
    /// Fills <see cref="_dirScratch"/> with a closed polar curve of the listener
    /// directional-dampening multiplier in the plane spanned by (fwd, planePerp): the
    /// radius at each angle is the gain a source there would receive, so the loop is full
    /// radius ahead, near-full at the sides, and pinches to the rear floor (minVolume)
    /// directly behind. Mirrors <c>BasisDirectionalDampenJob</c> so the picture matches
    /// the audio.
    /// </summary>
    private static void BuildDirectivityCurve(Vector3 center, Vector3 fwd, Vector3 planePerp, float radius, float minVolume, float cosHalfCone, float halfConeRad)
    {
        float span = Mathf.PI - halfConeRad;
        float invSpan = span > 1e-5f ? 1f / span : 0f;
        float step = (Mathf.PI * 2f) / DirectivitySegments;
        for (int i = 0; i < DirectivitySegments; i++)
        {
            float phi = i * step;
            float c = Mathf.Cos(phi);
            float s = Mathf.Sin(phi);

            float gain;
            if (c >= cosHalfCone)
            {
                gain = 1f;
            }
            else
            {
                float theta = Mathf.Acos(Mathf.Clamp(c, -1f, 1f));
                float u = Mathf.Clamp01((theta - halfConeRad) * invSpan);
                float falloff = u * u * (3f - 2f * u);
                gain = Mathf.Lerp(1f, minVolume, falloff);
            }

            Vector3 dir = fwd * c + planePerp * s;
            _dirScratch[i] = center + dir * (radius * gain);
        }
    }

    private static Color GainColor(float gain)
    {
        gain = Mathf.Clamp01(gain);
        return gain < 0.5f
            ? Color.Lerp(GainLow, GainMid, gain * 2f)
            : Color.Lerp(GainMid, GainHigh, (gain - 0.5f) * 2f);
    }

    // ── Lifecycle plumbing ──────────────────────────────────────────────────

    private static void EnsureRing(ref int id, string name, float width, Color color)
    {
        if (id <= 0)
        {
            BasisGizmoManager.CreateLineGizmo(name, out id, _ringScratch, width, color, true);
        }
    }

    private static void DestroyId(ref int id)
    {
        if (id > 0)
        {
            BasisGizmoManager.DestroyGizmo(id);
            id = -1;
        }
    }

    private static void DestroySpeaker(SpeakerGizmos g)
    {
        if (g.ArrowLine > 0) BasisGizmoManager.DestroyGizmo(g.ArrowLine);
        if (g.TipSphere > 0) BasisGizmoManager.DestroyGizmo(g.TipSphere);
        if (g.MinRing > 0) BasisGizmoManager.DestroyGizmo(g.MinRing);
        if (g.LevelFill > 0) BasisGizmoManager.DestroyGizmo(g.LevelFill);
        if (g.LevelHeadroom > 0) BasisGizmoManager.DestroyGizmo(g.LevelHeadroom);
        if (g.LevelLabel > 0) BasisGizmoManager.DestroyGizmo(g.LevelLabel);
    }

    private static void PruneStaleSpeakers()
    {
        if (_speakers.Count == _seen.Count)
        {
            return;
        }
        List<ushort> stale = null;
        foreach (KeyValuePair<ushort, SpeakerGizmos> kvp in _speakers)
        {
            if (!_seen.Contains(kvp.Key))
            {
                (stale ??= new List<ushort>()).Add(kvp.Key);
            }
        }
        if (stale == null)
        {
            return;
        }
        for (int i = 0; i < stale.Count; i++)
        {
            if (_speakers.TryGetValue(stale[i], out SpeakerGizmos g))
            {
                DestroySpeaker(g);
                _speakers.Remove(stale[i]);
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

    /// <summary>
    /// Master ShowGizmos going off destroys BasisGizmoManager's gizmo dictionaries,
    /// so our cached IDs are stale — forget them. Next Tick re-creates cleanly.
    /// </summary>
    private static void OnMasterToggleChanged(bool state)
    {
        if (state)
        {
            return;
        }
        _speakers.Clear();
        _hearingRingX = _hearingRingY = _hearingRingZ = -1;
        _dirRingH = _dirRingV = -1;
        _hearingLabel = _coneLabel = -1;
        _lastHearingMeters = int.MinValue;
        _lastConeDegrees = int.MinValue;
        _lastConeRearPct = int.MinValue;
    }
}
