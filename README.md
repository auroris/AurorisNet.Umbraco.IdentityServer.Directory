# Umbraco Add from Directory

> **Warning: additional packages and configuration required.** This package is part of a
> larger directory and single sign-on implementation. It requires:
>
> - A supported Umbraco 18 website running on .NET 10.
> - [`AurorisNet.IdentityServer.Directory`](https://github.com/auroris/AurorisNet.IdentityServer/blob/main/IdentityServer.Directory/README.md)
>   and `AurorisNet.ActiveDirectory`, installed automatically as NuGet dependencies.
> - A configured directory source: Active Directory with read access for the site's Windows
>   identity, local Windows accounts for development, or an external identity server serving
>   the [directory API](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/docs/REFERENCE.md#identity-server)
>   with its address and authentication configured.
> - For the created accounts to sign in, install and configure
>   [`AurorisNet.Umbraco.IdentityServer.SSO`](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.SSO)
>   or another provider that uses Windows SIDs, and match the `BackOfficeLoginProvider` and
>   `MemberLoginProvider` settings to that provider's scheme names.
>
> Installing this package alone does not configure directory access or provide single sign-on.

Create Umbraco backoffice users and members from the directory, linked to each person's
Windows SID before they first sign in.

![MIT license](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/license-mit.svg)
![.NET 10](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/dotnet-10.svg)
![Umbraco 18](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/umbraco-18.svg)

The package adds an **Add from directory** button to the Users and Members sections of the
backoffice. An administrator searches the directory and chooses a person and their groups.
The site creates an approved user or member that carries the person's SID as an external
login. The person's first single sign-on finds the account by that SID, so its username and
email can differ from the ones the sign-in reports.

The site reads people from one of three sources:

- **Active Directory**, which the site queries as its process account through the people
  lookup of [`AurorisNet.IdentityServer.Directory`](https://github.com/auroris/AurorisNet.IdentityServer).
- **An identity server's directory API**, for a site that signs in through an external
  identity server.
- **The computer's local accounts**, for a development machine outside a domain.

Active Directory and local accounts are read through `System.DirectoryServices` and need
Windows. The identity server source reads people over HTTP.

## Install

1. Add the package to a website that runs a supported Umbraco release:

   ```powershell
   dotnet add package AurorisNet.Umbraco.IdentityServer.Directory
   ```

2. Set the two login providers to the scheme names of the site's single sign-on, so the
   first sign-in finds the SID. For a site that signs in through an external identity
   server, also set that server's address and key:

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

The package registers itself through an Umbraco composer. A site that builds its services
by hand calls `builder.AddAccountDirectory()`; a second call changes nothing.

## Settings

The package reads the `Directory` section.

| Setting | Default | What it does |
|---|---|---|
| `Source` | `Auto` | `ActiveDirectory`, `IdentityServer` or `LocalAccounts` selects that source. `Auto` chooses one from the other settings and the computer, as the next table shows. |
| `UseLocalAccounts` | unset | With `Auto`, `true` selects local accounts and `false` keeps them out of the choice. |
| `IdentityServerAuthority` | empty | The identity server whose directory API the site searches, such as `https://login.example.com/IdentityServer`. |
| `IdentityServerApiKey` | empty | The key the site sends in the `X-Api-Key` header. When it is empty, the site authenticates as its Windows identity, which is the application pool account in IIS, and the server must admit that account. |
| `IdentityServerAcceptAnyServerCertificate` | `false` | Accepts any certificate from the identity server. **Use it only on an isolated intranet with a self-signed certificate.** |
| `BackOfficeLoginProvider` | `Umbraco.IdentityServer` | The external-login provider a new user's SID is stored under: the scheme name of the site's backoffice sign-in. |
| `MemberLoginProvider` | `UmbracoMembers.IdentityServer` | The same for members: the scheme name of the site's member sign-in. |
| `MemberTypeAlias` | `Member` | The member type of a member created without one. |
| `UserRootGlobal` | empty | The LDAP path that Active Directory searches and lookups start from, such as `LDAP://OU=People,DC=EXAMPLE,DC=COM`. Empty covers the whole domain. |

An empty login provider or member type means its default. The section also holds the other
settings of the people lookup, which its
[README](https://github.com/auroris/AurorisNet.IdentityServer/blob/main/IdentityServer.Directory/README.md)
describes.

### How the site chooses the source

The site chooses the source from the settings, at the first search, and contacts no
directory to do so. A directory that is down or misconfigured leaves the site running, and
the search reports the problem.

| `Source` | `UseLocalAccounts` | `IdentityServerAuthority` | Computer | Source in use |
|---|---|---|---|---|
| `ActiveDirectory`, `IdentityServer` or `LocalAccounts` | any | any | any | That source |
| `Auto` | `true` | any | any | Local accounts |
| `Auto` | unset or `false` | set | any | Identity server |
| `Auto` | unset | empty | outside a domain | Local accounts |
| `Auto` | unset | empty | in a domain | Active Directory |
| `Auto` | `false` | empty | any | Active Directory |

A computer is outside a domain when the domain of the site's process account is the
computer's own name. On Linux or macOS, set `IdentityServerAuthority`: Active Directory and
local accounts fail every search there with `PlatformNotSupportedException`.

Each source runs as the site. For Active Directory, the application pool identity needs
read access under `UserRootGlobal`.

## The dialog

1. In **Users** or **Members**, select **Add from directory**.
2. Type at least two characters of a name, username or email. The dialog lists up to 20
   enabled people, each with their Windows account name, email and title or department. A
   tag marks a person who already has an account of that kind: **Has an account**,
   **Account disabled** or **Account retired**.
3. Choose a person. For a user, choose at least one user group. For a member, choose the
   member type, when the site has more than one, and any member groups.
4. Select **Create user** or **Create member**. The list shows the new account, and a
   notification confirms it.

The site creates the account approved, with these values:

| Field | Value |
|---|---|
| Username | The Windows account name without its domain, in lower case: `DOMAIN\Test.User` becomes `test.user`. A user on a site with `Umbraco:CMS:Security:UsernameIsEmail` gets the email as username. |
| Name | The directory's display name. |
| Email | The directory's email, or `username@localhost` when it has none. |
| External login | The SID, under `BackOfficeLoginProvider` or `MemberLoginProvider`. |
| Password (members) | A random password. A password that a member-creation notification handler has set stays. |

The browser sends the person's SID and the chosen groups. The site reads the name, email and
account state from the directory again, so a changed request cannot give an account someone
else's identity.

## Account rules

The site creates the account, its SID link and an audit entry in one Umbraco scope, after
these checks:

1. The request names a SID, such as `S-1-5-21-...`. A user has at least one user group. A
   member's type and groups exist.
2. The directory has an account with the SID, and the account is enabled there.
3. The person has no account of that kind yet. An account is the person's when it is linked
   to the SID under the configured login provider or one of
   `ISignInLinkPolicy.PreviousLoginProviders`, or when its username is the SID and
   `IRetiredAccountPolicy.IsRetired` reports it as retired.
4. No other account holds the username or the email. An account that holds one of them and
   is linked to this SID alone, under any provider, counts as the person's own.
5. Umbraco accepts the new user or member. A user who is not an administrator can give a
   new user only the groups they belong to themselves.

The audit entry reads `Created from the directory for DOMAIN\user, linked to its Windows identity S-1-5-21-....`
When a check fails, the site creates nothing, and the dialog shows the problem's title and
advice:

| Status | Title | Cause |
|---|---|---|
| 400 | No person chosen | The request carries no valid SID. |
| 400 | Choose a user group | A user was requested without a user group. |
| 400 | Unknown member type, Unknown member group | The member type or a member group does not exist. |
| 404 | Not in the directory | The directory has no account with the SID. It may have been deleted since the search. |
| 400 | Disabled in the directory | The account is disabled in the directory, so nobody can sign in with it. |
| 409 | Already has an account | The person has an account of that kind, linked to their Windows identity. For a disabled one, the advice is to enable it in Users or Members. |
| 409 | Retired account | A retired account holds the person's SID. The advice is `IRetiredAccountPolicy.RestoreAdvice`. |
| 409 | Username taken, Email address taken | Another account holds the name. When that account is retired or linked to another identity, the advice is to rename or delete it. When it is unlinked and `ISignInLinkPolicy.FirstSignInLinksByName` is true, the advice is that the person can sign in and claim it. |
| 403 | Not allowed | A user who is not an administrator chose a group they do not belong to. |
| 400 | Unknown user group | A chosen user group was deleted while the dialog was open. |
| 409 | Account exists | Another user took the username or email a moment earlier. |
| 400 | Invalid email address | Umbraco refuses the directory's email address. |
| 400 | The user could not be created, The member could not be created | Umbraco refused the account, or a notification handler cancelled the save. The site log may say why. |
| 400 | The directory refused the search | The directory refused the request. The detail gives the reason. |
| 503 | The directory is not reachable | The site could not reach the directory. The site logs it as an error. |

## Extending

A site changes what the package does by registering its own services, before or after
`AddAccountDirectory()`. The package adds each default only when nothing is registered yet,
and a later registration replaces it.

| Service | Default | Replace it to |
|---|---|---|
| `IRetiredAccountPolicy` | `NoRetiredAccounts`: every existing account is active or disabled | Report the accounts a site keeps after deletion as retired, with advice on how to bring the person back. |
| `ISignInLinkPolicy` | `DefaultSignInLinkPolicy`: links under the configured providers only, and never by name | Name the providers an earlier sign-in used, and say whether the first sign-in claims an account by its username or email. |
| `IAccountDirectory` | Chosen by `Directory:Source` | Search another directory. |
| `IDirectoryLookup` | `ActiveDirectoryLookup` | Query Active Directory in another way. |

## Supported versions

| Umbraco | Supported releases | Runtime |
|---|---|---|
| 18 | <!-- umbraco-releases 18 -->18.0.0 to 18.3.2<!-- /umbraco-releases --> | .NET 10 |

The package accepts every release in these ranges, and the tests run on the first and the
newest release of each major version.

## Documentation

| Page | Covers |
|---|---|
| [Management API and sources](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/docs/REFERENCE.md) | The endpoints behind the dialog, the search rules, and the requests each source makes, including the directory API an identity server must serve. |
| [Security policy](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/SECURITY.md) | Reporting a vulnerability, and the access the package grants by design. |

## Developing

Building needs the .NET 10 SDK, which `global.json` selects, and a checkout of
[AurorisNet.IdentityServer](https://github.com/auroris/AurorisNet.IdentityServer) beside this
repository, at `../AurorisNet.IdentityServer`. The local account tests need Windows.

```powershell
dotnet build -c Release
# The tests on the newest supported Umbraco release
dotnet test --project tests/AurorisNet.Umbraco.IdentityServer.Directory.Tests -c Release
# The tests on another release
dotnet test --project tests/AurorisNet.Umbraco.IdentityServer.Directory.Tests -c Release -p:UmbracoVersion=18.0.0
# A package, numbered 0.0.0-local
dotnet pack src/AurorisNet.Umbraco.IdentityServer.Directory.csproj -c Release -o artifacts/packages
```

The full test matrix is the test command run once for each release in `UmbracoReleases` in
`Directory.Build.props`. It runs on a developer's machine. The workflows test one release.

Two properties of `src/AurorisNet.Umbraco.IdentityServer.Directory.csproj` choose where
`AurorisNet.IdentityServer.Directory` comes from:

| Property | Default | What it does |
|---|---|---|
| `UseIdentityServerSource` | `true` | `true` builds `IdentityServer.Directory/IdentityServer.Directory.csproj` from the checkout. `false` references the published package, at the version in `Directory.Packages.props`. |
| `IdentityServerRoot` | `../AurorisNet.IdentityServer` | The checkout to build from, such as `-p:IdentityServerRoot=D:\work\AurorisNet.IdentityServer`. |

The workflows build with `UseIdentityServerSource` set to `false`, and a release requires
it: a pack with `ContinuousIntegrationBuild` stops with an error otherwise. To build that
way locally, add `-p:UseIdentityServerSource=false`. The workflows also turn build warnings
into errors, so keep the build free of warnings.

The tests start one Umbraco site with SQLite in a temporary folder and a fake people
lookup. One local account test needs a computer outside a domain and is skipped elsewhere.

The package calls `ManagementApiControllerBase`, the section-access policies,
`IUserService.CreateAsync`, `IMemberService.CreateMemberWithIdentity`,
`IExternalLoginWithKeyService` and `IAuditService.AddAsync`, and the backoffice's
`collectionAction` and `modal` extension types. Look there first when a new Umbraco release
fails the tests.

## Releases

The package follows Umbraco and `AurorisNet.IdentityServer.Directory`. Each week,
`umbraco-releases.yml` looks on nuget.org for new Umbraco releases and tests the package on
each one. A release that passes is committed to `UmbracoReleases` in
`Directory.Build.props`, which also rewrites the ranges under **Supported versions**, and
`weekly-release.yml` then publishes a new version. A release that fails gets an issue.

Dependabot follows `AurorisNet.IdentityServer.Directory`. An update that passes `ci.yml` is
merged. One weekly release publishes the updates and the Umbraco releases of the week.

Versions are numbered MAJOR.MINOR.PATCH. A new Umbraco release raises the patch number, or
the minor number when it is a new Umbraco minor version. A person starts the first release
by hand, with a version, once `AurorisNet.IdentityServer.Directory` is on nuget.org at the
version in `Directory.Packages.props`. The
[release guide](https://github.com/auroris/AurorisNet.Build/blob/main/docs/releasing.md)
describes the workflows.

## License

[MIT](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/LICENSE).
