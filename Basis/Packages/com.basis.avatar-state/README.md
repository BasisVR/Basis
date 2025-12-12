
# Basis Avatar State

## !!Experimental!!

Library and tooling to transmit, store, apply changes to an avatar.

## Goals of Avatar State
 
### No Complex Behavior

It is a non-goal to handle complex behavior. Complex behavior should be written in a script or some other dynamic, application or future system. It will handle value updates on various aspects of an avatar and not much more.

### Readable and Portable Serialization

Avatar authors should easily be able to debug and modify properties on their clothing, textures, scripts, and shaders. This means _what_ is being modified should be easily understood, which unity animation name is not. (see XProp)

Ideally authors should be able to build these directly in the context of third party tools like blender. This means much of the serialized values should be stored as JSON (or easily converted to/from).

### Sparse Wire Format

Avatar state is shared sparsely (deltas) to keep bandwidth usage low. Avatar state should include a manifest of what _can_ be synced/modified. Syncing should only be sending updates by reference to that manifest and the new value. This means late joiners or lost events requires server stored state.
