HVR Comms
======

This document describes the systems used for the development of individual visual effects enclosed inside avatars
and avatar items that may be used in Basis.

To produce an effect on the avatar in a way that's networked, the user has to put various Actuators on their avatar.

An **Actuator** produces a visible effect.
- It registers itself to the *Acquisition* service, providing it with the addresses it wants to listen to.
- It registers itself to the *Networked Feature* service, providing it with a data model of what's necessary
  to transmit the functions of this actuator across the network.
    - **TODO: Build the feature progressively as information is received.**
- From the users' perspective, Actuators are all they need.
  - *Acquisition* and *Networked Feature* are derived from this Actuator, internally handled by the implementor of that actuator.

An **Acquisition** service receives large amounts of data from various providers.
- Example of providers: OpenXR Input, Websockets, OSC.
- A piece of data is a string address, and a float number associated to it.
- Listeners may declare what addresses they want to listen to.
- When data is received by this service, it forwards them to listeners.

A **Networked Feature** service stores, transmits, and receives data.
- Data is indexed by a number.
- The value is represented by a number.

The separation between the *Acquisition* service and the *Networked Feature* service ensures that:
- Only features that are actually used by the system is networked, regardless of how much information is received through acquisition.
  - For example, if Eye and Face Tracking information is received, but no Actuators exist for the mouth, then only Eye information will be networked.
- The information that is received through acquisition does not have to conform with how that information will be networked.
  - This avoids shifting the responsibility of encoding bits to the provider of Acquisition.

## HVR Comms Protocol

`bytes[0]` is a sub-packet identifier.

All data messages are trickling from the avatar wearer to the other observers.

### Transmission packet (\[0\] == 0)

- Who can send: Wearer
- Who can receive: Non-wearers

Transmission packets contain the data payload, along with relative timing information that will be used for interpolation.

The data payload specification depends entirely on the implementation.

#### Encoding

The following applies when `bytes[0]` is equal to 0.

The rest of the message depends on whether the packet is being received by a streaming feature or an event-driven feature:

##### Streaming

The value of `bytes[1]` is the interpolation duration needed for this packet.
- It is generally defined to be the number of seconds since the last packet was sent, multiplied by 60 (a second is quantized in 60 parts).
- It can be set to a different duration, or 0, in order to change the interpolation duration.
- As a result of this formula, the maximum encoded interpolation duration would be 4.25 seconds.
- The packet delivery guarantee is specified by the implementation.
- The receiver implementation reserves the right to speed up or slow down the playback of those packets,
  or apply multiple packets within the same frame in the order they were received,
  but it will not drop any packet that was effectively received.

Assume that:
- At least one valid Negotiation packet has been previously received.
- If a Negotiation packet has not been received yet prior to a Transmission packet being received,
it is not considered to be an error, as Transmission packets are sent to everyone regardless of whether
individual recipients have been sent a Negotiation packet yet.

Assert that:
- `bytes[0]` must be 0.
- `bytes.Length` must be greater or equal to 2.

##### Event-Driven

The rest of the message is not subject to additional restrictions.

Assert that:
- `bytes[0]` must be 0.
- At least one valid Negotiation packet has been previously received.
- `bytes.Length` must be greater or equal to 1.

### Avatar wearer is ready (\[0\] == 1)

- Who can send: Wearer
- Who can receive: Non-wearers

When this packet is received, the wearer has loaded their avatar.

### Remote requests initialization (\[0\] == 0)

- Who can send: Non-wearers
- Who can receive: Wearer

When this packet is received, a remote user has loaded the avatar and wants to initialize it.
