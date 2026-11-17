# Umbraco Add from Directory

Adds **Add from directory** to the Users and Members sections of Umbraco 18. An administrator
searches the directory, chooses a person and their groups, and the site creates an approved
backoffice user or member that carries the person's Windows SID as an external login. The
person's first single sign-on finds the account by that SID.

## What it does

- Searches Active Directory as the site's process account, an identity server's directory API
  (`{authority}/directory/users`), or the computer's local accounts on a development machine
  outside a domain.
- Fills the account from the directory: the username is the account name without its domain,
  in lower case, the name is the display name, and the email is the directory's or
  `username@localhost`.
- Reports a person who already has an account, and a username or email another account holds,
  and creates nothing in those cases.
- Answers a refused search with 400 and an unreachable directory with 503, both shown in the
  dialog.

The package runs on Windows and registers itself through Umbraco's composers.

## Quick start

```xml
<PackageReference Include="AurorisNet.Umbraco.IdentityServer.Directory" Version="1.*" />
```

Set the login providers to the scheme names of the site's single sign-on, and for an external
identity server its address and key:

```json
"Directory": {
  "IdentityServerAuthority": "https://login.example.com/IdentityServer",
  "IdentityServerApiKey": "one of the server's directory API keys",
  "BackOfficeLoginProvider": "Umbraco.IdentityServer",
  "MemberLoginProvider": "UmbracoMembers.IdentityServer"
}
```

## Settings

All settings are in the `Directory` section, beside the people lookup's LDAP roots and limits.

- `Source` (default `Auto`): `ActiveDirectory`, `IdentityServer` or `LocalAccounts`. `Auto`
  chooses local accounts when `UseLocalAccounts` is `true`, the identity server when
  `IdentityServerAuthority` is set, local accounts on a computer outside a domain, and Active
  Directory otherwise.
- `UseLocalAccounts` (unset): with `Auto`, `true` selects local accounts and `false` keeps them
  out of the choice.
- `IdentityServerAuthority` (empty): the identity server whose directory API is searched.
- `IdentityServerApiKey` (empty): sent in `X-Api-Key`; empty authenticates as the site's Windows
  identity.
- `IdentityServerAcceptAnyServerCertificate` (`false`): accepts a self-signed certificate.
- `BackOfficeLoginProvider` (`Umbraco.IdentityServer`) and `MemberLoginProvider`
  (`UmbracoMembers.IdentityServer`): the external-login providers the SID is stored under.
- `MemberTypeAlias` (`Member`): the member type of a member created without one.

## Documentation

- [README](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory#readme)
- [Behavior](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/docs/BEHAVIOR.md)
- [Issue tracker](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/issues)

Licensed under the [MIT license](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/blob/main/LICENSE).
