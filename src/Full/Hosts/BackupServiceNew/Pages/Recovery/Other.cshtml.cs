using BackupServiceNew.Recovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace BackupServiceNew.Pages.Recovery;
public sealed class OtherModel(IServiceProvider services) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty] public string[] Items { get; set; } = [];
    [BindProperty] public bool Authorized { get; set; }
    internal string[] Available { get; private set; } = [];
    internal RecoveryJob Job { get; private set; } = null!;
    public IActionResult OnGet() => Load();
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var result = Load();
        if (result is not PageResult) return result;
        if (!Authorized) { ModelState.AddModelError("", "Confirm writers are stopped and key replacement is authorized."); return Page(); }
        try
        {
            var id = await services.GetRequiredService<BackupJobs>().QueueOtherAsync(Id, Items, ct);
            return RedirectToPage("./Run", new { id });
        }
        catch (InvalidOperationException ex) { ModelState.AddModelError("", ex.Message); return Page(); }
    }
    private IActionResult Load()
    {
        Job = services.GetRequiredService<BackupJobs>().GetRecovery(Id)!;
        if (Job is not { Kind: "restore", Status: "completed", Stage: "database-validated" }) return NotFound();
        Available = services.GetRequiredService<LogicalBackups>().OtherData(Job.Plan.Backup.Label);
        return Page();
    }
}
