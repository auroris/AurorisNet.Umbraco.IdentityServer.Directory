using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DirectoryAccounts;

/// <summary>
/// The directory API of the identity server the site signs in through
/// (<c>{authority}/directory/users</c>), for a site whose people are that server's: only the server
/// is sure to see the directory its sign-ins come from, which may be another domain than the
/// site's, or none. The answers are <see cref="DirectoryUser"/> and <see cref="DirectoryUserDetail"/>
/// records, serialized camelCase.
/// </summary>
/// <remarks>
/// The site authenticates with <see cref="AccountDirectorySettings.IdentityServerApiKey"/> in the
/// <c>X-Api-Key</c> header when one is configured, and otherwise as its own Windows identity (the
/// application pool account), which the server must then admit. The server's refusals map onto the
/// directory exceptions: 400 becomes <see cref="DirectoryQueryException"/> with the server's
/// reason, and 503, a refused key and an unreachable server <see cref="DirectoryUnavailableException"/>.
/// </remarks>
public sealed class IdentityServerAccountDirectory(IHttpClientFactory httpClients, IOptions<AccountDirectorySettings> settings) : IAccountDirectory
{
    /// <summary>The named <see cref="HttpClient"/> registered for these requests; see <see cref="CreateHandler"/>.</summary>
    public const string HttpClientName = "DirectoryAccounts.IdentityServer";

    /// <summary>The request header the identity server's directory API reads an API key from.</summary>
    public const string ApiKeyHeader = "X-Api-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private AccountDirectorySettings Settings => settings.Value;

    private bool HasApiKey => !string.IsNullOrWhiteSpace(Settings.IdentityServerApiKey);

    /// <summary>
    /// The handler behind <see cref="HttpClientName"/>: the site's Windows credentials when it has no
    /// API key, and certificate leniency for an intranet server with a self-signed certificate when
    /// <see cref="AccountDirectorySettings.IdentityServerAcceptAnyServerCertificate"/> asks for it.
    /// </summary>
    public static HttpMessageHandler CreateHandler(AccountDirectorySettings settings)
    {
        var handler = new HttpClientHandler { UseDefaultCredentials = string.IsNullOrWhiteSpace(settings.IdentityServerApiKey) };
        if (settings.IdentityServerAcceptAnyServerCertificate)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return handler;
    }

    /// <remarks>
    /// Without <c>includeDisabled</c> the server applies its listing rules, such as a telephone
    /// directory's, which hide accounts an administrator may well need, so they are lifted and
    /// disabled accounts are dropped here instead.
    /// </remarks>
    public async Task<IReadOnlyList<DirectoryUser>> SearchAsync(string query, int take, CancellationToken cancellationToken = default)
    {
        AccountDirectoryRules.SearchTerms(query);
        var path = "directory/users?query=" + Uri.EscapeDataString(query)
            + "&take=" + Math.Clamp(take, 1, IAccountDirectory.MaxTake)
            + "&scope=global&includeDisabled=true";
        var people = await GetAsync<List<DirectoryUser>>(path, notFoundIsEmpty: false, cancellationToken);
        return people?.Where(person => person.Enabled).ToList() ?? [];
    }

    /// <inheritdoc />
    public Task<DirectoryUserDetail?> FindAsync(string sid, CancellationToken cancellationToken = default)
    {
        AccountDirectoryRules.EnsureSid(sid);
        return GetAsync<DirectoryUserDetail>("directory/users/" + Uri.EscapeDataString(sid), notFoundIsEmpty: true, cancellationToken);
    }

    /// <summary>Sends one GET to the directory API and reads the answer, mapping every failure onto the shared exceptions.</summary>
    /// <param name="path">The path under the authority, with its query string.</param>
    /// <param name="notFoundIsEmpty">Whether 404 means "no such account" (a lookup) rather than "no directory API here".</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    private async Task<T?> GetAsync<T>(string path, bool notFoundIsEmpty, CancellationToken cancellationToken) where T : class
    {
        var authority = Settings.IdentityServerAuthority;
        var address = new Uri(BaseAddress(), path);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.ParseAdd("application/json");
        if (HasApiKey) request.Headers.Add(ApiKeyHeader, Settings.IdentityServerApiKey!.Trim());

        using var client = httpClients.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw Unavailable($"The identity server at {authority} could not be reached: {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable($"The identity server at {authority} did not answer in time.", exception);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                        ?? throw new JsonException("The answer was empty.");
                }
                catch (JsonException exception)
                {
                    throw Unavailable($"The identity server at {authority} answered {path} with something other than directory accounts: {exception.Message}", exception);
                }
            }

            var reason = await ReasonAsync(response, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound when notFoundIsEmpty:
                    return null;
                case HttpStatusCode.BadRequest:
                    throw new DirectoryQueryException(reason ?? "The identity server refused the directory request.");
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw Unavailable($"The identity server at {authority} refused this site's directory request ({(int)response.StatusCode}). "
                        + (HasApiKey
                            ? "Check that Directory:IdentityServerApiKey is one of the keys the server's directory API accepts."
                            : $"Configure Directory:IdentityServerApiKey, or let {Environment.UserDomainName}\\{Environment.UserName} use the server's directory API."));
                case HttpStatusCode.NotFound:
                    throw Unavailable($"The identity server at {authority} has no directory API; it needs a version that serves /directory/users.");
                default:
                    throw Unavailable($"The identity server at {authority} could not search the directory ({(int)response.StatusCode}): {reason ?? response.ReasonPhrase}");
            }
        }
    }

    /// <summary>The authority with a trailing slash, so relative paths land under a server that lives in a sub-application.</summary>
    /// <exception cref="DirectoryUnavailableException">The authority is empty or not an absolute URL.</exception>
    private Uri BaseAddress()
    {
        var authority = Settings.IdentityServerAuthority?.Trim() ?? "";
        if (authority.Length == 0)
            throw Unavailable("Directory:Source is IdentityServer, but Directory:IdentityServerAuthority is empty. Set it to the identity server's address.");
        if (!Uri.TryCreate(authority.EndsWith('/') ? authority : authority + "/", UriKind.Absolute, out var address))
            throw Unavailable($"Directory:IdentityServerAuthority is not an absolute URL: {authority}");
        return address;
    }

    /// <summary>The <c>detail</c> of a problem-details answer, which the directory API writes for the person who asked.</summary>
    private static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var problem = JsonDocument.Parse(text);
            return problem.RootElement.ValueKind == JsonValueKind.Object
                && problem.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                    ? detail.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DirectoryUnavailableException Unavailable(string message, Exception? exception = null) => new(message, exception);
}
