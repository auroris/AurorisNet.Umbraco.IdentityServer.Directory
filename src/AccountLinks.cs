using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace DirectoryAccounts;

/// <summary>The Umbraco operations account creation needs beyond Umbraco's own services.</summary>
internal static class AccountLinks
{
    /// <summary>
    /// Records a Windows SID as an account's external login under each of the providers that does
    /// not have it yet, keeping the account's other logins, so the SID is stored before the person
    /// first signs in and that sign-in finds the account by it.
    /// </summary>
    /// <param name="externalLogins">Umbraco's external logins of users and members.</param>
    /// <param name="logger">Logs each link added.</param>
    /// <param name="accountKey">The user or member key.</param>
    /// <param name="providerKey">The Windows SID, stored as the login's provider key.</param>
    /// <param name="providers">The providers to link under, stored as the logins' provider names.</param>
    public static void Link(IExternalLoginWithKeyService externalLogins, ILogger logger, Guid accountKey, string providerKey, IEnumerable<string> providers)
    {
        var existing = externalLogins.GetExternalLogins(accountKey).ToList();
        var missing = providers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(provider => !existing.Any(login => string.Equals(login.LoginProvider, provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(login.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count == 0) return;
        var logins = existing
            .Select(login => (IExternalLogin)new ExternalLogin(login.LoginProvider, login.ProviderKey, login.UserData))
            .Concat(missing.Select(provider => new ExternalLogin(provider, providerKey)))
            .ToList();
        externalLogins.Save(accountKey, logins);
        foreach (var provider in missing)
            logger.LogInformation("Linked {Provider} key {ProviderKey} to account {Key}.", provider, providerKey, accountKey);
    }

    /// <summary>
    /// Gives a new member a private random password, so the member has a usable identity until an
    /// administrator resets it. A password a member-creation notification handler already set stays.
    /// </summary>
    public static async Task InitializeMemberPasswordAsync(IMember member, IMemberManager manager)
    {
        // A username lookup caches an uncommitted member even if creation is rolled back.
        var identity = await manager.FindByIdAsync(member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? throw new InvalidOperationException($"The new member {member.Username} could not be loaded.");
        if (!string.IsNullOrEmpty(identity.PasswordHash)) return;
        var result = await manager.AddPasswordAsync(identity, manager.GeneratePassword());
        if (!result.Succeeded)
            throw new InvalidOperationException($"The password for member {member.Username} could not be initialized: "
                + string.Join("; ", result.Errors.Select(error => error.Description)));
    }
}
