# Umbraco Add from Directory

`AurorisNet.Umbraco.IdentityServer.Directory` adds an **Add from directory** button to the Users
and Members sections of an Umbraco 18 backoffice. An administrator searches the directory,
chooses a person and their groups, and the site creates an approved backoffice user or member
that carries the person's Windows SID as an external login. The person's first single sign-on
finds the account by that SID, so the username and email can differ from the ones the sign-in
reports.

The package reads people from one of three sources:

- **Active Directory**, queried by the site as its process account through the
  [`AurorisNet.IdentityServer.Directory`](https://www.nuget.org/packages/AurorisNet.IdentityServer.Directory)
  people lookup.
- **An identity server's directory API**, for a site that signs in through an external identity
  server.
- **The computer's local accounts**, for a development machine outside a domain.

It targets .NET 10, registers itself through Umbraco's composers, and is licensed under the
[MIT license](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/LICENSE).
Active Directory and local accounts are read through `System.DirectoryServices` and need Windows;
the identity server source reads people over HTTP.

## Install

1. Add the package to a website running Umbraco 18:

   ```xml
   <PackageReference Include="AurorisNet.Umbraco.IdentityServer.Directory" Version="1.*" />
   ```

2. Set the login providers to the scheme names of the site's single sign-on, so the first
   sign-in finds the SID. For a site that signs in through an external identity server, also set
   its address and key:

   ```json
   "Directory": {
     "IdentityServerAuthority": "https://login.example.com/IdentityServer",
     "IdentityServerApiKey": "one of the server's directory API keys",
     "BackOfficeLoginProvider": "Umbraco.IdentityServer",
     "MemberLoginProvider": "UmbracoMembers.IdentityServer"
   }
   ```

3. Start the site. Once Umbraco has started, the log names the source in use:

   ```text
   Accounts are created from Active Directory, queried as DOMAIN\AppPool across the whole domain.
   ```

The `DirectoryAccountsComposer` registers everything through `.AddComposers()`. A site that
composes Umbraco by hand calls `builder.AddAccountDirectory()`; a second call does nothing.

## Settings

The package reads the `Directory` section, which also holds the people lookup's
[LDAP roots and result limits](https://www.nuget.org/packages/AurorisNet.IdentityServer.Directory)
(`UserRootGlobal`, `UserRoot`, `GlobalSearchMinLength`, `DefaultTake`, `MaxTake` and others).
Active Directory searches use `UserRootGlobal`.

| Setting | Default | Effect |
|---|---|---|
| `Directory:Source` | `Auto` | `ActiveDirectory`, `IdentityServer` or `LocalAccounts` selects that source. `Auto` chooses one from the settings below and the computer, as the next table shows. |
| `Directory:UseLocalAccounts` | unset | With `Auto`, `true` selects local accounts and `false` keeps them out of the choice. |
| `Directory:IdentityServerAuthority` | empty | The identity server whose directory API the site searches, such as `https://login.example.com/IdentityServer`. |
| `Directory:IdentityServerApiKey` | empty | Sent in the `X-Api-Key` header. Empty authenticates as the site's Windows identity, the application pool account in IIS, which the server must then admit. |
| `Directory:IdentityServerAcceptAnyServerCertificate` | `false` | Accepts any certificate from the identity server. For an isolated intranet with a self-signed certificate only. |
| `Directory:BackOfficeLoginProvider` | `Umbraco.IdentityServer` | The external-login provider a new user's SID is stored under: the scheme name of the site's backoffice sign-in. |
| `Directory:MemberLoginProvider` | `UmbracoMembers.IdentityServer` | The same for members: the scheme name of the site's member sign-in. |
| `Directory:MemberTypeAlias` | `Member` | The member type of a member created without one. |

An empty login provider or member type falls back to its default.

### Choosing the source

The site chooses the source from the settings alone, when the directory service is first
created, and contacts no directory to do so. A directory that is down or misconfigured leaves
the site running, and the first search reports the problem.

| `Source` | `UseLocalAccounts` | `IdentityServerAuthority` | Computer | Source in use |
|---|---|---|---|---|
| `ActiveDirectory`, `IdentityServer` or `LocalAccounts` | any | any | any | That source |
| `Auto` | `true` | any | any | Local accounts |
| `Auto` | unset or `false` | set | any | Identity server |
| `Auto` | unset | empty | outside a domain | Local accounts |
| `Auto` | unset | empty | in a domain | Active Directory |
| `Auto` | `false` | empty | any | Active Directory |

Windows reports a computer outside a domain by giving the process account's domain the
computer's name. On another platform, choosing Active Directory or local accounts stops the
first search with `PlatformNotSupportedException`.

Each source runs as the site. Against Active Directory, the application pool identity needs read
access under `UserRootGlobal`; `ApplicationPoolIdentity` has it on a domain-joined server.

## The dialog

1. In **Users** or **Members**, select **Add from directory**.
2. Type at least two characters of a name, username or email. The dialog lists the enabled
   people found, with their Windows account name, email and title or department, and marks
   those who already have an account of that kind as active, disabled or retired.
3. Choose a person, then their user groups (at least one), or their member type (when the site
   has more than one) and member groups.
4. Select **Create user** or **Create member**. The list shows the new account and a
   notification confirms it.

The new account is approved and filled in from the directory:

| Field | Value |
|---|---|
| Username | The Windows account name without its domain, in lower case: `DOMAIN\Test.User` becomes `test.user`. A site with `Umbraco:CMS:Security:UsernameIsEmail` gets the email instead. |
| Name | The directory's display name. |
| Email | The directory's email, or `username@localhost` when it has none. |
| External login | The SID, under `Directory:BackOfficeLoginProvider` or `Directory:MemberLoginProvider`. |
| Password (members) | A random password, kept when a member-creation handler has already set one. |

The browser sends only the person's SID. The site reads the name, email and account state from
the directory again, so a changed request cannot give an account someone else's identity.

## Account rules

The site creates the account, its SID link and an audit entry in one Umbraco scope, after these
checks:

1. The request names a SID, such as `S-1-5-21-...`. A user needs at least one user group, and
   the member type and member groups must exist.
2. The directory knows the SID, and the account is enabled there.
3. The person has no account of that kind yet. An account is the person's when it is linked to
   the SID under the configured login provider or one of `ISignInLinkPolicy.PreviousLoginProviders`,
   or when its username is the SID and `IRetiredAccountPolicy.IsRetired` reports it as retired.
4. No other account holds the username or email. An account linked only to this SID, under a
   provider the site no longer configures, counts as the person's own and gives the "Already has
   an account" answer.
5. Umbraco creates the user in the chosen groups, or the member with the chosen type and groups.
   Umbraco's own rule applies: a user who is not an administrator can give a new user only the
   groups they belong to.

The audit log records `Created from the directory for DOMAIN\user, linked to its Windows identity S-1-5-21-...`.
When a check fails, the site creates nothing and the dialog shows the problem's title and advice:

| Status | Title | Cause and advice shown |
|---|---|---|
| 400 | No person chosen | The request carries no valid SID. |
| 400 | Choose a user group | A user was requested without a user group. |
| 400 | Unknown member type, Unknown member group | The member type or a member group does not exist. |
| 404 | Not in the directory | The directory has no account with the SID; it may have been deleted since the search. |
| 400 | Disabled in the directory | The account is disabled in the directory, so nobody could sign in with it. |
| 409 | Already has an account | The person already has an account of that kind, linked to their Windows identity. For a disabled one, the advice is to enable it in Users or Members. |
| 409 | Retired account | A retired account holds the person's SID. The advice comes from `IRetiredAccountPolicy.RestoreAdvice`. |
| 409 | Username taken, Email address taken | Another account holds the name. When it is linked to another identity or retired, the advice is to rename or delete it. When it is unlinked and `ISignInLinkPolicy.FirstSignInLinksByName` is true, the advice says the person can sign in and claim it; otherwise, to rename or delete it. |
| 403 | Not allowed | A user who is not an administrator chose a group they do not belong to. |
| 400 | Unknown user group | A chosen user group was deleted while the dialog was open. |
| 409 | Account exists | Another user took the username or email a moment earlier. |
| 400 | Invalid email address | Umbraco refuses the directory's email address. |
| 400 | The user could not be created, The member could not be created | A notification handler cancelled the save; the site log may say why. |

## Management API

The dialog calls endpoints under `/umbraco/management/api/v1/directory` with the backoffice's
bearer token. The user endpoints need access to the Users section and the member endpoints to the
Members section, as Umbraco's own user and member endpoints do.

| Endpoint | Body or query | Answer |
|---|---|---|
| `GET users/search`, `GET members/search` | `query`, `take` | `results` (`sid`, `accountName`, `userName`, `displayName`, `email`, `department`, `title`, `enabled`) and `existing`, the accounts of that kind keyed by SID (`kind`, `key`, `username`, `state`: `active`, `disabled` or `retired`) |
| `POST users` | `sid`, `userGroupKeys` | 201 with `key`, `username`, `name`, `email` |
| `POST members` | `sid`, `memberTypeAlias` (empty means `Directory:MemberTypeAlias`), `memberGroupNames` | 201 as for users |

Every source applies the same request rules, so a request is refused with the same message
whichever source is in use:

- A search splits on spaces and commas into at most eight terms and 256 characters, and needs at
  least one term of two characters or more.
- A search returns at most 50 people, and 20 when `take` is left out.
- A lookup accepts a SID only, and the site refuses anything else before it asks a directory.

A refused search answers 400 with the title "The directory refused the search" and the reason. A
directory the site cannot reach answers 503 with the title "The directory is not reachable", and
the site logs it as an error. The dialog shows both.

## Sources

**Active Directory.** The site calls `IDirectoryLookup.SearchUsersAsync` with global scope, so
searches cover `UserRootGlobal`, and leaves out disabled accounts before the result limit. A
registered `IDirectoryUserFilter` applies to these searches.

**Identity server.** The site sends two requests to `IdentityServerAuthority`, each with
`Accept: application/json` and the API key when one is set:

| Request | Expected answer |
|---|---|
| `GET {authority}/directory/users?query=test%20user&take=20&scope=global&includeDisabled=true` | 200 with a JSON array of people in camelCase: `sid`, `guid`, `accountName`, `userName`, `displayName`, `email`, `title`, `department`, `enabled` and the other `DirectoryUser` fields. |
| `GET {authority}/directory/users/S-1-5-21-...` | 200 with one person and their `groups`, or 404 when nobody has the SID. |

The search asks for disabled people too, so the server's own listing rules leave in everyone an
administrator may need; the site then keeps the enabled ones. The site maps the answers as
follows:

| Answer | Result |
|---|---|
| 200 | The people, or the person with their groups |
| 404 on a lookup | Nobody has the SID |
| 400 | A refused search, with the problem's `detail` as the reason |
| 401 or 403 | An unreachable directory, with a message that names `Directory:IdentityServerApiKey` |
| 404 on a search | An unreachable directory: the server serves no directory API |
| Any other status, an unreachable server, a timeout or an unreadable body | An unreachable directory |

An empty or relative `IdentityServerAuthority` is reported as an unreachable directory at the
first search. The identity server host provides this API; the `AurorisNet.IdentityServer` library
on its own provides sign-in only.

**Local accounts.** The site enumerates the computer's user accounts through the WinNT provider.
A search term matches the account name, full name or description, ignoring case. The display
name is the full name, or `COMPUTER\user` when it has none, and a lookup's groups are the local
groups. Local accounts carry no email, department or title.

## Extending

A site registers its own services to change what the package does, before or after
`AddAccountDirectory()`. The package adds each default only when nothing is registered yet, and
a later registration replaces it.

| Service | Default | Replace it to |
|---|---|---|
| `IRetiredAccountPolicy` | `NoRetiredAccounts`: every existing account is active or disabled | Report accounts the site keeps after deletion as retired, with advice on bringing the person back. |
| `ISignInLinkPolicy` | `DefaultSignInLinkPolicy`: links only under the configured providers, never by name | Name the providers an earlier sign-in used, and say whether the first sign-in claims an account by its username or email. |
| `IAccountDirectory` | chosen by `Directory:Source` | Search another directory. |
| `IDirectoryLookup` | `ActiveDirectoryLookup` | Query Active Directory differently. |

## Developing

The source is in `src`: the settings and source choice, the three directories, the account
service and its management API controller, the two policies with their defaults, the composer,
and the backoffice extension in `src/wwwroot/App_Plugins/DirectoryAccounts`.

Requirements:

- Windows, for the local account tests and the Active Directory code.
- The .NET 10 SDK. `global.json` keeps every build on a .NET 10 SDK and selects the Microsoft
  Testing Platform runner.
- A checkout of the IdentityServer repository beside this one, at `..\IdentityServer`, while the
  build uses the people lookup from source.

By default the project builds the people lookup from that checkout:

| Property | Default | Effect |
|---|---|---|
| `UseIdentityServerSource` | `true` | `true` references `$(IdentityServerRoot)\IdentityServer.Directory\IdentityServer.Directory.csproj`; `false` references the `AurorisNet.IdentityServer.Directory` package at the version in `Directory.Packages.props`. |
| `IdentityServerRoot` | `..\IdentityServer` beside this repository | The checkout to build from, such as `-p:IdentityServerRoot=D:\work\IdentityServer` in a worktree. |

Build, test and pack:

```powershell
dotnet build -c Release
dotnet test --project tests/AurorisNet.Umbraco.IdentityServer.Directory.Tests -c Release
dotnet pack src/AurorisNet.Umbraco.IdentityServer.Directory.csproj -c Release -o artifacts/packages
```

The tests boot one Umbraco site with SQLite in a temporary folder and a fake people lookup, which
starts in about twenty seconds, and need nothing else. The local account tests run on a computer
outside a domain and are skipped elsewhere. A local package is numbered `0.0.0-local`, so NuGet's
cache keeps it apart from published versions; it contains `DirectoryAccounts.dll` with embedded
symbols and XML documentation, the backoffice extension, this README, the icon and the licence.

The package compiles against `UmbracoMinimumVersion` in `Directory.Build.props` and allows any
Umbraco 18 release from there; the tests build against `UmbracoVersion`, the newest release they
have run on. Add `-p:UmbracoVersion=18.0.0` to test another release. A minor Umbraco release
needs a new package version only when it changes `ManagementApiControllerBase`, the
section-access policies, `IUserService.CreateAsync`, `IMemberService.CreateMemberWithIdentity`,
`IExternalLoginWithKeyService`, `IAuditService.AddAsync`, or the backoffice's `collectionAction`
and `modal` extension types.

Keep the build free of warnings: the release workflow builds with `ContinuousIntegrationBuild`,
which turns warnings into errors, except NuGet's vulnerability reports. `docs/images/icon.svg` is
the source of `icon.png`; render it at 128 by 128 pixels after changing it.

### Publish a release

Releases use semantic versions: a fix raises the patch number, a new setting or behavior the
minor number, and a change a site must act on, such as a renamed setting or a new Umbraco major
version, the major number.

Run **Actions → Release → Run workflow** on `main` with the version, such as `1.0.1`. The
workflow checks the version, runs the tests, packs against the published people lookup, reserves
the tag `v<version>`, pushes the package to NuGet with trusted publishing as `AurorisNet`, and
creates the GitHub release with the package attached. `dry-run` stops after packing and keeps the
package as an artifact for seven days. A failed run can be started again with the same version.

A pack from source would depend on a lookup version nobody published, so a
`ContinuousIntegrationBuild` pack stops with an error unless `UseIdentityServerSource` is
`false`. Publishing therefore waits until `AurorisNet.IdentityServer.Directory` is on nuget.org at
the version `Directory.Packages.props` names. Trusted publishing needs a policy on nuget.org for
the `AurorisNet` account that names this repository and `release.yml`.

## Links

- [Source and issues](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory)
- [Security policy](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/SECURITY.md):
  reporting a vulnerability, and the access the package grants by design.
