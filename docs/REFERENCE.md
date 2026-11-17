# Management API and sources

This page lists the management API endpoints behind the **Add from directory** dialog, and
the requests each directory source makes. Use it to call the endpoints from other code, or
to serve the directory API that the identity server source reads. The
[README](../README.md) has the settings and the account rules.

## Management API

The dialog calls these endpoints under `/umbraco/management/api/v1/directory` with the
backoffice's bearer token. The user endpoints need access to the Users section and the
member endpoints access to the Members section.

| Endpoint | Query or body | Answer |
|---|---|---|
| `GET users/search`, `GET members/search` | `query`, `take` | `results`: the people, each with `sid`, `accountName`, `userName`, `displayName`, `email`, `department`, `title` and `enabled`. `existing`: the accounts of that kind some of them have, keyed by SID, each with `kind`, `key`, `username` and `state` (`active`, `disabled` or `retired`). |
| `POST users` | `sid`, `userGroupKeys` | 201 with `key`, `username`, `name` and `email` |
| `POST members` | `sid`, `memberTypeAlias` (empty means `Directory:MemberTypeAlias`), `memberGroupNames` | 201 with `key`, `username`, `name` and `email` |

A request that fails answers with a problem, whose title and status the README lists under
**Account rules**.

Every source applies the same request rules:

- A search holds at most 256 characters and splits on spaces and commas into at most eight
  terms. It needs at least one term, and each term has at least two characters.
- A search returns at most 50 people, and 20 when `take` is left out.
- A lookup accepts a SID only. The site refuses anything else before it asks a directory.

## Active Directory

The site calls `IDirectoryLookup.SearchUsersAsync` with global scope and without disabled
accounts, so a search covers `Directory:UserRootGlobal`. A registered `IDirectoryUserFilter`
applies to these searches.

## Identity server

The site sends two kinds of request to `Directory:IdentityServerAuthority`, each with
`Accept: application/json` and the API key when one is set:

| Request | Expected answer |
|---|---|
| `GET {authority}/directory/users?query=test%20user&take=20&scope=global&includeDisabled=true` | 200 with a JSON array of people in camelCase, with the fields of `DirectoryUser`: `sid`, `guid`, `accountName`, `userName`, `displayName`, `email`, `title`, `department`, `enabled` and others. |
| `GET {authority}/directory/users/S-1-5-21-...` | 200 with one person and their `groups`, or 404 when nobody has the SID. |

The search asks for disabled people as well, which lifts the server's own listing rules, and
the site then keeps the enabled people. The site reads the answers as follows:

| Answer | Result |
|---|---|
| 200 | The people, or the person with their groups |
| 404 on a lookup | Nobody has the SID |
| 400 | A refused search, with the problem's `detail` as the reason |
| 401 or 403 | An unreachable directory, with a message that names `Directory:IdentityServerApiKey` |
| 404 on a search | An unreachable directory: the server has no directory API |
| Any other status, an unreachable server, a timeout or an unreadable body | An unreachable directory |

An empty or relative `IdentityServerAuthority` is reported as an unreachable directory at
the first search.

## Local accounts

The site lists the computer's user accounts through the WinNT provider. Every search term
must appear in the account name, the full name or the description, ignoring case. The
display name is the full name, or `COMPUTER\user` when the account has none, and a lookup's
groups are the account's local groups. Local accounts have no email, department or title.
