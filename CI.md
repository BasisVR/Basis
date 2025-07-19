# CI Setup

## Secrets

In order to build the client automatically, you need to setup a unity license for Github Actions to use. This can be done by following the instructions at <https://game.ci/docs/github/activation>.

Additionally, to build the Android client in CI, you will need to make the following secrets available to Github Actions:
 - ANDROID_KEYSTORE_BASE64 : Base64 of your keystore
 - ANDROID_KEYSTORE_PASS: Password for your keystore
 - ANDROID_KEYALIAS_NAME: Name of the alias in your keystore
 - ANDROID_KEYALIAS_PASS: Password for the alias in your keystore

If you do not already have a keystore, you can follow the steps at <https://game.ci/docs/github/deployment/android/#3-generate-an-upload-key-and-keystore> to generate one.

## Caching Strategy

Cache management strategy is as follows:
 - Always attempt to restore cache, but only for the current platform.
   - This prevent's duplicating a platform's cache across multiple entries
 - Save cache whether the build succeeds or not, but only on the branch named `developer`.
   - Since the `developer` branch is the default branch, this maximizes what can *use* the cache.
 - Automatically delete any cache entries that are not the latest for this platform.
   - When you have gone above 10GB of cache, Github will automatically delete based on last access date.
   - Managing this ourselves allows us to retain the *latest* cache, rather than the *previous* cache.

Unfortunately, I'm not aware of any way to tell unity to remove entries from the Library folder that are not currently being used.
As-is, the cache is likely to grow over time and end up needing to be reset occasionally, resulting in a long build.
