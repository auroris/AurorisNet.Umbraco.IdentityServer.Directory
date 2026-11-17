using System.Collections.Concurrent;
using Umbraco.Cms.Core.Models.Membership;

namespace DirectoryAccounts.Tests;

/// <summary>
/// A people lookup over a fixed list, standing in for Active Directory. It answers as the real
/// lookup does (global terms of at least two characters, enabled accounts only unless asked) and
/// records what it was asked. Two queries and one SID simulate a directory's failures.
/// </summary>
internal sealed class FakeDirectoryLookup : IDirectoryLookup
{
    /// <summary>A search the directory refuses with <see cref="RefusalReason"/>.</summary>
    public const string RefusedQuery = "refused";

    /// <summary>The reason a refused search gives.</summary>
    public const string RefusalReason = "The test directory refuses this search.";

    /// <summary>A search that finds the directory unreachable.</summary>
    public const string OutageQuery = "outage";

    /// <summary>A SID whose lookup finds the directory unreachable.</summary>
    public const string OutageSid = "S-1-5-21-10-20-30-5030";

    /// <summary>The message of the simulated outage.</summary>
    public const string OutageMessage = "The test domain controller is not operational.";

    public List<DirectoryUserDetail> People { get; } = [];

    public ConcurrentQueue<(string? Query, int? Take, DirectoryScope Scope, bool IncludeDisabled)> Searches { get; } = new();

    public static DirectoryUserDetail Person(string sid, string userName, string displayName, string? email = null, bool enabled = true, params string[] groups)
        => new(new DirectoryUser
        {
            Sid = sid,
            Guid = Guid.NewGuid().ToString(),
            AccountName = $"TEST\\{userName}",
            UserName = userName,
            DisplayName = displayName,
            Email = email,
            Department = "Test Team",
            Title = "Test Role",
            Enabled = enabled,
        }, groups);

    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string? query, int? take = null, DirectoryScope scope = DirectoryScope.Local, bool includeDisabled = false,
        CancellationToken cancellationToken = default, IDirectoryUserFilter? filter = null, DirectorySearchMode mode = DirectorySearchMode.AddressBook)
    {
        Searches.Enqueue((query, take, scope, includeDisabled));
        var terms = DirectoryQuery.Tokenize(query);
        if (scope == DirectoryScope.Global) DirectoryQuery.EnsureGlobalTerms(terms, 2);
        if (terms.Contains(RefusedQuery, StringComparer.OrdinalIgnoreCase)) throw new DirectoryQueryException(RefusalReason);
        if (terms.Contains(OutageQuery, StringComparer.OrdinalIgnoreCase)) throw new DirectoryUnavailableException(OutageMessage);
        IReadOnlyList<DirectoryUser> found = People
            .Where(person => includeDisabled || person.Enabled)
            .Where(person => terms.All(term => person.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || person.UserName.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(person => person.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(take ?? 20)
            .Cast<DirectoryUser>()
            .ToList();
        return Task.FromResult(found);
    }

    public Task<DirectoryUserDetail?> FindUserAsync(string id, CancellationToken cancellationToken = default, IReadOnlyList<string>? additionalAttributes = null)
    {
        if (id == OutageSid) throw new DirectoryUnavailableException(OutageMessage);
        return Task.FromResult(People.FirstOrDefault(person => string.Equals(person.Sid, id, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<IReadOnlyList<string>?> GetUserGroupsAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string? query, int? take = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<DirectoryGroupDetail?> FindGroupAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<DirectoryGroupListing?> ListGroupMembersAsync(string group, string? query = null, int? take = null, DirectoryScope scope = DirectoryScope.Local,
        bool includeDisabled = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// A retirement rule like a site that keeps deleted accounts: an account whose username is a SID is
/// retired. Its advice is recognizable, so a test can see that the dialog passes it on.
/// </summary>
internal sealed class TestRetirementPolicy : IRetiredAccountPolicy
{
    public const string Advice = "Test advice: give the retired account back its name.";

    public bool IsRetired(IMembershipUser account) => AccountDirectoryRules.IsSid(account.Username);

    public string RestoreAdvice(DirectoryAccountKind kind) => Advice;
}

/// <summary>A site whose earlier sign-in linked backoffice users under <see cref="PreviousProvider"/>.</summary>
internal sealed class TestSignInLinkPolicy : ISignInLinkPolicy
{
    public const string PreviousProvider = "Umbraco.Windows";

    public IEnumerable<string> PreviousLoginProviders(DirectoryAccountKind kind)
        => kind == DirectoryAccountKind.User ? [PreviousProvider] : [];

    public bool FirstSignInLinksByName => true;
}
