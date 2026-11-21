using BackupServiceNew.PgBackRestWrapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BackupServiceNew.Pages.Recovery;

public sealed class OpenBaoModel(IServiceProvider services) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty] public bool ConfirmOpenBao { get; set; }
    private BackupJobs Jobs => services.GetRequiredService<BackupJobs>();
    internal RecoveryJob Job { get; private set; } = null!;
    internal RecoveryJob? OpenBaoJob { get; private set; }

    public IActionResult OnGet() => Load();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Load() is not PageResult) return RedirectToPage("./Run", new { id = Id });
        if (!ConfirmOpenBao)
        {
            ModelState.AddModelError(nameof(ConfirmOpenBao), "Confirm that OpenBao storage will be replaced and writers are stopped.");
            return Page();
        }
        try
        {
            await Jobs.QueueOpenBaoAsync(Id, ct);
            return RedirectToPage(new { id = Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or CommandFailedException or IOException)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
    }

    private IActionResult Load()
    {
        Job = Jobs.GetRecovery(Id)!;
        if (Job is not { Kind: "restore" }) return NotFound();
        if (Job.Stage != "database-validated" || Job.Status != "completed") return RedirectToPage("./Run", new { id = Id });
        OpenBaoJob = Jobs.GetPlanJob(Job.Plan.Id, "openbao");
        return Page();
    }
}
