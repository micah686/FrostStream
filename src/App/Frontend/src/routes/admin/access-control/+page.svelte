<script lang="ts">
  import { onMount } from 'svelte';
  import { Spinner } from 'flowbite-svelte';
  import BundleManagementSection from '$lib/components/admin/BundleManagementSection.svelte';
  import { ApiRequestError } from '$lib/api/http';
  import {
    checkEffectiveAccess,
    createAccessPolicy,
    deleteAccessPolicy,
    duplicateAccessPolicy,
    getEffectiveAccess,
    getPolicyMediaSummary,
    listAccessPolicies,
    listPolicyProviders,
    updateAccessPolicy,
    type AccessPolicy,
    type AccessPolicyAssignment,
    type AccessPolicyWriteRequest,
    type EffectiveAccess,
    type EffectiveAccessCheck,
    type MediaSummary
  } from '$lib/api/accessControl';
  import {
    listBundles,
    listCatalog,
    searchDirectory,
    type BundleView,
    type CatalogEntry,
    type DirectoryEntry,
    type GranteeType
  } from '$lib/api/bundles';

  type Tab = 'policies' | 'bundles' | 'effective';

  interface PolicyEditor {
    policyId: string | null;
    name: string;
    description: string;
    enabled: boolean;
    bundleIds: string[];
    mediaGuids: string[];
    providers: string[];
    ageThresholds: string;
    assignments: AccessPolicyAssignment[];
  }

  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const fieldClass =
    'w-full rounded-lg border border-slate-800 bg-slate-950/60 px-3 py-2 text-sm text-slate-200 outline-none placeholder:text-slate-600 focus:border-blue-500 focus:ring-1 focus:ring-blue-500';
  const secondaryButton =
    'inline-flex h-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/60 px-3 text-xs font-semibold text-slate-200 transition hover:border-slate-600 hover:bg-slate-800 disabled:opacity-50';
  const primaryButton =
    'inline-flex h-9 items-center justify-center rounded-lg bg-blue-600 px-4 text-xs font-semibold text-white transition hover:bg-blue-500 disabled:opacity-50';

  let activeTab = $state<Tab>('policies');
  let policies = $state<AccessPolicy[]>([]);
  let bundles = $state<BundleView[]>([]);
  let catalog = $state<CatalogEntry[]>([]);
  let providerCatalog = $state<string[]>([]);
  let selectedPolicyId = $state('');
  let editor = $state<PolicyEditor | null>(null);
  let loading = $state(true);
  let saving = $state(false);
  let error = $state<string | null>(null);
  let notice = $state<string | null>(null);

  let assignmentType = $state<GranteeType>('group');
  let assignmentQuery = $state('');
  let directoryResults = $state<DirectoryEntry[]>([]);
  let directoryLoading = $state(false);
  let mediaGuidInput = $state('');
  let mediaGuidLoading = $state(false);
  let mediaGuidError = $state<string | null>(null);
  let mediaSummaries = $state<Record<string, MediaSummary>>({});
  let providerInput = $state('');

  let effectiveType = $state<GranteeType>('user');
  let effectiveId = $state('');
  let effectivePrincipalQuery = $state('');
  let effectiveDirectoryResults = $state<DirectoryEntry[]>([]);
  let effectiveDirectoryLoading = $state(false);
  let effectiveEndpoint = $state('');
  let effectiveMediaGuid = $state('');
  let effectiveResult = $state<EffectiveAccess | null>(null);
  let effectiveCheck = $state<EffectiveAccessCheck | null>(null);
  let effectiveLoading = $state(false);
  let effectiveCheckLoading = $state(false);

  const selectedPolicy = $derived(policies.find((policy) => policy.policyId === selectedPolicyId) ?? policies[0] ?? null);
  const policyBundleReferences = $derived(
    bundles.map((bundle) => ({
      bundle,
      policies: policies.filter((policy) => policy.bundleIds.includes(bundle.id))
    }))
  );

  onMount(() => {
    void loadAll();
  });

  async function loadAll(preferredPolicyId = selectedPolicyId) {
    loading = true;
    error = null;
    try {
      const [nextPolicies, nextBundles, nextCatalog, nextProviders] = await Promise.all([
        listAccessPolicies(),
        listBundles(),
        listCatalog(),
        listPolicyProviders()
      ]);
      policies = [...nextPolicies].sort((a, b) => a.name.localeCompare(b.name));
      bundles = [...nextBundles].sort((a, b) => a.id.localeCompare(b.id));
      catalog = [...nextCatalog].sort((a, b) => a.id.localeCompare(b.id));
      providerCatalog = [...nextProviders].sort();
      selectedPolicyId = policies.some((policy) => policy.policyId === preferredPolicyId)
        ? preferredPolicyId
        : (policies[0]?.policyId ?? '');
    } catch (err) {
      error = messageFor(err, 'Could not load access-control data.');
    } finally {
      loading = false;
    }
  }

  function beginCreate() {
    editor = {
      policyId: null,
      name: '',
      description: '',
      enabled: true,
      bundleIds: [],
      mediaGuids: [],
      providers: [],
      ageThresholds: '',
      assignments: []
    };
    resetAssignmentPicker();
    resetPolicyScopePickers();
  }

  function beginEdit(policy: AccessPolicy) {
    editor = {
      policyId: policy.policyId,
      name: policy.name,
      description: policy.description ?? '',
      enabled: policy.enabled,
      bundleIds: [...policy.bundleIds],
      mediaGuids: [...policy.mediaGuids],
      providers: [...policy.providers],
      ageThresholds: policy.ageThresholds.join(', '),
      assignments: policy.assignments.map((assignment) => ({ ...assignment }))
    };
    resetAssignmentPicker();
    resetPolicyScopePickers();
    void resolveExistingMedia(policy.mediaGuids);
  }

  function resetAssignmentPicker() {
    assignmentType = 'group';
    assignmentQuery = '';
    directoryResults = [];
  }

  function resetPolicyScopePickers() {
    mediaGuidInput = '';
    mediaGuidError = null;
    providerInput = '';
  }

  function parseList(value: string): string[] {
    return [...new Set(value.split(/[\n,]/).map((item) => item.trim()).filter(Boolean))];
  }

  function requestFromEditor(value: PolicyEditor): AccessPolicyWriteRequest {
    const ages = parseList(value.ageThresholds)
      .map(Number)
      .filter((age) => Number.isInteger(age) && age >= 0)
      .sort((a, b) => a - b);
    return {
      name: value.name.trim(),
      description: value.description.trim() || null,
      enabled: value.enabled,
      bundleIds: [...new Set(value.bundleIds)].sort(),
      mediaGuids: [...new Set(value.mediaGuids)].sort(),
      providers: [...new Set(value.providers.map((provider) => provider.trim().toLowerCase()).filter(Boolean))].sort(),
      ageThresholds: [...new Set(ages)],
      assignments: value.assignments.map(({ type, id }) => ({ type, id }))
    };
  }

  async function savePolicy() {
    if (!editor || !editor.name.trim()) {
      error = 'A policy name is required.';
      return;
    }
    saving = true;
    error = null;
    notice = null;
    try {
      const request = requestFromEditor(editor);
      const saved = editor.policyId
        ? await updateAccessPolicy(editor.policyId, request)
        : await createAccessPolicy(request);
      editor = null;
      notice = saved.syncStatus === 'Synced'
        ? `Saved “${saved.name}”.`
        : `Saved “${saved.name}”; OpenFGA synchronization will retry automatically.`;
      await loadAll(saved.policyId);
    } catch (err) {
      error = messageFor(err, 'Could not save the policy.');
    } finally {
      saving = false;
    }
  }

  async function removePolicy(policy: AccessPolicy) {
    if (!confirm(`Delete “${policy.name}”? This removes its assignments, bundle access, and media scopes.`)) return;
    error = null;
    try {
      await deleteAccessPolicy(policy.policyId);
      notice = `Deleted “${policy.name}”.`;
      editor = null;
      await loadAll();
    } catch (err) {
      error = messageFor(err, 'Could not delete the policy.');
    }
  }

  async function duplicatePolicy(policy: AccessPolicy) {
    const name = prompt('Name for the disabled copy:', `${policy.name} copy`)?.trim();
    if (!name) return;
    error = null;
    try {
      const copy = await duplicateAccessPolicy(policy.policyId, name);
      notice = `Created disabled policy “${copy.name}”.`;
      await loadAll(copy.policyId);
    } catch (err) {
      error = messageFor(err, 'Could not duplicate the policy.');
    }
  }

  async function togglePolicy(policy: AccessPolicy) {
    error = null;
    try {
      const request: AccessPolicyWriteRequest = {
        name: policy.name,
        description: policy.description,
        enabled: !policy.enabled,
        bundleIds: policy.bundleIds,
        mediaGuids: policy.mediaGuids,
        providers: policy.providers,
        ageThresholds: policy.ageThresholds,
        assignments: policy.assignments.map(({ type, id }) => ({ type, id }))
      };
      await updateAccessPolicy(policy.policyId, request);
      await loadAll(policy.policyId);
    } catch (err) {
      error = messageFor(err, 'Could not change the policy state.');
    }
  }

  async function findAssignments() {
    const query = assignmentQuery.trim();
    if (query.length < 2) return;
    directoryLoading = true;
    error = null;
    try {
      directoryResults = await searchDirectory(assignmentType, query);
    } catch (err) {
      error = messageFor(err, 'Directory search failed. You can still add the exact identifier.');
    } finally {
      directoryLoading = false;
    }
  }

  function addAssignment(id = assignmentQuery.trim(), displayName?: string) {
    if (!editor || !id) return;
    if (!editor.assignments.some((item) => item.type === assignmentType && item.id === id)) {
      editor.assignments.push({ type: assignmentType, id, displayName });
    }
    assignmentQuery = '';
    directoryResults = [];
  }

  function removeAssignment(assignment: AccessPolicyAssignment) {
    if (!editor) return;
    editor.assignments = editor.assignments.filter(
      (item) => item.type !== assignment.type || item.id !== assignment.id
    );
  }

  function addKnownProvider(provider = providerInput) {
    if (!editor) return;
    const normalized = provider.trim().toLowerCase();
    if (!normalized) return;
    editor.providers = [...new Set([...editor.providers, normalized])].sort();
    providerInput = '';
  }

  function removeProvider(provider: string) {
    if (!editor) return;
    editor.providers = editor.providers.filter((item) => item !== provider);
  }

  async function resolveExistingMedia(mediaGuids: string[]) {
    const unresolved = mediaGuids.filter((guid) => !mediaSummaries[guid]);
    const results = await Promise.allSettled(unresolved.map((guid) => getPolicyMediaSummary(guid)));
    const next = { ...mediaSummaries };
    results.forEach((result, index) => {
      if (result.status === 'fulfilled') next[unresolved[index]] = result.value;
    });
    mediaSummaries = next;
  }

  async function addMediaGuid() {
    if (!editor) return;
    const mediaGuid = mediaGuidInput.trim();
    if (!mediaGuid) return;
    mediaGuidLoading = true;
    mediaGuidError = null;
    try {
      const summary = await getPolicyMediaSummary(mediaGuid);
      mediaSummaries = { ...mediaSummaries, [summary.mediaGuid]: summary };
      if (!editor.mediaGuids.includes(summary.mediaGuid)) {
        editor.mediaGuids = [...editor.mediaGuids, summary.mediaGuid].sort();
      }
      mediaGuidInput = '';
    } catch (err) {
      mediaGuidError = messageFor(err, 'Could not resolve that media GUID.');
    } finally {
      mediaGuidLoading = false;
    }
  }

  function removeMediaGuid(mediaGuid: string) {
    if (!editor) return;
    editor.mediaGuids = editor.mediaGuids.filter((guid) => guid !== mediaGuid);
  }

  async function findEffectivePrincipal() {
    const query = effectivePrincipalQuery.trim();
    if (query.length < 2) return;
    effectiveDirectoryLoading = true;
    error = null;
    try {
      effectiveDirectoryResults = await searchDirectory(effectiveType, query);
    } catch (err) {
      error = messageFor(err, 'Directory search failed. You can still use the exact identifier.');
    } finally {
      effectiveDirectoryLoading = false;
    }
  }

  function selectEffectivePrincipal(entry?: DirectoryEntry) {
    effectiveId = entry?.id ?? effectivePrincipalQuery.trim();
    if (entry) effectivePrincipalQuery = entry.name;
    effectiveDirectoryResults = [];
    effectiveResult = null;
    effectiveCheck = null;
  }

  async function evaluate() {
    if (!effectiveId.trim()) {
      error = 'Select or enter a user or group identifier to evaluate.';
      return;
    }
    effectiveLoading = true;
    effectiveResult = null;
    error = null;
    try {
      effectiveResult = await getEffectiveAccess(effectiveType, effectiveId.trim());
    } catch (err) {
      error = messageFor(err, 'Could not evaluate effective access.');
    } finally {
      effectiveLoading = false;
    }
  }

  async function runEffectiveCheck() {
    if (!effectiveId.trim()) {
      error = 'Select or enter a user or group identifier to evaluate.';
      return;
    }
    if (!effectiveEndpoint.trim() && !effectiveMediaGuid.trim()) {
      error = 'Enter an endpoint, a media GUID, or both to check.';
      return;
    }
    effectiveCheckLoading = true;
    effectiveCheck = null;
    error = null;
    try {
      effectiveCheck = await checkEffectiveAccess({
        principalType: effectiveType,
        principalId: effectiveId.trim(),
        endpointId: effectiveEndpoint.trim() || null,
        mediaGuid: effectiveMediaGuid.trim() || null
      });
    } catch (err) {
      error = messageFor(err, 'Could not check effective access.');
    } finally {
      effectiveCheckLoading = false;
    }
  }

  function policyName(policyId: string): string {
    return policies.find((policy) => policy.policyId === policyId)?.name ?? policyId;
  }

  function syncClass(policy: AccessPolicy): string {
    if (policy.syncStatus === 'Synced') return 'border-emerald-500/30 bg-emerald-500/10 text-emerald-200';
    if (policy.syncStatus === 'Failed') return 'border-red-500/30 bg-red-500/10 text-red-200';
    return 'border-amber-500/30 bg-amber-500/10 text-amber-200';
  }

  function messageFor(value: unknown, fallback: string): string {
    if (value instanceof ApiRequestError && value.status === 503) {
      return `${value.message || fallback} The synchronization service will retry pending policy revisions.`;
    }
    return value instanceof Error ? value.message : fallback;
  }
</script>

<svelte:head>
  <title>Access control · FrostStream</title>
</svelte:head>

<section class={cardClass} aria-labelledby="access-control-title">
  <div class="flex flex-wrap items-start justify-between gap-3">
    <div>
      <h2 id="access-control-title" class="text-base font-bold text-slate-100">Access control</h2>
      <p class="mt-2 max-w-3xl text-sm text-slate-400">
        Grant endpoint bundles and deny media GUIDs, providers, or age tiers with named policies assigned to users and groups.
      </p>
    </div>
    <button class={secondaryButton} type="button" disabled={loading} onclick={() => void loadAll()}>
      Refresh
    </button>
  </div>

  <div class="mt-5 flex flex-wrap gap-2 border-b border-slate-800" role="tablist" aria-label="Access control views">
    {#each ([['policies', 'Policies'], ['bundles', 'Bundles'], ['effective', 'Effective access']] as [Tab, string][]) as [id, label]}
      <button
        type="button"
        role="tab"
        aria-selected={activeTab === id}
        class={[
          'border-b-2 px-3 py-2.5 text-sm font-semibold transition',
          activeTab === id
            ? 'border-blue-400 text-blue-300'
            : 'border-transparent text-slate-500 hover:text-slate-200'
        ]}
        onclick={() => (activeTab = id)}
      >
        {label}
      </button>
    {/each}
  </div>

  {#if error}
    <div class="mt-4 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">{error}</div>
  {/if}
  {#if notice}
    <div class="mt-4 rounded-xl border border-emerald-800/60 bg-emerald-950/25 p-3 text-sm text-emerald-200" role="status">{notice}</div>
  {/if}
</section>

{#if activeTab === 'policies'}
  <section class={cardClass}>
    <div class="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h3 class="text-sm font-bold text-slate-100">Policies</h3>
        <p class="mt-1 text-xs text-slate-500">
          Media denies apply from the database immediately; OpenFGA synchronization status governs endpoint-bundle grants.
        </p>
      </div>
      <button class={primaryButton} type="button" onclick={beginCreate}>New policy</button>
    </div>

    {#if loading}
      <div class="mt-10 flex justify-center"><Spinner size="8" /></div>
    {:else if policies.length === 0}
      <div class="mt-5 rounded-xl border border-dashed border-slate-700 p-8 text-center text-sm text-slate-500">
        No access policies yet. Create one to combine endpoint grants and media denies.
      </div>
    {:else}
      <div class="mt-5 grid gap-5 2xl:grid-cols-[21rem_minmax(0,1fr)]">
        <aside class="max-h-[46rem] overflow-y-auto rounded-xl border border-slate-800">
          {#each policies as policy (policy.policyId)}
            <button
              type="button"
              class={[
                'block w-full border-b border-slate-800 px-4 py-3 text-left transition last:border-b-0',
                selectedPolicy?.policyId === policy.policyId ? 'bg-blue-500/10' : 'hover:bg-slate-800/45'
              ]}
              onclick={() => {
                selectedPolicyId = policy.policyId;
                editor = null;
              }}
            >
              <div class="flex items-center justify-between gap-2">
                <span class="truncate text-sm font-semibold text-slate-100">{policy.name}</span>
                <span class={['rounded-full border px-2 py-0.5 text-[10px] font-bold', syncClass(policy)]}>
                  {policy.syncStatus}
                </span>
              </div>
              <div class="mt-1 text-xs text-slate-500">
                {policy.assignments.length} principals · {policy.bundleIds.length} bundles · {policy.enabled ? 'Enabled' : 'Disabled'}
              </div>
            </button>
          {/each}
        </aside>

        {#if selectedPolicy}
          <div class="min-w-0 space-y-4">
            <div class="rounded-xl border border-slate-800 bg-slate-950/25 p-4">
              <div class="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <div class="flex flex-wrap items-center gap-2">
                    <h3 class="text-lg font-bold text-slate-100">{selectedPolicy.name}</h3>
                    <span class={['rounded-full border px-2 py-0.5 text-[10px] font-bold', syncClass(selectedPolicy)]}>
                      {selectedPolicy.syncStatus}
                    </span>
                    {#if !selectedPolicy.enabled}
                      <span class="rounded-full border border-slate-700 bg-slate-800 px-2 py-0.5 text-[10px] font-bold text-slate-400">Disabled</span>
                    {/if}
                  </div>
                  <p class="mt-2 text-sm text-slate-400">{selectedPolicy.description || 'No description.'}</p>
                  {#if selectedPolicy.syncError}
                    <p class="mt-2 text-xs text-red-300">{selectedPolicy.syncError}</p>
                  {/if}
                </div>
                <div class="flex flex-wrap gap-2">
                  <button class={secondaryButton} type="button" onclick={() => beginEdit(selectedPolicy)}>Edit</button>
                  <button class={secondaryButton} type="button" onclick={() => void duplicatePolicy(selectedPolicy)}>Duplicate</button>
                  <button class={secondaryButton} type="button" onclick={() => void togglePolicy(selectedPolicy)}>
                    {selectedPolicy.enabled ? 'Disable' : 'Enable'}
                  </button>
                  <button
                    class="inline-flex h-9 items-center rounded-lg border border-red-900/70 px-3 text-xs font-semibold text-red-300 hover:bg-red-950/40"
                    type="button"
                    onclick={() => void removePolicy(selectedPolicy)}
                  >Delete</button>
                </div>
              </div>
            </div>

            <div class="grid gap-4 lg:grid-cols-2">
              <div class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
                <h4 class="text-xs font-bold uppercase tracking-wide text-slate-500">Endpoint bundles</h4>
                <div class="mt-3 flex flex-wrap gap-2">
                  {#each selectedPolicy.bundleIds as bundleId}
                    <span class="rounded-lg border border-blue-500/25 bg-blue-500/10 px-2.5 py-1 font-mono text-xs text-blue-200">{bundleId}</span>
                  {:else}
                    <span class="text-sm text-slate-600">No endpoint bundles.</span>
                  {/each}
                </div>
              </div>
              <div class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
                <h4 class="text-xs font-bold uppercase tracking-wide text-slate-500">Policy assignments</h4>
                <div class="mt-3 space-y-2">
                  {#each selectedPolicy.assignments as assignment}
                    <div class="flex min-w-0 items-center gap-2 text-sm">
                      <span class="rounded bg-slate-800 px-2 py-0.5 text-[10px] font-bold uppercase text-slate-400">{assignment.type}</span>
                      <span class="truncate text-slate-200">{assignment.displayName || assignment.id}</span>
                      {#if assignment.displayName}<span class="truncate font-mono text-[10px] text-slate-600">{assignment.id}</span>{/if}
                    </div>
                  {:else}
                    <span class="text-sm text-slate-600">No assignments.</span>
                  {/each}
                </div>
              </div>
              <div class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
                <h4 class="text-xs font-bold uppercase tracking-wide text-slate-500">Denied media GUIDs</h4>
                <div class="mt-3 space-y-1">
                  {#each selectedPolicy.mediaGuids as guid}
                    <div class="break-all font-mono text-xs text-slate-300">{guid}</div>
                  {:else}
                    <span class="text-sm text-slate-600">No media GUID denies.</span>
                  {/each}
                </div>
              </div>
              <div class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
                <h4 class="text-xs font-bold uppercase tracking-wide text-slate-500">Denied providers and ages</h4>
                <div class="mt-3 flex flex-wrap gap-2">
                  {#each selectedPolicy.providers as provider}
                    <span class="rounded-lg border border-violet-500/25 bg-violet-500/10 px-2.5 py-1 text-xs text-violet-200">{provider}</span>
                  {/each}
                  {#each selectedPolicy.ageThresholds as age}
                    <span class="rounded-lg border border-amber-500/25 bg-amber-500/10 px-2.5 py-1 text-xs text-amber-200">Deny age {age}+</span>
                  {/each}
                  {#if selectedPolicy.providers.length === 0 && selectedPolicy.ageThresholds.length === 0}
                    <span class="text-sm text-slate-600">No provider or age denies.</span>
                  {/if}
                </div>
              </div>
            </div>
          </div>
        {/if}
      </div>
    {/if}
  </section>

  {#if editor}
    <section class={cardClass} aria-labelledby="policy-editor-title">
      <div class="flex items-center justify-between gap-3">
        <div>
          <h3 id="policy-editor-title" class="text-sm font-bold text-slate-100">{editor.policyId ? 'Edit policy' : 'Create policy'}</h3>
          <p class="mt-1 text-xs text-slate-500">
            Bundles grant endpoint access. Media GUIDs, providers, and age tiers deny playback; everything else stays watchable.
          </p>
        </div>
        <button class={secondaryButton} type="button" onclick={() => (editor = null)}>Cancel</button>
      </div>

      <div class="mt-5 grid gap-5 xl:grid-cols-2">
        <div class="space-y-4">
          <label class="block text-xs font-semibold text-slate-400">
            Name
            <input class={`${fieldClass} mt-1.5`} bind:value={editor.name} placeholder="Family viewing" />
          </label>
          <label class="block text-xs font-semibold text-slate-400">
            Description
            <textarea class={`${fieldClass} mt-1.5 min-h-20`} bind:value={editor.description} placeholder="What this policy grants and restricts"></textarea>
          </label>
          <label class="flex items-center gap-2 text-sm text-slate-300">
            <input type="checkbox" bind:checked={editor.enabled} class="rounded border-slate-700 bg-slate-950 text-blue-600" />
            Enabled
          </label>

          <fieldset>
            <legend class="text-xs font-semibold text-slate-400">Endpoint bundles</legend>
            <div class="mt-2 max-h-64 space-y-1 overflow-y-auto rounded-lg border border-slate-800 bg-slate-950/35 p-2">
              {#each bundles as bundle (bundle.id)}
                <label class="flex items-center gap-2 rounded px-2 py-1.5 text-sm text-slate-300 hover:bg-slate-800/50">
                  <input type="checkbox" value={bundle.id} bind:group={editor.bundleIds} class="rounded border-slate-700 bg-slate-950 text-blue-600" />
                  <span class="min-w-0 truncate font-mono text-xs">{bundle.id}</span>
                  <span class="ml-auto shrink-0 text-[10px] uppercase text-slate-600">{bundle.systemOwned ? 'system' : 'user'}</span>
                </label>
              {/each}
            </div>
          </fieldset>
        </div>

        <div class="space-y-4">
          <fieldset>
            <legend class="text-xs font-semibold text-slate-400">Denied media</legend>
            <p class="mt-1 text-xs text-slate-600">Resolve a media GUID before adding it so the policy targets a known item.</p>
            <div class="mt-2 flex gap-2">
              <input
                class={`${fieldClass} font-mono text-xs`}
                bind:value={mediaGuidInput}
                placeholder="00000000-0000-0000-0000-000000000000"
                onkeydown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    void addMediaGuid();
                  }
                }}
              />
              <button class={secondaryButton} type="button" disabled={mediaGuidLoading || !mediaGuidInput.trim()} onclick={() => void addMediaGuid()}>
                {mediaGuidLoading ? 'Resolving…' : 'Resolve & add'}
              </button>
            </div>
            {#if mediaGuidError}
              <p class="mt-2 text-xs text-red-300">{mediaGuidError}</p>
            {/if}
            <div class="mt-3 space-y-2">
              {#each editor.mediaGuids as mediaGuid (mediaGuid)}
                {@const summary = mediaSummaries[mediaGuid]}
                <div class="flex items-start gap-3 rounded-lg border border-slate-800 bg-slate-950/35 px-3 py-2">
                  <div class="min-w-0">
                    <div class="truncate text-xs font-semibold text-slate-200">{summary?.title || 'Media item'}</div>
                    <div class="mt-1 break-all font-mono text-[10px] text-slate-500">{mediaGuid}</div>
                    {#if summary}
                      <div class="mt-1 text-[10px] text-slate-600">
                        {summary.providers.join(', ') || 'No provider'} · {summary.ageLimit == null ? 'Unrated' : `${summary.ageLimit}+`}
                      </div>
                    {/if}
                  </div>
                  <button
                    type="button"
                    class="ml-auto shrink-0 text-xs font-semibold text-red-300 hover:text-red-200"
                    aria-label={`Remove media ${mediaGuid}`}
                    onclick={() => removeMediaGuid(mediaGuid)}
                  >Remove</button>
                </div>
              {:else}
                <div class="rounded-lg border border-dashed border-slate-800 px-3 py-3 text-xs text-slate-600">No media GUID denies.</div>
              {/each}
            </div>
          </fieldset>

          <fieldset>
            <legend class="text-xs font-semibold text-slate-400">Denied providers</legend>
            <div class="mt-2 flex gap-2">
              <input
                class={fieldClass}
                bind:value={providerInput}
                list="policy-provider-catalog"
                placeholder="Search or enter a provider"
                onkeydown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    addKnownProvider();
                  }
                }}
              />
              <datalist id="policy-provider-catalog">
                {#each providerCatalog as provider}<option value={provider}></option>{/each}
              </datalist>
              <button class={secondaryButton} type="button" disabled={!providerInput.trim()} onclick={() => addKnownProvider()}>Add</button>
            </div>
            <div class="mt-3 flex flex-wrap gap-2">
              {#each editor.providers as provider (provider)}
                <button
                  type="button"
                  class="rounded-lg border border-violet-500/25 bg-violet-500/10 px-2.5 py-1 text-xs text-violet-200 hover:border-red-500/40 hover:text-red-200"
                  title="Remove provider"
                  onclick={() => removeProvider(provider)}
                >{provider} ×</button>
              {:else}
                <span class="text-xs text-slate-600">No provider denies.</span>
              {/each}
            </div>
          </fieldset>

          <label class="block text-xs font-semibold text-slate-400">
            Deny at age or above
            <input class={`${fieldClass} mt-1.5`} bind:value={editor.ageThresholds} placeholder="13, 16, 18" />
          </label>

          <fieldset>
            <legend class="text-xs font-semibold text-slate-400">Who receives this policy</legend>
            <p class="mt-1 text-xs text-slate-600">
              These assignments receive every selected bundle and the configured media access in one place.
            </p>
            <div class="mt-2 flex gap-2">
              <select class={`${fieldClass} w-28`} bind:value={assignmentType}>
                <option value="group">Group</option>
                <option value="user">User</option>
              </select>
              <input
                class={fieldClass}
                bind:value={assignmentQuery}
                placeholder="Search or enter exact identifier"
                onkeydown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    void findAssignments();
                  }
                }}
              />
              <button class={secondaryButton} type="button" disabled={directoryLoading || assignmentQuery.trim().length < 2} onclick={() => void findAssignments()}>
                {directoryLoading ? 'Searching…' : 'Search'}
              </button>
              <button class={secondaryButton} type="button" disabled={!assignmentQuery.trim()} onclick={() => addAssignment()}>Add exact</button>
            </div>
            {#if directoryResults.length > 0}
              <div class="mt-2 max-h-44 overflow-y-auto rounded-lg border border-slate-700 bg-[#10141e]">
                {#each directoryResults as entry (entry.id)}
                  <button
                    type="button"
                    class="flex w-full items-center gap-2 border-b border-slate-800 px-3 py-2 text-left last:border-0 hover:bg-blue-500/10"
                    onclick={() => addAssignment(entry.id, entry.name)}
                  >
                    <span class="truncate text-sm font-semibold text-slate-100">{entry.name}</span>
                    <span class="truncate text-xs text-slate-500">{entry.description}</span>
                    <span class="ml-auto shrink-0 font-mono text-[10px] text-slate-600">{entry.id}</span>
                  </button>
                {/each}
              </div>
            {/if}
            <div class="mt-3 flex flex-wrap gap-2">
              {#each editor.assignments as assignment (`${assignment.type}:${assignment.id}`)}
                <button
                  type="button"
                  class="rounded-lg border border-slate-700 bg-slate-900 px-2.5 py-1 text-left text-xs text-slate-300 hover:border-red-700 hover:text-red-300"
                  title="Remove assignment"
                  onclick={() => removeAssignment(assignment)}
                >
                  <span class="mr-1 text-[10px] font-bold uppercase text-slate-500">{assignment.type}</span>
                  {assignment.displayName || assignment.id} ×
                </button>
              {/each}
            </div>
          </fieldset>
        </div>
      </div>

      <div class="mt-5 flex justify-end gap-2 border-t border-slate-800 pt-4">
        <button class={secondaryButton} type="button" onclick={() => (editor = null)}>Cancel</button>
        <button class={primaryButton} type="button" disabled={saving || !editor.name.trim()} onclick={() => void savePolicy()}>
          {saving ? 'Saving…' : 'Save policy'}
        </button>
      </div>
    </section>
  {/if}
{:else if activeTab === 'bundles'}
  <section class={cardClass}>
    <h3 class="text-sm font-bold text-slate-100">Policy membership by bundle</h3>
    <p class="mt-1 text-xs text-slate-500">
      Policies are the normal assignment path. Direct exceptions are shown separately for bootstrap and compatibility needs.
    </p>
    {#if loading}
      <div class="mt-6 flex justify-center"><Spinner size="8" /></div>
    {:else}
      <div class="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
        {#each policyBundleReferences as reference (reference.bundle.id)}
          <div class="rounded-xl border border-slate-800 bg-slate-950/25 p-3">
            <div class="flex items-center justify-between gap-2">
              <span class="truncate font-mono text-xs font-semibold text-slate-200">{reference.bundle.id}</span>
              <span class="text-[10px] uppercase text-slate-600">{reference.bundle.systemOwned ? 'system' : 'user'}</span>
            </div>
            <div class="mt-2 text-xs text-slate-500">
              {reference.bundle.endpointCount} endpoints · {reference.bundle.policyCount} policies
            </div>
            <div class="mt-2 flex flex-wrap gap-1.5">
              {#each reference.policies as policy (policy.policyId)}
                <button type="button" class="rounded bg-blue-500/10 px-2 py-1 text-[11px] text-blue-200" onclick={() => {
                  selectedPolicyId = policy.policyId;
                  activeTab = 'policies';
                }}>{policy.name}</button>
              {:else}
                <span class="text-xs text-slate-700">No policy references</span>
              {/each}
            </div>
          </div>
        {/each}
      </div>
    {/if}
  </section>
  <BundleManagementSection onManagePolicies={() => (activeTab = 'policies')} />
{:else}
  <section class={cardClass} aria-labelledby="effective-access-title">
    <h3 id="effective-access-title" class="text-sm font-bold text-slate-100">Effective access inspector</h3>
    <p class="mt-1 text-xs text-slate-500">
      Evaluate direct exceptions, group membership, policy-derived bundles, endpoint invocation, and every applicable media axis.
    </p>

    <div class="mt-5 grid gap-3 lg:grid-cols-[8rem_minmax(0,1fr)_auto]">
      <label class="text-xs font-semibold text-slate-400">
        Principal
        <select
          class={`${fieldClass} mt-1.5`}
          bind:value={effectiveType}
          onchange={() => {
            effectiveId = '';
            effectivePrincipalQuery = '';
            effectiveDirectoryResults = [];
            effectiveResult = null;
            effectiveCheck = null;
          }}
        >
          <option value="user">User</option>
          <option value="group">Group</option>
        </select>
      </label>
      <label class="text-xs font-semibold text-slate-400">
        Find user or group
        <input
          class={`${fieldClass} mt-1.5`}
          bind:value={effectivePrincipalQuery}
          placeholder="Search display name or enter exact identifier"
          oninput={() => {
            if (effectiveId) effectiveId = '';
          }}
          onkeydown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault();
              void findEffectivePrincipal();
            }
          }}
        />
      </label>
      <div class="flex items-end gap-2">
        <button class={secondaryButton} type="button" disabled={effectiveDirectoryLoading || effectivePrincipalQuery.trim().length < 2} onclick={() => void findEffectivePrincipal()}>
          {effectiveDirectoryLoading ? 'Searching…' : 'Search'}
        </button>
        <button class={secondaryButton} type="button" disabled={!effectivePrincipalQuery.trim()} onclick={() => selectEffectivePrincipal()}>
          Use exact
        </button>
      </div>
    </div>

    {#if effectiveDirectoryResults.length > 0}
      <div class="mt-2 max-h-48 overflow-y-auto rounded-lg border border-slate-700 bg-[#10141e]">
        {#each effectiveDirectoryResults as entry (`${entry.type}:${entry.id}`)}
          <button
            type="button"
            class="flex w-full items-center gap-2 border-b border-slate-800 px-3 py-2 text-left last:border-0 hover:bg-blue-500/10"
            onclick={() => selectEffectivePrincipal(entry)}
          >
            <span class="truncate text-sm font-semibold text-slate-100">{entry.name}</span>
            <span class="truncate text-xs text-slate-500">{entry.description}</span>
            <span class="ml-auto shrink-0 font-mono text-[10px] text-slate-600">{entry.id}</span>
          </button>
        {/each}
      </div>
    {/if}

    <div class="mt-3 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-800 bg-slate-950/25 px-3 py-3">
      <div class="min-w-0">
        <div class="text-[10px] font-bold uppercase text-slate-600">Selected principal</div>
        <div class="mt-1 truncate font-mono text-xs text-slate-300">{effectiveId || 'None selected'}</div>
      </div>
      <button class={primaryButton} type="button" disabled={effectiveLoading || !effectiveId.trim()} onclick={() => void evaluate()}>
        {effectiveLoading ? 'Loading…' : 'Load effective access'}
      </button>
    </div>

    <div class="mt-5 border-t border-slate-800 pt-5">
      <h4 class="text-xs font-bold uppercase tracking-wide text-slate-500">Check a resource</h4>
      <p class="mt-1 text-xs text-slate-600">Check an endpoint, a media item, or both and inspect every authorization-axis reason.</p>
      <div class="mt-3 grid gap-3 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]">
        <label class="text-xs font-semibold text-slate-400">
          Endpoint (optional)
          <input class={`${fieldClass} mt-1.5 font-mono text-xs`} bind:value={effectiveEndpoint} list="endpoint-catalog" placeholder="media.stream" />
          <datalist id="endpoint-catalog">{#each catalog as endpoint}<option value={endpoint.id}></option>{/each}</datalist>
        </label>
        <label class="text-xs font-semibold text-slate-400">
          Media GUID (optional)
          <input class={`${fieldClass} mt-1.5 font-mono text-xs`} bind:value={effectiveMediaGuid} placeholder="00000000-0000-0000-0000-000000000000" />
        </label>
        <div class="flex items-end">
          <button
            class={`${primaryButton} w-full`}
            type="button"
            disabled={effectiveCheckLoading || !effectiveId.trim() || (!effectiveEndpoint.trim() && !effectiveMediaGuid.trim())}
            onclick={() => void runEffectiveCheck()}
          >
            {effectiveCheckLoading ? 'Checking…' : 'Check access'}
          </button>
        </div>
      </div>
    </div>
  </section>

  {#if effectiveResult}
    <section class={cardClass}>
      <div class="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 class="text-base font-bold text-slate-100">{effectiveResult.principalType}: {effectiveResult.principalId}</h3>
          <p class="mt-1 text-xs text-slate-500">{effectiveResult.groups.length} group memberships · {effectiveResult.endpointIds.length} effective endpoints</p>
        </div>
      </div>

      <div class="mt-5 grid gap-4 lg:grid-cols-3">
        <div class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
          <h4 class="text-xs font-bold uppercase text-slate-500">Groups</h4>
          <div class="mt-3 flex flex-wrap gap-2">
            {#each effectiveResult.groups as group}<span class="rounded bg-slate-800 px-2 py-1 text-xs text-slate-300">{group}</span>{:else}<span class="text-sm text-slate-600">None</span>{/each}
          </div>
        </div>
        <div class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
          <h4 class="text-xs font-bold uppercase text-slate-500">Direct exceptions</h4>
          <div class="mt-3 space-y-1">
            {#each effectiveResult.directBundleIds as id}<div class="font-mono text-xs text-slate-300">{id}</div>{:else}<span class="text-sm text-slate-600">None</span>{/each}
          </div>
        </div>
        <div class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
          <h4 class="text-xs font-bold uppercase text-slate-500">Via policies</h4>
          <div class="mt-3 space-y-1">
            {#each effectiveResult.policyBundleIds as id}<div class="font-mono text-xs text-blue-300">{id}</div>{:else}<span class="text-sm text-slate-600">None</span>{/each}
          </div>
        </div>
      </div>

      <div class="mt-4 rounded-xl border border-slate-800 bg-slate-950/20 p-4">
        <h4 class="text-xs font-bold uppercase text-slate-500">Effective endpoints</h4>
        <div class="mt-3 grid max-h-72 gap-1.5 overflow-y-auto sm:grid-cols-2 xl:grid-cols-3">
          {#each effectiveResult.endpointIds as endpointId}
            <div class="truncate rounded bg-slate-900/70 px-2.5 py-1.5 font-mono text-xs text-emerald-200" title={endpointId}>{endpointId}</div>
          {:else}
            <span class="text-sm text-slate-600">No endpoint invocation grants.</span>
          {/each}
        </div>
      </div>

      <div class="mt-4 rounded-xl border border-slate-800 bg-slate-950/20 p-4">
        <h4 class="text-xs font-bold uppercase text-slate-500">Source policies</h4>
        <div class="mt-3 grid gap-2 lg:grid-cols-2">
          {#each effectiveResult.sourcePolicies as policy}
            <div class="rounded-lg border border-slate-800 bg-slate-950/30 px-3 py-2">
              <div class="flex flex-wrap items-center justify-between gap-2">
                <span class="text-xs font-semibold text-blue-200">{policy.name}</span>
                <span class="text-[10px] font-bold uppercase text-slate-500">{policy.syncStatus}</span>
              </div>
              <p class="mt-1 text-[11px] text-slate-500">
                {policy.contributesEndpoints ? 'Endpoint grants' : 'No synchronized endpoint grants'}
                · {policy.contributesDenies ? 'Media denies' : 'No media denies'}
              </p>
            </div>
          {:else}
            <span class="text-sm text-slate-600">No policy assignment contributes to this principal.</span>
          {/each}
        </div>
      </div>

      <div class="mt-4 grid gap-4 lg:grid-cols-3">
        <div class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
          <h4 class="text-xs font-bold uppercase text-slate-500">Denied media GUIDs</h4>
          <div class="mt-3 space-y-1">
            {#each effectiveResult.deniedMediaGuids as mediaGuid}
              <div class="truncate font-mono text-xs text-red-300" title={mediaGuid}>{mediaGuid}</div>
            {:else}
              <span class="text-sm text-slate-600">None</span>
            {/each}
          </div>
        </div>
        <div class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
          <h4 class="text-xs font-bold uppercase text-slate-500">Denied providers</h4>
          <div class="mt-3 flex flex-wrap gap-2">
            {#each effectiveResult.deniedProviders as provider}
              <span class="rounded bg-red-500/10 px-2 py-1 text-xs text-red-200">{provider}</span>
            {:else}
              <span class="text-sm text-slate-600">None</span>
            {/each}
          </div>
        </div>
        <div class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
          <h4 class="text-xs font-bold uppercase text-slate-500">Denied age tiers</h4>
          <div class="mt-3 flex flex-wrap gap-2">
            {#each effectiveResult.deniedAgeThresholds as threshold}
              <span class="rounded bg-red-500/10 px-2 py-1 text-xs text-red-200">{threshold}+</span>
            {:else}
              <span class="text-sm text-slate-600">None</span>
            {/each}
          </div>
        </div>
      </div>

    </section>
  {/if}

  {#if effectiveCheck}
    <section class={cardClass} aria-labelledby="effective-check-result-title">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h3 id="effective-check-result-title" class="text-sm font-bold text-slate-100">Resource check</h3>
          <p class="mt-1 text-xs text-slate-500">
            {effectiveCheck.principalType}: <span class="font-mono">{effectiveCheck.principalId}</span>
            {#if effectiveCheck.media?.title} · {effectiveCheck.media.title}{/if}
          </p>
        </div>
        <span class={[
          'rounded-full border px-3 py-1 text-xs font-bold',
          effectiveCheck.isAllowed
            ? 'border-emerald-500/30 bg-emerald-500/10 text-emerald-200'
            : 'border-red-500/30 bg-red-500/10 text-red-200'
        ]}>{effectiveCheck.isAllowed ? 'Allowed' : 'Denied'}</span>
      </div>

      <div class="mt-4 overflow-x-auto rounded-lg border border-slate-800">
        <table class="w-full min-w-[46rem] text-left text-xs">
          <thead class="bg-slate-900/80 text-slate-500">
            <tr><th class="px-3 py-2">Axis</th><th class="px-3 py-2">Resource</th><th class="px-3 py-2">Status</th><th class="px-3 py-2">Reason / provenance</th></tr>
          </thead>
          <tbody class="divide-y divide-slate-800">
            {#each effectiveCheck.decisions as decision}
              <tr>
                <td class="px-3 py-2 font-semibold text-slate-300">{decision.axis}</td>
                <td class="px-3 py-2 font-mono text-slate-400">{decision.resource || '—'}</td>
                <td class={['px-3 py-2 font-bold', decision.allowed ? 'text-emerald-300' : 'text-red-300']}>{decision.allowed ? 'Allowed' : 'Denied'}</td>
                <td class="px-3 py-2 text-slate-400">
                  {decision.reason}
                  {#if decision.grantingPolicyIds.length > 0}
                    <span class="ml-1 text-emerald-300">by {decision.grantingPolicyIds.map(policyName).join(', ')}</span>
                  {/if}
                  {#if decision.denyingPolicyIds.length > 0}
                    <span class="ml-1 text-red-300">by {decision.denyingPolicyIds.map(policyName).join(', ')}</span>
                  {/if}
                </td>
              </tr>
            {:else}
              <tr><td class="px-3 py-4 text-slate-500" colspan="4">No decision axes were returned.</td></tr>
            {/each}
          </tbody>
        </table>
      </div>
    </section>
  {/if}
{/if}
