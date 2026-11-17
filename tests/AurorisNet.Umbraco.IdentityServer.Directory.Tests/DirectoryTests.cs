using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace DirectoryAccounts.Tests;

/// <summary>
/// The client for an identity server's directory API, against a fake server that answers as the
/// identity server does: camelCase records, problem details for a refusal or an outage.
/// </summary>
public sealed class IdentityServerAccountDirectoryTests
{
    private const string Authority = "https://login.example/IdentityServer";
    private const string Key = "a-test-key-of-24-chars!!";

    private const string People = """
        [
          {"sid":"S-1-5-21-10-20-30-1001","guid":"6f1c0f3e-5d9a-4c2b-9a6e-2b4a7f3d0c01","accountName":"DOMAIN\\Test.User","userName":"Test.User",
           "displayName":"Test User","givenName":"Test","surname":"User","email":"test.user@example.invalid","telephone":null,"mobile":null,"fax":null,
           "office":null,"title":"Test Role","department":"Test Team","employeeId":null,"enabled":true},
          {"sid":"S-1-5-21-10-20-30-1002","guid":"6f1c0f3e-5d9a-4c2b-9a6e-2b4a7f3d0c02","accountName":"DOMAIN\\Test.Old","userName":"Test.Old",
           "displayName":"Test Old","givenName":null,"surname":null,"email":null,"telephone":null,"mobile":null,"fax":null,
           "office":null,"title":null,"department":null,"employeeId":null,"enabled":false}
        ]
        """;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A search asks the whole directory with the key and keeps enabled people.</summary>
    [Fact]
    public async Task A_search_asks_the_whole_directory_with_the_key_and_keeps_enabled_people()
    {
        var server = new FakeServer(_ => Answer(HttpStatusCode.OK, People));
        var people = await Client(server, Key).SearchAsync("test user", 20, Cancellation);

        var person = Assert.Single(people);
        Assert.Equal("S-1-5-21-10-20-30-1001", person.Sid);
        Assert.Equal("DOMAIN\\Test.User", person.AccountName);
        Assert.Equal("Test.User", person.UserName);
        Assert.Equal("Test User", person.DisplayName);
        Assert.Equal("test.user@example.invalid", person.Email);
        Assert.Equal("Test Team", person.Department);
        Assert.Equal("Test Role", person.Title);
        // The records carry no attributes, which the lookup leaves null when none were asked for.
        Assert.Null(person.Attributes);
        var request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{Authority}/directory/users?query=test%20user&take=20&scope=global&includeDisabled=true", request.RequestUri!.AbsoluteUri);
        Assert.Equal(Key, Assert.Single(request.Headers.GetValues(IdentityServerAccountDirectory.ApiKeyHeader)));
    }

    /// <summary>A search asks for at most fifty people.</summary>
    [Fact]
    public async Task A_search_asks_for_at_most_fifty_people()
    {
        var server = new FakeServer(_ => Answer(HttpStatusCode.OK, "[]"));
        await Client(server, Key).SearchAsync("test", 500, Cancellation);
        await Client(server, Key).SearchAsync("test", 0, Cancellation);
        Assert.Contains("&take=50&", server.Requests[0].RequestUri!.Query);
        Assert.Contains("&take=1&", server.Requests[1].RequestUri!.Query);
    }

    /// <summary>A lookup reads the detail and treats 404 as nobody.</summary>
    [Fact]
    public async Task A_lookup_reads_the_detail_and_treats_404_as_nobody()
    {
        const string detail = """
            {"groups":["Test Team Users","Report Users"],"sid":"S-1-5-21-10-20-30-1001","guid":"6f1c0f3e-5d9a-4c2b-9a6e-2b4a7f3d0c01",
             "accountName":"DOMAIN\\Test.User","userName":"Test.User","displayName":"Test User","email":null,"enabled":true}
            """;
        var server = new FakeServer(request => request.RequestUri!.AbsolutePath.EndsWith("-1001", StringComparison.Ordinal)
            ? Answer(HttpStatusCode.OK, detail)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var directory = Client(server, apiKey: null);

        var person = await directory.FindAsync("S-1-5-21-10-20-30-1001", Cancellation);
        Assert.NotNull(person);
        Assert.Equal("DOMAIN\\Test.User", person.AccountName);
        Assert.Null(person.Email);
        Assert.Equal(["Test Team Users", "Report Users"], person.Groups);
        Assert.Null(await directory.FindAsync("S-1-5-21-10-20-30-1009", Cancellation));
        Assert.Equal($"{Authority}/directory/users/S-1-5-21-10-20-30-1001", server.Requests[0].RequestUri!.AbsoluteUri);
        // Without a key the site authenticates as itself, so no key header is sent.
        Assert.All(server.Requests, request => Assert.False(request.Headers.Contains(IdentityServerAccountDirectory.ApiKeyHeader)));
        await Assert.ThrowsAsync<DirectoryQueryException>(() => directory.FindAsync("DOMAIN\\Test.User", Cancellation));
        Assert.Equal(2, server.Requests.Count);
    }

    /// <summary>Refusals and outages become the shared exceptions.</summary>
    [Fact]
    public async Task Refusals_and_outages_become_the_shared_exceptions()
    {
        var refused = Client(new FakeServer(_ => Answer(HttpStatusCode.BadRequest,
            """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"The directory request was refused.","status":400,"detail":"scope must be local or global."}""")), Key);
        Assert.Equal("scope must be local or global.", (await Assert.ThrowsAsync<DirectoryQueryException>(() => refused.SearchAsync("test", 20, Cancellation))).Message);

        var down = Client(new FakeServer(_ => Answer(HttpStatusCode.ServiceUnavailable,
            """{"title":"The directory is unavailable.","status":503,"detail":"Active Directory could not be queried: The server is not operational."}""")), Key);
        var outage = await Assert.ThrowsAsync<DirectoryUnavailableException>(() => down.SearchAsync("test", 20, Cancellation));
        Assert.Contains("The server is not operational.", outage.Message);
        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => down.FindAsync("S-1-5-21-10-20-30-1001", Cancellation));

        var wrongKey = Client(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), Key);
        Assert.Contains("Directory:IdentityServerApiKey", (await Assert.ThrowsAsync<DirectoryUnavailableException>(() => wrongKey.SearchAsync("test", 20, Cancellation))).Message);

        var noApi = Client(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), Key);
        Assert.Contains("no directory API", (await Assert.ThrowsAsync<DirectoryUnavailableException>(() => noApi.SearchAsync("test", 20, Cancellation))).Message);

        var unreachable = Client(new FakeServer(_ => throw new HttpRequestException("No such host is known.")), Key);
        Assert.Contains("No such host is known.", (await Assert.ThrowsAsync<DirectoryUnavailableException>(() => unreachable.SearchAsync("test", 20, Cancellation))).Message);

        var garbled = Client(new FakeServer(_ => Answer(HttpStatusCode.OK, "<html>Sign in</html>")), Key);
        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => garbled.SearchAsync("test", 20, Cancellation));

        // A term that is too short never reaches the server.
        var server = new FakeServer(_ => Answer(HttpStatusCode.OK, "[]"));
        await Assert.ThrowsAsync<DirectoryQueryException>(() => Client(server, Key).SearchAsync("b", 20, Cancellation));
        Assert.Empty(server.Requests);
    }

    /// <summary>A missing or invalid authority is an unavailable directory.</summary>
    [Fact]
    public async Task A_missing_or_invalid_authority_is_an_unavailable_directory()
    {
        var server = new FakeServer(_ => Answer(HttpStatusCode.OK, "[]"));
        var missing = new IdentityServerAccountDirectory(new FakeHttpClientFactory(server), Microsoft.Extensions.Options.Options.Create(new AccountDirectorySettings()));
        Assert.Contains("Directory:IdentityServerAuthority", (await Assert.ThrowsAsync<DirectoryUnavailableException>(() => missing.SearchAsync("test", 20, Cancellation))).Message);
        var relative = new IdentityServerAccountDirectory(new FakeHttpClientFactory(server),
            Microsoft.Extensions.Options.Options.Create(new AccountDirectorySettings { IdentityServerAuthority = "login/IdentityServer" }));
        Assert.Contains("not an absolute URL", (await Assert.ThrowsAsync<DirectoryUnavailableException>(() => relative.FindAsync("S-1-5-21-10-20-30-1001", Cancellation))).Message);
        Assert.Empty(server.Requests);
    }

    /// <summary>The handler uses the sites Windows identity only without a key.</summary>
    [Fact]
    public void The_handler_uses_the_sites_Windows_identity_only_without_a_key()
    {
        using var withKey = Assert.IsType<HttpClientHandler>(IdentityServerAccountDirectory.CreateHandler(new AccountDirectorySettings { IdentityServerApiKey = Key }));
        Assert.False(withKey.UseDefaultCredentials);
        Assert.Null(withKey.ServerCertificateCustomValidationCallback);
        using var withoutKey = Assert.IsType<HttpClientHandler>(IdentityServerAccountDirectory.CreateHandler(new AccountDirectorySettings { IdentityServerAcceptAnyServerCertificate = true }));
        Assert.True(withoutKey.UseDefaultCredentials);
        Assert.NotNull(withoutKey.ServerCertificateCustomValidationCallback);
    }

    private static IdentityServerAccountDirectory Client(FakeServer server, string? apiKey)
        => new(new FakeHttpClientFactory(server), Microsoft.Extensions.Options.Options.Create(
            new AccountDirectorySettings { IdentityServerAuthority = Authority, IdentityServerApiKey = apiKey }));

    private static HttpResponseMessage Answer(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, status == HttpStatusCode.OK ? "application/json" : "application/problem+json") };

    /// <summary>Answers every request through a function and keeps the requests for inspection.</summary>
    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>Active Directory through the people lookup, here a fake one.</summary>
public sealed class ActiveDirectoryAccountDirectoryTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A search asks the global scope for enabled people only.</summary>
    [Fact]
    public async Task A_search_asks_the_global_scope_for_enabled_people_only()
    {
        var lookup = new FakeDirectoryLookup();
        lookup.People.Add(FakeDirectoryLookup.Person("S-1-5-21-10-20-30-1001", "Test.User", "Test User"));
        lookup.People.Add(FakeDirectoryLookup.Person("S-1-5-21-10-20-30-1002", "Test.Old", "Test Old", enabled: false));
        var directory = new ActiveDirectoryAccountDirectory(lookup);

        Assert.Equal(["Test User"], (await directory.SearchAsync("test", 200, Cancellation)).Select(person => person.DisplayName));
        Assert.Equal(("test", 50, DirectoryScope.Global, false), Assert.Single(lookup.Searches));
    }

    /// <summary>A lookup takes a SID only.</summary>
    [Fact]
    public async Task A_lookup_takes_a_SID_only()
    {
        var lookup = new FakeDirectoryLookup();
        lookup.People.Add(FakeDirectoryLookup.Person("S-1-5-21-10-20-30-1001", "Test.User", "Test User", groups: ["Test Team Users"]));
        var directory = new ActiveDirectoryAccountDirectory(lookup);

        Assert.Equal(["Test Team Users"], (await directory.FindAsync("S-1-5-21-10-20-30-1001", Cancellation))!.Groups);
        Assert.Null(await directory.FindAsync("S-1-5-21-10-20-30-1009", Cancellation));
        // The lookup would accept DOMAIN\user or a GUID; the account directory does not.
        await Assert.ThrowsAsync<DirectoryQueryException>(() => directory.FindAsync("TEST\\Test.User", Cancellation));
        await Assert.ThrowsAsync<DirectoryQueryException>(() => directory.FindAsync(Guid.NewGuid().ToString(), Cancellation));
    }
}

/// <summary>
/// The computer's local accounts, read for real. The person searched for is whoever runs the tests,
/// which is a local account on a computer outside a domain.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalAccountDirectoryTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The current user is found by name and by SID.</summary>
    [Fact]
    public async Task The_current_user_is_found_by_name_and_by_SID()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Local accounts are read through the WinNT provider, on Windows.");
        Assert.SkipUnless(AccountDirectorySettings.IsOutsideDomain, "The current user is a domain account, which the local accounts do not list.");
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var directory = new LocalAccountDirectory();

        var people = await directory.SearchAsync(Environment.UserName, IAccountDirectory.MaxTake, Cancellation);
        var person = Assert.Single(people, candidate => candidate.Sid == sid);
        Assert.Equal($"{Environment.MachineName}\\{Environment.UserName}", person.AccountName, ignoreCase: true);
        Assert.Equal(Environment.UserName, person.UserName, ignoreCase: true);
        Assert.True(person.Enabled);
        Assert.Null(person.Email);
        Assert.All(people, candidate => Assert.True(candidate.Enabled));

        var detail = await directory.FindAsync(sid, Cancellation);
        Assert.NotNull(detail);
        Assert.Equal(person.AccountName, detail.AccountName);
        Assert.NotEmpty(detail.Groups);
    }

    /// <summary>Requests follow the shared rules.</summary>
    [Fact]
    public async Task Requests_follow_the_shared_rules()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Local accounts are read through the WinNT provider, on Windows.");
        var directory = new LocalAccountDirectory();
        Assert.Null(await directory.FindAsync("S-1-5-21-1-2-3-4242", Cancellation));
        await Assert.ThrowsAsync<DirectoryQueryException>(() => directory.FindAsync(Environment.UserName, Cancellation));
        await Assert.ThrowsAsync<DirectoryQueryException>(() => directory.SearchAsync("a", 20, Cancellation));
        Assert.True((await directory.SearchAsync(Environment.UserName, 1, Cancellation)).Count <= 1);
    }
}
