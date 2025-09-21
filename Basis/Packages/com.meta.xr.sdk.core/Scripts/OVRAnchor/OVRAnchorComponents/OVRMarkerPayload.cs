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
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

/// <summary>
/// The payload type of an <see cref="OVRMarkerPayload"/>.
/// </summary>
/// <remarks>
/// For an <see cref="OVRAnchor"/> that supports the <see cref="OVRMarkerPayload"/> component, this enum is used to
/// indicate how you should interpret the payload's data.
///
/// See the <see cref="OVRMarkerPayload.PayloadType"/> property.
/// </remarks>
public enum OVRMarkerPayloadType
{
    /// <summary>
    /// The payload represents an invalid QR Code.
    /// </summary>
    InvalidQRCode = OVRPlugin.SpaceMarkerPayloadType.InvalidQRCode,

    /// <summary>
    /// The payload is a QR Code encoding a UTF-8 string.
    /// </summary>
    /// <remarks>
    /// When an <see cref="OVRMarkerPayload"/> is of this type, use <see cref="OVRMarkerPayload.AsString"/> to get
    /// the payload as a [System.String](https://learn.microsoft.com/en-us/dotnet/api/system.string?view=net-8.0).
    /// </remarks>
    StringQRCode = OVRPlugin.SpaceMarkerPayloadType.StringQRCode,

    /// <summary>
    /// The payload is a QR Code with a binary blob.
    /// </summary>
    /// <remarks>
    /// When an <see cref="OVRMarkerPayload"/> is of this type, use <see cref="OVRMarkerPayload.Bytes"/> to get the raw
    /// data as an array of bytes.
    /// </remarks>
    BinaryQRCode = OVRPlugin.SpaceMarkerPayloadType.BinaryQRCode,
}

partial class OVRExtensions
{
    /// <summary>
    /// Determines whether a <see cref="OVRMarkerPayloadType"/> refers to a QR Code.
    /// </summary>
    /// <param name="value">The <see cref="OVRMarkerPayloadType"/> to test.</param>
    /// <returns>Returns `true` if <paramref name="value"/> is a QR Code; otherwise, `false`.</returns>
    public static bool IsQRCode(this OVRMarkerPayloadType value) => value switch
    {
        OVRMarkerPayloadType.InvalidQRCode => true,
        OVRMarkerPayloadType.StringQRCode => true,
        OVRMarkerPayloadType.BinaryQRCode => true,
        _ => false,
    };
}

/// <summary>
/// Represents a marker payload (QR Code) associated with an <see cref="OVRAnchor"/>.
/// </summary>
/// <remarks>
/// Anchors with a payload support this component type. In order to access an anchor's payload, first get this component
/// from the <see cref="OVRAnchor"/>, as in the following example:
///
/// <example>
/// <code><![CDATA[
/// void AccessPayload(OVRAnchor anchor) {
///   if (!anchor.TryGetComponent<OVRMarkerPayload>(out var payload)) {
///     Debug.LogError("Anchor does not have a payload component.");
///     return;
///   }
///
///   Debug.Log($"Anchor payload is of type {payload.PayloadType}.");
/// }
/// ]]></code></example>
///
/// A <see cref="OVRMarkerPayload"/> can represent different types of data, indicated by the <see cref="PayloadType"/>
/// property. If the type is a <see cref="OVRMarkerPayloadType.StringQRCode"/>, use <see cref="AsString"/> to get the payload
/// as a `System.String`.
///
/// You can access the raw bytes with the <see cref="GetBytes"/> method or <see cref="Bytes"/> property.
/// </remarks>
partial struct OVRMarkerPayload
{
    public OVRMarkerPayloadType PayloadType
    {
        get
        {
            var payload = default(OVRPlugin.SpaceMarkerPayload);
            if (!OVRPlugin.GetSpaceMarkerPayload(Handle, ref payload).IsSuccess())
            {
                return OVRMarkerPayloadType.InvalidQRCode;
            }

            return (OVRMarkerPayloadType)payload.PayloadType;
        }
    }

    /// <summary>
    /// Gets the payload as a string.
    /// </summary>
    /// <remarks>
    /// <see cref="PayloadType"/> must be <see cref="OVRMarkerPayloadType.StringQRCode"/>.
    /// </remarks>
    /// <returns>Returns the payload as a string.</returns>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="PayloadType"/> is not
    /// <see cref="OVRMarkerPayloadType.StringQRCode"/></exception>
    public string AsString()
    {
        if (PayloadType != OVRMarkerPayloadType.StringQRCode)
            throw new InvalidOperationException($"{nameof(PayloadType)} must be {OVRMarkerPayloadType.StringQRCode}.");

        using var buffer = new NativeArray<byte>(ByteCount, Allocator.Temp);
        unsafe
        {
            var ptr = buffer.GetUnsafeReadOnlyPtr();
            return Marshal.PtrToStringUTF8(new(ptr), GetBytes(new(ptr, buffer.Length)));
        }
    }

    /// <summary>
    /// A copy of the payload bytes.
    /// </summary>
    /// <remarks>
    /// This property creates a new copy of the payload bytes each time it is invoked. Use <see cref="GetBytes"/> to
    /// provide your own buffer.
    /// </remarks>
    public ArraySegment<byte> Bytes
    {
        get
        {
            var length = ByteCount;
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            var buffer = new byte[length];
            return new(buffer, 0, GetBytes(buffer));
        }
    }

    /// <summary>
    /// The number of bytes in the payload.
    /// </summary>
    public int ByteCount
    {
        get
        {
            var payload = default(OVRPlugin.SpaceMarkerPayload);
            return OVRPlugin.GetSpaceMarkerPayload(Handle, ref payload).IsSuccess()
                ? (int)payload.BufferCountOutput
                : 0;
        }
    }

    /// <summary>
    /// Copies the payload bytes to <paramref name="buffer"/>.
    /// </summary>
    /// <remarks>
    /// Use this method to copy the payload data into a byte array. <paramref name="buffer"/> must be large enough to
    /// hold the data. Use <see cref="ByteCount"/> to create a buffer large enough to hold the payload data.
    ///
    /// <example>
    /// <code><![CDATA[
    /// byte[] GetData(OVRMakerPayload payload)
    /// {
    ///     var bytes = new byte[payload.ByteCount];
    ///     var length = payload.GetBytes(bytes);
    ///     return bytes;
    /// }
    /// ]]></code>
    /// </example>
    /// </remarks>
    /// <param name="buffer">A `Span` to write the payload data to.</param>
    /// <returns>Returns the number of bytes written to <paramref name="buffer"/>.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="buffer"/> is less than <see cref="ByteCount"/>.</exception>
    public int GetBytes(Span<byte> buffer)
    {
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                var payload = new OVRPlugin.SpaceMarkerPayload
                {
                    BufferCapacityInput = (uint)buffer.Length,
                    Buffer = ptr
                };

                var result = OVRPlugin.GetSpaceMarkerPayload(Handle, ref payload);
                if (result == OVRPlugin.Result.Failure_InsufficientSize)
                {
                    throw new ArgumentException($"{nameof(buffer)} is not large enough to hold the payload data. It " +
                                                $"must be at least {payload.BufferCountOutput} but was {buffer.Length}.",
                        nameof(buffer));
                }

                return result.IsSuccess() ? (int)payload.BufferCountOutput : 0;
            }
        }
    }
}
