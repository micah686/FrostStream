using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace BackupServiceNew.Pages.Recovery;
public sealed class FinishModel(IServiceProvider services) : PageModel
{
    public IActionResult OnGet(Guid id)
    {
        var jobs = services.GetRequiredService<BackupJobs>();
        if (jobs.GetRecovery(id) is not { Kind: "restore", Status: "completed", Stage: "database-validated" } database)
            return NotFound();
        foreach (var kind in new[] { "openbao", "other" })
            if (jobs.GetPlanJob(id, kind) is { Status: "queued" or "running" or "failed" } pending)
                return RedirectToPage("./Run", new { id = pending.Id });
        ViewData["RecoveryType"] = database.Plan.Backup.Mode == "logical" ? "Logical" : "Physical";
        return Page();
    }
}
