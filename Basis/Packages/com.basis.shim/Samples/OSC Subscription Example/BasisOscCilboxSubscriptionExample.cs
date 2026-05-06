using Cilbox;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using HVR.Basis.Comms.OSC;
using System.Text;
using UnityEngine;

namespace Basis.Shims.Samples
{
    [Cilboxable]
    public class BasisOscCilboxSubscriptionExample : MonoBehaviour
    {
        private const string ExplicitTestAddress = "/avatar/parameters/test";
        private const string ImplicitTestAddress = "test";

        private Basis.Shims.BasisOsc osc;
        private BasisAvatar avatar;
        private Basis.Shims.BasisNetworkShim networkShim;
        private bool isNetworkReady;

        private void Start()
        {
            if (osc == null)
            {
                osc = GetComponent<Basis.Shims.BasisOsc>();
                if (osc == null)
                {
                    Debug.LogError("BasisOscCilboxSubscriptionExample requires a BasisOsc component.");
                    return;
                }
            }

            avatar = BasisAvatar.GetGameObject(this).GetComponent<BasisAvatar>();
            if (avatar == null)
            {
                Debug.Log("BasisOscCilboxSubscriptionExample did not find a BasisAvatar in the parent hierarchy.");
            }

            networkShim = this.gameObject.GetComponent<Basis.Shims.BasisNetworkShim>();

            networkShim.NetworkReady += OnNetworkReady;
            networkShim.NetworkMessageReceived += OnNetworkMessageReceived;

            osc.Subscribe(ExplicitTestAddress, OnExplicitTestTriggered, out string ExplicitAddress);
            osc.Subscribe(ImplicitTestAddress, OnImplicitTestTriggered, out string ImplicitAddress);

            Debug.Log("Subscribed to " + ExplicitAddress + " and implicit \"" + ImplicitAddress + "\" on avatar local=" + avatar?.IsOwnedLocally + ".");
        }

        private void OnDestroy()
        {
            if (osc != null)
            {
                osc.Unsubscribe(ExplicitTestAddress, OnExplicitTestTriggered);
                osc.Unsubscribe(ImplicitTestAddress, OnImplicitTestTriggered);
            }

            if (networkShim != null)
            {
                networkShim.NetworkReady -= OnNetworkReady;
                networkShim.NetworkMessageReceived -= OnNetworkMessageReceived;
            }
            Debug.Log("BasisOscCilboxSubscriptionExample destroyed and unsubscribed from OSC and network events.");
        }

        private void OnExplicitTestTriggered(OscMessage message, OscData[] arguments)
        {
            HandleOscMessage("Explicit", message, arguments);
        }

        private void OnImplicitTestTriggered(OscMessage message, OscData[] arguments)
        {
            HandleOscMessage("Implicit", message, arguments);
        }

        private void OnNetworkReady()
        {
            isNetworkReady = true;
            Debug.Log("BasisOscCilboxSubscriptionExample network ready for avatar local=" + avatar.IsOwnedLocally + ".");
        }

        private void OnNetworkMessageReceived(ushort playerId, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            string payload = buffer == null || buffer.Length == 0 ? string.Empty : Encoding.UTF8.GetString(buffer);
            Debug.Log("Received network relay from player " + playerId + " via " + deliveryMethod + ": " + payload);
        }

        private void HandleOscMessage(string label, OscMessage message, OscData[] arguments)
        {
            string decoded = BuildDecodedOscReport(message, arguments);
            Debug.Log(label + " OSC subscription fired: " + decoded);
            ForwardDecodedMessageToAvatarOwner(label, decoded);
        }

        private void ForwardDecodedMessageToAvatarOwner(string label, string decoded)
        {
            if (avatar == null || avatar.IsOwnedLocally)
            {
                return;
            }

            if (networkShim == null || !isNetworkReady)
            {
                Debug.LogWarning("Skipping network relay for remote avatar because BasisNetworkShim is not ready yet.");
                return;
            }

            if (!avatar.TryGetLinkedPlayer(out ushort linkedPlayerId))
            {
                Debug.LogWarning("Skipping network relay for remote avatar because no linked player ID was available.");
                return;
            }

            string payload = label + " OSC relay to linked player " + linkedPlayerId + ": " + MessageOrDefault(decoded);
            Debug.Log("Relaying OSC message to avatar owner: " + payload);
            networkShim.SendCustomNetworkEvent(Encoding.UTF8.GetBytes(payload), DeliveryMethod.ReliableUnordered, new ushort[] { linkedPlayerId });
        }

        private static string BuildDecodedOscReport(OscMessage message, OscData[] arguments)
        {
            string report = message.Path + " with " + GetArgumentCount(arguments) + " argument(s)";

            if (arguments == null || arguments.Length == 0)
            {
                return report;
            }

            for (int index = 0; index < arguments.Length; index++)
            {
                report += " | arg[" + index + "]=" + DescribeOscData(arguments[index]);
            }

            return report;
        }

        private static string DescribeOscData(OscData value)
        {
            if (value == null)
            {
                return "null";
            }

            switch (value.Kind)
            {
                case OscDataKind.Boolean:
                    return "bool(" + value.BoolValue + ")";
                case OscDataKind.Nil:
                    return "nil";
                case OscDataKind.Impulse:
                    return "impulse";
                case OscDataKind.Int32:
                    return "int(" + value.IntValue + ")";
                case OscDataKind.Float32:
                    return "float(" + value.FloatValue + ")";
                case OscDataKind.TimeTag:
                    return "timeTag(" + value.TimeTagSeconds + "s," + value.TimeTagNanoseconds + "ns)";
                case OscDataKind.String:
                    return "string(\"" + value.StringValue + "\")";
                case OscDataKind.Symbol:
                    return "symbol(\"" + value.StringValue + "\")";
                case OscDataKind.Blob:
                    return "blob[" + GetBlobLength(value.BlobValue) + "]";
                case OscDataKind.Color:
                    return "color(r:" + value.ColorR + ",g:" + value.ColorG + ",b:" + value.ColorB + ",a:" + value.ColorA + ")";
                case OscDataKind.Int64:
                    return "long(" + value.LongValue + ")";
                case OscDataKind.Float64:
                    return "double(" + value.DoubleValue + ")";
                case OscDataKind.Char:
                    return "char(" + DescribeCharacter(value.UIntValue) + ")";
                case OscDataKind.Midi:
                    return "midi(port:" + value.MidiPort + ",status:" + value.MidiStatus + ",data1:" + value.MidiData1 + ",data2:" + value.MidiData2 + ")";
                case OscDataKind.Array:
                    return "array(" + DescribeArray(value.Elements) + ")";
                default:
                    return "unknown(" + value.Kind + ")";
            }
        }

        private static string DescribeArray(OscData[] values)
        {
            if (values == null || values.Length == 0)
            {
                return "empty";
            }

            string description = string.Empty;
            for (int index = 0; index < values.Length; index++)
            {
                if (index > 0)
                {
                    description += ", ";
                }

                description += DescribeOscData(values[index]);
            }

            return description;
        }

        private static string DescribeCharacter(uint codePoint)
        {
            if (codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                return "invalid/" + codePoint;
            }

            return "\"" + char.ConvertFromUtf32((int)codePoint) + "\"/" + codePoint;
        }

        private static int GetBlobLength(byte[] blob)
        {
            return blob == null ? 0 : blob.Length;
        }

        private static int GetArgumentCount(OscData[] arguments)
        {
            return arguments == null ? 0 : arguments.Length;
        }

        private static string MessageOrDefault(string decoded)
        {
            return string.IsNullOrEmpty(decoded) ? "empty OSC message" : decoded;
        }
    }
}
