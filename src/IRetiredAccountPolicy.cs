using Umbraco.Cms.Core.Models.Membership;

namespace DirectoryAccounts;

/// <summary>
/// How the site retires accounts, for a site that keeps a deleted account as a disabled record
/// instead of removing it, so its name stays on everything it authored. The dialog then reports a
/// person whose SID a retired account still holds, with what to do, instead of creating a second
/// account for them. Register an implementation to replace <see cref="NoRetiredAccounts"/>.
/// </summary>
/// <remarks>
/// A retired account is found in two ways: linked to the person's SID under one of the login
/// providers (<see cref="AccountDirectorySettings.LoginProvider"/> and
/// <see cref="ISignInLinkPolicy.PreviousLoginProviders"/>), or having the SID as its username, the
/// name a retirement can give an account so that no sign-in produces it again.
/// </remarks>
public interface IRetiredAccountPolicy
{
    /// <summary>Whether a backoffice user or member is retired: kept for its history, and unusable.</summary>
    bool IsRetired(IMembershipUser account);

    /// <summary>
    /// One or more sentences that tell an administrator how to give a person whose SID a retired
    /// account holds an account again. The dialog shows them after naming the retired account.
    /// </summary>
    /// <param name="kind">The kind of account the administrator tried to create.</param>
    string RestoreAdvice(DirectoryAccountKind kind);
}

/// <summary>
/// The <see cref="IRetiredAccountPolicy"/> of a site that deletes accounts as Umbraco does: no
/// account is retired, so every existing account is active or disabled.
/// </summary>
public sealed class NoRetiredAccounts : IRetiredAccountPolicy
{
    /// <summary>Always false.</summary>
    public bool IsRetired(IMembershipUser account) => false;

    /// <inheritdoc />
    public string RestoreAdvice(DirectoryAccountKind kind) => kind == DirectoryAccountKind.User
        ? "Enable that user in Users instead of creating another."
        : "Enable that member in Members instead of creating another.";
}
