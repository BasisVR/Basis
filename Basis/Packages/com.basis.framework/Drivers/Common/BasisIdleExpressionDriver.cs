using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    [Serializable]
    public partial class BasisIdleExpressionDriver
    {
        [AutoStaticsCleanup] static readonly List<BasisIdleExpressionDriver> remoteDrivers = new List<BasisIdleExpressionDriver>();
        [AutoStaticsCleanup] static double talkerTime = double.NaN;
        [AutoStaticsCleanup] static int talkerCount;
        [NoAutoStaticsCleanup] static readonly Vector3[] talkerPositions = new Vector3[8];
        [NoAutoStaticsCleanup] static readonly float[] talkerLevels = new float[8];
        [NoAutoStaticsCleanup] static readonly BasisIdleExpressionDriver[] talkerOwners = new BasisIdleExpressionDriver[8];
        public const float MaxRemoteDistance = 15f, DetailDistance = 6f, ListenDistance = 6f, RangeCheckInterval = .25f, GainRate = 3f, SpeakAttack = 10f, SpeakRelease = 1.5f, SpeakThreshold = 0.08f;
        const int smileL = 0, smileR = 1, cheekSquintL = 2, cheekSquintR = 3, eyeSquintL = 4, eyeSquintR = 5, browInnerUp = 6, browOuterUpL = 7, browOuterUpR = 8, browDownL = 9, browDownR = 10, eyeWideL = 11, eyeWideR = 12, mouthPressL = 13, mouthPressR = 14, mouthFrownL = 15, mouthFrownR = 16, cheekPuffL = 17, cheekPuffR = 18, cheekPuffBoth = 19, cheekSuckL = 20, cheekSuckR = 21, cheekSuckBoth = 22, dimpleL = 23, dimpleR = 24, sneerL = 25, sneerR = 26, nasalL = 27, nasalR = 28, jawChannel = 29, rollLowerChannel = 30, rollUpperChannel = 31, shrugChannel = 32, sideL = 33, sideR = 34, sideLowerL = 35, sideLowerR = 36, genericSmile = 37, genericSurprised = 38, genericSad = 39, laughterChannel = 40, channelCount = 41;
        const int sSmile = 0, sAsym = 1, sSquint = 2, sBrowInner = 3, sBrowOuterL = 4, sBrowOuterR = 5, sBrowDownL = 6, sBrowDownR = 7, sWide = 8, sPress = 9, sFrown = 10, semanticCount = 11, microCount = 8, vSil = 0, vPP = 1, vFF = 2, vSS = 7, vRR = 9, vAA = 10, vE = 11, vIH = 12, vOH = 13, vOU = 14, visemeCount = 15;
        static readonly string[][] channelNames =
        {
            new[] { "mouthSmileLeft", "Mouth_Smile_Left", "SmileLeft", "MouthCornerPullLeft" },
            new[] { "mouthSmileRight", "Mouth_Smile_Right", "SmileRight", "MouthCornerPullRight" },
            new[] { "cheekSquintLeft" },
            new[] { "cheekSquintRight" },
            new[] { "eyeSquintLeft", "Eye_Left_Squeeze" },
            new[] { "eyeSquintRight", "Eye_Right_Squeeze" },
            new[] { "browInnerUp", "BrowInnerUpLeft", "BrowInnerUpRight" },
            new[] { "browOuterUpLeft" },
            new[] { "browOuterUpRight" },
            new[] { "browDownLeft", "BrowLowererLeft" },
            new[] { "browDownRight", "BrowLowererRight" },
            new[] { "eyeWideLeft", "Eye_Left_Wide" },
            new[] { "eyeWideRight", "Eye_Right_Wide" },
            new[] { "mouthPressLeft" },
            new[] { "mouthPressRight" },
            new[] { "mouthFrownLeft", "Mouth_Sad_Left" },
            new[] { "mouthFrownRight", "Mouth_Sad_Right" },
            new[] { "CheekPuffLeft", "Cheek_Puff_Left" },
            new[] { "CheekPuffRight", "Cheek_Puff_Right" },
            new[] { "cheekPuff", "Cheek_Puff" },
            new[] { "CheekSuckLeft", "Cheek_Suck_Left" },
            new[] { "CheekSuckRight", "Cheek_Suck_Right" },
            new[] { "cheekSuck", "Cheek_Suck" },
            new[] { "mouthDimpleLeft" },
            new[] { "mouthDimpleRight" },
            new[] { "noseSneerLeft" },
            new[] { "noseSneerRight" },
            new[] { "NasalDilationLeft" },
            new[] { "NasalDilationRight" },
            new[] { "jawOpen", "Jaw_Open" },
            new[] { "mouthRollLower", "LipSuckLowerLeft", "LipSuckLowerRight", "LipSuckLower", "Mouth_Lower_Inside" },
            new[] { "mouthRollUpper", "LipSuckUpperLeft", "LipSuckUpperRight", "LipSuckUpper", "Mouth_Upper_Inside" },
            new[] { "mouthShrugLower", "mouthRaiserLower" },
            new[] { "mouthLeft", "MouthUpperLeft", "Mouth_Upper_Left" },
            new[] { "mouthRight", "MouthUpperRight", "Mouth_Upper_Right" },
            new[] { "MouthLowerLeft", "Mouth_Lower_Left" },
            new[] { "MouthLowerRight", "Mouth_Lower_Right" },
        };
        static readonly string[][] genericKeywords = { new[] { "smile", "happy", "joy", "fun" }, new[] { "surprised", "surprise" }, new[] { "sad", "sorrow" } };
        static readonly float[][] presets =
        {
            new[] { .35f, 0f, .15f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
            new[] { .62f, 0f, .38f, .06f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
            new[] { .38f, .85f, .12f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
            new[] { 0f, 0f, 0f, .32f, .3f, 0f, 0f, 0f, .12f, 0f, 0f },
            new[] { 0f, 0f, .08f, 0f, 0f, 0f, .28f, 0f, 0f, .28f, 0f },
            new[] { .18f, 0f, 0f, .25f, .3f, .3f, 0f, 0f, 0f, 0f, 0f },
            new[] { 0f, 0f, 0f, .35f, 0f, 0f, 0f, 0f, 0f, .12f, .18f },
            new[] { .26f, 0f, .1f, 0f, 0f, 0f, 0f, 0f, 0f, .22f, 0f },
            new[] { .45f, 0f, .3f, .1f, .1f, .1f, 0f, 0f, 0f, 0f, 0f },
            new[] { .12f, .4f, 0f, 0f, .3f, 0f, 0f, .25f, 0f, .15f, 0f },
            new[] { 0f, 0f, 0f, 0f, 0f, 0f, .12f, .12f, 0f, .3f, .1f },
        };
        static readonly float[] idleWeights = { 3f, 1.4f, 1f, 1.4f, 1.2f, 1.2f, .5f, 1.4f, 1f, .5f, .6f };
        static readonly float[] listeningWeights = { 2.5f, 1.2f, .6f, 2.2f, 1.4f, 2.4f, .6f, 1f, 1.4f, .4f, .8f };
        struct Expression
        {
            public int preset;
            public bool mirror, active;
            public double start;
            public float attack, hold, release, intensity, phase, lagL, lagR;
        }
        [NonSerialized] SkinnedMeshRenderer[] renderers = Array.Empty<SkinnedMeshRenderer>();
        [NonSerialized] int[] indices = Array.Empty<int>();
        [NonSerialized] float[] lastWritten = Array.Empty<float>(), baseWeight = Array.Empty<float>(), contribution = Array.Empty<float>();
        [NonSerialized] readonly int[][] channelTargets = new int[channelCount][];
        [NonSerialized] readonly float[] semL = new float[semanticCount], semR = new float[semanticCount], channel = new float[channelCount];
        [NonSerialized] readonly float[] microFreqA = new float[microCount], microFreqB = new float[microCount], microPhaseA = new float[microCount], microPhaseB = new float[microCount];
        [NonSerialized] Expression current, previous;
        [NonSerialized] BasisAvatar avatar;
        [NonSerialized] BasisRemotePlayer remote;
        [NonSerialized] int registryIndex = -1, pendingPreset = -1;
        [NonSerialized] Vector3 position;
        [NonSerialized] float distanceSq = float.MaxValue;
        [NonSerialized] double nextRangeCheck;
        [NonSerialized] bool inRange;
        [NonSerialized] Unity.Mathematics.Random random = new Unity.Mathematics.Random(1);
        [NonSerialized] double nextExpressionTime, nextFlickTime, nextEmphasisTime, flickStart = double.NegativeInfinity, emphasisStart = double.NegativeInfinity;
        [NonSerialized] float gain, speaking, voiceFast, voiceSlow, attention, utterance, flickAmount, emphasisAmount, emphasisDuration = .6f, emphasisLeft, emphasisRight, emphasisInner;
        [NonSerialized] float expressiveness = 1f, smileBias = 1f, browBias = 1f, restSmile;
        [NonSerialized] float visemeRound, visemeSpread, visemeClosure, visemeConsonant, openFast, openSlow, laughter;
        [NonSerialized] readonly double[] laughPeaks = new double[4];
        [NonSerialized] int laughPeakIndex;
        [NonSerialized] double laughUntil = double.NegativeInfinity;
        [NonSerialized] double nextPuffTime, puffStart = double.NegativeInfinity, nextSuckTime, suckStart = double.NegativeInfinity, nextTwitchTime, twitchStart = double.NegativeInfinity;
        [NonSerialized] float puffDuration = 1f, puffAmount, puffSide, suckDuration = 1f, suckAmount, twitchDuration = .4f, twitchAmount;
        [NonSerialized] bool puffShift, puffShiftLeftFirst, twitchLeft, hmmLeft;
        [NonSerialized] double nextSwallowTime, lipsStart = double.NegativeInfinity, nextHmmTime, hmmStart = double.NegativeInfinity;
        [NonSerialized] float lipsDuration = .6f, lipsPress, lipsRollLower, lipsRollUpper, hmmDuration = 1f, hmmSide, hmmShrug, lipsNow, hmmNow;
        [NonSerialized] float breathPhase, breathPeriod = 4f, breathDepth = 1f, restJaw;
        [NonSerialized] bool flushed = true, hasSideSmile, hasWide, hasBrowInner, hasFrown, hasVisemes, openHigh, hasSidePuff, hasSideSuck;
        public bool IsEnabled { get; private set; }
        public int TargetCount => renderers.Length;
        public void Initialize(BasisAvatar target, BasisRemotePlayer owner = null)
        {
            Clear();
            avatar = target;
            if (avatar == null) return;
            SkinnedMeshRenderer[] smrs = avatar.SkinnedMeshRenderers != null && avatar.SkinnedMeshRenderers.Length > 0 ? avatar.SkinnedMeshRenderers : avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            List<SkinnedMeshRenderer> targetRenderers = new List<SkinnedMeshRenderer>();
            List<int> targetIndices = new List<int>();
            List<int>[] perChannel = new List<int>[channelCount];
            for (int c = 0; c < channelCount; c++) perChannel[c] = new List<int>();
            for (int r = 0; r < smrs.Length; r++)
            {
                SkinnedMeshRenderer smr = smrs[r];
                if (smr == null || smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) continue;
                BasisBlendshapeNameMatcher matcher = BasisBlendshapeNameMatcher.For(smr.sharedMesh);
                for (int c = 0; c < laughterChannel; c++)
                {
                    if (c == rollLowerChannel || c == rollUpperChannel)
                    {
                        string[] names = channelNames[c];
                        for (int n = 0; n < names.Length; n++) AddTarget(smr, matcher.Find(names[n]), perChannel[c], targetRenderers, targetIndices);
                    }
                    else AddTarget(smr, c < channelNames.Length ? matcher.FindFirst(channelNames[c]) : matcher.FindByLastToken(genericKeywords[c - channelNames.Length], 3), perChannel[c], targetRenderers, targetIndices);
                }
            }
            SkinnedMeshRenderer laughMesh = avatar.FaceVisemeMesh;
            if (laughMesh != null && laughMesh.sharedMesh != null && avatar.laughterBlendTarget >= 0 && avatar.laughterBlendTarget < laughMesh.sharedMesh.blendShapeCount)
            {
                perChannel[laughterChannel].Add(targetRenderers.Count);
                targetRenderers.Add(laughMesh);
                targetIndices.Add(avatar.laughterBlendTarget);
            }
            for (int c = 0; c < channelCount; c++) channelTargets[c] = perChannel[c].ToArray();
            hasSideSmile = channelTargets[smileL].Length + channelTargets[smileR].Length > 0;
            hasWide = channelTargets[eyeWideL].Length + channelTargets[eyeWideR].Length > 0;
            hasBrowInner = channelTargets[browInnerUp].Length > 0;
            hasFrown = channelTargets[mouthFrownL].Length + channelTargets[mouthFrownR].Length > 0;
            hasSidePuff = channelTargets[cheekPuffL].Length + channelTargets[cheekPuffR].Length > 0;
            hasSideSuck = channelTargets[cheekSuckL].Length + channelTargets[cheekSuckR].Length > 0;
            renderers = targetRenderers.ToArray();
            indices = targetIndices.ToArray();
            lastWritten = new float[renderers.Length];
            baseWeight = new float[renderers.Length];
            contribution = new float[renderers.Length];
            for (int t = 0; t < renderers.Length; t++) lastWritten[t] = float.NaN;
            IsEnabled = renderers.Length > 0;
            random = new Unity.Mathematics.Random(((uint)avatar.GetHashCode() * 2654435761u ^ (uint)Environment.TickCount) | 1u);
            expressiveness = random.NextFloat(.85f, 1.1f);
            smileBias = random.NextFloat(.85f, 1.2f);
            browBias = random.NextFloat(.85f, 1.2f);
            restSmile = random.NextFloat(0f, .05f);
            for (int m = 0; m < microCount; m++)
            {
                microFreqA[m] = random.NextFloat(.25f, .6f);
                microFreqB[m] = random.NextFloat(.7f, 1.4f);
                microPhaseA[m] = random.NextFloat(6.283f);
                microPhaseB[m] = random.NextFloat(6.283f);
            }
            double now = Time.timeAsDouble;
            nextExpressionTime = now + random.NextFloat(2f, 6f);
            nextFlickTime = now + random.NextFloat(4f, 10f);
            nextPuffTime = now + random.NextFloat(8f, 30f);
            nextSuckTime = now + random.NextFloat(20f, 50f);
            nextTwitchTime = now + random.NextFloat(3f, 10f);
            nextSwallowTime = now + random.NextFloat(15f, 45f);
            nextHmmTime = now + random.NextFloat(20f, 60f);
            restJaw = random.NextFloat(0f, .035f);
            breathPeriod = random.NextFloat(3.4f, 5.2f);
            breathPhase = random.NextFloat();
            if (IsEnabled && owner != null)
            {
                remote = owner;
                registryIndex = remoteDrivers.Count;
                remoteDrivers.Add(this);
            }
        }
        void AddTarget(SkinnedMeshRenderer smr, int found, List<int> channelTargetList, List<SkinnedMeshRenderer> targetRenderers, List<int> targetIndices)
        {
            if (found < 0 || IsReserved(smr, found)) return;
            int existing = -1;
            for (int t = 0; t < targetRenderers.Count; t++) if (targetRenderers[t] == smr && targetIndices[t] == found) { existing = t; break; }
            if (existing < 0)
            {
                existing = targetRenderers.Count;
                targetRenderers.Add(smr);
                targetIndices.Add(found);
            }
            if (!channelTargetList.Contains(existing)) channelTargetList.Add(existing);
        }
        bool IsReserved(SkinnedMeshRenderer smr, int index)
        {
            if (smr == avatar.FaceVisemeMesh)
            {
                if (index == avatar.laughterBlendTarget) return true;
                int[] visemes = avatar.FaceVisemeMovement;
                if (visemes != null) for (int i = 0; i < visemes.Length; i++) if (visemes[i] == index) return true;
            }
            if (smr == avatar.FaceBlinkMesh && avatar.BlinkViseme != null) for (int i = 0; i < avatar.BlinkViseme.Length; i++) if (avatar.BlinkViseme[i] == index) return true;
            return false;
        }
        public void Clear()
        {
            Flush();
            if (registryIndex >= 0)
            {
                int last = remoteDrivers.Count - 1;
                if (registryIndex <= last && remoteDrivers[registryIndex] == this)
                {
                    BasisIdleExpressionDriver moved = remoteDrivers[last];
                    remoteDrivers[registryIndex] = moved;
                    moved.registryIndex = registryIndex;
                    remoteDrivers.RemoveAt(last);
                }
                registryIndex = -1;
            }
            remote = null;
            inRange = false;
            nextRangeCheck = 0;
            distanceSq = float.MaxValue;
            renderers = Array.Empty<SkinnedMeshRenderer>();
            indices = Array.Empty<int>();
            lastWritten = baseWeight = contribution = Array.Empty<float>();
            for (int c = 0; c < channelCount; c++) channelTargets[c] = Array.Empty<int>();
            current = previous = default;
            avatar = null;
            gain = speaking = voiceFast = voiceSlow = attention = utterance = 0f;
            visemeRound = visemeSpread = visemeClosure = visemeConsonant = openFast = openSlow = laughter = 0f;
            laughUntil = double.NegativeInfinity;
            hasVisemes = openHigh = false;
            Array.Clear(laughPeaks, 0, laughPeaks.Length);
            pendingPreset = -1;
            flushed = true;
            flickStart = emphasisStart = puffStart = suckStart = twitchStart = lipsStart = hmmStart = double.NegativeInfinity;
            IsEnabled = false;
        }
        public void Simulate(double time, float dt, bool visible, float voice01)
        {
            using (BasisFaceMarkers.IdleLocal.Auto())
            {
                position = BasisLocalCameraDriver.Position;
                BasisLocalPlayer local = BasisLocalPlayer.Instance;
                Step(time, dt, visible, voice01, true, visible && local != null ? Visemes(local.LocalVisemeDriver) : null);
            }
        }
        static float[] Visemes(BasisAudioAndVisemeDriver driver)
        {
            BasisOpenLipSyncContext context = driver?.openLipSyncContext;
            return context != null && context.IsInitialized ? context.RawVisemeWeights : null;
        }
        void ReadVisemes(float dt, float[] visemes)
        {
            float round = 0f, spread = 0f, closure = 0f, consonant = 0f, open = 0f;
            hasVisemes = visemes != null && visemes.Length >= visemeCount;
            if (hasVisemes)
            {
                round = math.saturate(visemes[vOU] + visemes[vOH] * .6f + visemes[vRR] * .3f);
                spread = math.saturate(visemes[vE] + visemes[vIH] * .8f + visemes[vSS] * .3f);
                closure = math.saturate(visemes[vPP] + visemes[vFF] * .5f);
                open = math.saturate(visemes[vAA] + visemes[vOH] * .6f + visemes[vE] * .3f);
                for (int v = vPP; v < vAA; v++) consonant += visemes[v];
                consonant = math.saturate(consonant);
            }
            float shape = math.saturate(dt * 14f);
            visemeRound += (round - visemeRound) * shape;
            visemeSpread += (spread - visemeSpread) * shape;
            visemeClosure += (closure - visemeClosure) * shape;
            visemeConsonant += (consonant - visemeConsonant) * math.saturate(dt * 3f);
            openFast += (open - openFast) * math.saturate(dt * 18f);
            openSlow += (open - openSlow) * math.saturate(dt * 2.5f);
        }
        void Step(double time, float dt, bool visible, float voice01, bool detail, float[] visemes)
        {
            if (!IsEnabled) return;
            bool allowed = visible && BasisSettingsDefaults.IdleFaceExpressions.RawValue && avatar is not null && !avatar.FaceExpressionTrackingActive;
            if (allowed) gain = math.min(1f, gain + dt * GainRate);
            else
            {
                gain = visible ? math.max(0f, gain - dt * GainRate) : 0f;
                if (gain <= 0f)
                {
                    Flush();
                    current.active = previous.active = false;
                    attention = utterance = 0f;
                    pendingPreset = -1;
                    return;
                }
            }
            bool talking = voice01 > SpeakThreshold;
            speaking += ((talking ? 1f : 0f) - speaking) * math.saturate(dt * (talking ? SpeakAttack : SpeakRelease));
            voiceFast += (voice01 - voiceFast) * math.saturate(dt * 18f);
            voiceSlow += (voice01 - voiceSlow) * math.saturate(dt * 2.5f);
            ReadVisemes(dt, visemes);
            breathPhase += dt / breathPeriod;
            if (breathPhase >= 1f)
            {
                breathPhase -= 1f;
                breathPeriod = random.NextFloat(3.4f, 5.2f);
                breathDepth = random.NextFloat(.7f, 1.2f);
            }
            bool laughing = time < laughUntil;
            laughter += ((laughing ? 1f : 0f) - laughter) * math.saturate(dt * (laughing ? 8f : 2.5f));
            if (allowed)
            {
                Listen(time, dt);
                Converse(time, dt);
                Schedule(time);
            }
            Array.Clear(semL, 0, semanticCount);
            Array.Clear(semR, 0, semanticCount);
            bool any = Accumulate(ref current, time) | Accumulate(ref previous, time);
            float flick = Pulse((float)(time - flickStart), .8f) * flickAmount;
            float emphasis = Pulse((float)(time - emphasisStart), emphasisDuration) * emphasisAmount;
            bool micro = detail && allowed;
            float puff = Bump((float)(time - puffStart), puffDuration, .18f, .3f) * puffAmount;
            float suck = Bump((float)(time - suckStart), suckDuration, .3f, .4f) * suckAmount;
            float twitch = Pulse((float)(time - twitchStart), twitchDuration) * twitchAmount;
            lipsNow = Pulse((float)(time - lipsStart), lipsDuration);
            hmmNow = Bump((float)(time - hmmStart), hmmDuration, .25f, .35f);
            bool cheeks = puff > 0f || suck > 0f || twitch > 0f || lipsNow > 0f || hmmNow > 0f;
            bool idle = !any && flick <= 0f && emphasis <= 0f && !micro && !cheeks && laughter <= .001f;
            if (idle && flushed) return;
            Compose(time, flick, emphasis, micro, puff, suck, twitch);
            Write();
            flushed = idle;
            if (flushed) for (int t = 0; t < lastWritten.Length; t++) lastWritten[t] = float.NaN;
        }
        void Listen(double time, float dt)
        {
            GatherTalkers(time);
            float target = 0f;
            if (speaking < .3f)
            {
                for (int i = 0; i < talkerCount; i++)
                {
                    if (talkerOwners[i] == this) continue;
                    float dx = talkerPositions[i].x - position.x, dz = talkerPositions[i].z - position.z;
                    float near = 1f - math.saturate((math.sqrt(dx * dx + dz * dz) - 1.5f) / (ListenDistance - 1.5f));
                    target = math.max(target, math.saturate(talkerLevels[i] * 2.5f) * near);
                }
            }
            attention += (target - attention) * math.saturate(dt * (target > attention ? 1.5f : .35f));
        }
        void Converse(double time, float dt)
        {
            if (speaking > .5f) utterance += dt;
            else if (speaking < .2f)
            {
                if (utterance > 1.5f && random.NextFloat() < .3f)
                {
                    pendingPreset = random.NextBool() ? 0 : 7;
                    nextExpressionTime = math.min(nextExpressionTime, time + random.NextFloat(.15f, .5f));
                }
                if (utterance > 1.5f && random.NextFloat() < .4f) Lips(time + random.NextFloat(.1f, .4f), random.NextFloat(.5f, .9f), random.NextFloat(.1f, .2f), random.NextFloat(.2f, .35f));
                utterance = 0f;
            }
            DetectLaughter(time);
            if (laughter > .3f || speaking < .3f || time < nextEmphasisTime) return;
            bool onset = hasVisemes ? openFast > openSlow * 1.4f + .1f && voiceFast > SpeakThreshold : voiceFast > voiceSlow * 1.5f + .05f;
            if (!onset) return;
            nextEmphasisTime = time + .3f;
            if (random.NextFloat() >= .6f) return;
            emphasisStart = time;
            emphasisDuration = random.NextFloat(.45f, .8f);
            emphasisAmount = (hasVisemes ? math.clamp((openFast - openSlow) * .9f, .08f, .3f) : math.clamp((voiceFast - voiceSlow) * 1.4f, .08f, .3f)) * browBias;
            float side = random.NextFloat(-.35f, .35f);
            emphasisLeft = 1f - math.max(0f, side);
            emphasisRight = 1f + math.min(0f, side);
            emphasisInner = random.NextFloat(.4f, 1f);
            nextEmphasisTime = time + random.NextFloat(.5f, 1.3f);
        }
        void DetectLaughter(double time)
        {
            if (!hasVisemes)
            {
                openHigh = false;
                return;
            }
            if (openHigh)
            {
                if (openFast < .2f) openHigh = false;
                return;
            }
            if (openFast <= .45f || voiceFast <= .15f) return;
            openHigh = true;
            laughPeaks[laughPeakIndex] = time;
            laughPeakIndex = (laughPeakIndex + 1) % laughPeaks.Length;
            if (visemeConsonant > .25f * math.max(openSlow, .1f)) return;
            double oldest = laughPeaks[laughPeakIndex], previousPeak = oldest;
            if (time - oldest > 1.3) return;
            for (int i = 1; i < laughPeaks.Length; i++)
            {
                double peak = laughPeaks[(laughPeakIndex + i) % laughPeaks.Length], gap = peak - previousPeak;
                if (gap < .12 || gap > .4) return;
                previousPeak = peak;
            }
            laughUntil = time + random.NextFloat(.8f, 1.4f);
        }
        void Schedule(double time)
        {
            if (time >= nextExpressionTime)
            {
                if (current.active)
                {
                    previous = current;
                    previous.hold = math.max(0f, (float)(time - previous.start) - previous.attack);
                }
                int preset = pendingPreset >= 0 ? pendingPreset : Pick(attention > .35f ? listeningWeights : idleWeights);
                pendingPreset = -1;
                float lag = random.NextFloat(.03f, .12f);
                bool leftLeads = random.NextBool();
                current = new Expression { preset = preset, mirror = random.NextBool(), active = true, start = time, attack = random.NextFloat(.35f, .8f), hold = random.NextFloat(1.2f, 4.5f), release = random.NextFloat(.6f, 1.4f), intensity = random.NextFloat(.6f, 1.05f) * expressiveness, phase = random.NextFloat(6.283f), lagL = leftLeads ? 0f : lag, lagR = leftLeads ? lag : 0f };
                bool chain = random.NextFloat() < .3f;
                nextExpressionTime = time + current.attack + current.hold + (chain ? 0f : current.release + random.NextFloat(2.5f, 10f) * math.lerp(1f, .55f, attention));
            }
            Cheeks(time);
            if (time >= nextFlickTime)
            {
                if (speaking < .5f)
                {
                    flickStart = time;
                    flickAmount = random.NextFloat(.12f, .2f) * browBias;
                }
                nextFlickTime = time + math.lerp(random.NextFloat(7f, 18f), random.NextFloat(2.5f, 6f), attention);
            }
        }
        void Cheeks(double time)
        {
            bool quiet = speaking < .2f && laughter < .1f;
            bool puffing = time < puffStart + puffDuration, sucking = time < suckStart + suckDuration;
            if (time >= nextPuffTime)
            {
                if (quiet && !sucking)
                {
                    puffStart = time;
                    puffDuration = random.NextFloat(.7f, 1.6f);
                    puffAmount = random.NextFloat(.15f, .35f) * expressiveness;
                    puffSide = random.NextFloat() < .5f ? 0f : random.NextFloat(.5f, 1f) * (random.NextBool() ? 1f : -1f);
                    puffShift = puffSide == 0f && random.NextFloat() < .4f;
                    puffShiftLeftFirst = random.NextBool();
                    nextPuffTime = time + random.NextFloat(18f, 45f) * math.lerp(1f, 1.6f, attention);
                }
                else nextPuffTime = time + random.NextFloat(3f, 6f);
            }
            if (time >= nextSuckTime)
            {
                if (quiet && !puffing)
                {
                    suckStart = time;
                    suckDuration = random.NextFloat(1f, 2.5f);
                    suckAmount = random.NextFloat(.1f, .25f) * expressiveness;
                    nextSuckTime = time + random.NextFloat(30f, 70f);
                }
                else nextSuckTime = time + random.NextFloat(4f, 8f);
            }
            if (time >= nextSwallowTime)
            {
                if (quiet && !puffing)
                {
                    Lips(time, random.NextFloat(.5f, .7f), .25f, .3f);
                    nextSwallowTime = time + random.NextFloat(25f, 60f);
                }
                else nextSwallowTime = time + random.NextFloat(3f, 6f);
            }
            if (time >= nextHmmTime)
            {
                if (quiet && !puffing && !sucking)
                {
                    hmmStart = time;
                    hmmDuration = random.NextFloat(.8f, 1.6f);
                    hmmSide = random.NextFloat(.15f, .3f) * expressiveness;
                    hmmShrug = random.NextFloat(.1f, .2f) * expressiveness;
                    hmmLeft = random.NextBool();
                    nextHmmTime = time + math.lerp(random.NextFloat(40f, 90f), random.NextFloat(12f, 30f), attention);
                }
                else nextHmmTime = time + random.NextFloat(4f, 8f);
            }
            if (time >= nextTwitchTime)
            {
                twitchStart = time;
                twitchDuration = random.NextFloat(.3f, .5f);
                twitchAmount = random.NextFloat(.05f, .12f) * expressiveness;
                twitchLeft = random.NextBool();
                nextTwitchTime = time + random.NextFloat(6f, 16f);
            }
        }
        void Lips(double start, float duration, float press, float rollLower)
        {
            lipsStart = start;
            lipsDuration = duration;
            lipsPress = press * expressiveness;
            lipsRollLower = rollLower * expressiveness;
            lipsRollUpper = lipsRollLower * .6f;
        }
        static float Bump(float t, float duration, float rise, float fall)
        {
            if (t < 0f || t > duration) return 0f;
            rise = math.min(rise, duration * .45f);
            fall = math.min(fall, duration * .45f);
            return t < rise ? Smooth(t / rise) : t > duration - fall ? 1f - Smooth((t - duration + fall) / fall) : 1f;
        }
        int Pick(float[] weights)
        {
            float total = 0f;
            for (int i = 0; i < weights.Length; i++) total += weights[i];
            float pick = random.NextFloat(total);
            int preset = 0;
            for (; preset < weights.Length - 1; preset++) { pick -= weights[preset]; if (pick <= 0f) break; }
            return preset;
        }
        static float Envelope(in Expression expression, float t)
        {
            if (t <= 0f) return 0f;
            if (t < expression.attack) return Smooth(t / expression.attack);
            if (t < expression.attack + expression.hold) return 1f + .06f * math.sin(expression.phase + t * 1.3f);
            float release = (t - expression.attack - expression.hold) / expression.release;
            return release >= 1f ? 0f : 1f - Smooth(release);
        }
        bool Accumulate(ref Expression expression, double time)
        {
            if (!expression.active) return false;
            float t = (float)(time - expression.start);
            if (t - math.max(expression.lagL, expression.lagR) >= expression.attack + expression.hold + expression.release)
            {
                expression.active = false;
                return false;
            }
            float[] preset = presets[expression.preset];
            float left = Envelope(expression, t - expression.lagL) * expression.intensity, right = Envelope(expression, t - expression.lagR) * expression.intensity;
            bool mirror = expression.mirror;
            for (int s = 0; s < semanticCount; s++)
            {
                int source = !mirror ? s : s == sBrowOuterL ? sBrowOuterR : s == sBrowOuterR ? sBrowOuterL : s == sBrowDownL ? sBrowDownR : s == sBrowDownR ? sBrowDownL : s;
                float value = mirror && s == sAsym ? -preset[source] : preset[source];
                semL[s] += value * left;
                semR[s] += value * right;
            }
            return true;
        }
        static float Pulse(float t, float duration)
        {
            if (t < 0f || t > duration) return 0f;
            float rise = duration * .22f, top = duration * .41f;
            return t < rise ? Smooth(t / rise) : t < top ? 1f : 1f - Smooth((t - top) / (duration - top));
        }
        float Micro(float t, int m) => .5f + .3f * math.sin(t * microFreqA[m] + microPhaseA[m]) + .2f * math.sin(t * microFreqB[m] + microPhaseB[m]);
        void Compose(double time, float flick, float emphasis, bool micro, float puff, float suck, float twitch)
        {
            float speakSmile = (1f - (hasVisemes ? .2f : .45f) * speaking) * (1f - .7f * visemeRound) * (1f + .25f * visemeSpread) * smileBias;
            float mouth = (1f - .85f * speaking) * (1f - visemeClosure), frown = (1f - .6f * speaking) * (1f - .5f * openFast);
            float smileLeft = semL[sSmile] * speakSmile, smileRight = semR[sSmile] * speakSmile;
            float asymLeft = math.clamp(semL[sAsym] / math.max(semL[sSmile], 1e-4f), -1f, 1f), asymRight = math.clamp(semR[sAsym] / math.max(semR[sSmile], 1e-4f), -1f, 1f);
            float left = asymLeft >= 0f ? 1f : 1f + asymLeft, right = asymRight <= 0f ? 1f : 1f - asymRight;
            float microInner = 0f, microOuterL = 0f, microOuterR = 0f, microSmileL = 0f, microSmileR = 0f, microSquint = 0f, microCheekL = 0f, microCheekR = 0f;
            if (micro)
            {
                float t = (float)(time % 3600.0), rest = 1f - .8f * speaking;
                microInner = .03f * Micro(t, 0);
                microOuterL = .025f * Micro(t, 1);
                microOuterR = .025f * Micro(t, 2);
                microSmileL = (restSmile + .02f * Micro(t, 3)) * rest;
                microSmileR = (restSmile + .02f * Micro(t, 4)) * rest;
                microSquint = .02f * Micro(t, 5);
                microCheekL = .035f * Micro(t, 6) * rest;
                microCheekR = .035f * Micro(t, 7) * rest;
            }
            float talkCheek = visemeSpread * .06f * speaking, laughSmile = laughter * .55f * smileBias;
            float squintL = semL[sSquint] + microSquint + talkCheek + laughter * .45f, squintR = semR[sSquint] + microSquint + talkCheek + laughter * .45f;
            channel[smileL] = smileLeft * left + microSmileL + laughSmile;
            channel[smileR] = smileRight * right + microSmileR + laughSmile;
            channel[cheekSquintL] = squintL * .8f * left + microCheekL + (twitchLeft ? twitch : 0f);
            channel[cheekSquintR] = squintR * .8f * right + microCheekR + (twitchLeft ? 0f : twitch);
            channel[eyeSquintL] = squintL * .6f * left;
            channel[eyeSquintR] = squintR * .6f * right;
            channel[browInnerUp] = (semL[sBrowInner] + semR[sBrowInner]) * .5f * browBias + flick + emphasis * emphasisInner + microInner + laughter * .1f;
            channel[browOuterUpL] = semL[sBrowOuterL] * browBias + flick * .6f + emphasis * emphasisLeft * .7f + microOuterL;
            channel[browOuterUpR] = semR[sBrowOuterR] * browBias + flick * .6f + emphasis * emphasisRight * .7f + microOuterR;
            channel[browDownL] = semL[sBrowDownL] * browBias;
            channel[browDownR] = semR[sBrowDownR] * browBias;
            channel[eyeWideL] = semL[sWide] + emphasis * .2f;
            channel[eyeWideR] = semR[sWide] + emphasis * .2f;
            channel[mouthPressL] = semL[sPress] * mouth + lipsNow * lipsPress;
            channel[mouthPressR] = semR[sPress] * mouth + lipsNow * lipsPress;
            channel[rollLowerChannel] = lipsNow * lipsRollLower;
            channel[rollUpperChannel] = lipsNow * lipsRollUpper;
            channel[sideL] = channel[sideLowerL] = hmmLeft ? hmmNow * hmmSide : 0f;
            channel[sideR] = channel[sideLowerR] = hmmLeft ? 0f : hmmNow * hmmSide;
            channel[shrugChannel] = hmmNow * hmmShrug;
            float atRest = micro ? (1f - .9f * speaking) * (1f - lipsNow) : 0f, breath = (breathPhase < .4f ? Smooth(breathPhase / .4f) : 1f - Smooth((breathPhase - .4f) / .6f)) * breathDepth;
            channel[jawChannel] = (restJaw + .015f * breath) * atRest;
            channel[nasalL] = channel[nasalR] = .12f * breath * atRest;
            channel[dimpleL] = channel[smileL] * .35f;
            channel[dimpleR] = channel[smileR] * .35f;
            channel[sneerL] = channel[sneerR] = laughter * .3f + math.max(0f, (channel[smileL] + channel[smileR]) * .5f - .4f) * .35f;
            channel[mouthFrownL] = semL[sFrown] * frown;
            channel[mouthFrownR] = semR[sFrown] * frown;
            float puffLeft = 1f - math.max(0f, puffSide), puffRight = 1f + math.min(0f, puffSide);
            if (puffShift)
            {
                float across = Smooth(((float)(time - puffStart) / puffDuration - .3f) / .4f);
                puffLeft = puffShiftLeftFirst ? 1f - across : across;
                puffRight = 1f - puffLeft;
            }
            float plosive = visemeClosure * .08f * speaking;
            float puffL = puff * puffLeft + plosive, puffR = puff * puffRight + plosive;
            channel[cheekPuffL] = puffL;
            channel[cheekPuffR] = puffR;
            channel[cheekPuffBoth] = hasSidePuff ? 0f : math.max(puffL, puffR);
            channel[cheekSuckL] = channel[cheekSuckR] = suck;
            channel[cheekSuckBoth] = hasSideSuck ? 0f : suck;
            float smile = (smileLeft + smileRight) * .5f, asym = (asymLeft + asymRight) * .5f;
            channel[genericSmile] = hasSideSmile ? 0f : smile * (1f - .5f * math.abs(asym)) + (microSmileL + microSmileR) * .5f + laughSmile;
            channel[laughterChannel] = laughter * .8f;
            channel[genericSurprised] = hasWide || hasBrowInner ? 0f : math.max((semL[sWide] + semR[sWide]) * .5f, channel[browInnerUp]) * .5f;
            channel[genericSad] = hasFrown ? 0f : (semL[sFrown] + semR[sFrown]) * .25f * frown;
            for (int t = 0; t < contribution.Length; t++) contribution[t] = 0f;
            for (int c = 0; c < channelCount; c++)
            {
                float value = channel[c] * gain;
                if (value <= 0f) continue;
                int[] targets = channelTargets[c];
                for (int t = 0; t < targets.Length; t++) contribution[targets[t]] += value;
            }
        }
        void Write()
        {
            for (int t = 0; t < renderers.Length; t++)
            {
                float add = contribution[t];
                if (add <= 0f && float.IsNaN(lastWritten[t])) continue;
                SkinnedMeshRenderer smr = renderers[t];
                if (smr == null) continue;
                int index = indices[t];
                float weight = smr.GetBlendShapeWeight(index);
                if (add <= 0f)
                {
                    if (weight == lastWritten[t] && weight != baseWeight[t]) smr.SetBlendShapeWeight(index, baseWeight[t]);
                    lastWritten[t] = float.NaN;
                    continue;
                }
                if (float.IsNaN(lastWritten[t]) || weight != lastWritten[t]) baseWeight[t] = weight;
                float output = math.clamp(baseWeight[t] + math.min(add, 1f) * 100f, 0f, 100f);
                if (output != weight) smr.SetBlendShapeWeight(index, output);
                lastWritten[t] = output;
            }
        }
        void Flush()
        {
            if (flushed) return;
            for (int t = 0; t < renderers.Length; t++)
            {
                SkinnedMeshRenderer smr = renderers[t];
                if (smr == null || float.IsNaN(lastWritten[t])) continue;
                if (smr.GetBlendShapeWeight(indices[t]) == lastWritten[t] && lastWritten[t] != baseWeight[t]) smr.SetBlendShapeWeight(indices[t], baseWeight[t]);
                lastWritten[t] = float.NaN;
            }
            flushed = true;
        }
        static float Smooth(float x)
        {
            x = math.saturate(x);
            return x * x * x * (x * (x * 6f - 15f) + 10f);
        }
        static void GatherTalkers(double time)
        {
            if (time == talkerTime) return;
            talkerTime = time;
            talkerCount = 0;
            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            if (local != null)
            {
                float level = local.LocalVisemeDriver.VoiceLevel01;
                if (level > SpeakThreshold) AddTalker(local.IdleExpressionDriver, BasisLocalCameraDriver.Position, level);
            }
            BasisNetworkReceiver[] receivers = BasisNetworkPlayers.ReceiversSnapshot;
            int count = math.min(BasisNetworkPlayers.ReceiverCount, receivers.Length);
            for (int i = 0; i < count && talkerCount < talkerOwners.Length; i++)
            {
                BasisNetworkReceiver receiver = receivers[i];
                if (receiver is null || receiver.AudioReceiverModule is null) continue;
                float level = receiver.AudioReceiverModule.VoiceLevel01;
                if (level <= SpeakThreshold) continue;
                BasisRemotePlayer player = receiver.RemotePlayer;
                if (player is null || player.AvatarTransform == null) continue;
                AddTalker(player.IdleExpressionDriver, player.AvatarTransform.position, level);
            }
        }
        static void AddTalker(BasisIdleExpressionDriver owner, Vector3 at, float level)
        {
            talkerOwners[talkerCount] = owner;
            talkerPositions[talkerCount] = at;
            talkerLevels[talkerCount] = level;
            talkerCount++;
        }
        bool InRange(double time, Vector3 viewer, float maxSq)
        {
            if (time >= nextRangeCheck)
            {
                Transform root = remote.AvatarTransform;
                if (root != null)
                {
                    position = root.position;
                    distanceSq = (position - viewer).sqrMagnitude;
                }
                else distanceSq = float.MaxValue;
                inRange = distanceSq <= maxSq;
                nextRangeCheck = time + RangeCheckInterval + random.NextFloat(.1f);
            }
            return inRange;
        }
        public static void SimulateRemotes(double time, float dt)
        {
            int count = remoteDrivers.Count;
            if (count == 0) return;
            using var scope = BasisFaceMarkers.IdleRemote.Auto();
            bool enabled = BasisSettingsDefaults.IdleFaceExpressions.RawValue;
            Vector3 viewer = BasisLocalCameraDriver.Position;
            float maxSq = MaxRemoteDistance * MaxRemoteDistance, detailSq = DetailDistance * DetailDistance;
            for (int i = count - 1; i >= 0; i--)
            {
                BasisIdleExpressionDriver driver = remoteDrivers[i];
                BasisRemotePlayer player = driver.remote;
                if (driver.flushed && driver.gain <= 0f && (!enabled || !player.FaceIsVisible || (!driver.inRange && time < driver.nextRangeCheck))) continue;
                if (player.IsDestroyed) continue;
                bool visible = player.FaceIsVisible && driver.InRange(time, viewer, maxSq);
                BasisNetworkReceiver receiver = player.NetworkReceiver;
                BasisAudioReceiver audio = visible && receiver != null ? receiver.AudioReceiverModule : null;
                driver.Step(time, dt, visible, audio != null ? audio.VoiceLevel01 : 0f, driver.distanceSq <= detailSq, audio != null ? Visemes(audio.visemeDriver) : null);
            }
        }
    }
}
