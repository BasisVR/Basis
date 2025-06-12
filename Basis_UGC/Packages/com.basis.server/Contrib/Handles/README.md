# User Handles (aka usernames/display names)

An extensible system for representing handles/usernames in Basis.

Users report handles to their peers and/or server. They advertise 1 or more handles,
each from a different source:

* *Local* - Users choose their own handles, and negotiate any potential collisions with
  the server. Uniqueness guarantees can only be per-instance and are "first come first
  serve". Lowest security guarantees. Cannot prove custody over the handle, but the
  benefit is that local handles don't require any other service/account.
* *DNS* - custody is proven via a DNS TXT record that links to the player's DID.
  Identical or nearly identical to how bluesky's [ATProto][atproto handle] does it.
  Guaranteed to be globally unique and secure against impersonation.
* *HTTPS Well-Known* - custody is proven via GET request to a `.well-known` endpoint under
  a (sub)domain that links to the player's DID. Identical or nearly identical to to how
  bluesky's [ATProto][atproto handle] does it. Guaranteed to be globally unique and
  secure against impersonation.
* *Steam (TODO)* - custody is proven via your steam id
* *Oculus (TODO)* - custody is proven via your meta account

These APIs intentionally do not manage decisions about the UX around handles or how to
choose one handle system over the other. Those decisions are intentionally left up to
the application developer to give them maximum flexibility and be minimally opinionated.

[atproto handle]: https://atproto.com/specs/handle
