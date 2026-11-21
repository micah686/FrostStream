using BackupServiceNew.PgBackRestWrapper;
using BackupServiceNew.Recovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BackupServiceNew.Pages.Recovery;

public sealed class ChooseModel(IServiceProvider services) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "physical";
    internal global::Shared.Backups.BackupInfoDto[] LogicalInventory { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; }
    internal string SelectedMode => string.IsNullOrEmpty(Mode) ? "latest" : Mode;
    [BindProperty(SupportsGet = true)] public string? Query { get; set; }
    [BindProperty(SupportsGet = true)] public string? Type { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public int Number { get; set; } = 1;
    [BindProperty] public string? Target { get; set; }
    [BindProperty] public string? Label { get; set; }

    internal ReadinessReport Readiness { get; private set; } = RecoveryReadiness.Pending();
    internal BackupPage? Backups { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (!Readiness.Ready) return Page();
        if (!ModelState.IsValid) return Page();

        if (Kind == "logical")
        {
            try
            {
                var plan = await services.GetRequiredService<RecoveryPlans>().CreateLogicalAsync(services.GetRequiredService<LogicalBackups>(), Label ?? "", ct);
                return RedirectToPage("./Review", new { id = plan.Id });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            { ModelState.AddModelError("", ex.Message); return Page(); }
        }
        DateTimeOffset? target = null;
        if (SelectedMode == "time")
        {
            if (!RecoveryPlans.TryTarget(Target, out var parsed))
            {
                ModelState.AddModelError(nameof(Target), "Enter an ISO timestamp with an explicit UTC offset, for example 2026-11-20T09:30:00-08:00.");
                return Page();
            }
            target = parsed;
        }

        try
        {
            var mode = SelectedMode switch
            {
                "latest" => RecoveryMode.Latest,
                "time" => RecoveryMode.Timestamp,
                "manual" => RecoveryMode.Consistency,
                _ => throw new ArgumentException("Choose a recovery method.")
            };
            var plan = services.GetRequiredService<RecoveryPlans>().Create(Readiness, mode, Label, target);
            return RedirectToPage("./Review", new { id = plan.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (Kind == "logical")
        {
            var logical = services.GetRequiredService<LogicalBackups>();
            Readiness = await services.GetRequiredService<RecoveryReadiness>().CheckLogicalAsync(logical, ct);
            LogicalInventory = logical.List().Where(b => b.Mode == "logical").OrderByDescending(b => b.CompletedAt).ToArray();
            return;
        }
        Readiness = await services.GetRequiredService<RecoveryReadiness>().CheckAsync(ct);
        if (SelectedMode != "manual" || !Readiness.Ready) return;
        if (From > To) ModelState.AddModelError("", "The end date must be on or after the start date.");
        Backups = services.GetRequiredService<RecoveryPlans>().Browse(Readiness.Repository!, Number, Query?.Trim(), Type, From, To);
    }
}
