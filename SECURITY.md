# Security

## Report a vulnerability

Report a vulnerability in private, with
[GitHub's vulnerability report form](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory/security/advisories/new).
You also reach it from this repository's **Security** tab, under **Report a vulnerability**.
Please keep the details out of public issues until a fix is published.

A report is easier to act on when it includes:

- the package version and the site's Umbraco release;
- the `Directory` settings involved, without real API keys;
- the steps that show the problem, and what someone gains by it.

## Versions that get fixes

A fix is published as a new package version. Earlier versions stay as they are, so install
the newest version.

## What the package covers

The package lets backoffice users create accounts for people in the directory, so some
access is by design:

- A backoffice user with access to the Users section can search the directory and create a
  backoffice user for anyone in it, in the user groups Umbraco lets that user assign. A user
  with access to the Members section can do the same for members.
- Each search and lookup runs as the site: as its process account against Active Directory
  or local accounts, and with `Directory:IdentityServerApiKey` or the process account
  against an identity server. Those credentials decide what the directory shows.
- Anyone who can write the site's configuration chooses the directory, the API key and the
  login providers that new accounts are linked under.
- `Directory:IdentityServerAcceptAnyServerCertificate` turns off certificate validation for
  the requests to the identity server.

A vulnerability is anything beyond that. Examples are a search or an account creation by
someone without access to the section, an account linked to a SID other than the one the
directory returned for the chosen person, a name or email taken from the request when the
directory holds another, or an API key written to a log or a response. For a vulnerability in Umbraco itself, follow
[Umbraco's security policy](https://github.com/umbraco/Umbraco-CMS/security/policy).
