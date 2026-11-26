# Add from Directory

Adds an **Add from directory** dialog to Umbraco 18 **Users** and **Members** so administrators can search Active Directory, local Windows accounts, or an IdentityServer directory API and create approved accounts pre-linked to each person's Windows SID.

![MIT license](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/license-mit.svg)
![.NET 10](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/dotnet-10.svg)
![Umbraco 18](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/umbraco-18.svg)

> **Requires SSO for sign-in:** This package only creates SID-linked accounts—it does not provide single sign-on. Pair it with [`AurorisNet.Umbraco.IdentityServer.SSO`](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.SSO) (or another SID-based external login provider) and match `BackOfficeLoginProvider` / `MemberLoginProvider` to its scheme names. Active Directory and local account lookups require Windows; the HTTP IdentityServer API source works cross-platform.

## Install

```powershell
dotnet add package AurorisNet.Umbraco.IdentityServer.Directory
```

Registers automatically via a composer (or call `builder.AddAccountDirectory()` manually). `AurorisNet.IdentityServer.Directory` and `AurorisNet.ActiveDirectory` install automatically as dependencies.

Configure the `Directory` section in `appsettings.json`:

```json
"Directory": {
  "IdentityServerAuthority": "https://login.example.com/IdentityServer",
  "IdentityServerApiKey": "your-directory-api-key",
  "BackOfficeLoginProvider": "Umbraco.IdentityServer",
  "MemberLoginProvider": "UmbracoMembers.IdentityServer"
}
```

Omit `IdentityServerAuthority` and `IdentityServerApiKey` on a domain-joined Windows server to query Active Directory directly as the process account.

## Settings

| Setting | Default | Effect |
|---|---|---|
| `Source` | `Auto` | `ActiveDirectory`, `IdentityServer`, `LocalAccounts`, or `Auto` (selects IdentityServer if `IdentityServerAuthority` is set, else AD on domain-joined Windows or local accounts off-domain). |
| `UseLocalAccounts` | unset | Overrides `Auto` to force (`true`) or exclude (`false`) local Windows accounts. |
| `IdentityServerAuthority` | empty | Base URL of an external IdentityServer directory API. |
| `IdentityServerApiKey` | empty | `X-Api-Key` header for the directory API (empty uses Windows auth). |
| `IdentityServerAcceptAnyServerCertificate` | `false` | Skips TLS validation (**isolated intranets only**). |
| `BackOfficeLoginProvider` | `Umbraco.IdentityServer` | External login provider scheme stored with new users' SIDs. |
| `MemberLoginProvider` | `UmbracoMembers.IdentityServer` | External login provider scheme stored with new members' SIDs. |
| `MemberTypeAlias` | `Member` | Default member type alias. |
| `UserRootGlobal` | empty | Optional LDAP search root (e.g. `LDAP://OU=People,DC=EXAMPLE,DC=COM`). |

Additional lookup settings (`DefaultTake`, `MaxTake`, etc.) are documented in [`AurorisNet.IdentityServer.Directory`](https://github.com/auroris/AurorisNet.IdentityServer/blob/main/IdentityServer.Directory/README.md).

## Supported versions

- Umbraco 18: <!-- umbraco-releases 18 -->18.0.0 to 18.3.2<!-- /umbraco-releases --> (.NET 10)

## Documentation

- [Management API and directory sources](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/docs/REFERENCE.md)
- [Account rules, extending, developing, and releases](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/docs/developing.md)
- [Security policy](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/SECURITY.md)

## License

[MIT](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/LICENSE)
