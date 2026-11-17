using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace DirectoryAccounts.Tests;

/// <summary>
/// One real site for the whole class: the package arrives through composer discovery, the
/// <c>Directory</c> section chooses Active Directory, and a fake people lookup stands in for the
/// domain. Umbraco keeps static state, so the tests of this class, which xunit runs one at a time,
/// share the site and each uses people of its own.
/// </summary>
public sealed class SiteFixture : IAsyncLifetime
{
    internal const string UserProvider = "Umbraco.TestServer";
    internal const string MemberProvider = "UmbracoMembers.TestServer";

    private readonly string root = TestSite.CreateRoot();
    private TestSite? site;

    internal TestSite Site => site ?? throw new InvalidOperationException("The site has not started.");

    internal FakeDirectoryLookup Lookup { get; } = new();

    /// <summary>Starts the site with the fake lookup and the test policies.</summary>
    public async ValueTask InitializeAsync()
    {
        Lookup.People.AddRange(
        [
            FakeDirectoryLookup.Person(SiteTests.UserSid, "Test.User", "Test User", "test.user@example.invalid", groups: ["Test Team Users"]),
            FakeDirectoryLookup.Person(SiteTests.MemberSid, "Member.Person", "Member Person"),
            FakeDirectoryLookup.Person(SiteTests.DisabledSid, "Gone.Person", "Gone Person", enabled: false),
            FakeDirectoryLookup.Person(SiteTests.RetiredSid, "Retired.Person", "Retired Person"),
            FakeDirectoryLookup.Person(SiteTests.EarlierSid, "Earlier.Person", "Earlier Person"),
            FakeDirectoryLookup.Person(SiteTests.TakenSid, "Taken.Name", "Taken Name"),
            FakeDirectoryLookup.Person(FakeDirectoryLookup.OutageSid, "Outage.Person", "Outage Person"),
        ]);
        site = await TestSite.StartAsync(root, new Dictionary<string, string?>
        {
            ["Umbraco:CMS:Security:UsernameIsEmail"] = "false",
            ["Directory:Source"] = "ActiveDirectory",
            ["Directory:BackOfficeLoginProvider"] = UserProvider,
            ["Directory:MemberLoginProvider"] = MemberProvider,
        }, services =>
        {
            // Registered after the package's defaults, which they replace.
            services.AddSingleton<IDirectoryLookup>(Lookup);
            services.AddSingleton<IRetiredAccountPolicy, TestRetirementPolicy>();
            services.AddSingleton<ISignInLinkPolicy, TestSignInLinkPolicy>();
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>Stops the site and deletes its folder.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (site is not null) await site.DisposeAsync();
        }
        finally { TestSite.DeleteRoot(root); }
    }
}

/// <summary>The management API behind the "Add from directory" dialog, on a real site.</summary>
public sealed class SiteTests(SiteFixture fixture) : IClassFixture<SiteFixture>
{
    internal const string UserSid = "S-1-5-21-10-20-30-1001";
    internal const string MemberSid = "S-1-5-21-10-20-30-1002";
    internal const string DisabledSid = "S-1-5-21-10-20-30-1003";
    internal const string RetiredSid = "S-1-5-21-10-20-30-1004";
    internal const string EarlierSid = "S-1-5-21-10-20-30-1005";
    internal const string TakenSid = "S-1-5-21-10-20-30-1006";
    private const string Api = "/umbraco/management/api/v1/directory";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private TestSite Site => fixture.Site;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The settings choose Active Directory over the registered lookup.</summary>
    [Fact]
    public void The_settings_choose_Active_Directory_over_the_registered_lookup()
    {
        Assert.IsType<ActiveDirectoryAccountDirectory>(Site.Services.GetRequiredService<IAccountDirectory>());
        Assert.Same(fixture.Lookup, Site.Services.GetRequiredService<IDirectoryLookup>());
    }

    /// <summary>The backoffice extension is served and registered.</summary>
    [Fact]
    public async Task The_backoffice_extension_is_served_and_registered()
    {
        using var client = Site.CreateClient();
        using var manifest = await client.GetAsync("/App_Plugins/DirectoryAccounts/umbraco-package.json", Cancellation);
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        using var modal = await client.GetAsync("/App_Plugins/DirectoryAccounts/add-from-directory-modal.js", Cancellation);
        Assert.Equal(HttpStatusCode.OK, modal.StatusCode);

        var manifests = await Site.Services.GetRequiredService<IPackageManifestService>().GetAllPackageManifestsAsync();
        var package = Assert.Single(manifests, candidate => candidate.Name == "Directory Accounts");
        Assert.Equal(3, package.Extensions.Length);
    }

    /// <summary>A user created from the directory carries the SID.</summary>
    [Fact]
    public async Task A_user_created_from_the_directory_carries_the_SID()
    {
        using var administrator = await Site.SignInAsAdministratorAsync();

        // The search asks the whole directory for enabled people and finds the person, who has no user yet.
        var before = await SearchAsync(administrator, "users", "test user");
        var person = Assert.Single(before.Results);
        Assert.Equal((UserSid, "TEST\\Test.User", "Test User", "test.user@example.invalid", "Test Team", "Test Role", true),
            (person.Sid, person.AccountName, person.DisplayName, person.Email, person.Department, person.Title, person.Enabled));
        Assert.Empty(before.Existing);
        Assert.Contains(fixture.Lookup.Searches, search => search is { Query: "test user", Take: 20, Scope: DirectoryScope.Global, IncludeDisabled: false });

        // Created with the username in lower case, the directory's name and email, in the chosen group,
        // and linked under the configured provider before anyone signs in.
        var created = await ReadAsync<CreatedAccount>(await PostAsync(administrator, "users", new { sid = UserSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.Created);
        Assert.Equal(("test.user", "Test User", "test.user@example.invalid"), (created.Username, created.Name, created.Email));
        var users = Site.Services.GetRequiredService<IUserService>();
        var user = (await users.GetAsync(created.Key))!;
        Assert.True(user.IsApproved);
        Assert.Equal([Constants.Security.EditorGroupKey], user.Groups.Select(group => group.Key));
        Assert.Equal([(SiteFixture.UserProvider, UserSid)], Site.Logins(created.Key));
        using (var scope = Site.Services.CreateScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<IAuditService>().GetItemsByEntityAsync(user.Id, 0, 20);
            Assert.Contains(audit.Items, item => item.AuditType == AuditType.New && item.EntityType == "User" && item.Comment!.Contains(UserSid));
        }

        // The search now reports the user, and creating a second one is a conflict.
        var after = await SearchAsync(administrator, "users", "test user");
        Assert.Equal(new ExistingAccount("user", created.Key, "test.user", "active"), after.Existing[UserSid]);
        var again = await ProblemAsync(await PostAsync(administrator, "users", new { sid = UserSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.Conflict);
        Assert.Equal("Already has an account", again.Title);
        Assert.Contains("test.user", again.Detail);

        // A disabled user is reported as such, with what to do.
        user.IsApproved = false;
        users.Save(user);
        Assert.Equal("disabled", (await SearchAsync(administrator, "users", "test user")).Existing[UserSid].State);
        var disabled = await ProblemAsync(await PostAsync(administrator, "users", new { sid = UserSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.Conflict);
        Assert.Contains("enable it in Users", disabled.Detail);
    }

    /// <summary>A member created from the directory carries the SID.</summary>
    [Fact]
    public async Task A_member_created_from_the_directory_carries_the_SID()
    {
        using (var scope = Site.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMemberGroupService>().CreateAsync(new MemberGroup { Name = "Directory Readers" });
        using var administrator = await Site.SignInAsAdministratorAsync();

        // Member types and groups are checked before anything is created.
        Assert.Equal("Unknown member type", (await ProblemAsync(await PostAsync(administrator, "members",
            new { sid = MemberSid, memberTypeAlias = "noSuchType", memberGroupNames = Array.Empty<string>() }), HttpStatusCode.BadRequest)).Title);
        Assert.Equal("Unknown member group", (await ProblemAsync(await PostAsync(administrator, "members",
            new { sid = MemberSid, memberGroupNames = new[] { "No Such Group" } }), HttpStatusCode.BadRequest)).Title);

        var before = await SearchAsync(administrator, "members", "member person");
        Assert.Equal(MemberSid, Assert.Single(before.Results).Sid);
        Assert.Empty(before.Existing);

        // The member type defaults to Directory:MemberTypeAlias, and a person without an email gets username@localhost.
        var created = await ReadAsync<CreatedAccount>(await PostAsync(administrator, "members",
            new { sid = MemberSid, memberGroupNames = new[] { "Directory Readers" } }), HttpStatusCode.Created);
        Assert.Equal(("member.person", "Member Person", "member.person@localhost"), (created.Username, created.Name, created.Email));
        var members = Site.Services.GetRequiredService<IMemberService>();
        var member = members.GetById(created.Key)!;
        Assert.True(member.IsApproved);
        Assert.Equal(AccountDirectorySettings.DefaultMemberTypeAlias, member.ContentTypeAlias);
        Assert.False(string.IsNullOrEmpty(member.RawPasswordValue));
        Assert.Equal(["Directory Readers"], members.GetAllRoles(member.Id));
        Assert.Equal([(SiteFixture.MemberProvider, MemberSid)], Site.Logins(created.Key));

        var after = await SearchAsync(administrator, "members", "member person");
        Assert.Equal(new ExistingAccount("member", created.Key, "member.person", "active"), after.Existing[MemberSid]);
        Assert.Equal("Already has an account", (await ProblemAsync(await PostAsync(administrator, "members",
            new { sid = MemberSid, memberGroupNames = Array.Empty<string>() }), HttpStatusCode.Conflict)).Title);
        // A member is not a user: the user search still offers the person.
        Assert.Empty((await SearchAsync(administrator, "users", "member person")).Existing);
    }

    /// <summary>Existing accounts are found by SID under previous providers and when retired.</summary>
    [Fact]
    public async Task Existing_accounts_are_found_by_SID_under_previous_providers_and_when_retired()
    {
        using var administrator = await Site.SignInAsAdministratorAsync();
        var users = Site.Services.GetRequiredService<IUserService>();
        var externalLogins = Site.Services.GetRequiredService<IExternalLoginWithKeyService>();

        // Linked by an earlier sign-in under a provider the site no longer uses.
        var earlier = await Site.CreateEditorAsync("earlier.someone", "earlier@example.invalid", "Earlier Someone", "Local-test-only-Earlier71!");
        externalLogins.Save(earlier.Key, [new ExternalLogin(TestSignInLinkPolicy.PreviousProvider, EarlierSid)]);
        Assert.Equal(new ExistingAccount("user", earlier.Key, "earlier.someone", "active"),
            (await SearchAsync(administrator, "users", "earlier person")).Existing[EarlierSid]);
        Assert.Equal("Already has an account", (await ProblemAsync(await PostAsync(administrator, "users",
            new { sid = EarlierSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.Conflict)).Title);

        // Retired with the SID as its username: the site's retirement policy decides, and gives the advice.
        var retired = await Site.CreateEditorAsync("retired.person", "retired@example.invalid", "Retired Person", "Local-test-only-Retired72!");
        retired.Username = RetiredSid;
        retired.IsApproved = false;
        users.Save(retired);
        Assert.Equal("retired", (await SearchAsync(administrator, "users", "retired person")).Existing[RetiredSid].State);
        var problem = await ProblemAsync(await PostAsync(administrator, "users",
            new { sid = RetiredSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.Conflict);
        Assert.Equal("Retired account", problem.Title);
        Assert.EndsWith(TestRetirementPolicy.Advice, problem.Detail);
    }

    /// <summary>A name another account holds is a conflict.</summary>
    [Fact]
    public async Task A_name_another_account_holds_is_a_conflict()
    {
        using var administrator = await Site.SignInAsAdministratorAsync();
        var handMade = await Site.CreateEditorAsync("taken.name", "taken.by.hand@example.invalid", "Hand Made", "Local-test-only-Taken73!");

        var problem = await ProblemAsync(await PostAsync(administrator, "users",
            new { sid = TakenSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.Conflict);
        Assert.Equal("Username taken", problem.Title);
        // The site's sign-in links an unlinked account by name, so the person can sign in instead.
        Assert.Contains("their first sign-in links it", problem.Detail);
        Assert.Empty(Site.Logins(handMade.Key));

        // Linked to someone else, the account belongs to another person.
        Site.Services.GetRequiredService<IExternalLoginWithKeyService>().Save(handMade.Key, [new ExternalLogin(SiteFixture.UserProvider, "S-1-5-21-10-20-30-9999")]);
        Assert.Contains("linked to another Windows identity", (await ProblemAsync(await PostAsync(administrator, "users",
            new { sid = TakenSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.Conflict)).Detail);
    }

    /// <summary>Requests are checked before anything is created.</summary>
    [Fact]
    public async Task Requests_are_checked_before_anything_is_created()
    {
        using var administrator = await Site.SignInAsAdministratorAsync();

        using (var tooShort = await administrator.GetAsync($"{Api}/users/search?query=s", Cancellation))
        {
            var problem = await ProblemAsync(tooShort, HttpStatusCode.BadRequest);
            Assert.Equal("The directory refused the search", problem.Title);
            Assert.Contains("at least 2 characters", problem.Detail);
        }
        using (var empty = await administrator.GetAsync($"{Api}/members/search", Cancellation))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("No person chosen", (await ProblemAsync(await PostAsync(administrator, "users",
            new { sid = "TEST\\Test.User", userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.BadRequest)).Title);
        Assert.Equal("No person chosen", (await ProblemAsync(await PostAsync(administrator, "members",
            new { sid = "S-1-5-21-x", memberGroupNames = Array.Empty<string>() }), HttpStatusCode.BadRequest)).Title);
        Assert.Equal("Choose a user group", (await ProblemAsync(await PostAsync(administrator, "users",
            new { sid = UserSid, userGroupKeys = Array.Empty<Guid>() }), HttpStatusCode.BadRequest)).Title);
        Assert.Equal("Not in the directory", (await ProblemAsync(await PostAsync(administrator, "users",
            new { sid = "S-1-5-21-1-2-3-4242", userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.NotFound)).Title);
        Assert.Equal("Disabled in the directory", (await ProblemAsync(await PostAsync(administrator, "users",
            new { sid = DisabledSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.BadRequest)).Title);
        // Disabled people are never offered.
        Assert.Empty((await SearchAsync(administrator, "users", "gone person")).Results);
    }

    /// <summary>Directory refusals answer 400 and outages 503.</summary>
    [Fact]
    public async Task Directory_refusals_answer_400_and_outages_503()
    {
        using var administrator = await Site.SignInAsAdministratorAsync();

        using (var refused = await administrator.GetAsync($"{Api}/users/search?query={FakeDirectoryLookup.RefusedQuery}", Cancellation))
        {
            var problem = await ProblemAsync(refused, HttpStatusCode.BadRequest);
            Assert.Equal(("The directory refused the search", FakeDirectoryLookup.RefusalReason), problem);
        }
        using (var outage = await administrator.GetAsync($"{Api}/members/search?query={FakeDirectoryLookup.OutageQuery}", Cancellation))
        {
            var problem = await ProblemAsync(outage, HttpStatusCode.ServiceUnavailable);
            Assert.Equal("The directory is not reachable", problem.Title);
            Assert.Contains(FakeDirectoryLookup.OutageMessage, problem.Detail);
        }
        var lookup = await ProblemAsync(await PostAsync(administrator, "users",
            new { sid = FakeDirectoryLookup.OutageSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }), HttpStatusCode.ServiceUnavailable);
        Assert.Contains(FakeDirectoryLookup.OutageMessage, lookup.Detail);
    }

    /// <summary>Only people with access to the section may search or create.</summary>
    [Fact]
    public async Task Only_people_with_access_to_the_section_may_search_or_create()
    {
        const string password = "Local-test-only-Directory91!";
        using (var anonymous = Site.CreateClient())
        using (var response = await anonymous.GetAsync($"{Api}/users/search?query=test", Cancellation))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Editors have the Content and Media sections, but neither Users nor Members.
        await Site.CreateEditorAsync("directory.editor", "directory.editor@example.invalid", "Directory Editor", password);
        using var session = await Site.SignInAsync("directory.editor", password);
        foreach (var path in new[] { $"{Api}/users/search?query=test", $"{Api}/members/search?query=test" })
        {
            using var response = await session.GetAsync(path, Cancellation);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var response = await PostAsync(session, "users", new { sid = UserSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await PostAsync(session, "members", new { sid = MemberSid, memberGroupNames = Array.Empty<string>() }))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string kind, object body)
        => client.PostAsJsonAsync($"{Api}/{kind}", body, Cancellation);

    private static async Task<DirectorySearchResult> SearchAsync(HttpClient client, string kind, string query)
    {
        using var response = await client.GetAsync($"{Api}/{kind}/search?query={Uri.EscapeDataString(query)}", Cancellation);
        return await ReadAsync<DirectorySearchResult>(response, HttpStatusCode.OK);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
            return JsonSerializer.Deserialize<T>(body, Json)!;
        }
    }

    private static async Task<(string Title, string Detail)> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var problem = await ReadAsync<JsonElement>(response, expected);
        return (problem.GetProperty("title").GetString()!, problem.GetProperty("detail").GetString()!);
    }
}
