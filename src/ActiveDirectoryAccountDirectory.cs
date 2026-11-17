namespace DirectoryAccounts;

/// <summary>
/// Active Directory, queried in process through the people lookup (<see cref="IDirectoryLookup"/>),
/// for a site whose sign-ins come from the domain it is in, so that domain's directory is theirs.
/// Queries run as the site's process account (the application pool identity in IIS), which needs
/// read access to the roots configured in the <c>Directory</c> section.
/// </summary>
public sealed class ActiveDirectoryAccountDirectory(IDirectoryLookup lookup) : IAccountDirectory
{
    /// <remarks>
    /// The lookup excludes disabled accounts in LDAP before applying the result limit; the result is
    /// checked again so a lookup a site registered itself cannot return one either. A site's
    /// registered <see cref="IDirectoryUserFilter"/> applies to this search too.
    /// </remarks>
    public async Task<IReadOnlyList<DirectoryUser>> SearchAsync(string query, int take, CancellationToken cancellationToken = default)
    {
        var people = await lookup.SearchUsersAsync(
            query, Math.Clamp(take, 1, IAccountDirectory.MaxTake), DirectoryScope.Global, includeDisabled: false, cancellationToken);
        return people.Where(person => person.Enabled).ToList();
    }

    /// <inheritdoc />
    public Task<DirectoryUserDetail?> FindAsync(string sid, CancellationToken cancellationToken = default)
    {
        // The lookup also accepts a GUID or DOMAIN\user; this interface promises a SID.
        AccountDirectoryRules.EnsureSid(sid);
        return lookup.FindUserAsync(sid, cancellationToken);
    }
}
