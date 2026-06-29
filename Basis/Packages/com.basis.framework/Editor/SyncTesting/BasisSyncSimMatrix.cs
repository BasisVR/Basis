using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Basis.Scripts.Networking.Sync.Testing
{
    /// <summary>
    /// Builds and runs the full combinatorial test matrix over the value-sync data path:
    /// field configs (every type x quantize x interpolate, plus composites and transform-style layouts)
    /// x motion patterns x network impairment profiles x optional feature flags (extrapolation,
    /// teleport threshold) x seeds. Feasibility-pruned (e.g. teleport-threshold only on position-first
    /// layouts) so every row is a meaningful combination.
    ///
    /// Driven by the Basis > Networking > Sync Test Matrix editor window.
    /// </summary>
    public static class BasisSyncSimMatrix
    {
        public sealed class FieldConfig
        {
            public string Name;
            public List<SimFieldSpec> Fields;
            public bool PositionFirst;
        }

        public sealed class MatrixOptions
        {
            public bool QuantizeVariants = true;
            public bool InterpolateOffVariants = true;
            public bool ExtrapolateVariant = true;
            public bool TeleportThresholdVariant = true;
            public bool ChecksumOffVariant = false; // adds checksum-OFF runs on corrupting profiles (expected to fail) to characterize the option
            public bool CompositeConfigs = true;

            // Multi-object scenes: several objects share one wire, exercising batch coalesce/demux, the real Burst
            // interp over a multi-slot SoA, and a late-joiner. One scene produces one result row per object.
            public bool MultiObjectScenes = true;
            public bool BatchedTransport = true;   // route a scene's frame through the real BatchWriter/BatchReader
            public bool RealInterpJob = true;       // sample the "other side" with the real InterpolateSyncObjectsJob
            public bool LateJoinVariant = true;      // add a receiver that attaches mid-stream and recovers via keyframe

            public int Seeds = 1;
            public int BaseSeed = 0x5A1D;

            public List<SyncMotion> Motions;     // null => all
            public HashSet<string> ProfileNames; // null => all

            public float Dt = 1f / 72f;
            public float DurationSeconds = 6f;
            public float SettleSeconds = 1.8f;

            public static MatrixOptions Quick()
            {
                return new MatrixOptions
                {
                    QuantizeVariants = true,
                    InterpolateOffVariants = false,
                    ExtrapolateVariant = false,
                    TeleportThresholdVariant = true,
                    CompositeConfigs = true,
                    Seeds = 1,
                    DurationSeconds = 4f,
                    SettleSeconds = 1.5f,
                    Motions = new List<SyncMotion> { SyncMotion.Sine, SyncMotion.Step, SyncMotion.Teleport, SyncMotion.RandomWalk },
                    ProfileNames = new HashSet<string> { "perfect", "loss-heavy", "reorder", "bad-wifi", "corrupt", "chaos" },
                };
            }

            public static MatrixOptions Full() => new MatrixOptions();
        }

        // ── Field configs ──
        public static List<FieldConfig> BuildFieldConfigs(MatrixOptions o)
        {
            var list = new List<FieldConfig>();

            AddType(list, o, BasisSyncFieldType.Position, quantizable: true, interpToggle: false);
            AddType(list, o, BasisSyncFieldType.Rotation, quantizable: false, interpToggle: false);
            AddType(list, o, BasisSyncFieldType.Scale, quantizable: true, interpToggle: false);
            AddType(list, o, BasisSyncFieldType.Float, quantizable: true, interpToggle: true);
            AddType(list, o, BasisSyncFieldType.Angle, quantizable: true, interpToggle: true);
            AddType(list, o, BasisSyncFieldType.Vector2, quantizable: true, interpToggle: true);
            AddType(list, o, BasisSyncFieldType.Vector4, quantizable: true, interpToggle: true);
            AddType(list, o, BasisSyncFieldType.Color, quantizable: true, interpToggle: true);
            AddType(list, o, BasisSyncFieldType.Int, quantizable: false, interpToggle: false);
            AddType(list, o, BasisSyncFieldType.UInt, quantizable: false, interpToggle: false);
            AddType(list, o, BasisSyncFieldType.UShort, quantizable: false, interpToggle: false);
            AddType(list, o, BasisSyncFieldType.Bool, quantizable: false, interpToggle: false);
            AddType(list, o, BasisSyncFieldType.Byte, quantizable: false, interpToggle: false);

            // Variable-bit smallest-three rotation — the Rotation pool's selectable precision (default is 9 = 32-bit).
            if (o.QuantizeVariants)
            {
                list.Add(new FieldConfig { Name = "Rotation-compact", PositionFirst = false, Fields = new List<SimFieldSpec> { new SimFieldSpec(BasisSyncFieldType.Rotation) { RotBits = 7 } } });
                list.Add(new FieldConfig { Name = "Rotation-fine", PositionFirst = false, Fields = new List<SimFieldSpec> { new SimFieldSpec(BasisSyncFieldType.Rotation) { RotBits = 13 } } });
            }

            if (o.CompositeConfigs)
            {
                list.Add(KitchenSink());
                list.Add(TransformFull(half: false));
                list.Add(TransformFull(half: true));
                list.Add(TransformFullScale());
                list.Add(DoorRotY());
                list.Add(PosOnly());
            }

            return list;
        }

        static void AddType(List<FieldConfig> list, MatrixOptions o, BasisSyncFieldType t, bool quantizable, bool interpToggle)
        {
            bool posFirst = t == BasisSyncFieldType.Position;
            list.Add(One(t.ToString(), t, interpolate: true, quantize: false, posFirst));
            if (quantizable && o.QuantizeVariants)
                list.Add(One(t + "-quant", t, interpolate: true, quantize: true, posFirst));
            if (interpToggle && o.InterpolateOffVariants)
                list.Add(One(t + "-noInterp", t, interpolate: false, quantize: false, posFirst));
        }

        static FieldConfig One(string name, BasisSyncFieldType t, bool interpolate, bool quantize, bool posFirst)
        {
            return new FieldConfig
            {
                Name = name,
                PositionFirst = posFirst,
                Fields = new List<SimFieldSpec> { new SimFieldSpec(t, interpolate, quantize) },
            };
        }

        static FieldConfig KitchenSink()
        {
            return new FieldConfig
            {
                Name = "KitchenSink",
                PositionFirst = true,
                Fields = new List<SimFieldSpec>
                {
                    new SimFieldSpec(BasisSyncFieldType.Position),
                    new SimFieldSpec(BasisSyncFieldType.Rotation),
                    new SimFieldSpec(BasisSyncFieldType.Scale),
                    new SimFieldSpec(BasisSyncFieldType.Float),
                    new SimFieldSpec(BasisSyncFieldType.Angle),
                    new SimFieldSpec(BasisSyncFieldType.Vector2),
                    new SimFieldSpec(BasisSyncFieldType.Vector4, true, true),
                    new SimFieldSpec(BasisSyncFieldType.Color, true, true),
                    new SimFieldSpec(BasisSyncFieldType.Int),
                    new SimFieldSpec(BasisSyncFieldType.UInt),
                    new SimFieldSpec(BasisSyncFieldType.UShort),
                    new SimFieldSpec(BasisSyncFieldType.Bool),
                    new SimFieldSpec(BasisSyncFieldType.Byte),
                },
            };
        }

        static FieldConfig TransformFull(bool half)
        {
            return new FieldConfig
            {
                Name = half ? "Transform-half" : "Transform-full",
                PositionFirst = false,
                Fields = new List<SimFieldSpec>
                {
                    new SimFieldSpec(BasisSyncFieldType.Float, true, half),
                    new SimFieldSpec(BasisSyncFieldType.Float, true, half),
                    new SimFieldSpec(BasisSyncFieldType.Float, true, half),
                    new SimFieldSpec(BasisSyncFieldType.Rotation),
                },
            };
        }

        static FieldConfig TransformFullScale()
        {
            var c = TransformFull(half: false);
            c.Name = "Transform-full+scale";
            c.Fields.Add(new SimFieldSpec(BasisSyncFieldType.Float));
            c.Fields.Add(new SimFieldSpec(BasisSyncFieldType.Float));
            c.Fields.Add(new SimFieldSpec(BasisSyncFieldType.Float));
            return c;
        }

        static FieldConfig DoorRotY()
        {
            return new FieldConfig
            {
                Name = "Door-rotY",
                PositionFirst = false,
                Fields = new List<SimFieldSpec> { new SimFieldSpec(BasisSyncFieldType.Angle) },
            };
        }

        static FieldConfig PosOnly()
        {
            return new FieldConfig
            {
                Name = "Pos-xyz",
                PositionFirst = false,
                Fields = new List<SimFieldSpec>
                {
                    new SimFieldSpec(BasisSyncFieldType.Float),
                    new SimFieldSpec(BasisSyncFieldType.Float),
                    new SimFieldSpec(BasisSyncFieldType.Float),
                },
            };
        }

        // ── Network profiles ──
        public static List<NetworkProfile> BuildProfiles()
        {
            return new List<NetworkProfile>
            {
                new NetworkProfile { Name = "perfect" },
                new NetworkProfile { Name = "loss-light", DeltaLoss = 0.05f },
                new NetworkProfile { Name = "loss-heavy", DeltaLoss = 0.40f },
                new NetworkProfile { Name = "duplicate", DuplicateProb = 0.25f },
                new NetworkProfile { Name = "reorder", BaseLatencySec = 0.02f, JitterSec = 0.08f },
                new NetworkProfile { Name = "jitter-heavy", BaseLatencySec = 0.03f, JitterSec = 0.15f },
                new NetworkProfile { Name = "bad-wifi", DeltaLoss = 0.20f, DuplicateProb = 0.10f, BaseLatencySec = 0.04f, JitterSec = 0.12f },
                new NetworkProfile { Name = "corrupt", DeltaLoss = 0.02f, CorruptProb = 0.08f },
                new NetworkProfile { Name = "chaos", DeltaLoss = 0.35f, DuplicateProb = 0.20f, CorruptProb = 0.10f, BaseLatencySec = 0.03f, JitterSec = 0.20f, ImpairReliable = true, HardConvergence = false },
            };
        }

        static readonly SyncMotion[] AllMotions =
        {
            SyncMotion.Static, SyncMotion.Ramp, SyncMotion.Sine, SyncMotion.Step, SyncMotion.Teleport, SyncMotion.RandomWalk,
        };

        // ── Multi-object scenes ──
        static SimFieldSpec F(BasisSyncFieldType t, bool interp = true, bool quant = false) => new SimFieldSpec(t, interp, quant);
        static List<SimFieldSpec> Obj(params SimFieldSpec[] f) => new List<SimFieldSpec>(f);

        // Heterogeneous object sets: different cont/rot/discrete counts so the shared-wire batch and the multi-slot
        // SoA both run over non-trivial, mismatched per-object windows.
        static List<BasisSyncSceneObjectSpec> MixedSet() => new List<BasisSyncSceneObjectSpec>
        {
            new BasisSyncSceneObjectSpec("xform", Obj(F(BasisSyncFieldType.Position), F(BasisSyncFieldType.Rotation), F(BasisSyncFieldType.Scale))),
            new BasisSyncSceneObjectSpec("params", Obj(F(BasisSyncFieldType.Float), F(BasisSyncFieldType.Vector2), F(BasisSyncFieldType.Color, true, true), F(BasisSyncFieldType.Bool))),
            new BasisSyncSceneObjectSpec("door", Obj(F(BasisSyncFieldType.Angle))),
        };

        static List<BasisSyncSceneObjectSpec> TransformsSet() => new List<BasisSyncSceneObjectSpec>
        {
            new BasisSyncSceneObjectSpec("poseA", Obj(F(BasisSyncFieldType.Position), F(BasisSyncFieldType.Rotation))),
            new BasisSyncSceneObjectSpec("poseB-quant", Obj(F(BasisSyncFieldType.Position, true, true), F(BasisSyncFieldType.Rotation))),
            new BasisSyncSceneObjectSpec("full", Obj(F(BasisSyncFieldType.Position), F(BasisSyncFieldType.Rotation), F(BasisSyncFieldType.Scale))),
        };

        static List<BasisSyncSceneObjectSpec> DiscreteSet() => new List<BasisSyncSceneObjectSpec>
        {
            new BasisSyncSceneObjectSpec("flags", Obj(F(BasisSyncFieldType.Bool), F(BasisSyncFieldType.Byte), F(BasisSyncFieldType.Int), F(BasisSyncFieldType.UShort))),
            new BasisSyncSceneObjectSpec("counters", Obj(F(BasisSyncFieldType.UInt), F(BasisSyncFieldType.UShort))),
            new BasisSyncSceneObjectSpec("mix", Obj(F(BasisSyncFieldType.Int), F(BasisSyncFieldType.Bool), F(BasisSyncFieldType.Float))),
        };

        public static List<BasisSyncSceneScenario> EnumerateScenes(MatrixOptions o)
        {
            var scenes = new List<BasisSyncSceneScenario>();
            if (!o.MultiObjectScenes) return scenes;

            var sets = new (string name, List<BasisSyncSceneObjectSpec> objs)[]
            {
                ("Mixed", MixedSet()),
                ("Transforms", TransformsSet()),
                ("Discrete", DiscreteSet()),
            };

            List<NetworkProfile> profiles = BuildProfiles();
            if (o.ProfileNames != null) profiles = profiles.FindAll(p => o.ProfileNames.Contains(p.Name));
            SyncMotion[] motions = o.Motions != null ? o.Motions.ToArray() : new[] { SyncMotion.Sine, SyncMotion.Step, SyncMotion.RandomWalk };

            int activeFrames = (int)Math.Ceiling(o.DurationSeconds / (double)o.Dt);
            int lateJoinFrame = Math.Max(8, activeFrames / 3);
            SceneTransport transport = o.BatchedTransport ? SceneTransport.Batched : SceneTransport.PerPacket;
            SceneSample sample = o.RealInterpJob ? SceneSample.RealJob : SceneSample.Managed;

            foreach (var set in sets)
            {
                foreach (SyncMotion motion in motions)
                {
                    foreach (NetworkProfile profile in profiles)
                    {
                        int seed = unchecked(o.BaseSeed + Hash(set.name) * 31 + (int)motion * 7 + Hash(profile.Name) + 0x5CE2E);
                        scenes.Add(MakeScene(set.name, set.objs, motion, profile, seed, transport, sample, 0, o));

                        // A per-packet + managed control on a faithful profile pins the batched / real-job path against the simple one.
                        if (profile.Name == "perfect" && (transport != SceneTransport.PerPacket || sample != SceneSample.Managed))
                            scenes.Add(MakeScene(set.name, set.objs, motion, profile, seed ^ 0x2B2B, SceneTransport.PerPacket, SceneSample.Managed, 0, o));
                    }
                }

                if (o.LateJoinVariant)
                {
                    NetworkProfile lj = profiles.Find(p => p.Name == "reorder") ?? profiles.Find(p => p.Name == "perfect") ?? (profiles.Count > 0 ? profiles[0] : null);
                    if (lj != null)
                    {
                        int seed = unchecked(o.BaseSeed + Hash(set.name) * 17 + 0x1A7E);
                        scenes.Add(MakeScene(set.name + "-latejoin", set.objs, SyncMotion.Sine, lj, seed, transport, sample, lateJoinFrame, o));
                    }
                }
            }

            return scenes;
        }

        static BasisSyncSceneScenario MakeScene(string setName, List<BasisSyncSceneObjectSpec> objs, SyncMotion motion, NetworkProfile profile, int seed, SceneTransport transport, SceneSample sample, int lateJoinFrame, MatrixOptions o)
        {
            string tag = transport == SceneTransport.Batched ? "batched" : "perpkt";
            string smp = sample == SceneSample.RealJob ? "realjob" : "managed";
            return new BasisSyncSceneScenario
            {
                Name = $"Scene:{setName}/{motion}/{profile.Name}/{tag}/{smp}",
                Objects = objs,
                Motion = motion,
                Profile = profile,
                Seed = seed,
                Transport = transport,
                Sample = sample,
                LateJoinFrame = lateJoinFrame,
                IdleKeyframeBackoff = true,
                RotationSendThresholdDegrees = 0.5f,
                UseChecksum = true,
                Dt = o.Dt,
                DurationSeconds = o.DurationSeconds,
                SettleSeconds = o.SettleSeconds,
            };
        }

        // ── Enumeration ──
        public static List<BasisSyncSimScenario> Enumerate(MatrixOptions o)
        {
            var scenarios = new List<BasisSyncSimScenario>();
            List<FieldConfig> configs = BuildFieldConfigs(o);
            List<NetworkProfile> profiles = BuildProfiles();
            if (o.ProfileNames != null) profiles = profiles.FindAll(p => o.ProfileNames.Contains(p.Name));
            SyncMotion[] motions = o.Motions != null ? o.Motions.ToArray() : AllMotions;

            for (int ci = 0; ci < configs.Count; ci++)
            {
                FieldConfig cfg = configs[ci];
                foreach (SyncMotion motion in motions)
                {
                    foreach (NetworkProfile profile in profiles)
                    {
                        for (int seedIx = 0; seedIx < Math.Max(1, o.Seeds); seedIx++)
                        {
                            int seed = unchecked(o.BaseSeed + seedIx * 100003 + Hash(cfg.Name) * 31 + (int)motion * 7 + Hash(profile.Name));

                            scenarios.Add(MakeScenario(cfg, motion, profile, seed, o, extrapolate: false, teleport: false, suffix: ""));

                            if (o.ExtrapolateVariant && (motion == SyncMotion.Sine || motion == SyncMotion.RandomWalk || motion == SyncMotion.Step))
                                scenarios.Add(MakeScenario(cfg, motion, profile, seed ^ 0x1234, o, extrapolate: true, teleport: false, suffix: "+extrap"));

                            if (o.TeleportThresholdVariant && cfg.PositionFirst && (motion == SyncMotion.Teleport || motion == SyncMotion.Step))
                                scenarios.Add(MakeScenario(cfg, motion, profile, seed ^ 0x55AA, o, extrapolate: false, teleport: true, suffix: "+teleThresh"));

                            // Characterize the checksum: on corrupting profiles, also run it OFF (expected to fail) so the CSV shows the option carrying the recovery.
                            if (o.ChecksumOffVariant && profile.CorruptProb > 0f)
                                scenarios.Add(MakeScenario(cfg, motion, profile, seed ^ 0x0C0C, o, extrapolate: false, teleport: false, suffix: "+noChecksum", useChecksum: false));
                        }
                    }
                }
            }

            return scenarios;
        }

        static BasisSyncSimScenario MakeScenario(FieldConfig cfg, SyncMotion motion, NetworkProfile profile, int seed, MatrixOptions o, bool extrapolate, bool teleport, string suffix, bool useChecksum = true)
        {
            return new BasisSyncSimScenario
            {
                Name = $"{cfg.Name}/{motion}/{profile.Name}{suffix}",
                FieldConfigName = cfg.Name + suffix,
                Fields = cfg.Fields,
                Motion = motion,
                Profile = profile,
                Seed = seed,
                Extrapolate = extrapolate,
                TeleportThreshold = teleport,
                UseChecksum = useChecksum,
                Dt = o.Dt,
                DurationSeconds = o.DurationSeconds,
                SettleSeconds = o.SettleSeconds,
            };
        }

        // ── Runner ──
        public static List<BasisSyncSimResult> RunAll(MatrixOptions o, Action<int, int, BasisSyncSimResult> onResult, Func<bool> cancelled)
        {
            List<BasisSyncSimScenario> scenarios = Enumerate(o);
            List<BasisSyncSceneScenario> scenes = EnumerateScenes(o);
            int total = scenarios.Count + scenes.Count;
            var results = new List<BasisSyncSimResult>(total);

            int done = 0;
            for (int i = 0; i < scenarios.Count; i++)
            {
                if (cancelled != null && cancelled()) return results;
                BasisSyncSimResult r = BasisSyncSim.Run(scenarios[i]);
                results.Add(r);
                onResult?.Invoke(++done, total, r);
            }

            // Each scene is one run that yields one result row per object.
            for (int i = 0; i < scenes.Count; i++)
            {
                if (cancelled != null && cancelled()) return results;
                List<BasisSyncSimResult> sceneResults = BasisSyncSceneSim.RunScene(scenes[i]);
                results.AddRange(sceneResults);
                onResult?.Invoke(++done, total, sceneResults.Count > 0 ? sceneResults[0] : null);
            }

            return results;
        }

        public static int CountScenarios(MatrixOptions o) => Enumerate(o).Count + EnumerateScenes(o).Count;

        // ── CSV ──
        public static string ToCsv(List<BasisSyncSimResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine(BasisSyncSimResult.CsvHeader);
            for (int i = 0; i < results.Count; i++) sb.AppendLine(results[i].ToCsvRow());
            return sb.ToString();
        }

        /// <summary>
        /// Command-line entry point for CI: runs the full matrix and writes CSV to the given path.
        /// Usage: Unity -batchmode -executeMethod Basis.Scripts.Networking.Sync.Testing.BasisSyncSimMatrix.RunAndExportCSV -matrixOptions '{"Seeds":3}' -outputPath /path/to/output.csv
        /// </summary>
        public static void RunAndExportCSV()
        {
            string matrixOptionsJson = null;
            string outputPath = null;

            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-matrixOptions" && i + 1 < args.Length)
                    matrixOptionsJson = args[i + 1];
                else if (args[i] == "-outputPath" && i + 1 < args.Length)
                    outputPath = args[i + 1];
            }

            MatrixOptions options = MatrixOptions.Full();
            if (!string.IsNullOrEmpty(matrixOptionsJson))
            {
                try
                {
                    options = JsonUtility.FromJson<MatrixOptions>(matrixOptionsJson);
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogError($"Failed to parse -matrixOptions JSON: {e.Message}");
                    UnityEditor.EditorApplication.Exit(1);
                    return;
                }
            }

            if (string.IsNullOrEmpty(outputPath))
            {
                UnityEngine.Debug.LogError("-outputPath is required");
                UnityEditor.EditorApplication.Exit(1);
                return;
            }

            UnityEngine.Debug.Log($"Running sync sim matrix: {BasisSyncSimMatrix.CountScenarios(options)} scenarios");

            var results = RunAll(options, (done, total, r) =>
            {
                if (r != null)
                    UnityEngine.Debug.Log($"[{done}/{total}] {r.ScenarioName} => {(r.Warn ? "WARN" : (r.Pass ? "PASS" : "FAIL"))} {r.FailReason}");
            }, null);

            string csv = ToCsv(results);
            System.IO.File.WriteAllText(outputPath, csv);
            UnityEngine.Debug.Log($"CSV written to {outputPath} ({results.Count} rows)");
            UnityEditor.EditorApplication.Exit(0);
        }

        static int Hash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int h = 17;
            for (int i = 0; i < s.Length; i++) h = unchecked(h * 31 + s[i]);
            return h & 0x7FFFFFFF;
        }
    }
}