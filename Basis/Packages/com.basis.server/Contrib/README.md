# Basis Third-Party Contributions

This directory is for community contributions to Basis which are incubating,
insufficiently generic, or may have a practical reason to avoid direct
inclusion in the core project. For (non-exhaustive) examples of code that
should go in `contrib`:

* Integrations with third party cloud APIs/services.
* Code that is "insufficiently generic" or tailored to use cases that are too
  specific to be useful for all Basis-derived projects.
* Code that has not yet been agreed upon for general inclusion in all
  Basis-derived projects.

## Directory and Project Structure

Contributions are grouped into "categories", which are directories like
`contrib/auth` for authentication integrations, or `contrib/assets` for asset
related integrations.

Beneath each category are a list of directories, one directory per distinct
contribution. Each one will be one or more C# class libraries, with their own
`.csproj` files. This will allow applications that wish to use the code to depend
on it via MSBuild using either the [`<ProjectReference>`][ProjectReference]
property or other means.

## Disclaimers

* Projects in `contrib`, or `contrib` itself, may be relocated in the future,
  either incorporated into the core project or moved into an external repository,
  or both in parts.
* Although prior discussion is ideal, this may happen without much prior notice.
* Code in `contrib` should be considered "unsupported" API, for the purposes of
  API breakage.
* Incorporation of `contrib` libraries into the Basis Demo does not imply
  intent to merge into the core project.
* Alignment with the core project is not necessarily a requirement for
  inclusion into contrib.
* Contributors are encouraged to seek modular solutions that can be hosted in
  external repositories, when possible.

[ProjectReference]: https://learn.microsoft.com/en-us/visualstudio/msbuild/common-msbuild-project-items?view=vs-2022#projectreference
