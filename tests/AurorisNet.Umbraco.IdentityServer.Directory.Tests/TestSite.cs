using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace DirectoryAccounts.Tests;

/// <summary>
/// A normally composed Umbraco site with SQLite and Kestrel. The package arrives through ordinary
/// composer discovery, as on a real site; the test adds its settings and replaces services, such as
/// the people lookup, before the site is built.
/// </summary>
internal sealed class TestSite(WebApplication app) : IAsyncDisposable
{
    public const string Email = "directory-admin@example.test";
    public const string Password = "Installed-only-Test4821!";
    private const string BackOfficeApi = "/umbraco/management/api/v1/security/back-office";

    public IServiceProvider Services => app.Services;
    public Uri Address { get; } = new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    /// <summary>A uniquely named temporary folder for one site, deleted by <see cref="DeleteRoot"/>.</summary>
    public static string CreateRoot() => Directory.CreateTempSubdirectory("directory-accounts-test-").FullName;

    /// <summary>
    /// Starts a site on <paramref name="root"/>, installing it unattended. The administrator is
    /// installed as <see cref="Email"/> with <see cref="Password"/>.
    /// </summary>
    /// <param name="root">The content root, from <see cref="CreateRoot"/>.</param>
    /// <param name="settings">Configuration added after the site's own, such as the <c>Directory</c> section.</param>
    /// <param name="services">Registers test services before the site is built.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<TestSite> StartAsync(string root, IDictionary<string, string?> settings, Action<IServiceCollection> services, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TestSite).Assembly.GetName().Name,
            ContentRootPath = root,
            EnvironmentName = "Production",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // Serve the referenced package's wwwroot, as a published site does from its own wwwroot.
        builder.WebHost.UseStaticWebAssets();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Umbraco:CMS:Global:Id"] = "2a7d3c51-8e4f-4b19-a6c0-5f9e1d2b7a40",
            ["Umbraco:CMS:Global:UseHttps"] = "false",
            ["Umbraco:CMS:Global:VersionCheckPeriod"] = "0",
            ["Umbraco:CMS:WebRouting:ApplicationUrlDetection"] = "FirstRequest",
            ["Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:0"] = "xunit.",
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UpgradeUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Directory administrator",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = Email,
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = Password,
        });
        builder.Configuration.AddInMemoryCollection(settings);
        // Use the real discovery path; never call this package's composer directly here.
        builder.CreateUmbracoBuilder().AddBackOffice().AddWebsite().AddComposers().Build();
        services(builder.Services);

        var app = builder.Build();
        try
        {
            await app.BootUmbracoAsync();
            app.UseUmbraco().WithMiddleware(u => { u.UseBackOffice(); u.UseWebsite(); })
                .WithEndpoints(u => { u.UseBackOfficeEndpoints(); u.UseWebsiteEndpoints(); });
            await app.StartAsync(cancellationToken);
            // Umbraco installs in a hosted service after the server starts, then raises its started notification.
            var installer = app.Services.GetServices<IHostedService>().OfType<BackgroundService>()
                .SingleOrDefault(service => service.GetType().Name == "UnattendedUpgradeBackgroundService");
            if (installer?.ExecuteTask is { } installation)
                await installation.WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
            var runtime = app.Services.GetRequiredService<IRuntimeState>();
            var waited = Stopwatch.StartNew();
            while (runtime.Level != RuntimeLevel.Run && runtime.Level != RuntimeLevel.BootFailed && waited.Elapsed < TimeSpan.FromMinutes(1))
                await Task.Delay(100, cancellationToken);
            Assert.True(runtime.Level == RuntimeLevel.Run, runtime.BootFailedException?.ToString() ?? runtime.Level.ToString());
            return new TestSite(app);
        }
        catch
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            await Serilog.Log.CloseAndFlushAsync();
            throw;
        }
    }

    /// <summary>A client of this site that keeps cookies and does not follow redirects.</summary>
    public HttpClient CreateClient()
        => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
        {
            BaseAddress = Address,
            Timeout = TimeSpan.FromSeconds(30),
        };

    public Task<HttpClient> SignInAsAdministratorAsync() => SignInAsync(Email, Password);

    /// <summary>
    /// Signs in the way the backoffice does: the password login sets the backoffice cookie, and the
    /// PKCE authorization code flow of the <c>umbraco-back-office</c> client issues the management
    /// API token. Umbraco keeps the code and the token in HTTP-only cookies and hands out a redacted
    /// placeholder, which the cookie container and the bearer header carry back.
    /// </summary>
    public async Task<HttpClient> SignInAsync(string username, string password)
    {
        var client = CreateClient();
        try
        {
            using (var login = await client.PostAsJsonAsync(BackOfficeApi + "/login", new { username, password }))
                Assert.True(login.IsSuccessStatusCode, $"The login of {username} returned {login.StatusCode}: {await login.Content.ReadAsStringAsync()}");

            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var redirectUri = new Uri(Address, "/umbraco/oauth_complete").ToString();
            using var authorize = await client.GetAsync(QueryHelpers.AddQueryString(BackOfficeApi + "/authorize", new Dictionary<string, string?>
            {
                ["client_id"] = Constants.OAuthClientIds.BackOffice,
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri,
                ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["code_challenge_method"] = "S256",
                ["state"] = "directory-accounts-test",
            }));
            var code = authorize is { StatusCode: HttpStatusCode.Redirect, Headers.Location: { } location }
                ? QueryHelpers.ParseQuery(new Uri(Address, location).Query)["code"].ToString() : "";
            Assert.False(string.IsNullOrEmpty(code), $"Authorize for {username} returned {authorize.StatusCode} to {authorize.Headers.Location} without a code.");

            using var token = await client.PostAsync(BackOfficeApi + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = Constants.OAuthClientIds.BackOffice,
                ["redirect_uri"] = redirectUri,
                ["code"] = code,
                ["code_verifier"] = verifier,
            }));
            var body = await token.Content.ReadAsStringAsync();
            Assert.True(token.IsSuccessStatusCode, $"The token for {username} returned {token.StatusCode}: {body}");
            using var document = JsonDocument.Parse(body);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", document.RootElement.GetProperty("access_token").GetString());
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>Creates an approved backoffice user in Editors, with a password for the login form.</summary>
    public async Task<IUser> CreateEditorAsync(string username, string email, string name, string password)
    {
        using var scope = Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<IUserService>().CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel
        {
            UserName = username,
            Email = email,
            Name = name,
            Kind = UserKind.Default,
            UserGroupKeys = new HashSet<Guid> { Constants.Security.EditorGroupKey },
        }, approveUser: true);
        Assert.True(created.Success, created.Status.ToString());
        var user = created.Result.CreatedUser!;
        var manager = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        var identity = (await manager.FindByIdAsync(user.Key.ToString()))!;
        // Umbraco gives a created user a generated password, so set it the way a reset does.
        var reset = await manager.ChangePasswordWithResetAsync(identity.Id, await manager.GeneratePasswordResetTokenAsync(identity), password);
        Assert.True(reset.Succeeded, string.Join("; ", reset.Errors.Select(error => error.Description)));
        return user;
    }

    /// <summary>The account's external logins as (provider, provider key) pairs, in a stable order.</summary>
    public IReadOnlyList<(string Provider, string Key)> Logins(Guid key)
        => Services.GetRequiredService<IExternalLoginWithKeyService>().GetExternalLogins(key)
            .Select(login => (login.LoginProvider, login.ProviderKey))
            .OrderBy(login => login.LoginProvider, StringComparer.Ordinal)
            .ToList();

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync(CancellationToken.None);
        await app.DisposeAsync();
        await Serilog.Log.CloseAndFlushAsync();
    }

    /// <summary>Deletes a folder from <see cref="CreateRoot"/>, and nothing else.</summary>
    public static void DeleteRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullRoot).StartsWith("directory-accounts-test-", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected test directory: {fullRoot}");
        // SQLite can hold the file briefly after the site stops.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(fullRoot, recursive: true); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(250); }
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
