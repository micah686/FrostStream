using BackupServiceNew.Recovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BackupServiceNew.Pages;

public sealed class IndexModel(IServiceProvider services) : PageModel
{
    internal ReadinessReport Report { get; private set; } = RecoveryReadiness.Pending();
    internal RecoveryJob[] Recent { get; private set; } = [];

    public async Task OnGetAsync(bool run, CancellationToken ct)
    {
        if (run) Report = await services.GetRequiredService<RecoveryReadiness>().CheckAsync(ct);
        Recent = services.GetRequiredService<BackupJobs>().RecentRecoveries();
    }
}
