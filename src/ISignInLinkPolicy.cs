namespace DirectoryAccounts;

/// <summary>
/// What the site's sign-in does with the SID links this package creates, so the dialog finds every
/// account a person already has and gives advice that matches the site. Register an implementation
/// to replace <see cref="DefaultSignInLinkPolicy"/>.
/// </summary>
public interface ISignInLinkPolicy
{
    /// <summary>
    /// Login providers, besides <see cref="AccountDirectorySettings.LoginProvider"/>, under which a
    /// SID may already be linked to an account of the kind, such as the scheme of a sign-in the site
    /// used before. The dialog searches them to find existing accounts and never links under them.
    /// </summary>
    IEnumerable<string> PreviousLoginProviders(DirectoryAccountKind kind);

    /// <summary>
    /// Whether the site's first sign-in of a SID that no account is linked to claims an existing
    /// account whose username or email fits, and links it. When it does, the dialog tells an
    /// administrator that an account created by hand for the person needs no replacement: the
    /// person can sign in.
    /// </summary>
    bool FirstSignInLinksByName { get; }
}

/// <summary>
/// The <see cref="ISignInLinkPolicy"/> of a site whose only sign-in is the one configured in
/// <see cref="AccountDirectorySettings.BackOfficeLoginProvider"/> and
/// <see cref="AccountDirectorySettings.MemberLoginProvider"/>, and which opens an account only
/// through its SID link.
/// </summary>
public sealed class DefaultSignInLinkPolicy : ISignInLinkPolicy
{
    /// <summary>None.</summary>
    public IEnumerable<string> PreviousLoginProviders(DirectoryAccountKind kind) => [];

    /// <summary>False.</summary>
    public bool FirstSignInLinksByName => false;
}
