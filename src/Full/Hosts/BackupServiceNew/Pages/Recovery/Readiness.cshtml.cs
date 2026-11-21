using BackupServiceNew.Recovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BackupServiceNew.Pages.Recovery;

public sealed class ReadinessModel(IServiceProvider services) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "physical";
    internal ReadinessReport Report { get; private set; } = RecoveryReadiness.Pending();
    internal RecoveryJob[] Recent { get; private set; } = [];

    public async Task OnGetAsync(bool run, CancellationToken ct)
    {
        if (run) Report = Kind == "logical"
            ? await services.GetRequiredService<RecoveryReadiness>().CheckLogicalAsync(services.GetRequiredService<LogicalBackups>(), ct)
            : await services.GetRequiredService<RecoveryReadiness>().CheckAsync(ct);
        Recent = services.GetRequiredService<BackupJobs>().RecentRecoveries();
    }
}
