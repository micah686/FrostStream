using BackupServiceNew.PgBackRestWrapper;
using BackupServiceNew.Recovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BackupServiceNew.Pages.Recovery;

public sealed class ReviewModel(IServiceProvider services) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty] public string? Stanza { get; set; }
    [BindProperty] public bool Authorized { get; set; }
    [BindProperty] public string? Rollback { get; set; }
    [BindProperty] public bool Preserved { get; set; }
    [BindProperty] public bool AcceptUnknownWal { get; set; }
    [BindProperty] public bool AcceptMissingCompanion { get; set; }

    private RecoveryPlans Plans => services.GetRequiredService<RecoveryPlans>();
    private BackupJobs Jobs => services.GetRequiredService<BackupJobs>();

    internal RecoveryPlan? Plan { get; private set; }
    internal RecoveryJob? Rehearsal { get; private set; }
    internal RecoveryJob? Checksum { get; private set; }
    internal long? ScratchFreeBytes { get; private set; }
    internal string StanzaName => Plans.Stanza;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => await LoadAsync(ct) ?? Page();

    public async Task<IActionResult> OnPostRehearseAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is { } result) return result;
        if (Plan is null || !ModelState.IsValid) return Page();
        if (Plan.Mode == RecoveryMode.Latest || ScratchFreeBytes is null || ScratchFreeBytes < Plan.RequiredBytes)
        {
            ModelState.AddModelError("", "A rehearsal requires a fixed target and enough scratch space.");
            return Page();
        }
        return await EnqueueAsync("rehearsal", null, ct);
    }

    public async Task<IActionResult> OnPostChecksumAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is { } result) return result;
        return Plan is null || !ModelState.IsValid ? Page() : await EnqueueAsync("checksum", null, ct);
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is { } result) return result;
        if (Plan is not { } plan) return Page();
        if (!string.Equals(Stanza, Plans.Stanza, StringComparison.Ordinal))
            ModelState.AddModelError(nameof(Stanza), "Type the exact stanza name.");
        if (!Authorized)
            ModelState.AddModelError(nameof(Authorized), "Confirm services are stopped and replacement is authorized.");
        if (plan.Populated && !Preserved)
            ModelState.AddModelError(nameof(Preserved), "Confirm that you preserved an independent rollback copy.");
        if (Rehearsal?.Status is "queued" or "running" or "failed")
            ModelState.AddModelError("", "A completed rehearsal is required for this plan. Review its job log.");
        if (Checksum?.Status is "queued" or "running" || Checksum?.Stage == "checksum-failed")
            ModelState.AddModelError("", "Review the backup checksum job before continuing.");
        if (plan.Wal.State == CheckState.Unknown && Rehearsal?.Stage != "rehearsed" && !AcceptUnknownWal)
            ModelState.AddModelError(nameof(AcceptUnknownWal), "Acknowledge the limits of the available WAL evidence.");
        if (!plan.Backup.OpenBaoExportPresent && !AcceptMissingCompanion)
            ModelState.AddModelError(nameof(AcceptMissingCompanion), "Acknowledge the missing OpenBao companion.");

        if (!ModelState.IsValid) return Page();
        try
        {
            var rollback = plan.Populated ? Plans.ValidateRollback(plan, Rollback) : null;
            return await EnqueueAsync("restore", rollback, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(nameof(Rollback), ex.Message);
            return Page();
        }
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken ct)
    {
        if (Jobs.GetRecovery(Id) is { Kind: "restore" } existing)
            return RedirectToPage("./Run", new { id = existing.Id });
        Rehearsal = Jobs.GetPlanJob(Id, "rehearsal");
        Checksum = Jobs.GetPlanJob(Id, "checksum");
        var plan = Plans.Get(Id);
        // Long validations can outlive the original review. Keep their pinned plan
        // available for a fresh confirmation window after the evidence is observed.
        var evidence = new[] { Rehearsal, Checksum }.OfType<RecoveryJob>()
            .Where(job => job.Status == "completed" && job.Stage is "rehearsed" or "checksum-passed" or "checksum-unknown")
            .MaxBy(job => job.ObservedAt);
        if (evidence?.ObservedAt is { } observed && observed.AddMinutes(30) > (plan?.ExpiresAt ?? DateTimeOffset.UtcNow))
            plan = evidence.Plan with { ExpiresAt = observed.AddMinutes(30) };
        if (plan is null) return null;
        try
        {
            Plans.Revalidate(plan, await services.GetRequiredService<RecoveryReadiness>().CheckAsync(ct));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return null;
        }

        Plan = plan;
        if (plan.Mode != RecoveryMode.Latest)
        {
            try { ScratchFreeBytes = services.GetRequiredService<RecoveryExecution>().ScratchFreeBytes; }
            catch (IOException) { }
        }
        return null;
    }

    private async Task<IActionResult> EnqueueAsync(string kind, string? rollback, CancellationToken ct)
    {
        try
        {
            var id = await Jobs.QueueRecoveryAsync(Plan!, kind, rollback, ct);
            return RedirectToPage("./Run", new { id });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
    }
}
