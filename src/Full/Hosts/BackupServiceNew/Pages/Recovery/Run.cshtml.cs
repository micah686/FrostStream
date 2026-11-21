using BackupServiceNew.Recovery;
using BackupServiceNew.PgBackRestWrapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BackupServiceNew.Pages.Recovery;

public sealed class RunModel(IServiceProvider services) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    private BackupJobs Jobs => services.GetRequiredService<BackupJobs>();
    internal RecoveryJob Job { get; private set; } = null!;
    internal string DatabaseCommand
    {
        get
        {
            var user = services.GetRequiredService<WrapperOptions>().DatabaseUser;
            return "docker compose exec postgres psql -U '" + user.Replace("'", "'\"'\"'") + "' -d postgres";
        }
    }

    public IActionResult OnGet() => Load() ? Page() : NotFound();

    public async Task<IActionResult> OnPostObserveAsync(CancellationToken ct)
    {
        if (!Load()) return NotFound();
        try
        {
            await Jobs.ObserveRecoveryAsync(Id, ct);
            return RedirectToPage("./Run", new { id = Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or CommandFailedException or IOException)
        {
            ModelState.AddModelError("", ex is CommandFailedException
                ? "PostgreSQL could not complete the probe. Check its startup, recovery logs and socket permissions."
                : ex.Message);
            return Page();
        }
    }

    private bool Load()
    {
        Job = Jobs.GetRecovery(Id)!;
        return Job is not null;
    }
}
