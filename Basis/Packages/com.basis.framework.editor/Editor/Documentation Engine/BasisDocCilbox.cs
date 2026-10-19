// Editor/Documentation Engine/BasisDocCilbox.cs
// The API reference's opening into the Cilbox sandbox.
//
// The doc engine knows nothing about Cilbox and must keep working when com.basis.shim is not in
// the project, so the answer is supplied from outside: the shim package registers a provider at
// editor load and the reference asks it per member. Nothing here decides what is allowed — the
// provider calls the sandboxes' own check methods, so the reference cannot drift from the rules.

using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>What one sandbox says about one member.</summary>
public sealed class BasisDocCilboxBoxVerdict
{
    /// <summary>Display name of the sandbox: Avatar, Prop or Scene.</summary>
    public string BoxName;

    public bool Allowed;

    /// <summary>Why, in the sandbox's own words — already localized.</summary>
    public string Reason;
}

/// <summary>Everything the reference shows about calling one member from a cilboxed script.</summary>
public sealed class BasisDocCilboxAdvice
{
    public List<BasisDocCilboxBoxVerdict> Boxes = new();

    /// <summary>Set when the sandbox serves a different type than the one written in source.</summary>
    public string SwapNote;

    /// <summary>Caveats that apply to this member specifically, not to cilbox in general.</summary>
    public List<string> Notes = new();

    /// <summary>Title of the matching entry in the Cilbox API reference, when there is one.</summary>
    public string RelatedTitle;

    /// <summary>That entry's hand-written example, which is usually richer than a generated one.</summary>
    public string RelatedExample;

    /// <summary>Opens the Cilbox Permissions window on this type. Null when the window is unavailable.</summary>
    public Action Reveal;

    public bool AnyAllowed
    {
        get
        {
            for (int i = 0; i < Boxes.Count; i++)
            {
                if (Boxes[i].Allowed) return true;
            }
            return false;
        }
    }
}

/// <summary>
/// The registration point. <see cref="Provider"/> is set by com.basis.shim; when it is null the
/// reference simply shows no Cilbox section.
/// </summary>
[NoAutoStaticsCleanup]
public static class BasisDocCilbox
{
    /// <summary>Answers for one member, or null when the member is outside the sandbox's world.</summary>
    public static Func<MemberInfo, BasisDocCilboxAdvice> Provider;

    public static bool Available => Provider != null;

    /// <summary>Asks the registered provider, swallowing anything it throws — this is documentation.</summary>
    public static BasisDocCilboxAdvice Describe(MemberInfo member)
    {
        if (Provider == null || member == null) return null;
        try
        {
            return Provider(member);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BasisDocCilbox] provider failed for {member.DeclaringType?.FullName}.{member.Name}: {e.Message}");
            return null;
        }
    }
}
