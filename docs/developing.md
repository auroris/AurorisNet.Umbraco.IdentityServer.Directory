# Account rules, extending, and developing

## Source selection (`Directory:Source = Auto`)

| `UseLocalAccounts` | `IdentityServerAuthority` | Machine | Selected source |
|---|---|---|---|
| `true` | any | any | Local accounts |
| unset / `false` | set | any | IdentityServer API |
| unset | empty | off-domain | Local accounts |
| unset | empty | domain-joined | Active Directory |
| `false` | empty | any | Active Directory |

On Linux/macOS, set `IdentityServerAuthority` (AD and local accounts throw `PlatformNotSupportedException`).

## Account creation and validation

Created accounts are approved immediately with:
- **Username:** lowercased Windows account name without domain (or email when `Umbraco:CMS:Security:UsernameIsEmail` is `true`).
- **Name / Email:** directory display name and email (fallback `username@localhost`).
- **External login:** Windows SID under `BackOfficeLoginProvider` or `MemberLoginProvider`.
- **Password (members):** random password unless a notification handler sets one.

The server re-reads the person from the directory by SID inside one Umbraco scope, verifying the account is enabled, not already linked/retired, and that username/email are free (non-admin users can only assign groups they belong to).

| Status | Title | Cause |
|---|---|---|
| 400 | No person chosen / Choose a user group / Unknown member type or group / Unknown user group | Invalid SID, missing user group, or unknown type/group. |
| 400 | Disabled in the directory / Invalid email address / Could not be created / Directory refused search | Disabled AD account, Umbraco validation failure, or rejected directory query. |
| 403 | Not allowed | Caller lacks membership in a selected user group. |
| 404 | Not in the directory | SID no longer exists in the directory. |
| 409 | Already has an account / Retired account / Username or Email taken / Account exists | Duplicate SID, retired account, or conflicting username/email. |
| 503 | The directory is not reachable | Directory connectivity failure. |

## Extending

Register replacements before or after `AddAccountDirectory()`:

- `IRetiredAccountPolicy` — detect retired accounts and customize restore advice (default: `NoRetiredAccounts`).
- `ISignInLinkPolicy` — add legacy login providers or enable name-based claiming on first sign-in (default: `DefaultSignInLinkPolicy`).
- `IAccountDirectory` — replace directory search entirely.
- `IDirectoryLookup` — replace Active Directory lookup (default: `ActiveDirectoryLookup`).

## Developing

Requires the .NET 10 SDK (`global.json`) and a sibling `../AurorisNet.IdentityServer` checkout (or `-p:UseIdentityServerSource=false` to use NuGet). Release packs require `-p:UseIdentityServerSource=false`.

```powershell
dotnet build -c Release
dotnet test --project tests/AurorisNet.Umbraco.IdentityServer.Directory.Tests -c Release
dotnet test --project tests/AurorisNet.Umbraco.IdentityServer.Directory.Tests -c Release -p:UmbracoVersion=18.0.0
dotnet pack src/AurorisNet.Umbraco.IdentityServer.Directory.csproj -c Release -o artifacts/packages
```

MSBuild properties: `UseIdentityServerSource` (default `true`) and `IdentityServerRoot` (default `../AurorisNet.IdentityServer`).

Umbraco API touchpoints to check if a new release fails tests: `ManagementApiControllerBase`, section-access policies, `IUserService.CreateAsync`, `IMemberService.CreateMemberWithIdentity`, `IExternalLoginWithKeyService`, `IAuditService.AddAsync`, and backoffice `collectionAction`/`modal` extensions.

## Releases

See the shared [release guide](https://github.com/auroris/AurorisNet.Build/blob/main/docs/releasing.md).
