using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.Membership;

namespace DirectoryAccounts.Tests;

/// <summary>The <c>Directory</c> settings, the choice of source, and the default seams, without a site.</summary>
public sealed class SettingsTests
{
    private const string Authority = "https://login.example.invalid/IdentityServer";

    /// <summary>The defaults link under the IdentityServer schemes and create Members.</summary>
    [Fact]
    public void The_defaults_link_under_the_IdentityServer_schemes_and_create_Members()
    {
        var settings = new AccountDirectorySettings();
        Assert.Equal(AccountDirectorySource.Auto, settings.Source);
        Assert.Null(settings.UseLocalAccounts);
        Assert.Null(settings.IdentityServerAuthority);
        Assert.Null(settings.IdentityServerApiKey);
        Assert.False(settings.IdentityServerAcceptAnyServerCertificate);
        Assert.Equal("Umbraco.IdentityServer", settings.LoginProvider(DirectoryAccountKind.User));
        Assert.Equal("UmbracoMembers.IdentityServer", settings.LoginProvider(DirectoryAccountKind.Member));
        Assert.Equal("Member", settings.EffectiveMemberTypeAlias);

        // Empty values mean the defaults; others are trimmed.
        var blank = new AccountDirectorySettings { BackOfficeLoginProvider = " ", MemberLoginProvider = "", MemberTypeAlias = " " };
        Assert.Equal(AccountDirectorySettings.DefaultBackOfficeLoginProvider, blank.LoginProvider(DirectoryAccountKind.User));
        Assert.Equal(AccountDirectorySettings.DefaultMemberLoginProvider, blank.LoginProvider(DirectoryAccountKind.Member));
        Assert.Equal(AccountDirectorySettings.DefaultMemberTypeAlias, blank.EffectiveMemberTypeAlias);
        Assert.Equal("Umbraco.Domain", new AccountDirectorySettings { BackOfficeLoginProvider = " Umbraco.Domain " }.LoginProvider(DirectoryAccountKind.User));
    }

    /// <summary>Auto follows the local accounts setting then the identity server then the domain.</summary>
    [Fact]
    public void Auto_follows_the_local_accounts_setting_then_the_identity_server_then_the_domain()
    {
        var external = new AccountDirectorySettings { IdentityServerAuthority = Authority };
        Assert.Equal(AccountDirectorySource.LocalAccounts, Choose(new() { UseLocalAccounts = true, IdentityServerAuthority = Authority }));
        Assert.Equal(AccountDirectorySource.IdentityServer, Choose(external));
        Assert.Equal(AccountDirectorySource.IdentityServer, Choose(new() { UseLocalAccounts = false, IdentityServerAuthority = Authority }));
        Assert.Equal(AccountDirectorySource.ActiveDirectory, Choose(new() { UseLocalAccounts = false }));
        Assert.Equal(AccountDirectorySource.ActiveDirectory, Choose(new() { UseLocalAccounts = false, IdentityServerAuthority = " " }));
        Assert.Equal(AccountDirectorySettings.IsOutsideDomain ? AccountDirectorySource.LocalAccounts : AccountDirectorySource.ActiveDirectory,
            Choose(new()));
    }

    /// <summary>An explicit source wins.</summary>
    [Theory]
    [InlineData(AccountDirectorySource.ActiveDirectory)]
    [InlineData(AccountDirectorySource.IdentityServer)]
    [InlineData(AccountDirectorySource.LocalAccounts)]
    public void An_explicit_source_wins(AccountDirectorySource source)
    {
        Assert.Equal(source, Choose(new() { Source = source }));
        Assert.Equal(source, Choose(new() { Source = source, UseLocalAccounts = true, IdentityServerAuthority = Authority }));
        Assert.Equal(source, Choose(new() { Source = source, UseLocalAccounts = false }));
    }

    /// <summary>Both settings classes bind from the Directory section.</summary>
    [Fact]
    public void Both_settings_classes_bind_from_the_Directory_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Directory:Source"] = "identityserver",
            ["Directory:UseLocalAccounts"] = "false",
            ["Directory:IdentityServerAuthority"] = Authority,
            ["Directory:IdentityServerApiKey"] = "a-test-key",
            ["Directory:IdentityServerAcceptAnyServerCertificate"] = "true",
            ["Directory:BackOfficeLoginProvider"] = "Umbraco.Domain",
            ["Directory:MemberLoginProvider"] = "UmbracoMembers.Domain",
            ["Directory:MemberTypeAlias"] = "employee",
            ["Directory:UserRootGlobal"] = "LDAP://OU=People,DC=example,DC=invalid",
            ["Directory:MaxTake"] = "40",
        }).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration);
        services.AddOptions<AccountDirectorySettings>().BindConfiguration(AccountDirectorySettings.SectionName);
        services.AddOptions<DirectoryOptions>().BindConfiguration(DirectoryOptions.SectionName);
        using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<IOptions<AccountDirectorySettings>>().Value;
        Assert.Equal(AccountDirectorySource.IdentityServer, settings.Source);
        Assert.False(settings.UseLocalAccounts);
        Assert.Equal((Authority, "a-test-key", true), (settings.IdentityServerAuthority, settings.IdentityServerApiKey, settings.IdentityServerAcceptAnyServerCertificate));
        Assert.Equal(("Umbraco.Domain", "UmbracoMembers.Domain", "employee"), (settings.BackOfficeLoginProvider, settings.MemberLoginProvider, settings.MemberTypeAlias));
        var lookup = provider.GetRequiredService<IOptions<DirectoryOptions>>().Value;
        Assert.Equal(("LDAP://OU=People,DC=example,DC=invalid", 40), (lookup.UserRootGlobal, lookup.MaxTake));
    }

    /// <summary>The startup description names the source without the key.</summary>
    [Fact]
    public void The_startup_description_names_the_source_without_the_key()
    {
        var external = new AccountDirectorySettings { IdentityServerAuthority = Authority, IdentityServerApiKey = "secret-test-key" };
        var described = AccountDirectoryExtensions.Describe(AccountDirectorySource.IdentityServer, external, new DirectoryOptions());
        Assert.Contains(Authority, described);
        Assert.Contains("Directory:IdentityServerApiKey", described);
        Assert.DoesNotContain("secret-test-key", described);
        Assert.Contains("under LDAP://DC=example,DC=invalid", AccountDirectoryExtensions.Describe(
            AccountDirectorySource.ActiveDirectory, new AccountDirectorySettings(), new DirectoryOptions { UserRootGlobal = "LDAP://DC=example,DC=invalid" }));
        Assert.Contains("Directory:UseLocalAccounts", AccountDirectoryExtensions.Describe(
            AccountDirectorySource.LocalAccounts, new AccountDirectorySettings { UseLocalAccounts = true }, new DirectoryOptions()));
    }

    /// <summary>The default seams know no retired accounts and no earlier sign in.</summary>
    [Fact]
    public void The_default_seams_know_no_retired_accounts_and_no_earlier_sign_in()
    {
        var retirement = new NoRetiredAccounts();
        Assert.False(retirement.IsRetired(new User(new Umbraco.Cms.Core.Configuration.Models.GlobalSettings(), "Someone", "someone@example.invalid", "S-1-5-21-1-2-3-1001", "unused")));
        Assert.Contains("Users", retirement.RestoreAdvice(DirectoryAccountKind.User));
        Assert.Contains("Members", retirement.RestoreAdvice(DirectoryAccountKind.Member));

        var signIn = new DefaultSignInLinkPolicy();
        Assert.Empty(signIn.PreviousLoginProviders(DirectoryAccountKind.User));
        Assert.Empty(signIn.PreviousLoginProviders(DirectoryAccountKind.Member));
        Assert.False(signIn.FirstSignInLinksByName);
    }

    /// <summary>Only a SID identifies a person.</summary>
    [Theory]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-1001", true)]
    [InlineData("s-1-5-21-10-20-30-1001", true)]
    [InlineData("S-1-5-21-x", false)]
    [InlineData("DOMAIN\\Test.User", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_SID_identifies_a_person(string? value, bool isSid)
    {
        Assert.Equal(isSid, AccountDirectoryRules.IsSid(value));
        if (isSid) AccountDirectoryRules.EnsureSid(value);
        else Assert.Throws<DirectoryQueryException>(() => AccountDirectoryRules.EnsureSid(value));
    }

    /// <summary>A search needs terms of two characters.</summary>
    [Fact]
    public void A_search_needs_terms_of_two_characters()
    {
        Assert.Equal(["test", "user"], AccountDirectoryRules.SearchTerms("test, user"));
        Assert.Contains("at least 2 characters", Assert.Throws<DirectoryQueryException>(() => AccountDirectoryRules.SearchTerms("test u")).Message);
        Assert.Throws<DirectoryQueryException>(() => AccountDirectoryRules.SearchTerms(""));
        Assert.Throws<DirectoryQueryException>(() => AccountDirectoryRules.SearchTerms(null));
    }

    private static AccountDirectorySource Choose(AccountDirectorySettings settings) => AccountDirectorySettings.Choose(settings);
}
