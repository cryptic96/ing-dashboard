using FluentAssertions;
using Ledger.Repository.Entities;
using Ledger.Service.Pages.Account;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Ledger.UnitTests.Hosting;

/// <summary>Verifies the password page never leaves a sign-in behind when the password check does not ask for a second factor.</summary>
[Trait("Category", "OAuth")]
public class LoginPageTests
{
    [Fact]
    public async Task A_password_check_that_signs_in_without_a_second_factor_is_a_failure_and_withdraws_the_sign_in()
    {
        var user = new LedgerUserEntity { Id = Guid.NewGuid(), UserName = "household-admin", TwoFactorEnabled = true };
        var users = CreateUserManager();
        users.FindByNameAsync("household-admin").Returns(user);
        users.GetTwoFactorEnabledAsync(user).Returns(true);
        users.GetAuthenticatorKeyAsync(user).Returns("KEYKEYKEYKEYKEYK");

        var authentication = Substitute.For<IAuthenticationService>();
        var signIn = CreateSignInManager(users);
        signIn.PasswordSignInAsync(user, "a long enough password", false, true).Returns(Microsoft.AspNetCore.Identity.SignInResult.Success);

        var page = new LoginModel(signIn, users)
        {
            UserName = "household-admin",
            Password = "a long enough password",
            PageContext = new PageContext { HttpContext = CreateHttpContext(authentication) }
        };

        var result = await page.OnPostAsync();

        result.Should().BeOfType<PageResult>();
        page.Failed.Should().BeTrue();
        await authentication.Received(1).SignOutAsync(Arg.Any<HttpContext>(), IdentityConstants.ApplicationScheme, Arg.Any<AuthenticationProperties>());
        await authentication.Received(1).SignOutAsync(Arg.Any<HttpContext>(), IdentityConstants.TwoFactorUserIdScheme, Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task A_password_check_that_asks_for_a_second_factor_continues_to_the_code_page_without_signing_out()
    {
        var user = new LedgerUserEntity { Id = Guid.NewGuid(), UserName = "household-admin", TwoFactorEnabled = true };
        var users = CreateUserManager();
        users.FindByNameAsync("household-admin").Returns(user);
        users.GetTwoFactorEnabledAsync(user).Returns(true);
        users.GetAuthenticatorKeyAsync(user).Returns("KEYKEYKEYKEYKEYK");

        var authentication = Substitute.For<IAuthenticationService>();
        var signIn = CreateSignInManager(users);
        signIn.PasswordSignInAsync(user, "a long enough password", false, true).Returns(Microsoft.AspNetCore.Identity.SignInResult.TwoFactorRequired);

        var page = new LoginModel(signIn, users)
        {
            UserName = "household-admin",
            Password = "a long enough password",
            PageContext = new PageContext { HttpContext = CreateHttpContext(authentication) },
            Url = Substitute.For<IUrlHelper>()
        };

        var result = await page.OnPostAsync();

        result.Should().BeOfType<LocalRedirectResult>().Which.Url.Should().StartWith("/account/totp");
        page.Failed.Should().BeFalse();
        await authentication.DidNotReceive().SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties>());
    }

    private static HttpContext CreateHttpContext(IAuthenticationService authentication)
    {
        var services = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider();

        return new DefaultHttpContext { RequestServices = services };
    }

    private static UserManager<LedgerUserEntity> CreateUserManager()
    {
        return Substitute.For<UserManager<LedgerUserEntity>>(
            Substitute.For<IUserStore<LedgerUserEntity>>(),
            Options.Create(new IdentityOptions()),
            Substitute.For<IPasswordHasher<LedgerUserEntity>>(),
            Array.Empty<IUserValidator<LedgerUserEntity>>(),
            Array.Empty<IPasswordValidator<LedgerUserEntity>>(),
            Substitute.For<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            Substitute.For<IServiceProvider>(),
            NullLogger<UserManager<LedgerUserEntity>>.Instance);
    }

    private static SignInManager<LedgerUserEntity> CreateSignInManager(UserManager<LedgerUserEntity> users)
    {
        return Substitute.For<SignInManager<LedgerUserEntity>>(
            users,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IUserClaimsPrincipalFactory<LedgerUserEntity>>(),
            Options.Create(new IdentityOptions()),
            NullLogger<SignInManager<LedgerUserEntity>>.Instance,
            Substitute.For<IAuthenticationSchemeProvider>(),
            Substitute.For<IUserConfirmation<LedgerUserEntity>>());
    }
}
