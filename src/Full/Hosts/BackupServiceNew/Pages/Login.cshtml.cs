using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BackupServiceNew.Recovery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace BackupServiceNew.Pages;

[EnableRateLimiting(RecoveryConsole.LoginLimit)]
public sealed class LoginModel(IServiceProvider services) : PageModel
{
    [BindProperty, Required, StringLength(2048)]
    public string Token { get; set; } = "";

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? RedirectToPage("/Index") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var credential = services.GetRequiredService<RecoveryCredential>();
        if (!ModelState.IsValid || !credential.Matches(Token))
        {
            ModelState.Clear();
            Token = "";
            ModelState.AddModelError("", "The restore console token is invalid.");
            return Page();
        }

        var scheme = CookieAuthenticationDefaults.AuthenticationScheme;
        await HttpContext.SignInAsync(scheme, new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "restore-operator")], scheme)));
        return RedirectToPage("/Index");
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync();
        return RedirectToPage("/Login");
    }
}
