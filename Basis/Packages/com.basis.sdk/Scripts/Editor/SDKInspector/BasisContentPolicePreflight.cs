using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Shows what Content Police will do to this content before anyone builds or uploads it.
///
/// Enforcement is runtime-only: nothing in the build pipeline consults the allow-list, so without
/// this an author finds out a component was stripped by loading the finished upload and noticing
/// something missing. The same walk runs here against the same selector, in the editor, for free.
///
/// Two outcomes are reported separately on purpose. A constraint is not "disallowed" — it is
/// rewritten onto the Basis solver and keeps working, which is worth saying plainly so nobody
/// removes it by hand thinking it is a problem.
/// </summary>
public static class BasisContentPolicePreflight
{
    public struct Finding
    {
        public string TypeName;
        public string ObjectPath;
        public bool Converted;
    }

    /// <summary>
    /// Walks the content and reports every component the load-time pass will remove or rewrite.
    /// Returns an empty list when the selector cannot be resolved, rather than guessing at a policy.
    /// </summary>
    public static List<Finding> Inspect(GameObject root, BundledContentHolder.Selector selector)
    {
        List<Finding> findings = new List<Finding>();
        if (root == null)
        {
            return findings;
        }

        ContentPoliceSelector police = ResolveSelector(selector);
        if (police == null)
        {
            return findings;
        }

        Component[] components = root.GetComponentsInChildren<Component>(true);
        for (int Index = 0; Index < components.Length; Index++)
        {
            Component component = components[Index];
            if (component == null)
            {
                // A missing script reads as null here, and it will be stripped on load too.
                continue;
            }

            Type componentType = component.GetType();
            string typeName = componentType.FullName;
            if (typeName == null)
            {
                continue;
            }

            if (BasisCilboxTypeCheck.IsCilboxable(componentType))
            {
                continue;
            }

            bool converted = BasisConstraintConversionNames.IsConverted(typeName);
            if (!converted && police.ApprovedTypeNames.Contains(typeName))
            {
                continue;
            }

            findings.Add(new Finding
            {
                TypeName = typeName,
                ObjectPath = PathOf(component.transform, root.transform),
                Converted = converted,
            });
        }
        return findings;
    }

    /// <summary>
    /// Builds the inspector section. Returns a collapsed, quiet element when there is nothing to
    /// say, so clean content does not grow a permanent warning box.
    /// </summary>
    public static VisualElement CreateSection(GameObject root, BundledContentHolder.Selector selector)
    {
        VisualElement container = new VisualElement();
        container.style.marginTop = 6;

        List<Finding> findings = Inspect(root, selector);
        if (findings.Count == 0)
        {
            return container;
        }

        Finding[] removed = findings.Where(f => !f.Converted).ToArray();
        Finding[] converted = findings.Where(f => f.Converted).ToArray();

        if (removed.Length > 0)
        {
            container.Add(Box(
                $"{removed.Length} component(s) will be REMOVED when this loads",
                "These are not on the approved list for this content type. They are stripped before " +
                "anything runs, so whatever depends on them will not work once uploaded.",
                removed,
                HelpBoxMessageType.Error));
        }

        if (converted.Length > 0)
        {
            container.Add(Box(
                $"{converted.Length} component(s) will be CONVERTED when this loads",
                "Unity and Animation Rigging constraints are rewritten onto the Basis solver, and the " +
                "rig that drove them is removed. They keep working, so no action is needed — but the " +
                "originals will not be there afterwards, so anything referencing them by type needs " +
                "to expect the Basis equivalent instead.",
                converted,
                HelpBoxMessageType.Warning));
        }

        return container;
    }

    static VisualElement Box(
        string title, string body, IReadOnlyList<Finding> findings, HelpBoxMessageType severity)
    {
        VisualElement group = new VisualElement();
        group.style.marginTop = 4;
        group.Add(new HelpBox($"{title}\n\n{body}", severity));

        Foldout foldout = new Foldout { text = "Show affected objects", value = false };
        foldout.style.marginLeft = 4;
        for (int Index = 0; Index < findings.Count; Index++)
        {
            Finding finding = findings[Index];
            foldout.Add(new Label($"{finding.ObjectPath}  —  {finding.TypeName}")
            {
                style = { whiteSpace = WhiteSpace.Normal, opacity = 0.9f },
            });
        }
        group.Add(foldout);
        return group;
    }

    static ContentPoliceSelector ResolveSelector(BundledContentHolder.Selector selector)
    {
        BundledContentHolder holder = UnityEngine.Object.FindFirstObjectByType<BundledContentHolder>(
            FindObjectsInactive.Include);
        if (holder == null || !holder.GetSelector(selector, out ContentPoliceSelector police))
        {
            return null;
        }
        return police;
    }

    static string PathOf(Transform target, Transform root)
    {
        if (target == root)
        {
            return target.name;
        }
        string path = target.name;
        Transform cursor = target.parent;
        while (cursor != null && cursor != root)
        {
            path = cursor.name + "/" + path;
            cursor = cursor.parent;
        }
        return root.name + "/" + path;
    }
}

/// <summary>
/// The type names the load-time pass rewrites rather than removes. Kept as strings here so the
/// editor check needs no reference to the Animation Rigging package to know what it is looking at.
/// </summary>
public static class BasisConstraintConversionNames
{
    static readonly HashSet<string> Converted = new HashSet<string>
    {
        "UnityEngine.Animations.ParentConstraint",
        "UnityEngine.Animations.PositionConstraint",
        "UnityEngine.Animations.RotationConstraint",
        "UnityEngine.Animations.ScaleConstraint",
        "UnityEngine.Animations.AimConstraint",
        "UnityEngine.Animations.LookAtConstraint",
        "UnityEngine.Animations.Rigging.MultiParentConstraint",
        "UnityEngine.Animations.Rigging.MultiPositionConstraint",
        "UnityEngine.Animations.Rigging.MultiRotationConstraint",
        "UnityEngine.Animations.Rigging.MultiAimConstraint",
        "UnityEngine.Animations.Rigging.MultiReferentialConstraint",
        "UnityEngine.Animations.Rigging.TwoBoneIKConstraint",
        "UnityEngine.Animations.Rigging.ChainIKConstraint",
        "UnityEngine.Animations.Rigging.DampedTransform",
        "UnityEngine.Animations.Rigging.TwistCorrection",
        "UnityEngine.Animations.Rigging.TwistChainConstraint",
        "UnityEngine.Animations.Rigging.BlendConstraint",
        "UnityEngine.Animations.Rigging.OverrideTransform",
        "UnityEngine.Animations.Rigging.Rig",
        "UnityEngine.Animations.Rigging.RigBuilder",
        "UnityEngine.Animations.Rigging.RigTransform",
        "UnityEngine.Animations.Rigging.BoneRenderer",
    };

    public static bool IsConverted(string fullTypeName) => Converted.Contains(fullTypeName);
}

/// <summary>
/// Types marked with cilbox's [Cilboxable] are replaced by a CilboxProxy before the content is
/// written, and that proxy is on the approved list, so the authored script never reaches the
/// load-time pass and must not be reported against it. Resolved by name for the same reason the
/// constraint list is: this assembly does not reference the cilbox package, which is optional.
/// </summary>
public static class BasisCilboxTypeCheck
{
    static readonly Dictionary<Type, bool> Cache = new Dictionary<Type, bool>();
    static Type AttributeType;
    static bool AttributeResolved;

    public static bool IsCilboxable(Type type)
    {
        if (type == null)
        {
            return false;
        }
        if (Cache.TryGetValue(type, out bool cached))
        {
            return cached;
        }

        bool cilboxable = false;
        Type attribute = ResolveAttribute();
        if (attribute != null)
        {
            Type cursor = type;
            while (cursor != null)
            {
                if (cursor.GetCustomAttributes(attribute, true).Length > 0)
                {
                    cilboxable = true;
                    break;
                }
                cursor = cursor.DeclaringType;
            }
        }

        Cache[type] = cilboxable;
        return cilboxable;
    }

    static Type ResolveAttribute()
    {
        if (AttributeResolved)
        {
            return AttributeType;
        }
        AttributeResolved = true;

        AttributeType = Type.GetType("CilboxableAttribute, Cilbox");
        if (AttributeType == null)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int Index = 0; Index < assemblies.Length; Index++)
            {
                AttributeType = assemblies[Index].GetType("CilboxableAttribute", false);
                if (AttributeType != null)
                {
                    break;
                }
            }
        }
        return AttributeType;
    }
}
