<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { onMount } from 'svelte';
  import { ArrowLeft, Copy, Edit, Trash2, X } from '@lucide/svelte';
  import { Modal } from '$lib/components/ui';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
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

  const cardClass = 'card border border-base-300 bg-base-100 p-5 sm:p-6';
  const secondaryButton = 'btn btn-sm btn-neutral text-xs';
  const primaryButton = 'btn btn-sm btn-primary text-xs';

  const policyEditorView = $derived(page.url.searchParams.get('view'));
  const isPolicyEditorView = $derived(policyEditorView === 'new-policy' || policyEditorView === 'edit-policy');
  const isEditingPolicy = $derived(policyEditorView === 'edit-policy');
  let activeTab = $state<Tab>(page.url.searchParams.get('tab') === 'bundles' ? 'bundles' : 'policies');
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
  let duplicateModalOpen = $state(false);
  let duplicateSource = $state<AccessPolicy | null>(null);
  let duplicateName = $state('');
  let duplicating = $state(false);
  let deleteModalOpen = $state(false);
  let deleteTarget = $state<AccessPolicy | null>(null);

  let assignmentType = $state<GranteeType>('group');
  let assignmentQuery = $state('');
  let directoryResults = $state<DirectoryEntry[]>([]);
  let directoryLoading = $state(false);
  let mediaGuidInput = $state('');
  let mediaGuidLoading = $state(false);
  let mediaGuidError = $state<string | null>(null);
  let mediaSummaries = $state<Record<string, MediaSummary>>({});
  let providerInput = $state('');
  let assignmentSearchTimer: ReturnType<typeof setTimeout> | undefined;

  let effectiveType = $state<GranteeType>('user');
  let effectiveId = $state('');
  let effectivePrincipalQuery = $state('');
  let effectiveDirectoryResults = $state<DirectoryEntry[]>([]);
  let effectiveDirectoryLoading = $state(false);
  let effectiveEndpoint = $state('');
  let endpointDropdownOpen = $state(false);
  let effectiveMediaGuid = $state('');
  let effectiveResult = $state<EffectiveAccess | null>(null);
  let effectiveCheck = $state<EffectiveAccessCheck | null>(null);
  let effectiveLoading = $state(false);
  let effectiveCheckLoading = $state(false);
  let effectiveSearchTimer: ReturnType<typeof setTimeout> | undefined;

  const selectedPolicy = $derived(policies.find((policy) => policy.policyId === selectedPolicyId) ?? policies[0] ?? null);
  onMount(() => {
    void loadAll();
  });

  $effect(() => {
    if (!isPolicyEditorView) {
      if (editor) editor = null;
      return;
    }
    if (isPolicyEditorView && !editor && !saving) {
      if (isEditingPolicy) {
        const policyId = page.url.searchParams.get('policyId');
        const policy = policies.find((item) => item.policyId === policyId);
        if (policy) beginEdit(policy);
      } else {
        beginCreate();
      }
    }
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
      const nextSelectedPolicy = policies.find((policy) => policy.policyId === selectedPolicyId);
      if (nextSelectedPolicy) void resolveExistingMedia(nextSelectedPolicy.mediaGuids);
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
      if (isPolicyEditorView) await goto('/admin/access-control');
    } catch (err) {
      error = messageFor(err, 'Could not save the policy.');
    } finally {
      saving = false;
    }
  }

  function openDeleteModal(policy: AccessPolicy) {
    deleteTarget = policy;
    deleteModalOpen = true;
  }

  async function removePolicy() {
    const policy = deleteTarget;
    if (!policy) return;
    try {
      await deleteAccessPolicy(policy.policyId);
      notice = `Deleted “${policy.name}”.`;
      editor = null;
      deleteModalOpen = false;
      await loadAll();
    } catch (err) {
      throw new Error(messageFor(err, 'Could not delete the policy.'));
    }
  }

  function openDuplicateModal(policy: AccessPolicy) {
    duplicateSource = policy;
    duplicateName = `${policy.name}-copy`.toLowerCase().replace(/[^a-z0-9-]/g, '');
    duplicateModalOpen = true;
  }

  async function duplicatePolicy() {
    const policy = duplicateSource;
    const name = duplicateName.trim().toLowerCase().replace(/[^a-z0-9-]/g, '');
    if (!policy || !name) return;
    duplicating = true;
    error = null;
    try {
      const copy = await duplicateAccessPolicy(policy.policyId, name);
      notice = `Created disabled policy “${copy.name}”.`;
      duplicateModalOpen = false;
      await loadAll(copy.policyId);
    } catch (err) {
      error = messageFor(err, 'Could not duplicate the policy.');
    } finally {
      duplicating = false;
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
    if (query.length < 2) {
      directoryResults = [];
      return;
    }
    directoryLoading = true;
    error = null;
    try {
      const results = await searchDirectory(assignmentType, query);
      if (assignmentQuery.trim() === query) directoryResults = results;
    } catch (err) {
      error = messageFor(err, 'Directory search failed. You can still add the exact identifier.');
    } finally {
      directoryLoading = false;
    }
  }

  function queueAssignmentSearch() {
    if (assignmentSearchTimer) clearTimeout(assignmentSearchTimer);
    directoryResults = [];
    if (assignmentQuery.trim().length < 2) return;
    assignmentSearchTimer = setTimeout(() => void findAssignments(), 250);
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

  function matchingProviders(): string[] {
    const query = providerInput.trim().toLowerCase();
    return providerCatalog.filter((provider) =>
      !editor?.providers.includes(provider) && (!query || provider.toLowerCase().includes(query))
    );
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

  function queueEffectivePrincipalSearch() {
    if (effectiveSearchTimer) clearTimeout(effectiveSearchTimer);
    effectiveDirectoryResults = [];
    if (effectivePrincipalQuery.trim().length < 2) return;
    effectiveSearchTimer = setTimeout(() => void findEffectivePrincipal(), 250);
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
      void resolveExistingMedia(effectiveResult.deniedMediaGuids);
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
    if (policy.syncStatus === 'Synced') return 'badge-accent';
    if (policy.syncStatus === 'Failed') return 'badge-error';
    return 'badge-warning';
  }

  function messageFor(value: unknown, fallback: string): string {
    if (value instanceof ApiRequestError && value.status === 503) {
      return `${value.message || fallback} The synchronization service will retry pending policy revisions.`;
    }
    return value instanceof Error ? value.message : fallback;
  }
</script>

<svelte:head>
  <title>{isEditingPolicy ? 'Edit policy · FrostStream' : isPolicyEditorView ? 'Create policy · FrostStream' : 'Access control · FrostStream'}</title>
</svelte:head>

{#if !isPolicyEditorView}
<section class={cardClass} aria-labelledby="access-control-title">
  <div class="flex flex-wrap items-start justify-between gap-3">
    <div>
      <h2 id="access-control-title" class="text-base font-bold text-base-content">Access control</h2>
      <p class="mt-2 max-w-3xl text-sm text-base-content/60">
        Grant endpoint bundles and deny media GUIDs, providers, or age tiers with named policies assigned to users and groups.
      </p>
    </div>
    <button class={secondaryButton} type="button" disabled={loading} onclick={() => void loadAll()}>
      Refresh
    </button>
  </div>

  <div class="mt-5 flex flex-wrap gap-2 border-b border-base-300" role="tablist" aria-label="Access control views">
    {#each ([['policies', 'Policies'], ['bundles', 'Bundles'], ['effective', 'Effective access']] as [Tab, string][]) as [id, label]}
      <button
        type="button"
        role="tab"
        aria-selected={activeTab === id}
        class={[
          'border-b-2 px-3 py-2.5 text-sm font-semibold transition',
          activeTab === id
            ? 'border-primary text-primary'
            : 'border-transparent text-base-content/50 hover:text-base-content/90'
        ]}
        onclick={() => { activeTab = id; notice = null; }}
      >
        {label}
      </button>
    {/each}
  </div>

  {#if error}
    <div class="alert alert-error mt-4 text-sm" role="alert">{error}</div>
  {/if}
  {#if notice}
    <div class="alert alert-success mt-4 text-sm" role="status">{notice}</div>
  {/if}
</section>
{/if}

<Modal bind:open={duplicateModalOpen} title="Duplicate policy" size="md">
  <div class="space-y-4">
    <p class="text-sm text-base-content/70">
      Create a disabled copy of <span class="font-semibold text-base-content">{duplicateSource?.name}</span>.
    </p>
    <div>
      <label class="label text-sm" for="duplicate-policy-name">New policy name</label>
      <input
        id="duplicate-policy-name"
        class="input w-full"
        type="text"
        bind:value={duplicateName}
        placeholder="policy-copy"
        autocomplete="off"
      />
      <p class="mt-1.5 text-xs text-base-content/50">Use lowercase letters, numbers, and hyphens.</p>
    </div>
  </div>

  {#snippet footer()}
    <div class="flex w-full justify-end gap-2">
      <button class="btn btn-sm btn-ghost text-xs" type="button" disabled={duplicating} onclick={() => (duplicateModalOpen = false)}>
        Cancel
      </button>
      <button class="btn btn-sm btn-neutral text-xs" type="button" disabled={duplicating || !duplicateName.trim()} onclick={() => void duplicatePolicy()}>
        {#if duplicating}<span class="loading loading-spinner loading-xs mr-1.5"></span>{/if}
        Duplicate
      </button>
    </div>
  {/snippet}
</Modal>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete access policy"
  message={`Delete “${deleteTarget?.name ?? ''}”? This removes its assignments, bundle access, and media scopes.`}
  confirmLabel="Delete policy"
  onConfirm={removePolicy}
/>

{#if activeTab === 'policies'}
  {#if !isPolicyEditorView}
  <section class={cardClass}>
    <div class="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h3 class="text-sm font-bold text-base-content">Policies</h3>
        <p class="mt-1 text-xs text-base-content/50">
          Media denies apply from the database immediately; OpenFGA synchronization status governs endpoint-bundle grants.
        </p>
      </div>
      <a class={primaryButton} href="/admin/access-control/policies/new">New policy</a>
    </div>

    {#if loading}
      <div class="mt-10 flex justify-center"><span class="loading loading-spinner loading-md"></span></div>
    {:else if policies.length === 0}
      <div class="mt-5 rounded-xl border border-dashed border-base-content/20 p-8 text-center text-sm text-base-content/50">
        No access policies yet. Create one to combine endpoint grants and media denies.
      </div>
    {:else}
      <div class="mt-5 grid gap-5 2xl:grid-cols-[21rem_minmax(0,1fr)]">
        <aside class="max-h-[46rem] overflow-y-auto rounded-xl border border-base-300">
          {#each policies as policy (policy.policyId)}
            <button
              type="button"
              class={[
                'block w-full border-b border-base-300 px-4 py-3 text-left transition last:border-b-0',
                selectedPolicy?.policyId === policy.policyId ? 'bg-base-200' : 'hover:bg-base-200'
              ]}
              onclick={() => {
                selectedPolicyId = policy.policyId;
                editor = null;
                void resolveExistingMedia(policy.mediaGuids);
              }}
            >
              <div class="min-w-0 text-sm font-semibold text-base-content">{policy.name}</div>
              <div class="mt-1 flex flex-wrap items-center gap-1.5">
                <span class="text-xs text-base-content/50">
                  {policy.assignments.length} principals · {policy.bundleIds.length} bundles
                </span>
                <span class={['badge badge-sm', syncClass(policy)]}>
                  {policy.syncStatus}
                </span>
                {#if !policy.enabled}
                  <span class="badge badge-sm badge-warning">Disabled</span>
                {/if}
              </div>
            </button>
          {/each}
        </aside>

        {#if selectedPolicy}
          <div class="min-w-0 space-y-4">
            <div class="card border border-base-300 bg-base-100 p-5 sm:p-6">
              <div class="flex flex-wrap items-start justify-between gap-3">
                <div class="min-w-0">
                  <h3 class="text-lg font-bold text-base-content">{selectedPolicy.name}</h3>
                  <p class="mt-2 text-sm text-base-content/60">{selectedPolicy.description || 'No description.'}</p>
                  {#if selectedPolicy.syncError}
                    <p class="mt-2 text-xs text-error">{selectedPolicy.syncError}</p>
                  {/if}
                </div>
                <div class="flex flex-wrap items-center gap-2">
                  <input
                    type="checkbox"
                    class="toggle toggle-primary"
                    checked={selectedPolicy.enabled}
                    aria-label={selectedPolicy.enabled ? 'Disable policy' : 'Enable policy'}
                    onchange={() => void togglePolicy(selectedPolicy)}
                  />
                  <a class="btn btn-sm btn-neutral text-xs" href={`/admin/access-control/policies/${encodeURIComponent(selectedPolicy.policyId)}/edit`}>
                    <Edit class="mr-1.5 h-4 w-4" />
                    Edit
                  </a>
                  <button class="btn btn-sm btn-neutral text-xs" type="button" onclick={() => openDuplicateModal(selectedPolicy)}>
                    <Copy class="mr-1.5 h-4 w-4" />
                    Duplicate
                  </button>
                  <button
                    class="btn btn-sm btn-neutral text-xs text-error hover:text-error"
                    type="button"
                    onclick={() => openDeleteModal(selectedPolicy)}
                  >
                    <Trash2 class="mr-1.5 h-4 w-4" />
                    Delete
                  </button>
                </div>
              </div>
            </div>

            <div class="mt-5 space-y-2">
              <details class="collapse rounded-lg border border-base-300 bg-base-100">
                <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">
                  <span>Endpoint bundles</span>
                </summary>
                <div class="collapse-content px-0 pb-0">
                  <div class="divide-y divide-base-300/70 border-t border-base-300/80">
                    {#each selectedPolicy.bundleIds as bundleId}
                      <div class="px-3 py-2 font-mono text-xs text-base-content/80">{bundleId}</div>
                    {:else}
                      <div class="px-3 py-3 text-sm text-base-content/40">No endpoint bundles.</div>
                    {/each}
                  </div>
                </div>
              </details>

              <details class="collapse rounded-lg border border-base-300 bg-base-100">
                <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">
                  <span>Policy assignments</span>
                </summary>
                <div class="collapse-content px-0 pb-0">
                  {#if selectedPolicy.assignments.length === 0}
                    <div class="border-t border-base-300/80 px-3 py-3 text-sm text-base-content/40">No assignments.</div>
                  {:else}
                    <div class="overflow-x-auto border-t border-base-300/80">
                      <table class="w-full min-w-[30rem] text-left text-xs">
                        <thead class="bg-base-200 text-base-content">
                          <tr>
                            <th class="px-3 py-2 font-semibold">User/Group</th>
                            <th class="px-3 py-2 font-semibold">Name</th>
                            <th class="px-3 py-2 font-semibold">ID</th>
                          </tr>
                        </thead>
                        <tbody class="divide-y divide-base-300/80">
                          {#each selectedPolicy.assignments as assignment}
                            <tr>
                              <td class="px-3 py-2 font-semibold text-base-content/60">{assignment.type === 'user' ? 'User' : 'Group'}</td>
                              <td class="px-3 py-2 text-base-content/90">{assignment.displayName || '—'}</td>
                              <td class="break-all px-3 py-2 font-mono text-base-content/60">{assignment.id}</td>
                            </tr>
                          {/each}
                        </tbody>
                      </table>
                    </div>
                  {/if}
                </div>
              </details>

              <details class="collapse rounded-lg border border-base-300 bg-base-100">
                <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">
                  <span>Denied media GUIDs</span>
                </summary>
                <div class="collapse-content px-0 pb-0">
                  {#if selectedPolicy.mediaGuids.length === 0}
                    <div class="border-t border-base-300/80 px-3 py-3 text-sm text-base-content/40">No media GUID denies.</div>
                  {:else}
                    <div class="overflow-x-auto border-t border-base-300/80">
                      <table class="w-full table-fixed text-left text-xs">
                        <thead class="bg-base-200 text-base-content">
                          <tr>
                            <th class="w-1/2 px-3 py-2 font-semibold">Title</th>
                            <th class="w-1/2 px-3 py-2 font-semibold">GUID</th>
                          </tr>
                        </thead>
                        <tbody class="divide-y divide-base-300/80">
                          {#each selectedPolicy.mediaGuids as guid}
                            {@const summary = mediaSummaries[guid]}
                            <tr>
                              <td class="max-w-0 px-3 py-2">
                                <div class="truncate text-base-content/90" title={summary?.title || 'Media item'}>{summary?.title || 'Media item'}</div>
                              </td>
                              <td class="break-all px-3 py-2 font-mono text-base-content/60">{guid}</td>
                            </tr>
                          {/each}
                        </tbody>
                      </table>
                    </div>
                  {/if}
                </div>
              </details>

              <details class="collapse rounded-lg border border-base-300 bg-base-100">
                <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">
                  <span>Denied providers</span>
                </summary>
                <div class="collapse-content px-0 pb-0">
                  <div class="divide-y divide-base-300/70 border-t border-base-300/80">
                    {#each selectedPolicy.providers as provider}
                      <div class="px-3 py-2 text-xs text-base-content/80">{provider}</div>
                    {:else}
                      <div class="px-3 py-3 text-sm text-base-content/40">No provider denies.</div>
                    {/each}
                  </div>
                </div>
              </details>

              <details class="collapse rounded-lg border border-base-300 bg-base-100">
                <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">
                  <span>Denied ages</span>
                </summary>
                <div class="collapse-content px-0 pb-0">
                  <div class="divide-y divide-base-300/70 border-t border-base-300/80">
                    {#each selectedPolicy.ageThresholds as age}
                      <div class="px-3 py-2 text-xs text-base-content/80">Deny age {age}+</div>
                    {:else}
                      <div class="px-3 py-3 text-sm text-base-content/40">No age denies.</div>
                    {/each}
                  </div>
                </div>
              </details>
            </div>
          </div>
        {/if}
      </div>
    {/if}
  </section>
  {/if}

  {#if editor}
    <section class={cardClass} aria-labelledby="policy-editor-title">
      <div class="flex items-center justify-between gap-3">
        <div>
          <h3 id="policy-editor-title" class="text-sm font-bold text-base-content">{isEditingPolicy ? 'Edit policy' : 'Create policy'}</h3>
          <p class="mt-1 text-xs text-base-content/50">
            Bundles grant endpoint access. Media GUIDs, providers, and age tiers deny playback; everything else stays watchable.
          </p>
        </div>
      </div>

      <div class="mt-5 grid min-w-0 gap-5 xl:grid-cols-2">
        <div class="min-w-0 space-y-4">
          <label class="block text-xs text-base-content/60">
            Name
            <input
              class="input mt-1.5 w-full"
              bind:value={editor.name}
              placeholder="family-viewing"
              maxlength="100"
              autocomplete="off"
              autocapitalize="none"
              spellcheck="false"
              disabled={isEditingPolicy}
              oninput={(event) => {
                if (!editor) return;
                editor.name = (event.currentTarget as HTMLInputElement).value.toLowerCase().replace(/[^a-z0-9-]/g, '');
              }}
            />
            <span class="mt-1 block text-xs text-base-content/40">Use lowercase letters, numbers, and dashes only.</span>
          </label>
          <label class="block text-xs text-base-content/60">
            Description
            <textarea class="textarea w-full mt-1.5 min-h-20" bind:value={editor.description} placeholder="What this policy grants and restricts"></textarea>
          </label>
          <label class="flex items-center gap-2 text-sm text-base-content/80">
            <input type="checkbox" bind:checked={editor.enabled} class="checkbox checkbox-sm checkbox-primary" />
            Enabled
          </label>

          <fieldset class="min-w-0 [min-inline-size:0]">
            <legend class="text-xs font-semibold text-base-content/60">Endpoint bundles</legend>
            <ul class="mt-2 max-h-64 divide-y divide-base-300 overflow-y-auto rounded-lg border border-base-300 bg-base-100">
              {#each bundles as bundle (bundle.id)}
                <li>
                <label class="flex items-center gap-2 px-3 py-2 text-sm text-base-content/80 hover:bg-base-200">
                  <input type="checkbox" value={bundle.id} bind:group={editor.bundleIds} class="checkbox checkbox-sm checkbox-primary" />
                  <span class="min-w-0 truncate font-mono text-xs">{bundle.id}</span>
                  <span class="ml-auto shrink-0 text-[10px] uppercase text-base-content/40">{bundle.systemOwned ? 'system' : 'user'}</span>
                </label>
                </li>
              {/each}
            </ul>
          </fieldset>
        </div>

        <div class="min-w-0 space-y-4">
          <fieldset class="min-w-0 [min-inline-size:0]">
            <legend class="text-xs font-semibold text-base-content/60">Denied media</legend>
            <p class="mt-1 text-xs text-base-content/40">Resolve a media GUID before adding it so the policy targets a known item.</p>
            <div class="mt-2 flex gap-2">
              <input
                class="input w-full max-w-xs font-mono text-xs"
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
                {mediaGuidLoading ? 'Adding…' : 'Add'}
              </button>
            </div>
            {#if mediaGuidError}
              <p class="mt-2 text-xs text-error">{mediaGuidError}</p>
            {/if}
            <div class="mt-3 w-full min-w-0 max-w-full space-y-2 overflow-y-auto rounded-lg border border-base-300 bg-base-100 p-2">
              {#each editor.mediaGuids as mediaGuid (mediaGuid)}
                {@const summary = mediaSummaries[mediaGuid]}
                <div class="flex w-full min-w-0 max-w-full items-start gap-3 overflow-hidden rounded-lg border border-base-300 bg-base-200/35 px-3 py-2">
                  <div class="min-w-0 flex-1 overflow-hidden">
                    <div class="block w-full min-w-0 truncate text-xs font-semibold text-base-content/90">{summary?.title || 'Media item'}</div>
                    <div class="mt-1 block w-full min-w-0 truncate font-mono text-[10px] text-base-content/50">{mediaGuid}</div>
                    {#if summary}
                      <div class="mt-1 truncate text-[10px] text-base-content/40">
                        {summary.providers.join(', ') || 'No provider'} · {summary.ageLimit == null ? 'Unrated' : `${summary.ageLimit}+`}
                      </div>
                    {/if}
                  </div>
                  <button
                    type="button"
                    class="btn btn-sm btn-neutral ml-auto shrink-0 text-xs"
                    aria-label={`Remove media ${mediaGuid}`}
                    onclick={() => removeMediaGuid(mediaGuid)}
                  >
                    <Trash2 class="mr-1.5 h-4 w-4" />
                    Delete
                  </button>
                </div>
              {:else}
                <div class="rounded-lg border border-dashed border-base-300 px-3 py-3 text-xs text-base-content/40">No media GUID denies.</div>
              {/each}
            </div>
          </fieldset>

          <fieldset>
            <legend class="text-xs font-semibold text-base-content/60">Denied providers</legend>
            <div class="relative mt-2">
              <input
                class="input w-full"
                bind:value={providerInput}
                placeholder="Search providers"
                autocomplete="off"
                onkeydown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    const provider = matchingProviders()[0];
                    if (provider) addKnownProvider(provider);
                  }
                }}
              />
              {#if providerInput.trim().length > 0 && matchingProviders().length > 0}
                <div class="absolute z-10 mt-1 max-h-44 w-full overflow-y-auto rounded-lg border border-base-content/20 bg-base-200 shadow-xl shadow-black/30">
                  {#each matchingProviders() as provider (provider)}
                    <button
                      type="button"
                      class="block w-full px-3 py-2 text-left text-sm text-base-content/90 hover:bg-primary/10"
                      onclick={() => addKnownProvider(provider)}
                    >{provider}</button>
                  {/each}
                </div>
              {/if}
            </div>
            <div class="mt-3 flex flex-wrap gap-2">
              {#each editor.providers as provider (provider)}
                <button
                  type="button"
                  class="badge badge-accent gap-1 text-xs"
                  title="Remove provider"
                  onclick={() => removeProvider(provider)}
                >
                  {provider}
                  <X class="h-3 w-3" />
                </button>
              {:else}
                <span class="text-xs text-base-content/40">No provider denies.</span>
              {/each}
            </div>
          </fieldset>

          <label class="block text-xs text-base-content/60">
            Deny at age or above
            <input class="input w-full mt-1.5" bind:value={editor.ageThresholds} placeholder="13, 16, 18" />
          </label>

          <fieldset>
            <legend class="text-xs font-semibold text-base-content/60">Who receives this policy</legend>
            <p class="mt-1 text-xs text-base-content/40">
              These assignments receive every selected bundle and the configured media access in one place.
            </p>
            <div class="mt-2 flex gap-2">
              <select
                class="select w-24 shrink-0"
                bind:value={assignmentType}
                onchange={() => queueAssignmentSearch()}
              >
                <option value="group">Group</option>
                <option value="user">User</option>
              </select>
              <input
                class="input min-w-0 flex-1"
                bind:value={assignmentQuery}
                placeholder="Search Authentik users or groups"
                autocomplete="off"
                oninput={queueAssignmentSearch}
                onkeydown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    const entry = directoryResults[0];
                    if (entry) addAssignment(entry.id, entry.name);
                    else addAssignment();
                  }
                }}
              />
            </div>
            {#if directoryLoading || directoryResults.length > 0}
              <ul class="mt-2 max-h-44 w-full min-w-0 divide-y divide-base-300 overflow-x-hidden overflow-y-auto rounded-lg border border-base-300 bg-base-100">
                {#if directoryLoading}
                  <li class="px-3 py-2 text-xs text-base-content/50">Searching Authentik…</li>
                {:else}
                  {#each directoryResults as entry (entry.id)}
                  <li>
                  <button
                    type="button"
                    class="flex w-full min-w-0 items-center gap-2 px-3 py-2 text-left hover:bg-base-200"
                    onclick={() => addAssignment(entry.id, entry.name)}
                  >
                    <div class="min-w-0 flex-1">
                      <span class="block truncate text-sm font-semibold text-base-content">{entry.name}</span>
                      <span class="block truncate text-xs text-base-content/50">{entry.description}</span>
                      <span class="block truncate font-mono text-[10px] text-base-content/40">{entry.id}</span>
                    </div>
                  </button>
                  </li>
                  {/each}
                {/if}
              </ul>
            {/if}
            <div class="mt-3 flex flex-wrap gap-2">
              {#each editor.assignments as assignment (`${assignment.type}:${assignment.id}`)}
                <button
                  type="button"
                  class="badge badge-accent h-auto gap-1 py-1 text-left text-xs"
                  title="Remove assignment"
                  onclick={() => removeAssignment(assignment)}
                >
                  <span class="text-[10px] font-bold uppercase">{assignment.type}</span>
                  <span class="max-w-48 truncate">{assignment.displayName || assignment.id}</span>
                  <X class="h-3 w-3 shrink-0" />
                </button>
              {/each}
            </div>
          </fieldset>
        </div>
      </div>

      <div class="mt-5 flex items-center justify-between gap-2 border-t border-base-300 pt-4">
        <a class={secondaryButton} href="/admin/access-control"><ArrowLeft class="mr-1.5 h-4 w-4" />Back</a>
        <button class={primaryButton} type="button" disabled={saving || !editor.name.trim()} onclick={() => void savePolicy()}>
          {saving ? 'Saving…' : 'Save policy'}
        </button>
      </div>
    </section>
  {/if}
{:else if activeTab === 'bundles'}
  <BundleManagementSection policies={policies} onManagePolicies={() => (activeTab = 'policies')} />
{:else}
  <section class={cardClass} aria-labelledby="effective-access-title">
    <h3 id="effective-access-title" class="text-sm font-bold text-base-content">Effective access inspector</h3>
    <p class="mt-1 text-xs text-base-content/50">
      Evaluate direct exceptions, group membership, policy-derived bundles, endpoint invocation, and every applicable media axis.
    </p>

    <div class="mt-5 grid gap-3 lg:grid-cols-[8rem_minmax(0,1fr)_auto]">
      <label class="text-xs font-semibold text-base-content/60">
        Principal
        <select
          class="select w-full mt-1.5"
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
      <label class="text-xs font-semibold text-base-content/60">
        Find user or group
        <input
          class="input w-full mt-1.5"
          bind:value={effectivePrincipalQuery}
          placeholder="Search users or groups"
          oninput={() => {
            if (effectiveId) effectiveId = '';
            queueEffectivePrincipalSearch();
          }}
          onkeydown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault();
              const entry = effectiveDirectoryResults[0];
              if (entry) selectEffectivePrincipal(entry);
              else selectEffectivePrincipal();
            }
          }}
        />
      </label>
    </div>

    {#if effectiveDirectoryLoading || effectiveDirectoryResults.length > 0}
      <div class="mt-2 max-h-48 overflow-y-auto rounded-lg border border-base-content/20 bg-base-200">
        {#if effectiveDirectoryLoading}
          <div class="px-3 py-2 text-xs text-base-content/50">Searching Authentik…</div>
        {:else}
          {#each effectiveDirectoryResults as entry (`${entry.type}:${entry.id}`)}
            <button
              type="button"
              class="flex w-full items-center gap-2 border-b border-base-300 px-3 py-2 text-left last:border-0 hover:bg-primary/10"
              onclick={() => selectEffectivePrincipal(entry)}
            >
              <span class="truncate text-sm font-semibold text-base-content">{entry.name}</span>
              <span class="truncate text-xs text-base-content/50">{entry.description}</span>
              <span class="ml-auto shrink-0 font-mono text-[10px] text-base-content/40">{entry.id}</span>
            </button>
          {/each}
        {/if}
      </div>
    {/if}

    <div class="mt-3 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-base-300 bg-base-200/25 px-3 py-3">
      <div class="min-w-0">
        <div class="text-[10px] font-bold uppercase text-base-content/40">Selected principal</div>
        <div class="mt-1 truncate font-mono text-xs text-base-content/80">{effectiveId || 'None selected'}</div>
      </div>
      <button class={primaryButton} type="button" disabled={effectiveLoading || !effectiveId.trim()} onclick={() => void evaluate()}>
        {effectiveLoading ? 'Loading…' : 'Load effective access'}
      </button>
    </div>

    <div class="mt-5 border-t border-base-300 pt-5">
      <h4 class="text-xs font-bold uppercase tracking-wide text-base-content/50">Check a resource</h4>
      <p class="mt-1 text-xs text-base-content/40">Check an endpoint, a media item, or both and inspect every authorization-axis reason.</p>
      <div class="mt-3 grid gap-3 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]">
        <div class="text-xs font-semibold text-base-content/60">
          Endpoint (optional)
          <div class={['dropdown mt-1.5 w-full', endpointDropdownOpen && 'dropdown-open']}>
            <input
              class="input w-full font-mono text-xs"
              bind:value={effectiveEndpoint}
              placeholder="media.stream"
              autocomplete="off"
              role="combobox"
              aria-expanded={endpointDropdownOpen}
              aria-controls="effective-endpoint-options"
              onfocus={() => (endpointDropdownOpen = true)}
              oninput={() => (endpointDropdownOpen = true)}
              onkeydown={(event) => {
                if (event.key === 'Escape') endpointDropdownOpen = false;
                if (event.key === 'Enter' && endpointDropdownOpen) endpointDropdownOpen = false;
              }}
            />
            {#if endpointDropdownOpen}
              <ul id="effective-endpoint-options" class="dropdown-content menu z-20 mt-1 max-h-60 w-full flex-nowrap overflow-y-auto rounded-lg border border-base-300 bg-base-100 p-1 shadow-xl" role="listbox">
                {#each catalog.filter((endpoint) => !effectiveEndpoint.trim() || endpoint.id.toLowerCase().includes(effectiveEndpoint.trim().toLowerCase())) as endpoint (endpoint.id)}
                  <li>
                    <button type="button" class="block w-full truncate text-left font-mono text-xs" onclick={() => { effectiveEndpoint = endpoint.id; endpointDropdownOpen = false; }}>
                      {endpoint.id}
                    </button>
                  </li>
                {:else}
                  <li><span class="text-xs text-base-content/50">No matching endpoints.</span></li>
                {/each}
              </ul>
            {/if}
          </div>
        </div>
        <label class="text-xs font-semibold text-base-content/60">
          Media GUID (optional)
          <input class="input w-full mt-1.5 font-mono text-xs" bind:value={effectiveMediaGuid} placeholder="00000000-0000-0000-0000-000000000000" />
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
          <h3 class="text-base font-bold text-base-content">{effectiveResult.principalType}: {effectiveResult.principalId}</h3>
          <p class="mt-1 text-xs text-base-content/50">{effectiveResult.groups.length} group memberships · {effectiveResult.endpointIds.length} effective endpoints</p>
        </div>
      </div>

      <div class="mt-5 space-y-2">
        <details class="collapse rounded-lg border border-base-300 bg-base-100">
          <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">Groups</summary>
          <div class="collapse-content px-0 pb-0">
            <div class="divide-y divide-base-300/70 border-t border-base-300/80">
              {#each effectiveResult.groups as group}
                <div class="px-3 py-2 text-xs text-base-content/80">{group}</div>
              {:else}
                <div class="px-3 py-3 text-sm text-base-content/40">None</div>
              {/each}
            </div>
          </div>
        </details>

        <details class="collapse rounded-lg border border-base-300 bg-base-100">
          <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">Direct exceptions</summary>
          <div class="collapse-content px-0 pb-0">
            <div class="divide-y divide-base-300/70 border-t border-base-300/80">
              {#each effectiveResult.directBundleIds as id}
                <div class="px-3 py-2 font-mono text-xs text-base-content/80">{id}</div>
              {:else}
                <div class="px-3 py-3 text-sm text-base-content/40">None</div>
              {/each}
            </div>
          </div>
        </details>

        <details class="collapse rounded-lg border border-base-300 bg-base-100">
          <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">Via policies</summary>
          <div class="collapse-content px-0 pb-0">
            <div class="divide-y divide-base-300/70 border-t border-base-300/80">
              {#each effectiveResult.policyBundleIds as id}
                <div class="px-3 py-2 font-mono text-xs text-base-content">{id}</div>
              {:else}
                <div class="px-3 py-3 text-sm text-base-content/40">None</div>
              {/each}
            </div>
          </div>
        </details>

        <details class="collapse rounded-lg border border-base-300 bg-base-100">
          <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">Effective endpoints</summary>
          <div class="collapse-content px-0 pb-0">
            <div class="max-h-72 divide-y divide-base-300/70 overflow-y-auto border-t border-base-300/80">
              {#each effectiveResult.endpointIds as endpointId}
                <div class="truncate px-3 py-2 font-mono text-xs text-base-content" title={endpointId}>{endpointId}</div>
              {:else}
                <div class="px-3 py-3 text-sm text-base-content/40">No endpoint invocation grants.</div>
              {/each}
            </div>
          </div>
        </details>

        <details class="collapse rounded-lg border border-base-300 bg-base-100">
          <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">Source policies</summary>
          <div class="collapse-content px-0 pb-0">
            <div class="divide-y divide-base-300/70 border-t border-base-300/80">
              {#each effectiveResult.sourcePolicies as policy}
                <div class="px-3 py-2">
                  <span class="block truncate text-xs font-semibold text-base-content" title={policy.name}>{policy.name}</span>
                </div>
              {:else}
                <div class="px-3 py-3 text-sm text-base-content/40">No policy assignment contributes to this principal.</div>
              {/each}
            </div>
          </div>
        </details>

        <details class="collapse rounded-lg border border-base-300 bg-base-100">
          <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">Denied media GUIDs</summary>
          <div class="collapse-content px-0 pb-0">
            {#if effectiveResult.deniedMediaGuids.length === 0}
              <div class="border-t border-base-300/80 px-3 py-3 text-sm text-base-content/40">None</div>
            {:else}
              <div class="overflow-x-auto border-t border-base-300/80">
                <table class="w-full table-fixed text-left text-xs">
                  <thead class="bg-base-200 text-base-content">
                    <tr>
                      <th class="w-1/2 px-3 py-2 font-semibold">Title</th>
                      <th class="w-1/2 px-3 py-2 font-semibold">GUID</th>
                    </tr>
                  </thead>
                  <tbody class="divide-y divide-base-300/80">
                    {#each effectiveResult.deniedMediaGuids as mediaGuid}
                      {@const summary = mediaSummaries[mediaGuid]}
                      <tr>
                        <td class="max-w-0 px-3 py-2">
                          <div class="truncate text-base-content" title={summary?.title || 'Media item'}>{summary?.title || 'Media item'}</div>
                        </td>
                        <td class="break-all px-3 py-2 font-mono text-base-content">{mediaGuid}</td>
                      </tr>
                    {/each}
                  </tbody>
                </table>
              </div>
            {/if}
          </div>
        </details>

        <details class="collapse rounded-lg border border-base-300 bg-base-100">
          <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">Denied providers</summary>
          <div class="collapse-content px-0 pb-0">
            <div class="divide-y divide-base-300/70 border-t border-base-300/80">
              {#each effectiveResult.deniedProviders as provider}
                <div class="px-3 py-2 text-xs text-base-content">{provider}</div>
              {:else}
                <div class="px-3 py-3 text-sm text-base-content/40">None</div>
              {/each}
            </div>
          </div>
        </details>

        <details class="collapse rounded-lg border border-base-300 bg-base-100">
          <summary class="collapse-title flex min-h-0 cursor-pointer list-none items-center gap-2 rounded-t-lg bg-base-300 px-3 py-2 text-xs font-semibold text-base-content [&::-webkit-details-marker]:hidden">Denied age tiers</summary>
          <div class="collapse-content px-0 pb-0">
            <div class="divide-y divide-base-300/70 border-t border-base-300/80">
              {#each effectiveResult.deniedAgeThresholds as threshold}
                <div class="px-3 py-2 text-xs text-base-content">{threshold}+</div>
              {:else}
                <div class="px-3 py-3 text-sm text-base-content/40">None</div>
              {/each}
            </div>
          </div>
        </details>
      </div>

    </section>
  {/if}

  {#if effectiveCheck}
    <section class={cardClass} aria-labelledby="effective-check-result-title">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h3 id="effective-check-result-title" class="text-sm font-bold text-base-content">Resource check</h3>
          <p class="mt-1 text-xs text-base-content/50">
            {effectiveCheck.principalType}: <span class="font-mono">{effectiveCheck.principalId}</span>
            {#if effectiveCheck.media?.title} · {effectiveCheck.media.title}{/if}
          </p>
        </div>
        <span class={[
          'rounded-full border px-3 py-1 text-xs font-bold',
          effectiveCheck.isAllowed
            ? 'border-success/30 bg-success/10 text-success'
            : 'border-error/30 bg-error/10 text-error'
        ]}>{effectiveCheck.isAllowed ? 'Allowed' : 'Denied'}</span>
      </div>

      <div class="mt-4 overflow-x-auto rounded-lg border border-base-300">
        <table class="w-full min-w-[46rem] text-left text-xs">
          <thead class="bg-base-200 text-base-content">
            <tr><th class="px-3 py-2">Axis</th><th class="px-3 py-2">Resource</th><th class="px-3 py-2">Status</th><th class="px-3 py-2">Reason / provenance</th></tr>
          </thead>
          <tbody class="divide-y divide-base-300">
            {#each effectiveCheck.decisions as decision}
              <tr>
                <td class="px-3 py-2 font-semibold text-base-content/80">{decision.axis}</td>
                <td class="px-3 py-2 font-mono text-base-content/60">{decision.resource || '—'}</td>
                <td class={['px-3 py-2 font-bold', decision.allowed ? 'text-success' : 'text-error']}>{decision.allowed ? 'Allowed' : 'Denied'}</td>
                <td class="px-3 py-2 text-base-content/60">
                  {decision.reason}
                  {#if decision.grantingPolicyIds.length > 0}
                    <span class="ml-1 text-success">by {decision.grantingPolicyIds.map(policyName).join(', ')}</span>
                  {/if}
                  {#if decision.denyingPolicyIds.length > 0}
                    <span class="ml-1 text-error">by {decision.denyingPolicyIds.map(policyName).join(', ')}</span>
                  {/if}
                </td>
              </tr>
            {:else}
              <tr><td class="px-3 py-4 text-base-content/50" colspan="4">No decision axes were returned.</td></tr>
            {/each}
          </tbody>
        </table>
      </div>
    </section>
  {/if}
{/if}
