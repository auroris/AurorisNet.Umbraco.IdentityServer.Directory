# Behavior

The package adds the **Add from directory** dialog to the Users and Members collections, a
management API behind it, and the services that search the directory and create accounts. The
`DirectoryAccountsComposer` registers them through `.AddComposers()`; a site that composes
Umbraco by hand calls `builder.AddAccountDirectory()`. A second call does nothing.

## Choosing the source

`AccountDirectorySettings.Choose` reads the `Directory` section once, when the
`IAccountDirectory` service is first created, and never contacts a directory. A directory that
is down or misconfigured therefore leaves the site running; the first search reports it. After
Umbraco starts, the log names the source:

```text
Accounts are created from the directory API of the identity server at https://login.example.com/IdentityServer, with the key in Directory:IdentityServerApiKey.
```

| `Source` | `UseLocalAccounts` | `IdentityServerAuthority` | Computer | Source in use |
|---|---|---|---|---|
| `ActiveDirectory`, `IdentityServer` or `LocalAccounts` | any | any | any | That source |
| `Auto` | `true` | any | any | Local accounts |
| `Auto` | unset or `false` | set | any | Identity server |
| `Auto` | unset | empty | outside a domain | Local accounts |
| `Auto` | unset | empty | in a domain | Active Directory |
| `Auto` | `false` | empty | any | Active Directory |

Windows reports a computer outside a domain by giving the process account's domain the
computer's name. Active Directory and local accounts need Windows; on another platform the
first search fails with `PlatformNotSupportedException`.

## The sources

**Active Directory.** `ActiveDirectoryAccountDirectory` calls `IDirectoryLookup.SearchUsersAsync`
with global scope, so searches cover `UserRootGlobal`, and leaves out disabled accounts before
the result limit. A site's registered `IDirectoryUserFilter` applies to these searches.
`FindAsync` accepts a SID only, although the lookup also accepts a GUID or `DOMAIN\user`.

**Identity server.** `IdentityServerAccountDirectory` sends `GET directory/users` and
`GET directory/users/{sid}` under `IdentityServerAuthority`, with `includeDisabled=true` so the
server's listing rules, such as a telephone directory's, do not hide people an administrator
needs; the site drops disabled people itself. Answers map onto the directory exceptions:

| Answer | Result |
|---|---|
| 200 | The people, or the person with their groups |
| 404 on a lookup | Nobody has the SID |
| 400 | `DirectoryQueryException` with the problem's `detail` |
| 401 or 403 | `DirectoryUnavailableException` that names `Directory:IdentityServerApiKey` |
| 404 on a search | `DirectoryUnavailableException`: the server has no directory API |
| Other status, unreachable server, timeout, unreadable body | `DirectoryUnavailableException` |

An empty or relative `IdentityServerAuthority` is reported the same way at the first search.

**Local accounts.** `LocalAccountDirectory` enumerates the computer's user accounts through the
WinNT provider. A search term matches the account name, full name or description, ignoring case.
Local accounts have no email, department or title; the display name is the full name, or
`COMPUTER\user` when it has none, and a lookup's groups are the local groups.

## Request rules

Every source applies `AccountDirectoryRules`, so a request is refused with the same message
whichever source is in use:

- A search splits on spaces and commas into at most eight terms and 256 characters, and needs
  at least one term of two characters or more.
- A person is named by a SID of the form `S-1-5-21-...`; anything else is refused before a
  directory is asked.
- A search returns at most 50 people, 20 unless asked for another number.

## Management API

The endpoints are under `/umbraco/management/api/v1/directory` and take the backoffice's bearer
token. The user endpoints need access to the Users section and the member endpoints to the
Members section, as Umbraco's own user and member endpoints do.

| Endpoint | Body or query | Answer |
|---|---|---|
| `GET users/search`, `GET members/search` | `query`, `take` | `results` (`sid`, `accountName`, `userName`, `displayName`, `email`, `department`, `title`, `enabled`) and `existing`, the accounts of that kind by SID (`kind`, `key`, `username`, `state`: `active`, `disabled` or `retired`) |
| `POST users` | `sid`, `userGroupKeys` | 201 with `key`, `username`, `name`, `email` |
| `POST members` | `sid`, `memberTypeAlias` (empty means `Directory:MemberTypeAlias`), `memberGroupNames` | 201 as for users |

A `DirectoryQueryException` answers 400 with the title "The directory refused the search" and
the exception's message. A `DirectoryUnavailableException` answers 503 with the title "The
directory is not reachable" and is logged as an error.

## Creating an account

`DirectoryAccountService` checks the request, reads the person from the directory again by SID,
and creates the account in one Umbraco scope with its SID link and audit entry:

1. The SID must be valid; a user needs at least one user group; the member type and member
   groups must exist.
2. The directory must know the SID (404 "Not in the directory") and the account must be enabled
   there (400 "Disabled in the directory").
3. The person must not already have an account of the kind (409). An account is the person's
   when it is linked to the SID under `BackOfficeLoginProvider` or `MemberLoginProvider`, or
   under one of `ISignInLinkPolicy.PreviousLoginProviders`, or when its username is the SID and
   `IRetiredAccountPolicy.IsRetired` says it is retired.
4. No other account may hold the username or email (409). An account linked only to this SID
   under a provider no longer configured counts as the person's own. For an unlinked account,
   the advice depends on `ISignInLinkPolicy.FirstSignInLinksByName`.
5. Umbraco creates the user, approved and in the chosen groups, or the member, approved and
   with a random password that a member-creation handler may replace.
6. The SID is stored as an external login under the configured provider, and the audit log
   records "Created from the directory for DOMAIN\user, linked to its Windows identity S-1-5-21-...".

Umbraco's own user rules apply: a user who is not an administrator can give a new user only the
groups they belong to (403).

## Seams for the host

| Interface | Default | Used for |
|---|---|---|
| `IRetiredAccountPolicy` | `NoRetiredAccounts`: no account is retired | The `retired` state, and the advice in the "Retired account" conflict |
| `ISignInLinkPolicy` | `DefaultSignInLinkPolicy`: no earlier providers, no linking by name | Finding links from an earlier sign-in, and the advice when an unlinked account holds the name |

The defaults suit a site whose single sign-on links accounts only by SID under the configured
providers.

## Tests

`tests/AurorisNet.Umbraco.IdentityServer.Directory.Tests` covers:

- **`SiteTests`**: one Umbraco site with SQLite, composed through `.AddComposers()`, with
  `Directory:Source` set to `ActiveDirectory`, a fake `IDirectoryLookup`, and test policies.
  It signs in through the backoffice's login and token endpoints and checks: user and member
  creation with the SID link, audit entry, groups and member type; existing accounts reported
  as active, disabled and retired, and found under an earlier provider; username conflicts; the
  request checks; the 400 and 503 answers; section access; and that the backoffice extension is
  served at `/App_Plugins/DirectoryAccounts/` and registered.
- **`IdentityServerAccountDirectoryTests`**: the requests, key header, status mapping and
  certificate handling against a fake server.
- **`ActiveDirectoryAccountDirectoryTests`**: global scope, enabled people and SID-only lookups
  over the fake lookup.
- **`LocalAccountDirectoryTests`**: finds the account running the tests by name and by SID on
  a computer outside a domain, and applies the request rules.
- **`SettingsTests`**: defaults, the source rule, binding from the `Directory` section, the
  startup description and the default seams.
