using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace DirectoryAccounts;

/// <summary>Adds "Add from directory" to an Umbraco site through ordinary composer discovery.</summary>
public sealed class DirectoryAccountsComposer : IComposer
{
    /// <inheritdoc />
    public void Compose(IUmbracoBuilder builder) => builder.AddAccountDirectory();
}

/// <summary>
/// Registers the directory that backoffice users and members are created from, and the service and
/// management API that create them. Settings come from the <c>Directory</c> section: the people
/// lookup's <see cref="DirectoryOptions"/> and this package's <see cref="AccountDirectorySettings"/>.
/// </summary>
public static class AccountDirectoryExtensions
{
    /// <summary>
    /// Adds <see cref="IAccountDirectory"/>, <see cref="DirectoryAccountService"/> and the default
    /// <see cref="IRetiredAccountPolicy"/> and <see cref="ISignInLinkPolicy"/>. Nothing touches a
    /// directory until someone searches it, so a directory that is down or misconfigured never stops
    /// the site from starting; the search reports it instead. Calling it again does nothing.
    /// </summary>
    /// <remarks>
    /// The <see cref="DirectoryAccountsComposer"/> calls this, so a site needs no code. A site
    /// replaces a part by registering its own <see cref="IDirectoryLookup"/>,
    /// <see cref="IAccountDirectory"/>, <see cref="IRetiredAccountPolicy"/> or
    /// <see cref="ISignInLinkPolicy"/>, before or after this call: the defaults are added only when
    /// none is registered yet, and a later registration wins.
    /// </remarks>
    public static IUmbracoBuilder AddAccountDirectory(this IUmbracoBuilder builder)
    {
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(DirectoryAccountService)))
            return builder;

        builder.Services.AddOptions<DirectoryOptions>().BindConfiguration(DirectoryOptions.SectionName);
        builder.Services.AddOptions<AccountDirectorySettings>().BindConfiguration(AccountDirectorySettings.SectionName);
        builder.Services.TryAddSingleton<IDirectoryLookup>(services =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Active Directory lookups in process need Windows.");
            return ActivatorUtilities.CreateInstance<ActiveDirectoryLookup>(services);
        });
        builder.Services.AddHttpClient(IdentityServerAccountDirectory.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(services =>
                IdentityServerAccountDirectory.CreateHandler(services.GetRequiredService<IOptions<AccountDirectorySettings>>().Value));
        builder.Services.TryAddSingleton<IAccountDirectory>(CreateDirectory);
        builder.Services.TryAddSingleton<IRetiredAccountPolicy, NoRetiredAccounts>();
        builder.Services.TryAddSingleton<ISignInLinkPolicy, DefaultSignInLinkPolicy>();
        builder.Services.AddScoped<DirectoryAccountService>();
        builder.AddNotificationHandler<UmbracoApplicationStartedNotification, AccountDirectoryStartupLog>();
        return builder;
    }

    /// <summary>The source chosen at startup, described for the log and for whoever reads it.</summary>
    public static string Describe(AccountDirectorySource source, AccountDirectorySettings settings, DirectoryOptions lookup)
        => source switch
        {
            AccountDirectorySource.LocalAccounts => settings.Source == AccountDirectorySource.LocalAccounts
                ? $"the local accounts of {Environment.MachineName}, as Directory:Source asks"
                : settings.UseLocalAccounts == true
                    ? $"the local accounts of {Environment.MachineName}, as Directory:UseLocalAccounts asks"
                    : $"the local accounts of {Environment.MachineName}, which is not in a domain (set Directory:Source to ActiveDirectory to query Active Directory)",
            AccountDirectorySource.IdentityServer => $"the directory API of the identity server at {settings.IdentityServerAuthority}, "
                + (string.IsNullOrWhiteSpace(settings.IdentityServerApiKey)
                    ? $"as {Environment.UserDomainName}\\{Environment.UserName}"
                    : "with the key in Directory:IdentityServerApiKey"),
            _ => $"Active Directory, queried as {Environment.UserDomainName}\\{Environment.UserName} "
                + (string.IsNullOrWhiteSpace(lookup.UserRootGlobal) ? "across the whole domain" : $"under {lookup.UserRootGlobal}"),
        };

    private static IAccountDirectory CreateDirectory(IServiceProvider services)
    {
        var source = AccountDirectorySettings.Choose(services.GetRequiredService<IOptions<AccountDirectorySettings>>().Value);
        if (source == AccountDirectorySource.IdentityServer)
            return ActivatorUtilities.CreateInstance<IdentityServerAccountDirectory>(services);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Directory lookups in process need Windows; set Directory:IdentityServerAuthority to use an identity server's directory instead.");
        return source == AccountDirectorySource.LocalAccounts
            ? new LocalAccountDirectory()
            : ActivatorUtilities.CreateInstance<ActiveDirectoryAccountDirectory>(services);
    }
}

/// <summary>Says at startup where accounts will be created from, without asking that directory anything.</summary>
public sealed class AccountDirectoryStartupLog(
    IOptions<AccountDirectorySettings> settings,
    IOptions<DirectoryOptions> lookup,
    ILogger<AccountDirectoryStartupLog> logger) : INotificationHandler<UmbracoApplicationStartedNotification>
{
    /// <inheritdoc />
    public void Handle(UmbracoApplicationStartedNotification notification)
    {
        var source = AccountDirectorySettings.Choose(settings.Value);
        logger.LogInformation("Accounts are created from {Directory}.",
            AccountDirectoryExtensions.Describe(source, settings.Value, lookup.Value));
    }
}
