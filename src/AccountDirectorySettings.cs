namespace DirectoryAccounts;

/// <summary>Where <see cref="IAccountDirectory"/> finds people.</summary>
public enum AccountDirectorySource
{
    /// <summary>
    /// Decided from the other settings when the site starts; see <see cref="AccountDirectorySettings.Choose"/>.
    /// Only a setting has this value: the source in use is always one of the others.
    /// </summary>
    Auto,

    /// <summary>Active Directory, queried by the site itself as its process account.</summary>
    ActiveDirectory,

    /// <summary>The directory API of the identity server in <see cref="AccountDirectorySettings.IdentityServerAuthority"/>.</summary>
    IdentityServer,

    /// <summary>The local accounts of the computer the site runs on.</summary>
    LocalAccounts,
}

/// <summary>
/// This package's settings in the <c>Directory</c> section. The section also holds the people
/// lookup's <see cref="DirectoryOptions"/> (LDAP roots and result limits), which are bound from it
/// as well, so a site configures its directory in one place.
/// </summary>
public sealed class AccountDirectorySettings
{
    /// <summary>The configuration section these settings are bound from, shared with <see cref="DirectoryOptions"/>.</summary>
    public const string SectionName = DirectoryOptions.SectionName;

    /// <summary>The default of <see cref="BackOfficeLoginProvider"/>: Umbraco's backoffice prefix and <c>IdentityServer</c>.</summary>
    public const string DefaultBackOfficeLoginProvider = Umbraco.Cms.Core.Constants.Security.BackOfficeExternalAuthenticationTypePrefix + "IdentityServer";

    /// <summary>The default of <see cref="MemberLoginProvider"/>: Umbraco's member prefix and <c>IdentityServer</c>.</summary>
    public const string DefaultMemberLoginProvider = Umbraco.Cms.Core.Constants.Security.MemberExternalAuthenticationTypePrefix + "IdentityServer";

    /// <summary>The default of <see cref="MemberTypeAlias"/>, the member type a new Umbraco site has.</summary>
    public const string DefaultMemberTypeAlias = "Member";

    /// <summary>
    /// Where people are found. <see cref="AccountDirectorySource.Auto"/>, the default, decides from
    /// <see cref="UseLocalAccounts"/>, <see cref="IdentityServerAuthority"/> and whether the computer
    /// is in a domain; any other value is used as it is.
    /// </summary>
    public AccountDirectorySource Source { get; set; } = AccountDirectorySource.Auto;

    /// <summary>
    /// With <see cref="Source"/> set to <see cref="AccountDirectorySource.Auto"/>: true searches the
    /// local accounts of the computer the site runs on, which suits a development machine that is
    /// not in a domain; false never does. Unset uses local accounts on a computer that is not in a
    /// domain and has no <see cref="IdentityServerAuthority"/>, where the people who sign in are
    /// local accounts too.
    /// </summary>
    public bool? UseLocalAccounts { get; set; }

    /// <summary>
    /// The identity server whose directory API is searched (<c>{authority}/directory/users</c>),
    /// such as <c>https://login.example.com/IdentityServer</c>; normally the issuer the site signs in
    /// through. Setting it makes <see cref="AccountDirectorySource.Auto"/> choose that API.
    /// </summary>
    public string? IdentityServerAuthority { get; set; }

    /// <summary>
    /// The key this site sends in the <c>X-Api-Key</c> header to the identity server's directory
    /// API; one of the keys the server accepts. Empty authenticates as the site's own Windows
    /// identity (the application pool account) instead, which the server must then admit.
    /// </summary>
    public string? IdentityServerApiKey { get; set; }

    /// <summary>
    /// Skip certificate validation on the requests to the identity server's directory API. For an
    /// isolated intranet with a self-signed certificate only; never on an untrusted network.
    /// </summary>
    public bool IdentityServerAcceptAnyServerCertificate { get; set; }

    /// <summary>
    /// The external-login provider a new backoffice user's SID is linked under: the scheme name of
    /// the site's backoffice sign-in, which stores the SID as the login's provider key, so the
    /// person's first sign-in finds the account by it. Umbraco's backoffice schemes start with
    /// <c>Umbraco.</c>. Empty means <see cref="DefaultBackOfficeLoginProvider"/>.
    /// </summary>
    public string BackOfficeLoginProvider { get; set; } = DefaultBackOfficeLoginProvider;

    /// <summary>
    /// The external-login provider a new member's SID is linked under: the scheme name of the
    /// site's member sign-in. Umbraco's member schemes start with <c>UmbracoMembers.</c>. Empty
    /// means <see cref="DefaultMemberLoginProvider"/>.
    /// </summary>
    public string MemberLoginProvider { get; set; } = DefaultMemberLoginProvider;

    /// <summary>
    /// The member type of a member created without one, as the dialog does when the site has a
    /// single member type. Empty means <see cref="DefaultMemberTypeAlias"/>.
    /// </summary>
    public string MemberTypeAlias { get; set; } = DefaultMemberTypeAlias;

    /// <summary>The provider a SID is linked under for one kind of account, with empty meaning the default.</summary>
    public string LoginProvider(DirectoryAccountKind kind) => kind == DirectoryAccountKind.User
        ? (string.IsNullOrWhiteSpace(BackOfficeLoginProvider) ? DefaultBackOfficeLoginProvider : BackOfficeLoginProvider.Trim())
        : (string.IsNullOrWhiteSpace(MemberLoginProvider) ? DefaultMemberLoginProvider : MemberLoginProvider.Trim());

    /// <summary><see cref="MemberTypeAlias"/>, with empty meaning the default.</summary>
    public string EffectiveMemberTypeAlias
        => string.IsNullOrWhiteSpace(MemberTypeAlias) ? DefaultMemberTypeAlias : MemberTypeAlias.Trim();

    /// <summary>Whether <see cref="IdentityServerAuthority"/> has a value.</summary>
    public bool HasIdentityServer => !string.IsNullOrWhiteSpace(IdentityServerAuthority);

    /// <summary>
    /// Chooses the source from configuration alone, without touching any directory, so startup can
    /// say which one is in use and a directory that is down only shows when someone searches it.
    /// Never returns <see cref="AccountDirectorySource.Auto"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An explicit <see cref="Source"/> wins. <see cref="AccountDirectorySource.Auto"/> takes, in order:
    /// local accounts when <see cref="UseLocalAccounts"/> is true; the identity server's directory API
    /// when <see cref="IdentityServerAuthority"/> is set, even on a computer outside the domain,
    /// because the people who sign in are the server's and their SIDs are what the accounts must
    /// carry; local accounts when <see cref="UseLocalAccounts"/> is unset and the computer is not in a
    /// domain; Active Directory otherwise.
    /// </para>
    /// <para>
    /// This is the rule an Umbraco site had when the identity server's authority decided between
    /// the site's own directory and an external server's: an empty authority meant the site signs
    /// people in through its embedded identity server, against its own domain.
    /// </para>
    /// </remarks>
    public static AccountDirectorySource Choose(AccountDirectorySettings settings)
    {
        if (settings.Source != AccountDirectorySource.Auto) return settings.Source;
        if (settings.UseLocalAccounts == true) return AccountDirectorySource.LocalAccounts;
        if (settings.HasIdentityServer) return AccountDirectorySource.IdentityServer;
        return settings.UseLocalAccounts is null && IsOutsideDomain
            ? AccountDirectorySource.LocalAccounts
            : AccountDirectorySource.ActiveDirectory;
    }

    /// <summary>
    /// Whether the site's process account belongs to the computer rather than a domain, as on a
    /// computer that is not domain-joined: Windows then reports the computer's name as its domain.
    /// </summary>
    public static bool IsOutsideDomain
        => string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
}
