using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Reads a file written by <see cref="BasisAvatarRecorder"/> (headerless version 1 or version 2)
/// and composes the humanoid body pose with the recorded avatar root motion.
/// </summary>
public sealed class BasisAvatarRecording
{
    public int Version;
    public bool HasRootMotion;
    public float HumanScale = 1f;
    public int FrameCount;
    public string[] BonePaths = Array.Empty<string>();
    public float[] Intervals;
    public float[] Times;
    public Quaternion[] BodyRotations;
    public Vector3[] BodyPositions;
    public float[][] Muscles;
    public float[] Scales;
    public Vector3[] RootPositions;
    public Quaternion[] RootRotations;
    public float[] RootScales;
    // Indexed [frame * BonePaths.Length + bone]
    public Vector3[] BoneLocalPositions;
    public Quaternion[] BoneLocalRotations;
    public Vector3[] BoneLocalScales;

    public int BoneCount => BonePaths.Length;

    public static void ReadHeader(Stream stream, out int version, out int flags, out float humanScale, out string[] bonePaths)
    {
        version = 1; flags = 0; humanScale = 1f; bonePaths = Array.Empty<string>();
        long start = stream.Position;
        using BinaryReader br = new BinaryReader(stream, Encoding.UTF8, true);
        if (stream.Length - start < 8 || br.ReadInt32() != BasisAvatarRecorder.FileMagic)
        {
            stream.Position = start;
            return;
        }
        version = br.ReadInt32();
        if (version != BasisAvatarRecorder.FileVersion) throw new InvalidDataException($"Unsupported avatar recording version {version}");
        flags = br.ReadInt32();
        humanScale = br.ReadSingle();
        int count = br.ReadInt32();
        if (count < 0 || count > 65536) throw new InvalidDataException($"Invalid avatar recording bone count {count}");
        bonePaths = new string[count];
        for (int i = 0; i < count; i++) bonePaths[i] = br.ReadString();
    }

    public static int GetFloatsPerFrame(int version, int bones) => version >= 2 ? BasisAvatarRecorder.GetFloatsPerFrame(bones) : BasisAvatarRecorder.FloatsPerFrame;

    public static int CountFrames(string path)
    {
        using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ReadHeader(fs, out int version, out _, out _, out string[] bonePaths);
        return (int)((fs.Length - fs.Position) / (GetFloatsPerFrame(version, bonePaths.Length) * sizeof(float)));
    }

    public static BasisAvatarRecording Load(string path)
    {
        using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Read(fs);
    }

    public static BasisAvatarRecording Read(Stream stream)
    {
        ReadHeader(stream, out int version, out int flags, out float humanScale, out string[] bonePaths);
        int bones = bonePaths.Length;
        int floatsPerFrame = GetFloatsPerFrame(version, bones);
        int frames = (int)((stream.Length - stream.Position) / (floatsPerFrame * sizeof(float)));
        int muscleCount = BasisAvatarRecorder.MuscleCount;
        BasisAvatarRecording r = new BasisAvatarRecording
        {
            Version = version,
            HasRootMotion = version >= 2 && (flags & BasisAvatarRecorder.FlagRootMotion) != 0,
            HumanScale = humanScale,
            FrameCount = frames,
            BonePaths = bonePaths,
            Intervals = new float[frames],
            Times = new float[frames],
            BodyRotations = new Quaternion[frames],
            BodyPositions = new Vector3[frames],
            Muscles = new float[muscleCount][],
            Scales = new float[frames],
            RootPositions = new Vector3[frames],
            RootRotations = new Quaternion[frames],
            RootScales = new float[frames],
            BoneLocalPositions = new Vector3[frames * bones],
            BoneLocalRotations = new Quaternion[frames * bones],
            BoneLocalScales = new Vector3[frames * bones],
        };
        for (int m = 0; m < muscleCount; m++) r.Muscles[m] = new float[frames];

        byte[] bytes = new byte[floatsPerFrame * sizeof(float)];
        float[] f = new float[floatsPerFrame];
        float t = 0f;
        for (int i = 0; i < frames; i++)
        {
            int read = 0;
            while (read < bytes.Length)
            {
                int n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);

            r.Intervals[i] = f[0];
            if (i > 0) t += f[0];
            r.Times[i] = t;
            r.BodyRotations[i] = new Quaternion(f[1], f[2], f[3], f[4]);
            r.BodyPositions[i] = new Vector3(f[5], f[6], f[7]);
            for (int m = 0; m < muscleCount; m++) r.Muscles[m][i] = f[8 + m];
            r.Scales[i] = f[8 + muscleCount];

            int o = BasisAvatarRecorder.FloatsPerFrame;
            if (version >= 2)
            {
                r.RootPositions[i] = new Vector3(f[o], f[o + 1], f[o + 2]);
                r.RootRotations[i] = new Quaternion(f[o + 3], f[o + 4], f[o + 5], f[o + 6]);
                r.RootScales[i] = f[o + 7];
                o += BasisAvatarRecorder.RootFloats;
                for (int b = 0, k = i * bones; b < bones; b++, k++, o += BasisAvatarRecorder.BoneFloats)
                {
                    r.BoneLocalPositions[k] = new Vector3(f[o], f[o + 1], f[o + 2]);
                    r.BoneLocalRotations[k] = new Quaternion(f[o + 3], f[o + 4], f[o + 5], f[o + 6]);
                    r.BoneLocalScales[k] = new Vector3(f[o + 7], f[o + 8], f[o + 9]);
                }
            }
            else
            {
                r.RootRotations[i] = Quaternion.identity;
                r.RootScales[i] = 1f;
            }
        }
        return r;
    }

    /// <summary>
    /// Humanoid RootT/RootQ for one frame: the body pose (relative to the avatar root, RootT in
    /// human scale units) carried through the root's world motion since the first frame.
    /// </summary>
    public static void ComposeRoot(Vector3 rootPos0, Quaternion rootRot0, float rootScale0, float humanScale, Vector3 rootPos, Quaternion rootRot, float rootScale, Vector3 bodyPos, Quaternion bodyRot, out Vector3 rootT, out Quaternion rootQ)
    {
        Quaternion inv0 = Quaternion.Inverse(SafeRotation(rootRot0));
        float s0 = SafeScale(rootScale0);
        float h = SafeScale(humanScale);
        Quaternion rel = inv0 * SafeRotation(rootRot);
        rootT = inv0 * (rootPos - rootPos0) / (h * s0) + rel * bodyPos * (SafeScale(rootScale) / s0);
        rootQ = rel * bodyRot;
    }

    public void ComputeRootCurves(out Vector3[] rootT, out Quaternion[] rootQ)
    {
        rootT = new Vector3[FrameCount];
        rootQ = new Quaternion[FrameCount];
        if (!HasRootMotion || FrameCount == 0)
        {
            Array.Copy(BodyPositions, rootT, FrameCount);
            Array.Copy(BodyRotations, rootQ, FrameCount);
            return;
        }
        for (int i = 0; i < FrameCount; i++)
            ComposeRoot(RootPositions[0], RootRotations[0], RootScales[0], HumanScale, RootPositions[i], RootRotations[i], RootScales[i], BodyPositions[i], BodyRotations[i], out rootT[i], out rootQ[i]);
    }

    private static float SafeScale(float s) => float.IsFinite(s) && Mathf.Abs(s) > 1e-6f ? s : 1f;

    private static Quaternion SafeRotation(Quaternion q)
    {
        float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (!(m > 1e-6f) || !float.IsFinite(m)) return Quaternion.identity;
        return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
    }
}
