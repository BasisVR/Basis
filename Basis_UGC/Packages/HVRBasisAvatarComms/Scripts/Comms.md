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

By default, all messages used by HVR Comms use index `0xC0`, unless the Framework has a message index provisioning system.

- `bytes[0]` is a sub-packet identifier.

All messages are trickling from the avatar wearer to the other observers.

### Negotiation packet (\[0\] == 255)

- Who can send: Wearer
- Who can receive: Non-wearers

The Negotiation packet should be the first packet transmitted to all other clients when the avatar loads.

If a client sends us a Reserved packet of type *Remote Requests Initialization* (described later in this document),
a Negotiation packet should be sent to them.

This packet defines a sequence of GUIDs. The GUIDs are already uniquely assigned to specific networked systems, as such they serve
as networked IDs.

The index of the elements in that sequence will be used to refer to packets pertaining to the networked system represented by this GUID.

The aim of using GUIDs instead of numerical indices directly is as a defensive device, to mitigate the risk of a discrepancy between the
wearer and observers in the event of an updated asset where numerical indices would be assigned to different objects.

There cannot be more than 128 GUIDs.
- It is recommended to combine lots of information together inside the Transmission packets within a single networked system, rather than
  create lots of small networked systems.
- Example: Face Tracking can be a single networked system, represented by a single GUID, and the Transmission packet for that GUID
  may be used in any flexible way to transmit all face tracking data, a portion of the face tracking data, or the absence of face tracking data.
- Example: A single networked system may represent all toggles contained in an avatar, with a Transmission packet serving as the initial snapshot,
  and subsequent packets serving as change events.

#### Encoding

The following applies when `bytes[0]` equals 255.

It describes a sequence of GUIDs (can be empty), encoded in groups of 16 bytes.
<br>*As a reminder, a GUID is not transmitted as a string.*

Assert that:
- `(1 + (bytes.Length - 1) % 16) == 1` must be true.
- In `NumberOfGuids = (bytes.Length - 1) / 16`, `NumberOfGuids` must be between 0 (inclusive) and 127 (inclusive).
- **(TODO)** There must not be two identical GUIDs in that sequence.
- **(TODO)** All GUIDs must conform to UUID version 4 (https://datatracker.ietf.org/doc/html/rfc9562#name-uuid-version-4). *Other versions, and non-standard versionless GUIDs are prohibited.*
- **(TODO)** Using the Nil UUID is prohibited.
- **(TODO)** Using the Max UUID is prohibited.

### Reserved packet (\[0\] == 254)

This packet is reserved for future use, and will be used to communicate internal events.

Non-wearers can send this packet.

#### Encoding

The following applies when `bytes[0]` equals 254.

The value of `bytes[1]` corresponds to the reserved packet identifier.

Assert that:
- `bytes.Length` must be greater or equal to 2.

#### Reserved packet identifiers

- (\[1\] == 0) **Remote Requests Initialization**
  - Signals when the avatar is loaded on a non-wearer, so that the Negotiation packet may be sent.
  - Who can send: Non-wearers
  - Who can receive: Wearer

Assert that `bytes.Length == 2`

### Transmission packet (\[0\] < 254)

- Who can send: Wearer
- Who can receive: Non-wearers

Transmission packets contains the data payload, along with relative timing information that will be used for interpolation.

The data payload specification depends entirely on the implementation.

#### Encoding

The following applies when `bytes[0]` is strictly less than 255.

The value of `bytes[0]` corresponds to the index of the GUID that represents the component.

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
- `bytes[0]` must be less than the `NumberOfGuids` received in the last Negotiation packet.
- `bytes.Length` must be greater or equal to 2.

##### Event-Driven

The rest of the message is not subject to additional restrictions.

Assert that:
- `bytes[0]` must be less than the `NumberOfGuids` received in the last Negotiation packet.
- At least one valid Negotiation packet has been previously received.
- `bytes.Length` must be greater or equal to 1.
