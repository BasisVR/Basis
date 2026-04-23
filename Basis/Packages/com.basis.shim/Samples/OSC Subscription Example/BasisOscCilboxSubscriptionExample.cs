using Cilbox;
using HVR.Basis.Comms.OSC;
using UnityEngine;

namespace Basis.Shims.Samples
{
    [Cilboxable]
    public class BasisOscCilboxSubscriptionExample : MonoBehaviour
    {
        private const string ExplicitTestAddress = "/avatar/parameters/test";
        private const string ImplicitTestAddress = "test";

        private Basis.Shims.BasisOsc osc;


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

            osc.Subscribe(ExplicitTestAddress, OnExplicitTestTriggered);
            osc.Subscribe(ImplicitTestAddress, OnImplicitTestTriggered);

            Debug.Log("Subscribed to " + ExplicitTestAddress + " and implicit \"" + ImplicitTestAddress + "\".");
        }

        private void OnDisable()
        {
            if (osc == null)
            {
                return;
            }

            osc.Unsubscribe(ExplicitTestAddress, OnExplicitTestTriggered);
            osc.Unsubscribe(ImplicitTestAddress, OnImplicitTestTriggered);
        }

        private void OnExplicitTestTriggered(OscMessage message, OscData[] arguments)
        {
            Debug.Log("Explicit OSC subscription fired for " + message.Path + " with " + GetArgumentCount(arguments) + " argument(s).");
        }

        private void OnImplicitTestTriggered(OscMessage message, OscData[] arguments)
        {
            Debug.Log("Implicit OSC subscription fired for " + message.Path + " with " + GetArgumentCount(arguments) + " argument(s).");
        }

        private static int GetArgumentCount(OscData[] arguments)
        {
            return arguments == null ? 0 : arguments.Length;
        }
    }
}
