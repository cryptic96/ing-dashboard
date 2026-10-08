using Ledger.Domain.Auth;
using Ledger.Repository.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ledger.Service.Pages.Account;

/// <summary>
/// The one-time code page, the second step of sign-in. The code must be valid for the login that just entered its password and
/// must belong to a later 30-second time step than any code the login used before, and every refusal counts towards the
/// login's lockout and shows the same message as a wrong password.
/// </summary>
[AllowAnonymous]
public class TotpModel(
    SignInManager<LedgerUserEntity> signInManager,
    UserManager<LedgerUserEntity> users,
    ITotpReplayStore replayStore,
    TimeProvider timeProvider) : PageModel
{
    private const string AuthenticationMethod = "mfa";

    /// <summary>Where to continue after signing in; only an address on this host is followed.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The code the person typed.</summary>
    [BindProperty]
    public string? Code { get; set; }

    /// <summary>Whether the last attempt failed.</summary>
    public bool Failed { get; private set; }

    /// <summary>Whether the person is now signed in and has nowhere further to go.</summary>
    public bool SignedIn { get; private set; }

    /// <summary>Shows the form, or sends the person back to the password page when no password was entered or it timed out.</summary>
    public async Task<IActionResult> OnGetAsync()
    {
        return await signInManager.GetTwoFactorAuthenticationUserAsync() is null ? BackToPassword() : Page();
    }

    /// <summary>Verifies the code, claims it, and completes the sign-in.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();

        if (user is null)
        {
            return BackToPassword();
        }

        var code = NormaliseCode(Code);

        if (await users.IsLockedOutAsync(user))
        {
            return await RefuseAsync(user, countFailure: false);
        }

        if (!TotpCodes.IsWellFormed(code))
        {
            return await RefuseAsync(user, countFailure: true);
        }

        var step = TotpCodes.MatchTimeStep(await users.GetAuthenticatorKeyAsync(user), code, timeProvider.GetUtcNow());

        if (step is null)
        {
            return await RefuseAsync(user, countFailure: true);
        }

        var claimed = await replayStore.TryClaimAsync(user.Id, step.Value, cancellationToken);

        if (!claimed)
        {
            return await RefuseAsync(user, countFailure: true);
        }

        await users.ResetAccessFailedCountAsync(user);
        await signInManager.SignInAsync(user, new AuthenticationProperties { IsPersistent = false }, AuthenticationMethod);
        await HttpContext.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        SignedIn = true;
        return Page();
    }

    private async Task<IActionResult> RefuseAsync(LedgerUserEntity user, bool countFailure)
    {
        if (countFailure)
        {
            await users.AccessFailedAsync(user);
        }

        Failed = true;
        return Page();
    }

    private RedirectResult BackToPassword()
    {
        var query = !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? QueryString.Create("returnUrl", ReturnUrl)
            : QueryString.Empty;

        return Redirect("/account/login" + query);
    }

    private static string NormaliseCode(string? code)
    {
        return (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
    }
}
