/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */



using System;
using System.Collections.Generic;
using static OVRPlugin;

public readonly partial struct OVRLocatable : IOVRAnchorComponent<OVRLocatable>, IEquatable<OVRLocatable>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRLocatable>.Type => Type;

    ulong IOVRAnchorComponent<OVRLocatable>.Handle => Handle;

    OVRLocatable IOVRAnchorComponent<OVRLocatable>.FromAnchor(OVRAnchor anchor) => new OVRLocatable(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRLocatable.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRLocatable Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// <summary>
    /// Sets the enabled status of this component.
    /// </summary>
    /// <remarks>
    /// A component must be enabled in order to access its data or do enable its functionality.
    ///
    /// This method is asynchronous. Use the returned task to track the completion of this operation. The task's value
    /// indicates whether the operation was successful.
    ///
    /// If the current enabled state matches <paramref name="enabled"/>, then the returned task completes immediately
    /// with a `true` result. If there is already a pending change to the enabled state, the new request is queued.
    /// </remarks>
    /// <param name="enabled">The desired state of the component.</param>
    /// <param name="timeout">The timeout, in seconds, for the operation. Use zero to indicate an infinite timeout.</param>
    /// <returns>Returns an <see cref="OVRTask"/>&lt;bool&gt; whose result indicates the result of the operation.</returns>
    public OVRTask<bool> SetEnabledAsync(bool enabled, double timeout = 0)
    {
        if (!GetSpaceComponentStatus(Handle, Type, out var isEnabled, out var changePending))
        {
            return OVRTask.FromResult(false);
        }

        if (changePending)
        {
            return OVRAnchor.CreateDeferredSpaceComponentStatusTask(Handle, Type, enabled, timeout);
        }

        return isEnabled == enabled
            ? OVRTask.FromResult(true)
            : OVRTask
                .Build(SetSpaceComponentStatus(Handle, Type, enabled, timeout, out var requestId), requestId)
                .ToTask(failureValue: false);
    }

    /// <summary>
    /// (Obsolete) Sets the enabled status of this component if it differs from the current enabled state.
    /// </summary>
    /// <remarks>
    /// This method is obsolete. Use <see cref="SetEnabledAsync"/> instead.
    /// </remarks>
    /// <param name="enabled">The desired state of the component.</param>
    /// <param name="timeout">The timeout, in seconds, for the operation. Use zero to indicate an infinite timeout.</param>
    /// <returns>Returns an <see cref="OVRTask"/>&lt;bool&gt; whose result indicates the result of the operation.</returns>
    [Obsolete("Use SetEnabledAsync instead.")]
    public OVRTask<bool> SetEnabledSafeAsync(bool enabled, double timeout = 0) => SetEnabledAsync(enabled, timeout);

    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRLocatable other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRLocatable lhs, OVRLocatable rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRLocatable lhs, OVRLocatable rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRLocatable and <see cref="Equals(OVRLocatable)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRLocatable other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.Locatable";

    internal SpaceComponentType Type => SpaceComponentType.Locatable;

    internal ulong Handle { get; }

    private OVRLocatable(OVRAnchor anchor) => Handle = anchor.Handle;
}

public readonly partial struct OVRStorable : IOVRAnchorComponent<OVRStorable>, IEquatable<OVRStorable>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRStorable>.Type => Type;

    ulong IOVRAnchorComponent<OVRStorable>.Handle => Handle;

    OVRStorable IOVRAnchorComponent<OVRStorable>.FromAnchor(OVRAnchor anchor) => new OVRStorable(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRStorable.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRStorable Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// <summary>
    /// Sets the enabled status of this component.
    /// </summary>
    /// <remarks>
    /// A component must be enabled in order to access its data or do enable its functionality.
    ///
    /// This method is asynchronous. Use the returned task to track the completion of this operation. The task's value
    /// indicates whether the operation was successful.
    ///
    /// If the current enabled state matches <paramref name="enabled"/>, then the returned task completes immediately
    /// with a `true` result. If there is already a pending change to the enabled state, the new request is queued.
    /// </remarks>
    /// <param name="enabled">The desired state of the component.</param>
    /// <param name="timeout">The timeout, in seconds, for the operation. Use zero to indicate an infinite timeout.</param>
    /// <returns>Returns an <see cref="OVRTask"/>&lt;bool&gt; whose result indicates the result of the operation.</returns>
    public OVRTask<bool> SetEnabledAsync(bool enabled, double timeout = 0)
    {
        if (!GetSpaceComponentStatus(Handle, Type, out var isEnabled, out var changePending))
        {
            return OVRTask.FromResult(false);
        }

        if (changePending)
        {
            return OVRAnchor.CreateDeferredSpaceComponentStatusTask(Handle, Type, enabled, timeout);
        }

        return isEnabled == enabled
            ? OVRTask.FromResult(true)
            : OVRTask
                .Build(SetSpaceComponentStatus(Handle, Type, enabled, timeout, out var requestId), requestId)
                .ToTask(failureValue: false);
    }

    /// <summary>
    /// (Obsolete) Sets the enabled status of this component if it differs from the current enabled state.
    /// </summary>
    /// <remarks>
    /// This method is obsolete. Use <see cref="SetEnabledAsync"/> instead.
    /// </remarks>
    /// <param name="enabled">The desired state of the component.</param>
    /// <param name="timeout">The timeout, in seconds, for the operation. Use zero to indicate an infinite timeout.</param>
    /// <returns>Returns an <see cref="OVRTask"/>&lt;bool&gt; whose result indicates the result of the operation.</returns>
    [Obsolete("Use SetEnabledAsync instead.")]
    public OVRTask<bool> SetEnabledSafeAsync(bool enabled, double timeout = 0) => SetEnabledAsync(enabled, timeout);

    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRStorable other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRStorable lhs, OVRStorable rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRStorable lhs, OVRStorable rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRStorable and <see cref="Equals(OVRStorable)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRStorable other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.Storable";

    internal SpaceComponentType Type => SpaceComponentType.Storable;

    internal ulong Handle { get; }

    private OVRStorable(OVRAnchor anchor) => Handle = anchor.Handle;
}

public readonly partial struct OVRSharable : IOVRAnchorComponent<OVRSharable>, IEquatable<OVRSharable>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRSharable>.Type => Type;

    ulong IOVRAnchorComponent<OVRSharable>.Handle => Handle;

    OVRSharable IOVRAnchorComponent<OVRSharable>.FromAnchor(OVRAnchor anchor) => new OVRSharable(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRSharable.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRSharable Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// <summary>
    /// Sets the enabled status of this component.
    /// </summary>
    /// <remarks>
    /// A component must be enabled in order to access its data or do enable its functionality.
    ///
    /// This method is asynchronous. Use the returned task to track the completion of this operation. The task's value
    /// indicates whether the operation was successful.
    ///
    /// If the current enabled state matches <paramref name="enabled"/>, then the returned task completes immediately
    /// with a `true` result. If there is already a pending change to the enabled state, the new request is queued.
    /// </remarks>
    /// <param name="enabled">The desired state of the component.</param>
    /// <param name="timeout">The timeout, in seconds, for the operation. Use zero to indicate an infinite timeout.</param>
    /// <returns>Returns an <see cref="OVRTask"/>&lt;bool&gt; whose result indicates the result of the operation.</returns>
    public OVRTask<bool> SetEnabledAsync(bool enabled, double timeout = 0)
    {
        if (!GetSpaceComponentStatus(Handle, Type, out var isEnabled, out var changePending))
        {
            return OVRTask.FromResult(false);
        }

        if (changePending)
        {
            return OVRAnchor.CreateDeferredSpaceComponentStatusTask(Handle, Type, enabled, timeout);
        }

        return isEnabled == enabled
            ? OVRTask.FromResult(true)
            : OVRTask
                .Build(SetSpaceComponentStatus(Handle, Type, enabled, timeout, out var requestId), requestId)
                .ToTask(failureValue: false);
    }

    /// <summary>
    /// (Obsolete) Sets the enabled status of this component if it differs from the current enabled state.
    /// </summary>
    /// <remarks>
    /// This method is obsolete. Use <see cref="SetEnabledAsync"/> instead.
    /// </remarks>
    /// <param name="enabled">The desired state of the component.</param>
    /// <param name="timeout">The timeout, in seconds, for the operation. Use zero to indicate an infinite timeout.</param>
    /// <returns>Returns an <see cref="OVRTask"/>&lt;bool&gt; whose result indicates the result of the operation.</returns>
    [Obsolete("Use SetEnabledAsync instead.")]
    public OVRTask<bool> SetEnabledSafeAsync(bool enabled, double timeout = 0) => SetEnabledAsync(enabled, timeout);

    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRSharable other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRSharable lhs, OVRSharable rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRSharable lhs, OVRSharable rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRSharable and <see cref="Equals(OVRSharable)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRSharable other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.Sharable";

    internal SpaceComponentType Type => SpaceComponentType.Sharable;

    internal ulong Handle { get; }

    private OVRSharable(OVRAnchor anchor) => Handle = anchor.Handle;
}

public readonly partial struct OVRBounded2D : IOVRAnchorComponent<OVRBounded2D>, IEquatable<OVRBounded2D>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRBounded2D>.Type => Type;

    ulong IOVRAnchorComponent<OVRBounded2D>.Handle => Handle;

    OVRBounded2D IOVRAnchorComponent<OVRBounded2D>.FromAnchor(OVRAnchor anchor) => new OVRBounded2D(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRBounded2D.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRBounded2D Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// @cond
    OVRTask<bool> IOVRAnchorComponent<OVRBounded2D>.SetEnabledAsync(bool enabled, double timeout)
        => throw new NotSupportedException("The Bounded2D component cannot be enabled or disabled.");
    /// @endcond


    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRBounded2D other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRBounded2D lhs, OVRBounded2D rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRBounded2D lhs, OVRBounded2D rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRBounded2D and <see cref="Equals(OVRBounded2D)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRBounded2D other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.Bounded2D";

    internal SpaceComponentType Type => SpaceComponentType.Bounded2D;

    internal ulong Handle { get; }

    private OVRBounded2D(OVRAnchor anchor) => Handle = anchor.Handle;
}

public readonly partial struct OVRBounded3D : IOVRAnchorComponent<OVRBounded3D>, IEquatable<OVRBounded3D>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRBounded3D>.Type => Type;

    ulong IOVRAnchorComponent<OVRBounded3D>.Handle => Handle;

    OVRBounded3D IOVRAnchorComponent<OVRBounded3D>.FromAnchor(OVRAnchor anchor) => new OVRBounded3D(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRBounded3D.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRBounded3D Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// @cond
    OVRTask<bool> IOVRAnchorComponent<OVRBounded3D>.SetEnabledAsync(bool enabled, double timeout)
        => throw new NotSupportedException("The Bounded3D component cannot be enabled or disabled.");
    /// @endcond


    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRBounded3D other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRBounded3D lhs, OVRBounded3D rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRBounded3D lhs, OVRBounded3D rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRBounded3D and <see cref="Equals(OVRBounded3D)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRBounded3D other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.Bounded3D";

    internal SpaceComponentType Type => SpaceComponentType.Bounded3D;

    internal ulong Handle { get; }

    private OVRBounded3D(OVRAnchor anchor) => Handle = anchor.Handle;
}

public readonly partial struct OVRSemanticLabels : IOVRAnchorComponent<OVRSemanticLabels>, IEquatable<OVRSemanticLabels>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRSemanticLabels>.Type => Type;

    ulong IOVRAnchorComponent<OVRSemanticLabels>.Handle => Handle;

    OVRSemanticLabels IOVRAnchorComponent<OVRSemanticLabels>.FromAnchor(OVRAnchor anchor) => new OVRSemanticLabels(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRSemanticLabels.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRSemanticLabels Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// @cond
    OVRTask<bool> IOVRAnchorComponent<OVRSemanticLabels>.SetEnabledAsync(bool enabled, double timeout)
        => throw new NotSupportedException("The SemanticLabels component cannot be enabled or disabled.");
    /// @endcond


    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRSemanticLabels other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRSemanticLabels lhs, OVRSemanticLabels rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRSemanticLabels lhs, OVRSemanticLabels rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRSemanticLabels and <see cref="Equals(OVRSemanticLabels)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRSemanticLabels other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.SemanticLabels";

    internal SpaceComponentType Type => SpaceComponentType.SemanticLabels;

    internal ulong Handle { get; }

    private OVRSemanticLabels(OVRAnchor anchor) => Handle = anchor.Handle;
}

public readonly partial struct OVRRoomLayout : IOVRAnchorComponent<OVRRoomLayout>, IEquatable<OVRRoomLayout>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRRoomLayout>.Type => Type;

    ulong IOVRAnchorComponent<OVRRoomLayout>.Handle => Handle;

    OVRRoomLayout IOVRAnchorComponent<OVRRoomLayout>.FromAnchor(OVRAnchor anchor) => new OVRRoomLayout(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRRoomLayout.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRRoomLayout Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// @cond
    OVRTask<bool> IOVRAnchorComponent<OVRRoomLayout>.SetEnabledAsync(bool enabled, double timeout)
        => throw new NotSupportedException("The RoomLayout component cannot be enabled or disabled.");
    /// @endcond


    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRRoomLayout other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRRoomLayout lhs, OVRRoomLayout rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRRoomLayout lhs, OVRRoomLayout rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRRoomLayout and <see cref="Equals(OVRRoomLayout)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRRoomLayout other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.RoomLayout";

    internal SpaceComponentType Type => SpaceComponentType.RoomLayout;

    internal ulong Handle { get; }

    private OVRRoomLayout(OVRAnchor anchor) => Handle = anchor.Handle;
}

public readonly partial struct OVRAnchorContainer : IOVRAnchorComponent<OVRAnchorContainer>, IEquatable<OVRAnchorContainer>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRAnchorContainer>.Type => Type;

    ulong IOVRAnchorComponent<OVRAnchorContainer>.Handle => Handle;

    OVRAnchorContainer IOVRAnchorComponent<OVRAnchorContainer>.FromAnchor(OVRAnchor anchor) => new OVRAnchorContainer(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRAnchorContainer.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRAnchorContainer Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// @cond
    OVRTask<bool> IOVRAnchorComponent<OVRAnchorContainer>.SetEnabledAsync(bool enabled, double timeout)
        => throw new NotSupportedException("The AnchorContainer component cannot be enabled or disabled.");
    /// @endcond


    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRAnchorContainer other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRAnchorContainer lhs, OVRAnchorContainer rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRAnchorContainer lhs, OVRAnchorContainer rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRAnchorContainer and <see cref="Equals(OVRAnchorContainer)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRAnchorContainer other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.AnchorContainer";

    internal SpaceComponentType Type => SpaceComponentType.SpaceContainer;

    internal ulong Handle { get; }

    private OVRAnchorContainer(OVRAnchor anchor) => Handle = anchor.Handle;
}

public readonly partial struct OVRTriangleMesh : IOVRAnchorComponent<OVRTriangleMesh>, IEquatable<OVRTriangleMesh>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRTriangleMesh>.Type => Type;

    ulong IOVRAnchorComponent<OVRTriangleMesh>.Handle => Handle;

    OVRTriangleMesh IOVRAnchorComponent<OVRTriangleMesh>.FromAnchor(OVRAnchor anchor) => new OVRTriangleMesh(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRTriangleMesh.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRTriangleMesh Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// @cond
    OVRTask<bool> IOVRAnchorComponent<OVRTriangleMesh>.SetEnabledAsync(bool enabled, double timeout)
        => throw new NotSupportedException("The TriangleMesh component cannot be enabled or disabled.");
    /// @endcond


    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRTriangleMesh other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRTriangleMesh lhs, OVRTriangleMesh rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRTriangleMesh lhs, OVRTriangleMesh rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRTriangleMesh and <see cref="Equals(OVRTriangleMesh)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRTriangleMesh other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.TriangleMesh";

    internal SpaceComponentType Type => SpaceComponentType.TriangleMesh;

    internal ulong Handle { get; }

    private OVRTriangleMesh(OVRAnchor anchor) => Handle = anchor.Handle;
}


public readonly partial struct OVRDynamicObject : IOVRAnchorComponent<OVRDynamicObject>, IEquatable<OVRDynamicObject>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRDynamicObject>.Type => Type;

    ulong IOVRAnchorComponent<OVRDynamicObject>.Handle => Handle;

    OVRDynamicObject IOVRAnchorComponent<OVRDynamicObject>.FromAnchor(OVRAnchor anchor) => new OVRDynamicObject(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRDynamicObject.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRDynamicObject Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// @cond
    OVRTask<bool> IOVRAnchorComponent<OVRDynamicObject>.SetEnabledAsync(bool enabled, double timeout)
        => throw new NotSupportedException("The DynamicObject component cannot be enabled or disabled.");
    /// @endcond


    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRDynamicObject other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRDynamicObject lhs, OVRDynamicObject rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRDynamicObject lhs, OVRDynamicObject rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRDynamicObject and <see cref="Equals(OVRDynamicObject)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRDynamicObject other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.DynamicObject";

    internal SpaceComponentType Type => SpaceComponentType.DynamicObject;

    internal ulong Handle { get; }

    private OVRDynamicObject(OVRAnchor anchor) => Handle = anchor.Handle;
}

public readonly partial struct OVRMarkerPayload : IOVRAnchorComponent<OVRMarkerPayload>, IEquatable<OVRMarkerPayload>
{
    /// @cond

    SpaceComponentType IOVRAnchorComponent<OVRMarkerPayload>.Type => Type;

    ulong IOVRAnchorComponent<OVRMarkerPayload>.Handle => Handle;

    OVRMarkerPayload IOVRAnchorComponent<OVRMarkerPayload>.FromAnchor(OVRAnchor anchor) => new OVRMarkerPayload(anchor);

    /// @endcond

    /// <summary>
    /// A null representation of an OVRMarkerPayload.
    /// </summary>
    /// <remarks>
    /// Use this to compare with another component to determine whether it is null.
    /// </remarks>
    public static readonly OVRMarkerPayload Null = default;

    /// <summary>
    /// Whether this object represents a valid anchor component.
    /// </summary>
    public bool IsNull => Handle == 0;

    /// <summary>
    /// True if this component is enabled and no change to its enabled status is pending.
    /// </summary>
    public bool IsEnabled => !IsNull && GetSpaceComponentStatus(Handle, Type, out var enabled, out var pending) && enabled && !pending;




    /// @cond
    OVRTask<bool> IOVRAnchorComponent<OVRMarkerPayload>.SetEnabledAsync(bool enabled, double timeout)
        => throw new NotSupportedException("The MarkerPayload component cannot be enabled or disabled.");
    /// @endcond


    /// <summary>
    /// Compares this component for equality with <paramref name="other" />.
    /// </summary>
    /// <param name="other">The other component to compare with.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public bool Equals(OVRMarkerPayload other) => Handle == other.Handle;

    /// <summary>
    /// Compares two components for equality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if both components belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator ==(OVRMarkerPayload lhs, OVRMarkerPayload rhs) => lhs.Equals(rhs);

    /// <summary>
    /// Compares two components for inequality.
    /// </summary>
    /// <param name="lhs">The component to compare with <paramref name="rhs" />.</param>
    /// <param name="rhs">The component to compare with <paramref name="lhs" />.</param>
    /// <returns>True if the components do not belong to the same <see cref="OVRAnchor" />, otherwise false.</returns>
    public static bool operator !=(OVRMarkerPayload lhs, OVRMarkerPayload rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Compares this component for equality with <paramref name="obj" />.
    /// </summary>
    /// <param name="obj">The `object` to compare with.</param>
    /// <returns>True if <paramref name="obj" /> is an OVRMarkerPayload and <see cref="Equals(OVRMarkerPayload)" /> is true, otherwise false.</returns>
    public override bool Equals(object obj) => obj is OVRMarkerPayload other && Equals(other);

    /// <summary>
    /// Gets a hashcode suitable for use in a Dictionary or HashSet.
    /// </summary>
    /// <returns>A hashcode for this component.</returns>
    public override int GetHashCode() => unchecked(Handle.GetHashCode() * 486187739 + ((int)Type).GetHashCode());

    /// <summary>
    /// Gets a string representation of this component.
    /// </summary>
    /// <returns>A string representation of this component.</returns>
    public override string ToString() => $"{Handle}.MarkerPayload";

    internal SpaceComponentType Type => SpaceComponentType.MarkerPayload;

    internal ulong Handle { get; }

    private OVRMarkerPayload(OVRAnchor anchor) => Handle = anchor.Handle;
}


partial struct OVRAnchor
{
    internal static readonly Dictionary<Type, SpaceComponentType> _typeMap = new()
    {
        { typeof(OVRLocatable), SpaceComponentType.Locatable },
        { typeof(OVRStorable), SpaceComponentType.Storable },
        { typeof(OVRSharable), SpaceComponentType.Sharable },
        { typeof(OVRBounded2D), SpaceComponentType.Bounded2D },
        { typeof(OVRBounded3D), SpaceComponentType.Bounded3D },
        { typeof(OVRSemanticLabels), SpaceComponentType.SemanticLabels },
        { typeof(OVRRoomLayout), SpaceComponentType.RoomLayout },
        { typeof(OVRAnchorContainer), SpaceComponentType.SpaceContainer },
        { typeof(OVRTriangleMesh), SpaceComponentType.TriangleMesh },
        { typeof(OVRDynamicObject), SpaceComponentType.DynamicObject },
        { typeof(OVRMarkerPayload), SpaceComponentType.MarkerPayload },
    };
}
