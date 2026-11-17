<h1 align="center">Umbraco Add from Directory</h1>

<p align="center">
  Create Umbraco backoffice users and members from the directory, linked to each person's Windows SID before they first sign in.
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT license" src="docs/images/badge-license.svg"></a>
  <img alt=".NET 10" src="docs/images/badge-dotnet-10.svg">
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/AurorisNet.Umbraco.IdentityServer.Directory">NuGet package</a> ·
  <a href="#settings">Settings</a> ·
  <a href="docs/BEHAVIOR.md">Behavior</a>
</p>

## What it does

`AurorisNet.Umbraco.IdentityServer.Directory` adds an **Add from directory** button to the
Users and Members sections of an Umbraco 18 backoffice. An administrator searches the
directory, chooses a person and their groups, and the site creates an approved account that
carries the person's Windows SID as an external login. The person's first single sign-on
finds the account by that SID, so usernames and email addresses never have to match.

The package reads people from one of three sources:

- **Active Directory**, queried by the site as its process account through the
  [`AurorisNet.IdentityServer.Directory`](https://www.nuget.org/packages/AurorisNet.IdentityServer.Directory)
  people lookup.
- **An identity server's directory API**, for a site that signs in through an external
  identity server; see [The identity server's directory API](#the-identity-servers-directory-api).
- **The computer's local accounts**, for a development machine outside a domain.

The package runs on Windows: Active Directory and local accounts are read through
`System.DirectoryServices`. It registers itself through Umbraco's composers.

## The dialog

1. In **Users** or **Members**, select **Add from directory**.
2. Type at least two characters of a name, username or email. The dialog lists the enabled
   people found, with their Windows account name, email and title or department, and marks
   those who already have an account of that kind as active, disabled or retired.
3. Choose a person, then their user groups (at least one), or their member type (when the site
   has more than one) and member groups.
4. Select **Create user** or **Create member**. The list shows the new account and a
   notification confirms it.

The account is approved and filled in from the directory:

| Field | Value |
|---|---|
| Username | The Windows account name without its domain, in lower case: `DOMAIN\Test.User` becomes `test.user`. A site with `Umbraco:CMS:Security:UsernameIsEmail` gets the email instead. |
| Name | The directory's display name. |
| Email | The directory's email, or `username@localhost` when it has none. |
| External login | The SID, under `Directory:BackOfficeLoginProvider` or `Directory:MemberLoginProvider`. |

The browser sends only the person's SID; the site reads everything else from the directory
again. When the person already has an account of that kind, or another account holds the
username or email, the dialog explains the conflict and what to do, and creates nothing. The
audit log records each creation.

## Quick start

1. Add the package to a website running Umbraco 18 on Windows:

   ```xml
   <PackageReference Include="AurorisNet.Umbraco.IdentityServer.Directory" Version="1.*" />
   ```

2. Set the login providers to the scheme names of the site's single sign-on, so the first
   sign-in finds the SID. For a site that signs in through an external identity server, also
   set its address and key:

   ```json
   "Directory": {
     "IdentityServerAuthority": "https://login.example.com/IdentityServer",
     "IdentityServerApiKey": "one of the server's directory API keys",
     "BackOfficeLoginProvider": "Umbraco.IdentityServer",
     "MemberLoginProvider": "UmbracoMembers.IdentityServer"
   }
   ```

3. Start the site. The log names the source in use, such as
   `Accounts are created from Active Directory, queried as DOMAIN\AppPool across the whole domain.`

## Settings

The package reads the `Directory` section, which also holds the people lookup's
[LDAP roots and result limits](https://www.nuget.org/packages/AurorisNet.IdentityServer.Directory)
(`UserRootGlobal`, `UserRoot`, `GlobalSearchMinLength`, `DefaultTake`, `MaxTake` and others).
Active Directory searches use `UserRootGlobal`.

| Setting | Default | Effect |
|---|---|---|
| `Directory:Source` | `Auto` | `ActiveDirectory`, `IdentityServer` or `LocalAccounts` selects that source. `Auto` chooses local accounts when `UseLocalAccounts` is `true`, the identity server when `IdentityServerAuthority` is set, local accounts when the computer is outside a domain and `UseLocalAccounts` is unset, and Active Directory otherwise. |
| `Directory:UseLocalAccounts` | unset | With `Auto`, `true` selects local accounts and `false` keeps them out of the choice. |
| `Directory:IdentityServerAuthority` | empty | The identity server whose directory API the site searches, such as `https://login.example.com/IdentityServer`. |
| `Directory:IdentityServerApiKey` | empty | Sent in the `X-Api-Key` header. Empty authenticates as the site's Windows identity, the application pool account in IIS. |
| `Directory:IdentityServerAcceptAnyServerCertificate` | `false` | Accepts any certificate from the identity server. For an isolated intranet with a self-signed certificate. |
| `Directory:BackOfficeLoginProvider` | `Umbraco.IdentityServer` | The external-login provider a new user's SID is stored under: the scheme name of the site's backoffice sign-in. |
| `Directory:MemberLoginProvider` | `UmbracoMembers.IdentityServer` | The same for members: the scheme name of the site's member sign-in. |
| `Directory:MemberTypeAlias` | `Member` | The member type of a member created without one. |

Each source runs as the site. Against Active Directory the application pool identity needs read
access under `UserRootGlobal`; `ApplicationPoolIdentity` has it on a domain-joined server.

### Settings an UmbracoAppHost site receives

UmbracoAppHost fills these settings from its own sections, so its sites configure nothing new:

| `Directory` setting | Comes from |
|---|---|
| `IdentityServerAuthority` | `IdentityServer:Authority` (empty for the embedded identity server) |
| `IdentityServerApiKey` | `IdentityServer:DirectoryApiKey` |
| `IdentityServerAcceptAnyServerCertificate` | `IdentityServer:AcceptAnyServerCertificate` |
| `BackOfficeLoginProvider`, `MemberLoginProvider` | The IdentityServer sign-in's scheme names, `Umbraco.<Name>` and `UmbracoMembers.<Name>` |
| `MemberTypeAlias` | `Accounts:MemberTypeAlias` |

A value set in the `Directory` section itself takes precedence.

## The identity server's directory API

With the identity server source, the site sends two requests to `IdentityServerAuthority`,
each with `Accept: application/json` and the API key when one is set:

| Request | Expected answer |
|---|---|
| `GET {authority}/directory/users?query=test%20user&take=20&scope=global&includeDisabled=true` | 200 with a JSON array of people, camelCase: `sid`, `guid`, `accountName`, `userName`, `displayName`, `email`, `title`, `department`, `enabled` and the other `DirectoryUser` fields. The site keeps the enabled ones. |
| `GET {authority}/directory/users/S-1-5-21-...` | 200 with one person and their `groups`, or 404 when nobody has the SID. |

A 400 problem-details answer shows its `detail` to the administrator. A 401 or 403, a 404 on
the search, any other failure and an unreachable server show as an unavailable directory. The
identity server host provides this API; the `AurorisNet.IdentityServer` library on its own
provides sign-in only.

## Extending

A site registers its own services to change what the package does. Each default is added only
when nothing is registered yet, and a later registration replaces it.

| Service | Default | Replace it to |
|---|---|---|
| `IRetiredAccountPolicy` | `NoRetiredAccounts` | Report accounts the site keeps after deletion as retired, with advice on bringing the person back. |
| `ISignInLinkPolicy` | `DefaultSignInLinkPolicy` | Find SIDs linked under providers an earlier sign-in used, and say whether the first sign-in claims an account by its name. |
| `IAccountDirectory` | chosen by `Directory:Source` | Search another directory. |
| `IDirectoryLookup` | `ActiveDirectoryLookup` | Query Active Directory differently. |

## Documentation

| Guide | Read it for |
|---|---|
| [Behavior](docs/BEHAVIOR.md) | The management API, the account rules and conflicts, and test coverage. |
| [Developing](docs/DEVELOPING.md) | Building, testing and publishing a release. |
| [Security policy](SECURITY.md) | Reporting a vulnerability. |

## License

[MIT](LICENSE)
