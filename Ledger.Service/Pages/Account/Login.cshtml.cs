using Ledger.Repository.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ledger.Service.Pages.Account;

/// <summary>
/// The password sign-in page, the first of two steps. A correct password only continues to the one-time code page; a login
/// without a confirmed second factor, or without a readable authenticator secret, can never sign in. Every way a sign-in can fail, including an unknown user name and a
/// locked login, shows the same message, so the page never reveals which logins exist.
/// </summary>
[AllowAnonymous]
public class LoginModel(
    SignInManager<LedgerUserEntity> signInManager,
    UserManager<LedgerUserEntity> users) : PageModel
{
    /// <summary>Where to continue after signing in; only an address on this host is followed.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The login name the person typed.</summary>
    [BindProperty]
    public string? UserName { get; set; }

    /// <summary>The password the person typed.</summary>
    [BindProperty]
    public string? Password { get; set; }

    /// <summary>Whether the last attempt failed.</summary>
    public bool Failed { get; private set; }

    /// <summary>Shows the form.</summary>
    public void OnGet()
    {
    }

    /// <summary>Checks the password with lockout on repeated failures and continues to the one-time code page on success.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrEmpty(UserName) || string.IsNullOrEmpty(Password))
        {
            Failed = true;
            return Page();
        }

        var user = await users.FindByNameAsync(UserName);

        if (user is null || !await users.GetTwoFactorEnabledAsync(user) || string.IsNullOrEmpty(await users.GetAuthenticatorKeyAsync(user)))
        {
            users.PasswordHasher.HashPassword(new LedgerUserEntity(), Password);
            Failed = true;
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(user, Password, isPersistent: false, lockoutOnFailure: true);

        if (!result.RequiresTwoFactor)
        {
            Failed = true;
            return Page();
        }

        var query = !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? QueryString.Create("returnUrl", ReturnUrl)
            : QueryString.Empty;

        return LocalRedirect("/account/totp" + query);
    }
}
