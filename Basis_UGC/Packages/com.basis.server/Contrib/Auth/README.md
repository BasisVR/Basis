# Authentication

This directory houses all third-party authentication integrations.

For things dealing with usernames/display names, see [handles][handles]. For things
dealing with user permissions or roles, see Basis's core apis for Authorization.

## Difference between Authentication, Authorization, and Handles

- Authentication: how you log in, how you prove you are who you say you are, your
  machine-readable account identifier, etc
- Authorization: Layer on top of authentication - once users have accounts / account IDs
  authorization is a system to describe what they are *allowed* to do.
- Handles: human-readable systems for identifying users. These may change, and are often
  not suitable as permanent identifiers, so they are treated as a *separate* system from
  authentication.

[handles]: ../Handles/
