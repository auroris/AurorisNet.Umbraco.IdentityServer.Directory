# Developing

This page covers building the package, running its tests and publishing a release. The source
is in `src`: the settings and the choice of source, the three directories, the account service
and its management API controller, the two policy interfaces with their defaults, the composer,
and the backoffice extension in `src/wwwroot/App_Plugins/DirectoryAccounts`. The test project
holds the site fixture, the fakes and the tests that [Behavior](BEHAVIOR.md#tests) lists.

## Requirements

- Windows, for the local account tests and the Active Directory code.
- The .NET 10 SDK. [`global.json`](../global.json) keeps every build on a .NET 10 SDK and selects
  the Microsoft Testing Platform runner.
- A checkout of the IdentityServer repository beside this one, at `..\IdentityServer`, until
  `AurorisNet.IdentityServer.Directory` is on nuget.org. See [The people lookup](#the-people-lookup).
- PowerShell 7 to run the release workflow's steps by hand.

The tests need nothing else: the site uses its own SQLite database in a temporary folder and a
fake people lookup.

## The people lookup

The package depends on `AurorisNet.IdentityServer.Directory`, which ships from the
IdentityServer repository. By default the project builds it from source:

| Property | Default | Effect |
|---|---|---|
| `UseIdentityServerSource` | `true` | `true` references `$(IdentityServerRoot)\IdentityServer.Directory\IdentityServer.Directory.csproj`; `false` references the `AurorisNet.IdentityServer.Directory` package at the version in [`Directory.Packages.props`](../Directory.Packages.props). |
| `IdentityServerRoot` | `..\IdentityServer` beside this repository | The IdentityServer checkout to build from. |

Building from source also builds the IdentityServer repository's `IdentityServer.Directory` and
`ActiveDirectory` projects in their own `obj` and `bin` folders. Point `IdentityServerRoot` at
another checkout, for example in a worktree:

```powershell
dotnet build -c Release -p:IdentityServerRoot=D:\work\IdentityServer
```

A package packed from source would depend on a lookup version nobody published, so a
`ContinuousIntegrationBuild` pack stops with an error unless `UseIdentityServerSource` is
`false`. The release workflow sets it to `false`; publishing therefore waits until the lookup
package is on nuget.org at the version `Directory.Packages.props` names.

## Build

```powershell
dotnet build -c Release
```

The package compiles against the oldest supported Umbraco release, `UmbracoMinimumVersion` in
[`Directory.Build.props`](../Directory.Build.props), and its NuGet dependency allows any Umbraco
18 release from there. The tests build against `UmbracoVersion`, the newest release they have
been run on. Add `-p:UmbracoVersion=18.0.0` to build the tests against another release.

Both projects generate XML documentation, so the compiler checks every doc comment. Keep the
build free of warnings: the Release workflow builds with `ContinuousIntegrationBuild`, which
turns a warning into an error. NuGet's vulnerability reports stay warnings there.

To build a package:

```powershell
dotnet pack src/AurorisNet.Umbraco.IdentityServer.Directory.csproj -c Release -o artifacts/packages
```

A local package is numbered `0.0.0-local`, so NuGet's cache keeps it apart from published
versions. It contains `DirectoryAccounts.dll` with its embedded symbols and XML documentation,
the backoffice extension under `staticwebassets/App_Plugins/DirectoryAccounts`, the NuGet
README, the icon and the MIT licence.

## Tests

```powershell
dotnet test --project tests/AurorisNet.Umbraco.IdentityServer.Directory.Tests -c Release
```

`SiteTests` share one site, which starts in about twenty seconds; the other classes need no
site. The local account tests are skipped on a computer in a domain, where the account running
the tests is a domain account.

The fixture in `TestSite.cs` composes the site through `.AddComposers()`, as a real site does,
adds the test's `Directory` settings, then registers the fake lookup and test policies after the
package's defaults, which they replace. `UseStaticWebAssets()` serves the package's `wwwroot` in
the test's Production environment.

## Support a new Umbraco release

A minor Umbraco 18 release needs nothing unless it changes what the package relies on:
`ManagementApiControllerBase` and its routing attributes, the section-access policies,
`IUserService.CreateAsync`, `IMemberService.CreateMemberWithIdentity`,
`IExternalLoginWithKeyService`, `IAuditService.AddAsync`, and the backoffice's `collectionAction`
and `modal` extension types. Set `UmbracoVersion` to the new release, run the tests, and publish
a release if anything had to change.

## Publish a release

Releases use semantic versions: a fix raises the patch number, a new setting or behavior the
minor number, and a change that a site must act on, such as a renamed setting or a new Umbraco
major version, the major number.

Run **Actions → Release → Run workflow** on `main` with the version, such as `1.0.1`. The
workflow:

1. checks the version and rejects a tag that already exists on another commit;
2. runs the tests and packs the package with that version, against the published lookup;
3. reserves the tag `v<version>`, so no other run can take the version;
4. signs in to NuGet with trusted publishing as `AurorisNet` and pushes the package;
5. creates the GitHub release with the package attached, generated notes and the notes from
   the run.

`dry-run` stops after step 2 and keeps the package as an artifact for seven days. A failed run
can be started again with the same version: a tag on the same commit is reused, NuGet skips a
package it already has, and an existing GitHub release receives the package again. Trusted
publishing needs a policy on nuget.org for the `AurorisNet` account that names this repository
and `release.yml`.

## Documents

`docs/NUGET.md` is the package's page on nuget.org, which shows Markdown without HTML or relative
links. Keep it in step with the README when settings change. `docs/images/icon.svg` is the source
of `icon.png`; render it at 128 by 128 pixels after changing it, for example with the `sharp`
Node package.
