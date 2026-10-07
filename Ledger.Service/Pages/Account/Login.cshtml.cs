using Ledger.Repository.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ledger.Service.Pages.Account;

/// <summary>
/// The password sign-in page. Every way a sign-in can fail, including an unknown user name, shows the same message, so the page
/// never reveals which logins exist.
/// </summary>
[AllowAnonymous]
public class LoginModel(SignInManager<LedgerUserEntity> signInManager) : PageModel
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

    /// <summary>Whether the person is now signed in and has nowhere further to go.</summary>
    public bool SignedIn { get; private set; }

    /// <summary>Shows the form.</summary>
    public void OnGet()
    {
    }

    /// <summary>Checks the password with lockout on repeated failures and continues to the local return address on success.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrEmpty(UserName) || string.IsNullOrEmpty(Password))
        {
            Failed = true;
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(
            UserName,
            Password,
            isPersistent: false,
            lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            Failed = true;
            return Page();
        }

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        SignedIn = true;
        return Page();
    }
}
