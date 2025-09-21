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
using System.Runtime.InteropServices;
using JetBrains.Annotations;
using Unity.Collections;
using static OVRPlugin;

/// <summary>
/// (Internal) Utility to assist with queries for <see cref="OVRSpace"/>s.
/// </summary>
internal static class OVRSpaceQuery
{
    public const int MaxResultsForAnchors = SpaceFilterInfoIdsMaxSize;
    public const int MaxResultsForGroup = MaxQuerySpacesByGroup;
    public const SpaceStorageLocation DefaultStorageLocation = SpaceStorageLocation.Cloud;
    public const double DefaultTimeout = 0.0; // 0 = no timeout

    //
    // for anchors

    public static (Result result, string why) ForAnchors([CanBeNull] IEnumerable<Guid> anchorIds, out SpaceQueryInfo2 query)
    {
        query = s_TemplateQuery;
        query.FilterType = SpaceQueryFilterType.Ids;
        query.MaxQuerySpaces = MaxResultsForAnchors;
        return AppendAnchors(ref query, anchorIds);
    }

    internal static SpaceQueryInfo2 ForAnchorsUnchecked(OVREnumerable<Guid> anchorIds)
    {
        var query = s_TemplateQuery;
        query.FilterType = SpaceQueryFilterType.Ids;
        query.MaxQuerySpaces = MaxResultsForAnchors;
        foreach (var id in anchorIds)
        {
            query.IdInfo.Ids[query.IdInfo.NumIds++] = id;
        }
        _ = PostProcessQuery(ref query, Result.Success, string.Empty);
        return query;
    }

    internal static SpaceQueryInfo2 ForAnchorsThrow([NotNull] IEnumerable<Guid> anchorIds, string argName = null)
    {
        var (queryValidation, why) = ForAnchors(anchorIds, out var query);

        if (queryValidation.IsSuccess())
            return query;

        why = $"{why} ({(int)queryValidation} {queryValidation})";

        switch (queryValidation)
        {
            case Result.Failure_InvalidParameter:
            case Result.Failure_HandleInvalid:
                throw new ArgumentException(why, argName);
            default:
                throw new InvalidOperationException(why);
        }
    }

    //
    // for single component

    public static (Result result, string why) ForComponent(SpaceComponentType type, out SpaceQueryInfo2 query)
    {
        query = s_TemplateQuery;
        query.FilterType = SpaceQueryFilterType.Components;
        query.Location = SpaceStorageLocation.Local; // Component queries only support Local
        query.MaxQuerySpaces = MaxResultsForAnchors;
        query.ComponentsInfo.Components[0] = type;
        query.ComponentsInfo.NumComponents = 1;

        // no validation necessary

        return PostProcessQuery(ref query, Result.Success, string.Empty);
    }

    internal static SpaceQueryInfo2 ForComponentUnchecked(SpaceComponentType type)
    {
        var query = s_TemplateQuery;
        query.FilterType = SpaceQueryFilterType.Components;
        query.Location = SpaceStorageLocation.Local; // Component queries only support Local
        query.MaxQuerySpaces = MaxResultsForAnchors;
        query.ComponentsInfo.Components[0] = type;
        query.ComponentsInfo.NumComponents = 1;
        _ = PostProcessQuery(ref query, Result.Success, string.Empty);
        return query;
    }

    internal static SpaceQueryInfo2 ForComponentThrow(SpaceComponentType type, string argName = null)
    {
        var (queryValidation, why) = ForComponent(type, out var query);
        // Note: ForComponent doesn't currently fail for any reason, but that's a hidden detail

        if (queryValidation.IsSuccess())
            return query;

        why = $"{why} ({(int)queryValidation} {queryValidation})";

        switch (queryValidation)
        {
            case Result.Failure_InvalidParameter:
                throw new ArgumentException(why, argName);
            default:
                throw new InvalidOperationException(why);
        }
    }

    //
    // for single group

    public static (Result result, string why) ForGroup(Guid groupUuid, out SpaceQueryInfo2 query, IEnumerable<Guid> anchorIds = null)
    {
        query = s_TemplateQuery;
        query.FilterType = SpaceQueryFilterType.Group;
        query.MaxQuerySpaces = MaxResultsForGroup;
        query.GroupUuidInfo = groupUuid;

        var result = Result.Success;
        var why = string.Empty;

        if (groupUuid == Guid.Empty)
        {
            result = Result.Failure_InvalidParameter;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            why = $"Guid value {groupUuid:P} is not a valid Group UUID.";
#endif
        }
        else if (anchorIds != null)
        {
            return AppendAnchors(ref query, anchorIds);
        }

        return PostProcessQuery(ref query, result, why);
    }

    internal static SpaceQueryInfo2 ForGroupUnchecked(Guid groupUuid, OVREnumerable<Guid> anchorIds = default)
    {
        var query = s_TemplateQuery;
        query.FilterType = SpaceQueryFilterType.Group;
        query.MaxQuerySpaces = MaxResultsForGroup;
        query.GroupUuidInfo = groupUuid;
        foreach (var id in anchorIds)
        {
            query.IdInfo.Ids[query.IdInfo.NumIds++] = id;
        }
        _ = PostProcessQuery(ref query, Result.Success, string.Empty);
        return query;
    }

    internal static SpaceQueryInfo2 ForGroupThrow(Guid groupUuid, string argName = null, IEnumerable<Guid> anchorIds = null)
    {
        var (queryValidation, why) = ForGroup(groupUuid, out var query, anchorIds);

        if (queryValidation.IsSuccess())
            return query;

        why = $"{why} ({(int)queryValidation} {queryValidation})";

        switch (queryValidation)
        {
            case Result.Failure_InvalidParameter:
            case Result.Failure_HandleInvalid:
                throw new ArgumentException(why, argName);
            default:
                throw new InvalidOperationException(why);
        }
    }


    //
    // v1 <-> v2 conversion

    public static SpaceQueryInfo ToV1(in this SpaceQueryInfo2 query2)
    {
        return new QueryInfoUnion { V2 = query2 }.V1;
    }

    public static SpaceQueryInfo2 ToV2(in this SpaceQueryInfo query1)
    {
        return new QueryInfoUnion { V1 = query1 }.V2;
    }


    //
    // private

    static (Result result, string why) AppendAnchors(ref SpaceQueryInfo2 query, IEnumerable<Guid> anchorIds)
    {
        var result = Result.Success;
        var why = string.Empty;

        if (query.FilterType != SpaceQueryFilterType.Ids &&
            query.FilterType != SpaceQueryFilterType.Group)
        {
            result = Result.Failure_InvalidOperation;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            why = $"SpaceQueryFilterType.{query.FilterType} does not support secondary filtering by anchor Uuids.";
#endif
            return PostProcessQuery(ref query, result, why);
        }

        foreach (var id in anchorIds.ToNonAlloc()) // extension guards against null enumerable
        {
            if (query.IdInfo.NumIds >= query.MaxQuerySpaces)
            {
                result = Result.Failure_InvalidParameter;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                why = $"You may only fetch up to {query.MaxQuerySpaces} anchors per {query.FilterType} query.";
#endif
                return PostProcessQuery(ref query, result, why);
            }

            query.IdInfo.Ids[query.IdInfo.NumIds++] = id;
        }

        return PostProcessQuery(ref query, result, why);
    }

    static (Result result, string why) PostProcessQuery(ref SpaceQueryInfo2 query, Result result, in string why)
    {
        if (result.IsSuccess())
        {
            if (query.MaxQuerySpaces > query.IdInfo.NumIds && query.IdInfo.NumIds > 0)
                query.MaxQuerySpaces = query.IdInfo.NumIds;
        }
        else
        {
            query.MaxQuerySpaces = 0;
            query.IdInfo.NumIds = 0;
        }
        return (result, why);
    }

    #region details

    [StructLayout(LayoutKind.Explicit)]
    struct QueryInfoUnion
    {
        [FieldOffset(0)]
        public SpaceQueryInfo V1;
        [FieldOffset(0)]
        public SpaceQueryInfo2 V2;
    }

    static readonly Guid[] s_Ids = new Guid[MaxResultsForAnchors];
    static readonly SpaceComponentType[] s_ComponentTypes = new SpaceComponentType[SpaceFilterInfoComponentsMaxSize];
    static readonly SpaceQueryInfo2 s_TemplateQuery = new()
    {
        QueryType = SpaceQueryType.Action,
        ActionType = SpaceQueryActionType.Load,
        Location = DefaultStorageLocation,
        Timeout = DefaultTimeout,
        IdInfo = new SpaceFilterInfoIds
        {
            Ids = s_Ids,
        },
        ComponentsInfo = new SpaceFilterInfoComponents
        {
            Components = s_ComponentTypes,
        },
    };

    /// <summary>
    /// (Obsolete)(Internal) Represents options used to generate an <see cref="OVRSpaceQuery"/>.
    /// </summary>
    [Obsolete("This helper is for obsolete usages of xrQuerySpacesFB. See OVRAnchor.FetchAnchorsAsync.")]
    public struct Options
    {
        /// <summary>
        /// The maximum number of UUIDs which can be used in a <see cref="UuidFilter"/>.
        /// </summary>
        public const int MaxUuidCount = OVRPlugin.SpaceFilterInfoIdsMaxSize;

        /// <summary>
        /// The maximum number of results the query can return.
        /// </summary>
        public int MaxResults { get; set; }

        /// <summary>
        /// The timeout, in seconds for the query.
        /// </summary>
        /// <remarks>
        /// Zero indicates the query does not timeout.
        /// </remarks>
        public double Timeout { get; set; }

        /// <summary>
        /// The storage location to query.
        /// </summary>
        public OVRSpace.StorageLocation Location { get; set; }

        /// <summary>
        /// The type of query to perform.
        /// </summary>
        public SpaceQueryType QueryType { get; set; }

        /// <summary>
        /// The type of action to perform.
        /// </summary>
        public SpaceQueryActionType ActionType { get; set; }

        private SpaceComponentType _componentType;

        private IEnumerable<Guid> _uuidFilter;

        private Guid? _groupFilter;

        /// <summary>
        /// The components which must be present on the space in order to match the query.
        /// </summary>
        /// <remarks>
        /// The query will be limited to spaces that have this set of components. You may filter by component type or
        /// UUID (see <see cref="UuidFilter"/>), but not both at the same time.
        ///
        /// Currently, only one component is allowed at a time.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown if <see cref="UuidFilter"/> is not `null`.</exception>
        public SpaceComponentType ComponentFilter
        {
            get => _componentType;
            set
            {
                ValidateSingleFilter(_uuidFilter, value, _groupFilter);
                _componentType = value;
            }
        }

        /// <summary>
        /// A set of UUIDs used to filter the query.
        /// </summary>
        /// <remarks>
        /// The query will look for this set of UUIDs and only return matching UUIDs up to <see cref="MaxResults"/>.
        /// You may filter by component type (see <see cref="ComponentFilter"/>) or UUIDs, but not both at the same
        /// time.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown if <see cref="ComponentFilter"/> is not 0.</exception>
        /// <exception cref="ArgumentException">Thrown if <see cref="UuidFilter"/> is set to a value that contains more
        /// than <seealso cref="MaxUuidCount"/> UUIDs.</exception>
        public IEnumerable<Guid> UuidFilter
        {
            get => _uuidFilter;
            set
            {
                ValidateSingleFilter(value, _componentType, _groupFilter);

                if (value is IReadOnlyCollection<Guid> collection && collection.Count > MaxUuidCount)
                    throw new ArgumentException(
                        $"There must not be more than {MaxUuidCount} UUIDs specified by the {nameof(UuidFilter)} (new value contains {collection.Count} UUIDs).",
                        nameof(value));

                _uuidFilter = value;
            }
        }

        public Guid? GroupFilter
        {
            get => _groupFilter;
            set
            {
                ValidateSingleFilter(_uuidFilter, _componentType, value);
                _groupFilter = value;
            }
        }

        /// <summary>
        /// Creates a new <see cref="OVRPlugin.SpaceQueryInfo"/> from this.
        /// </summary>
        /// <returns>The newly created info.</returns>
        public SpaceQueryInfo ToQueryInfo()
        {
            Result queryValidation;
            string why;
            SpaceQueryInfo2 query2;

            if (_uuidFilter != null)
                (queryValidation, why) = ForAnchors(_uuidFilter, out query2);
            else
                (queryValidation, why) = ForComponent(_componentType, out query2);

            if (queryValidation.IsSuccess())
                return query2.ToV1();

            if (queryValidation == Result.Failure_InvalidParameter)
                throw new InvalidOperationException(
                    $"{nameof(UuidFilter)} must not contain more than {MaxUuidCount} UUIDs.");

            throw new InvalidOperationException(why);
        }

        /// <summary>
        /// Creates a new <see cref="OVRPlugin.SpaceQueryInfo2"/> from this.
        /// </summary>
        /// <returns>The newly created info.</returns>
        public SpaceQueryInfo2 ToQueryInfo2()
        {
            Result queryValidation;
            string why;
            SpaceQueryInfo2 query2;

            if (_groupFilter.HasValue)
                (queryValidation, why) = ForGroup(_groupFilter.Value, out query2, _uuidFilter);
            else if (_uuidFilter != null)
                (queryValidation, why) = ForAnchors(_uuidFilter, out query2);
            else
                (queryValidation, why) = ForComponent(_componentType, out query2);

            if (queryValidation.IsSuccess())
                return query2;

            if (queryValidation == Result.Failure_InvalidParameter)
                throw new InvalidOperationException(
                    $"{nameof(UuidFilter)} must not contain more than {MaxUuidCount} UUIDs.");

            throw new InvalidOperationException(why);
        }

        /// <summary>
        /// Initiates a space query.
        /// </summary>
        /// <param name="requestId">When this method returns, <paramref name="requestId"/> will represent a valid
        /// request if successful, or an invalid request if not. This parameter is passed initialized.</param>
        /// <returns>`true` if the query was successfully started; otherwise, `false`.</returns>
        public bool TryQuerySpaces(out ulong requestId)
        {
            var querySpaces = QuerySpaces(ToQueryInfo(), out requestId);
            return querySpaces;
        }

        private static void ValidateSingleFilter(IEnumerable<Guid> uuidFilter, SpaceComponentType componentFilter, Guid? groupFilter)
        {
            int filterCount = 0;

            if (uuidFilter != null)
                filterCount++;

            if (groupFilter.HasValue)
                filterCount++;

            if (componentFilter != 0)
                filterCount++;

            if (filterCount > 1)
                throw new InvalidOperationException($"You may only query by one of UUID, Group, or component type.");
        }
    }

    #endregion details

}
