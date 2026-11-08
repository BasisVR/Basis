using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Basis.BasisUI;
using UnityEngine;

public enum BasisInspectorCategory : byte { None, GameObject, Transform, Camera, Light, Mesh, Animation, Audio, Physics, UI, Particles, Network, Avatar, Script, Missing, Scene, Material, Texture, Asset }

public enum BasisInspectorTone : byte { Plain, True, False, Number, Text, Reference, None, Missing, Hidden, Unreadable, Enum, Vector, Color, Enabled, Disabled, Collection }

[AutoStaticsCleanup]
public static partial class BasisInspectorStyle
{
    public const int CategoryCount = 19, ToneCount = 16;
    [NoAutoStaticsCleanup] static readonly string[] iconAddresses =
    {
        null,
        "Packages/com.basis.sdk/Textures/Runtime/items.png",
        "Packages/com.basis.sdk/Textures/Runtime/move-outline.png",
        "Packages/com.basis.sdk/Textures/Runtime/camera-outline.png",
        "Packages/com.basis.sdk/Textures/Runtime/eye-outline.png",
        "Packages/com.basis.sdk/Textures/Runtime/polygons.png",
        "Packages/com.basis.sdk/Textures/Runtime/bones.png",
        null,
        "Packages/com.basis.sdk/Textures/Runtime/scan-outline.png",
        "Packages/com.basis.sdk/Textures/Runtime/sliders.png",
        "Packages/com.basis.sdk/Textures/Runtime/materials.png",
        "Packages/com.basis.sdk/Textures/Runtime/network.png",
        "Packages/com.basis.sdk/Textures/Runtime/avatarWhite.png",
        "Packages/com.basis.sdk/Textures/Runtime/file-tray.png",
        "Packages/com.basis.sdk/Textures/Runtime/xWhite.png",
        "Packages/com.basis.sdk/Textures/Runtime/worlds.png",
        "Packages/com.basis.sdk/Textures/Runtime/materials.png",
        "Packages/com.basis.sdk/Textures/Runtime/embedded.png",
        "Packages/com.basis.sdk/Textures/Runtime/information.png",
    };
    [NoAutoStaticsCleanup] static readonly Color[] tints =
    {
        new Color(0.75f, 0.75f, 0.8f, 1f),
        new Color(0.78f, 0.78f, 0.86f, 1f),
        new Color(0.5f, 0.7f, 1f, 1f),
        new Color(0.35f, 0.78f, 0.98f, 1f),
        new Color(1f, 0.82f, 0.36f, 1f),
        new Color(0.31f, 0.82f, 0.77f, 1f),
        new Color(0.78f, 0.57f, 0.92f, 1f),
        new Color(1f, 0.72f, 0.42f, 1f),
        new Color(0.55f, 0.89f, 0.55f, 1f),
        new Color(0.96f, 0.56f, 0.69f, 1f),
        new Color(1f, 0.54f, 0.4f, 1f),
        new Color(0.39f, 0.71f, 0.96f, 1f),
        new Color(0.73f, 0.41f, 0.78f, 1f),
        new Color(0.69f, 0.75f, 0.77f, 1f),
        new Color(1f, 0.36f, 0.36f, 1f),
        new Color(0.6f, 0.8f, 0.6f, 1f),
        new Color(0.95f, 0.6f, 0.85f, 1f),
        new Color(0.55f, 0.85f, 1f, 1f),
        new Color(0.8f, 0.8f, 0.6f, 1f),
    };
    [NoAutoStaticsCleanup] static readonly Color[] toneColors =
    {
        default,
        new Color(0.49f, 0.85f, 0.57f, 1f),
        new Color(1f, 0.54f, 0.5f, 1f),
        new Color(0.61f, 0.86f, 1f, 1f),
        new Color(0.81f, 0.57f, 0.47f, 1f),
        new Color(0.42f, 0.71f, 1f, 1f),
        new Color(0.54f, 0.54f, 0.6f, 1f),
        new Color(1f, 0.42f, 0.42f, 1f),
        new Color(0.54f, 0.54f, 0.6f, 1f),
        new Color(1f, 0.42f, 0.42f, 1f),
        new Color(0.84f, 0.73f, 0.49f, 1f),
        new Color(0.71f, 0.81f, 0.66f, 1f),
        new Color(0.9f, 0.9f, 0.9f, 1f),
        new Color(0.49f, 0.85f, 0.57f, 1f),
        new Color(0.62f, 0.62f, 0.68f, 1f),
        new Color(0.78f, 0.78f, 0.86f, 1f),
    };
    static Sprite[] icons;
    static bool[] iconTried;
    static readonly Dictionary<Type, BasisInspectorCategory> categories = new Dictionary<Type, BasisInspectorCategory>();
    public static Color Tint(BasisInspectorCategory category) => tints[(int)category < CategoryCount ? (int)category : 0];
    public static Color ToneColor(BasisInspectorTone tone, Color plain) => tone == BasisInspectorTone.Plain || (int)tone >= ToneCount ? plain : toneColors[(int)tone];
    public static Sprite Icon(BasisInspectorCategory category)
    {
        int index = (int)category;
        if (index <= 0 || index >= CategoryCount) return null;
        icons ??= new Sprite[CategoryCount];
        iconTried ??= new bool[CategoryCount];
        if (iconTried[index]) return icons[index];
        iconTried[index] = true;
        icons[index] = category == BasisInspectorCategory.Audio ? BasisWorldPanelKit.SpeakerIcon : BasisWorldPanelKit.AddressableIcon(iconAddresses[index]);
        return icons[index];
    }
    public static BasisInspectorCategory Classify(Type type)
    {
        if (type == null) return BasisInspectorCategory.Missing;
        if (categories.TryGetValue(type, out BasisInspectorCategory category)) return category;
        category = ClassifyUncached(type);
        categories[type] = category;
        return category;
    }
    static BasisInspectorCategory ClassifyUncached(Type type)
    {
        if (type == typeof(GameObject)) return BasisInspectorCategory.GameObject;
        if (typeof(Material).IsAssignableFrom(type)) return BasisInspectorCategory.Material;
        if (typeof(Texture).IsAssignableFrom(type)) return BasisInspectorCategory.Texture;
        if (typeof(Mesh).IsAssignableFrom(type)) return BasisInspectorCategory.Mesh;
        if (typeof(ScriptableObject).IsAssignableFrom(type)) return BasisInspectorCategory.Script;
        if (!typeof(Component).IsAssignableFrom(type) && typeof(UnityEngine.Object).IsAssignableFrom(type)) return BasisInspectorCategory.Asset;
        if (typeof(Transform).IsAssignableFrom(type)) return BasisInspectorCategory.Transform;
        if (typeof(Camera).IsAssignableFrom(type)) return BasisInspectorCategory.Camera;
        if (typeof(Light).IsAssignableFrom(type) || typeof(LightProbeGroup).IsAssignableFrom(type) || typeof(ReflectionProbe).IsAssignableFrom(type)) return BasisInspectorCategory.Light;
        if (typeof(ParticleSystem).IsAssignableFrom(type) || typeof(ParticleSystemRenderer).IsAssignableFrom(type) || typeof(TrailRenderer).IsAssignableFrom(type) || typeof(LineRenderer).IsAssignableFrom(type)) return BasisInspectorCategory.Particles;
        if (typeof(Animator).IsAssignableFrom(type) || typeof(Animation).IsAssignableFrom(type) || typeof(SkinnedMeshRenderer).IsAssignableFrom(type)) return BasisInspectorCategory.Animation;
        if (typeof(Renderer).IsAssignableFrom(type) || typeof(MeshFilter).IsAssignableFrom(type) || typeof(LODGroup).IsAssignableFrom(type)) return BasisInspectorCategory.Mesh;
        if (typeof(AudioSource).IsAssignableFrom(type) || typeof(AudioListener).IsAssignableFrom(type) || typeof(AudioReverbZone).IsAssignableFrom(type) || typeof(AudioBehaviour).IsAssignableFrom(type)) return BasisInspectorCategory.Audio;
        if (typeof(Collider).IsAssignableFrom(type) || typeof(Rigidbody).IsAssignableFrom(type) || typeof(Joint).IsAssignableFrom(type) || typeof(Collider2D).IsAssignableFrom(type) || typeof(Rigidbody2D).IsAssignableFrom(type)) return BasisInspectorCategory.Physics;
        if (typeof(Canvas).IsAssignableFrom(type) || typeof(CanvasGroup).IsAssignableFrom(type) || typeof(CanvasRenderer).IsAssignableFrom(type) || InNamespace(type, "UnityEngine.UI") || InNamespace(type, "UnityEngine.EventSystems") || InNamespace(type, "TMPro")) return BasisInspectorCategory.UI;
        string name = type.FullName ?? type.Name;
        if (name.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0) return BasisInspectorCategory.Network;
        if (name.IndexOf("Avatar", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0) return BasisInspectorCategory.Avatar;
        if (typeof(MonoBehaviour).IsAssignableFrom(type)) return BasisInspectorCategory.Script;
        return BasisInspectorCategory.None;
    }
    static bool InNamespace(Type type, string root)
    {
        string space = type.Namespace;
        return space != null && space.StartsWith(root, StringComparison.Ordinal);
    }
    public static BasisInspectorCategory GameObjectCategory(GameObject go, List<Component> scratch)
    {
        if (go == null) return BasisInspectorCategory.None;
        scratch.Clear();
        go.GetComponents(scratch);
        BasisInspectorCategory best = BasisInspectorCategory.GameObject;
        int bestRank = 0, count = Mathf.Min(scratch.Count, 12);
        for (int i = 0; i < count; i++)
        {
            Component component = scratch[i];
            if (component == null) continue;
            BasisInspectorCategory category = Classify(component.GetType());
            int rank = Rank(category);
            if (rank > bestRank)
            {
                bestRank = rank;
                best = category;
            }
        }
        scratch.Clear();
        return best;
    }
    static int Rank(BasisInspectorCategory category)
    {
        switch (category)
        {
            case BasisInspectorCategory.Camera: return 12;
            case BasisInspectorCategory.Light: return 11;
            case BasisInspectorCategory.Avatar: return 10;
            case BasisInspectorCategory.Audio: return 9;
            case BasisInspectorCategory.Animation: return 8;
            case BasisInspectorCategory.Particles: return 7;
            case BasisInspectorCategory.UI: return 6;
            case BasisInspectorCategory.Mesh: return 5;
            case BasisInspectorCategory.Network: return 4;
            case BasisInspectorCategory.Physics: return 3;
            case BasisInspectorCategory.Script: return 2;
        }
        return 0;
    }
    public static BasisInspectorTone ToneOf(object value)
    {
        switch (value)
        {
            case null: return BasisInspectorTone.None;
            case UnityEngine.Object unityObject: return unityObject == null ? BasisInspectorTone.Missing : BasisInspectorTone.Reference;
            case string _: return BasisInspectorTone.Text;
            case char _: return BasisInspectorTone.Text;
            case bool flag: return flag ? BasisInspectorTone.True : BasisInspectorTone.False;
            case Enum _: return BasisInspectorTone.Enum;
            case Color _: return BasisInspectorTone.Color;
            case Color32 _: return BasisInspectorTone.Color;
            case Vector2 _: case Vector3 _: case Vector4 _: case Vector2Int _: case Vector3Int _: case Quaternion _: case Rect _: case Bounds _: return BasisInspectorTone.Vector;
            case Array _: return BasisInspectorTone.Collection;
            case System.Collections.ICollection _: return BasisInspectorTone.Collection;
        }
        return value.GetType().IsPrimitive ? BasisInspectorTone.Number : BasisInspectorTone.Plain;
    }
    public static bool IsLocalizedTone(BasisInspectorTone tone) => tone == BasisInspectorTone.True || tone == BasisInspectorTone.False || tone == BasisInspectorTone.None || tone == BasisInspectorTone.Missing || tone == BasisInspectorTone.Hidden || tone == BasisInspectorTone.Unreadable || tone == BasisInspectorTone.Enabled || tone == BasisInspectorTone.Disabled;
    public static string LocalizedText(BasisInspectorTone tone)
    {
        switch (tone)
        {
            case BasisInspectorTone.True: return BasisInspectorReflection.TextTrue;
            case BasisInspectorTone.False: return BasisInspectorReflection.TextFalse;
            case BasisInspectorTone.None: return BasisInspectorReflection.TextNone;
            case BasisInspectorTone.Missing: return BasisInspectorReflection.TextMissing;
            case BasisInspectorTone.Hidden: return BasisInspectorReflection.TextHidden;
            case BasisInspectorTone.Unreadable: return BasisInspectorReflection.TextUnreadable;
            case BasisInspectorTone.Enabled: return BasisInspectorReflection.TextEnabled;
            case BasisInspectorTone.Disabled: return BasisInspectorReflection.TextDisabled;
        }
        return null;
    }
    public static BasisInspectorTone StateTone(Component component)
    {
        switch (component)
        {
            case Behaviour behaviour: return behaviour.enabled ? BasisInspectorTone.Enabled : BasisInspectorTone.Disabled;
            case Renderer renderer: return renderer.enabled ? BasisInspectorTone.Enabled : BasisInspectorTone.Disabled;
            case Collider collider: return collider.enabled ? BasisInspectorTone.Enabled : BasisInspectorTone.Disabled;
        }
        return BasisInspectorTone.Plain;
    }
}
