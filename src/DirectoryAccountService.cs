using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;

namespace DirectoryAccounts;

/// <summary>Which kind of account the directory dialog works with.</summary>
public enum DirectoryAccountKind
{
    /// <summary>A backoffice user.</summary>
    User,

    /// <summary>A website member.</summary>
    Member,
}

/// <summary>A person found in the directory, with what the dialog shows about them.</summary>
/// <param name="Sid">The account's security identifier, which identifies the person in every later request.</param>
/// <param name="AccountName">The Windows account name, <c>DOMAIN\user</c>.</param>
/// <param name="UserName">The account name without its domain.</param>
/// <param name="DisplayName">The directory's display name.</param>
/// <param name="Email">The directory's email address, if any.</param>
/// <param name="Department">The directory's department, if any.</param>
/// <param name="Title">The directory's job title, if any.</param>
/// <param name="Enabled">Whether the account is enabled in the directory.</param>
public sealed record DirectoryPerson(
    string Sid,
    string AccountName,
    string UserName,
    string DisplayName,
    string? Email,
    string? Department,
    string? Title,
    bool Enabled)
{
    /// <summary>The dialog's view of a directory record.</summary>
    public static DirectoryPerson From(DirectoryUser user)
        => new(user.Sid, user.AccountName, user.UserName, user.DisplayName, user.Email, user.Department, user.Title, user.Enabled);
}

/// <summary>The account a person in the search results already has.</summary>
/// <param name="Kind"><c>user</c> or <c>member</c>.</param>
/// <param name="Key">The account's key.</param>
/// <param name="Username">The account's username.</param>
/// <param name="State"><c>active</c>, <c>disabled</c> or <c>retired</c>.</param>
public sealed record ExistingAccount(string Kind, Guid Key, string Username, string State);

/// <summary>
/// The people a search found, and the accounts some of them already have, keyed by SID, so the
/// dialog can tell an administrator before they try to create a second one.
/// </summary>
/// <param name="Results">The people found, sorted by display name.</param>
/// <param name="Existing">The accounts of the kind that some of them already have, by SID.</param>
public sealed record DirectorySearchResult(IReadOnlyList<DirectoryPerson> Results, IReadOnlyDictionary<string, ExistingAccount> Existing);

/// <summary>The account created for a person.</summary>
/// <param name="Key">The new account's key.</param>
/// <param name="Username">Its username.</param>
/// <param name="Name">Its name, the directory's display name.</param>
/// <param name="Email">Its email address.</param>
public sealed record CreatedAccount(Guid Key, string Username, string Name, string Email);

/// <summary>Why an account was not created: an HTTP status and a problem the dialog shows as it is.</summary>
/// <param name="Status">The HTTP status the management API answers with.</param>
/// <param name="Title">The problem's title.</param>
/// <param name="Detail">What happened and what to do, written for the administrator.</param>
public sealed record DirectoryAccountProblem(int Status, string Title, string Detail);

/// <summary>The account created, or the problem that stopped it.</summary>
/// <param name="Account">The account, when it was created.</param>
/// <param name="Problem">The problem, when it was not.</param>
public sealed record DirectoryAccountOutcome(CreatedAccount? Account, DirectoryAccountProblem? Problem);

/// <summary>
/// Creates backoffice users and members for people chosen from the directory, with their Windows SID
/// linked from the start. The first sign-in then finds the account by SID, and no username or email
/// ever has to match.
/// </summary>
/// <remarks>
/// <para>
/// The browser names the person by SID only; everything else is read from the directory again here,
/// so a tampered request cannot give someone else's name, email or identity to an account. The
/// username is the account name without its domain, in lower case (<c>DOMAIN\Test.User</c>
/// becomes <c>test.user</c>), the name is the directory's display name, and the email is the
/// directory's or <c>username@localhost</c>, as a sign-in without one would get.
/// </para>
/// <para>
/// A person who already has an account of the kind, linked or retired under their SID, and a
/// username or email that another account holds, are conflicts that name the account and what to
/// do; nothing is changed. The SID is linked under <see cref="AccountDirectorySettings.LoginProvider"/>
/// for the kind, in the same transaction as the account and its audit entry.
/// </para>
/// </remarks>
public sealed class DirectoryAccountService(
    IAccountDirectory directory,
    IOptions<AccountDirectorySettings> settings,
    IRetiredAccountPolicy retirement,
    ISignInLinkPolicy signIn,
    IUserService userService,
    IMemberService memberService,
    IMemberManager memberManager,
    IMemberTypeService memberTypeService,
    IMemberGroupService memberGroupService,
    IExternalLoginWithKeyService externalLogins,
    ICoreScopeProvider scopeProvider,
    IAuditService auditService,
    IOptions<SecuritySettings> security,
    ILogger<DirectoryAccountService> logger)
{
    /// <summary>
    /// Searches the directory and reports which of the people found already have an account of the
    /// kind: linked to their SID under one of the login providers, or retired under it.
    /// </summary>
    public async Task<DirectorySearchResult> SearchAsync(DirectoryAccountKind kind, string? query, int take, CancellationToken cancellationToken = default)
    {
        var people = await directory.SearchAsync(query ?? "", take, cancellationToken);
        var existing = new Dictionary<string, ExistingAccount>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in people)
        {
            if (!existing.ContainsKey(person.Sid) && await FindExistingAsync(kind, person.Sid) is { } account)
                existing[person.Sid] = new ExistingAccount(KindName(kind), account.Key, account.Username, account.State);
        }
        return new DirectorySearchResult(people.Select(DirectoryPerson.From).ToList(), existing);
    }

    /// <summary>Creates an approved backoffice user for a person in the directory, in the given groups.</summary>
    /// <param name="performingUserKey">The administrator creating it, for Umbraco's group rules and the audit log.</param>
    /// <param name="sid">The person's SID, as the search returned it.</param>
    /// <param name="userGroupKeys">The user groups; at least one.</param>
    /// <param name="cancellationToken">Cancels the directory request.</param>
    public async Task<DirectoryAccountOutcome> CreateUserAsync(
        Guid performingUserKey, string? sid, IReadOnlyCollection<Guid> userGroupKeys, CancellationToken cancellationToken = default)
    {
        if (!AccountDirectoryRules.IsSid(sid)) return InvalidSid();
        if (userGroupKeys.Count == 0)
            return Problem(StatusCodes.Status400BadRequest, "Choose a user group",
                "A backoffice user needs at least one user group. Choose the groups that give this person the access they need.");

        var person = await directory.FindAsync(sid!, cancellationToken);
        if (Unusable(person, sid!) is { } unusable) return unusable;
        if (await FindExistingAsync(DirectoryAccountKind.User, person!.Sid) is { } existing)
            return AlreadyHasAccount(DirectoryAccountKind.User, person, existing);

        var username = UserNameOf(person);
        var email = EmailOf(person, username);
        // Umbraco refuses a username other than the email when a site requires that.
        if (security.Value.UsernameIsEmail) username = email;
        if (userService.GetByUsername(username) is { } sameName)
            return Taken(DirectoryAccountKind.User, person, "username", username, Ref(sameName));
        if (userService.GetByEmail(email) is { } sameEmail)
            return Taken(DirectoryAccountKind.User, person, "email address", email, Ref(sameEmail));

        using var scope = scopeProvider.CreateCoreScope();
        var created = await userService.CreateAsync(performingUserKey, new UserCreateModel
        {
            UserName = username,
            Email = email,
            Name = person.DisplayName,
            Kind = UserKind.Default,
            UserGroupKeys = userGroupKeys.ToHashSet(),
        }, approveUser: true);
        if (!created.Success || created.Result.CreatedUser is not { } user)
            return UserNotCreated(created.Status, created.Result.Error?.ErrorMessage);

        AccountLinks.Link(externalLogins, logger, user.Key, person.Sid, [settings.Value.LoginProvider(DirectoryAccountKind.User)]);
        await AuditAsync(performingUserKey, user.Id, "User", person);
        scope.Complete();
        logger.LogInformation("Created backoffice user {Username} for {Account} ({Sid}) from the directory, in groups {Groups}.",
            user.Username, person.AccountName, person.Sid, string.Join(", ", user.Groups.Select(group => group.Alias)));
        return new DirectoryAccountOutcome(new CreatedAccount(user.Key, user.Username, user.Name ?? person.DisplayName, user.Email), null);
    }

    /// <summary>Creates an approved member for a person in the directory, of a member type and in member groups.</summary>
    /// <param name="performingUserKey">The backoffice user creating it, for the audit log.</param>
    /// <param name="sid">The person's SID, as the search returned it.</param>
    /// <param name="memberTypeAlias">The member type; empty means <see cref="AccountDirectorySettings.MemberTypeAlias"/>.</param>
    /// <param name="memberGroupNames">The member groups, which may be none.</param>
    /// <param name="cancellationToken">Cancels the directory request.</param>
    public async Task<DirectoryAccountOutcome> CreateMemberAsync(
        Guid performingUserKey, string? sid, string? memberTypeAlias, IReadOnlyCollection<string> memberGroupNames, CancellationToken cancellationToken = default)
    {
        if (!AccountDirectoryRules.IsSid(sid)) return InvalidSid();
        var typeAlias = string.IsNullOrWhiteSpace(memberTypeAlias) ? settings.Value.EffectiveMemberTypeAlias : memberTypeAlias.Trim();
        if (memberTypeService.Get(typeAlias) is null)
            return Problem(StatusCodes.Status400BadRequest, "Unknown member type", $"There is no member type with the alias {typeAlias}.");
        var groups = memberGroupNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var name in groups)
        {
            if (await memberGroupService.GetByNameAsync(name) is null)
                return Problem(StatusCodes.Status400BadRequest, "Unknown member group", $"There is no member group named {name}. Close the dialog and try again.");
        }

        var person = await directory.FindAsync(sid!, cancellationToken);
        if (Unusable(person, sid!) is { } unusable) return unusable;
        if (await FindExistingAsync(DirectoryAccountKind.Member, person!.Sid) is { } existing)
            return AlreadyHasAccount(DirectoryAccountKind.Member, person, existing);

        var username = UserNameOf(person);
        var email = EmailOf(person, username);
        if (memberService.GetByUsername(username) is { } sameName)
            return Taken(DirectoryAccountKind.Member, person, "username", username, Ref(sameName));
        if (memberService.GetByEmail(email) is { } sameEmail)
            return Taken(DirectoryAccountKind.Member, person, "email address", email, Ref(sameEmail));

        using var scope = scopeProvider.CreateCoreScope();
        // Saved by the member service as it is created; a notification handler that cancels the
        // save leaves it without an identity.
        var member = memberService.CreateMemberWithIdentity(username, email, person.DisplayName, typeAlias, isApproved: true);
        if (!member.HasIdentity)
            return Problem(StatusCodes.Status400BadRequest, "The member could not be created", "Saving the member was cancelled. The site log may say why.");
        await AccountLinks.InitializeMemberPasswordAsync(member, memberManager);
        if (groups.Length > 0) memberService.AssignRoles([member.Username], groups);
        AccountLinks.Link(externalLogins, logger, member.Key, person.Sid, [settings.Value.LoginProvider(DirectoryAccountKind.Member)]);
        await AuditAsync(performingUserKey, member.Id, "Member", person);
        scope.Complete();
        logger.LogInformation("Created member {Username} for {Account} ({Sid}) from the directory, of type {MemberType}, in groups {Groups}.",
            member.Username, person.AccountName, person.Sid, typeAlias, string.Join(", ", groups));
        return new DirectoryAccountOutcome(new CreatedAccount(member.Key, member.Username, member.Name ?? person.DisplayName, member.Email), null);
    }

    /// <summary>
    /// The account of the kind that belongs to a SID: linked to it under any of the login providers,
    /// or retired with the SID as its username.
    /// </summary>
    private async Task<AccountRef?> FindExistingAsync(DirectoryAccountKind kind, string sid)
    {
        foreach (var provider in LookupProviders(kind))
        {
            foreach (var login in externalLogins.Find(provider, sid))
            {
                if (!Guid.TryParse(login.UserId, out var key)) continue;
                var linked = kind == DirectoryAccountKind.User
                    ? Ref(await userService.GetAsync(key))
                    : Ref(memberService.GetById(key));
                if (linked is not null) return linked;
            }
        }

        var named = kind == DirectoryAccountKind.User
            ? Ref(userService.GetByUsername(sid))
            : Ref(memberService.GetByUsername(sid));
        return named is { State: AccountRef.Retired } ? named : null;
    }

    /// <summary>The configured provider, then the providers the site's sign-in used before.</summary>
    private IEnumerable<string> LookupProviders(DirectoryAccountKind kind)
        => signIn.PreviousLoginProviders(kind)
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Prepend(settings.Value.LoginProvider(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The platform's username for a directory account: its user name without the domain, in lower case.</summary>
    private static string UserNameOf(DirectoryUser person) => person.UserName.Trim().ToLowerInvariant();

    /// <summary>The directory's email, or <c>username@localhost</c> like a sign-in without one.</summary>
    private static string EmailOf(DirectoryUser person, string username)
        => string.IsNullOrWhiteSpace(person.Email) ? $"{username}@localhost" : person.Email.Trim();

    private async Task AuditAsync(Guid performingUserKey, int id, string entityType, DirectoryUser person)
    {
        var result = await auditService.AddAsync(AuditType.New, performingUserKey, id, entityType,
            $"Created from the directory for {person.AccountName}, linked to its Windows identity {person.Sid}.");
        if (!result.Success)
            logger.LogWarning("The creation of the account for {Account} was not recorded in the audit log: {Status}.", person.AccountName, result.Result);
    }

    private static DirectoryAccountOutcome? Unusable(DirectoryUserDetail? person, string sid)
    {
        if (person is null)
            return Problem(StatusCodes.Status404NotFound, "Not in the directory",
                $"The directory has no account with the SID {sid}. It may have been deleted since the search.");
        if (!person.Enabled)
            return Problem(StatusCodes.Status400BadRequest, "Disabled in the directory",
                $"{person.AccountName} is disabled in the directory, so nobody could sign in with the account.");
        return null;
    }

    private static DirectoryAccountOutcome InvalidSid()
        => Problem(StatusCodes.Status400BadRequest, "No person chosen",
            "The person must be identified by the SID of their Windows account (S-1-5-21-...), as the directory search returns it.");

    /// <summary>
    /// The person already has an account of the kind. A retired one is brought back as the site's
    /// <see cref="IRetiredAccountPolicy.RestoreAdvice"/> says.
    /// </summary>
    private DirectoryAccountOutcome AlreadyHasAccount(DirectoryAccountKind kind, DirectoryUser person, AccountRef account)
    {
        var noun = KindName(kind);
        return account.State switch
        {
            AccountRef.Retired => Problem(StatusCodes.Status409Conflict, "Retired account",
                $"{person.DisplayName}'s Windows identity belongs to the retired {noun} {account.Name}. {retirement.RestoreAdvice(kind)}"),
            AccountRef.Disabled => Problem(StatusCodes.Status409Conflict, "Already has an account",
                $"{person.DisplayName} already has the {noun} {account.Name} ({account.Username}), linked to their Windows identity. "
                + $"It is disabled: enable it in {SectionName(kind)} instead of creating another."),
            _ => Problem(StatusCodes.Status409Conflict, "Already has an account",
                $"{person.DisplayName} already has the {noun} {account.Name} ({account.Username}), linked to their Windows identity."),
        };
    }

    /// <summary>A username or email held by another account, which Umbraco would refuse to duplicate.</summary>
    private DirectoryAccountOutcome Taken(DirectoryAccountKind kind, DirectoryUser person, string what, string value, AccountRef other)
    {
        var logins = externalLogins.GetExternalLogins(other.Key).ToList();
        // Linked to this very SID under a provider no longer configured: it is the person's own account.
        if (logins.Count > 0 && logins.All(login => string.Equals(login.ProviderKey, person.Sid, StringComparison.OrdinalIgnoreCase)))
            return AlreadyHasAccount(kind, person, other);

        var noun = KindName(kind);
        var title = what == "username" ? "Username taken" : "Email address taken";
        var start = $"The {what} {value} belongs to the {noun} {other.Name}";
        if (other.State == AccountRef.Retired || logins.Count > 0)
            return Problem(StatusCodes.Status409Conflict, title,
                $"{start}, which is linked to another Windows identity, probably someone who had this name before. "
                + $"Rename or delete that {noun} to free the name, then add {person.DisplayName} again.");
        return Problem(StatusCodes.Status409Conflict, title, signIn.FirstSignInLinksByName
            ? $"{start}, which is not linked to anyone yet. If it was made for {person.DisplayName}, they can sign in: "
                + $"their first sign-in links it. Otherwise rename or delete that {noun}, then add the person again."
            : $"{start}, which is not linked to anyone, and signing in does not link an account by its name. "
                + $"Rename or delete that {noun}, then add {person.DisplayName} again.");
    }

    private static DirectoryAccountOutcome UserNotCreated(UserOperationStatus status, string? error) => status switch
    {
        UserOperationStatus.Unauthorized => Problem(StatusCodes.Status403Forbidden, "Not allowed",
            "You can only give a new user groups you belong to yourself, unless you are an administrator."),
        UserOperationStatus.MissingUserGroup => Problem(StatusCodes.Status400BadRequest, "Unknown user group",
            "One of the chosen user groups no longer exists. Close the dialog and try again."),
        UserOperationStatus.DuplicateUserName or UserOperationStatus.DuplicateEmail => Problem(StatusCodes.Status409Conflict, "Account exists",
            "Another user took this username or email address a moment ago. Search again to see it."),
        UserOperationStatus.InvalidEmail => Problem(StatusCodes.Status400BadRequest, "Invalid email address",
            "Umbraco does not accept the directory's email address for this person."),
        UserOperationStatus.CancelledByNotification => Problem(StatusCodes.Status400BadRequest, "The user could not be created",
            "Saving the user was cancelled. The site log may say why."),
        _ => Problem(StatusCodes.Status400BadRequest, "The user could not be created",
            string.IsNullOrWhiteSpace(error) ? $"Umbraco refused to create the user ({status})." : error),
    };

    private static DirectoryAccountOutcome Problem(int status, string title, string detail)
        => new(null, new DirectoryAccountProblem(status, title, detail));

    private static string KindName(DirectoryAccountKind kind) => kind == DirectoryAccountKind.User ? "user" : "member";

    private static string SectionName(DirectoryAccountKind kind) => kind == DirectoryAccountKind.User ? "Users" : "Members";

    [return: NotNullIfNotNull(nameof(user))]
    private AccountRef? Ref(IUser? user) => user is null ? null : Ref(user, user.Name);

    [return: NotNullIfNotNull(nameof(member))]
    private AccountRef? Ref(IMember? member) => member is null ? null : Ref(member, member.Name);

    private AccountRef Ref(IMembershipUser account, string? name)
        => new(account.Key, account.Username, name ?? account.Username,
            retirement.IsRetired(account) ? AccountRef.Retired : account.IsApproved ? AccountRef.Active : AccountRef.Disabled);

    /// <summary>What the conflicts need to know about an existing user or member.</summary>
    private sealed record AccountRef(Guid Key, string Username, string Name, string State)
    {
        public const string Active = "active";
        public const string Disabled = "disabled";
        public const string Retired = "retired";
    }
}
