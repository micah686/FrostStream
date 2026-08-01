<script lang="ts">
  import { onMount } from 'svelte';
  import { Modal, Select } from '$lib/components/ui';
  import {
    Boxes,
    Check,
    ChevronDown,
    ChevronUp,
    CircleAlert,
    Pencil,
    Plus,
    RefreshCw,
    Server,
    Trash2,
    Users
  } from '@lucide/svelte';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import { ApiRequestError } from '$lib/api/http';
  import {
    createRuntimeBundle,
    deleteRuntimeBundle,
    listBundles,
    listCatalog,
    replaceBundleEndpoints,
    type BundleView,
    type CatalogEntry
  } from '$lib/api/bundles';

  let { onManagePolicies }: { onManagePolicies?: () => void } = $props();

  interface CatalogGroup {
    bundle: string;
    entries: CatalogEntry[];
  }

  const cardClass = 'card border border-base-300 bg-base-100 p-5 sm:p-6';

  let bundles = $state<BundleView[]>([]);
  let catalog = $state<CatalogEntry[]>([]);
  let selectedBundleId = $state('');
  let loading = $state(true);
  let loadError = $state<Error | null>(null);
  let mutationError = $state<Error | null>(null);

  let pickerOpen = $state(false);
  let pickerMode = $state<'create' | 'edit'>('create');
  let pickerBundleId = $state('');
  let pickerBundleSuffix = $state('');
  let pickerCloneFrom = $state('');
  let pickerSearch = $state('');
  let pickerEndpoints = $state<string[]>([]);
  let pickerSaving = $state(false);
  let pickerError = $state<Error | null>(null);

  let deleteModalOpen = $state(false);
  let deleteTarget = $state<BundleView | null>(null);
  let deletingBundleId = $state<string | null>(null);

  const selectedBundle = $derived(bundles.find((bundle) => bundle.id === selectedBundleId) ?? bundles[0] ?? null);
  const systemBundles = $derived(bundles.filter((bundle) => bundle.systemOwned));
  const runtimeBundles = $derived(bundles.filter((bundle) => !bundle.systemOwned));
  const cloneSources = $derived(systemBundles.filter((bundle) => bundle.id !== 'all'));
  let systemGroupOpen = $state(true);
  let runtimeGroupOpen = $state(true);
  let selectedEndpointSection = $state('all');
  const openFgaUnavailable = $derived(isStatus(loadError, 503) || isStatus(mutationError, 503) || isStatus(pickerError, 503));

  onMount(() => {
    void loadAll();
  });

  function sortBundles(items: BundleView[]): BundleView[] {
    return [...items].sort((a, b) => a.id.localeCompare(b.id));
  }

  function sortCatalog(items: CatalogEntry[]): CatalogEntry[] {
    return [...items].sort((a, b) => a.bundle.localeCompare(b.bundle) || a.id.localeCompare(b.id));
  }

  async function loadAll() {
    loading = true;
    loadError = null;
    try {
      const [nextCatalog, nextBundles] = await Promise.all([listCatalog(), listBundles()]);
      catalog = sortCatalog(nextCatalog);
      applyBundles(nextBundles);
    } catch (err) {
      loadError = err instanceof Error ? err : new Error('Could not load bundle management data.');
    } finally {
      loading = false;
    }
  }

  async function reloadBundles(selectId = selectedBundleId) {
    try {
      const nextBundles = await listBundles();
      applyBundles(nextBundles, selectId);
    } catch (err) {
      mutationError = err instanceof Error ? err : new Error('Could not reload bundles.');
    }
  }

  function applyBundles(nextBundles: BundleView[], preferredId = selectedBundleId) {
    bundles = sortBundles(
      nextBundles.map((bundle) => ({
        ...bundle,
        endpoints: [...(bundle.endpoints ?? [])].sort(),
        memberPolicies: [...(bundle.memberPolicies ?? [])].sort((a, b) => a.name.localeCompare(b.name))
      }))
    );
    selectedBundleId = bundles.some((bundle) => bundle.id === preferredId) ? preferredId : (bundles[0]?.id ?? '');
  }

  function isStatus(error: Error | null, status: number): boolean {
    return error instanceof ApiRequestError && error.status === status;
  }

  function displayError(error: Error | null, fallback: string): string {
    if (!error) return fallback;
    if (error instanceof ApiRequestError) {
      if (error.status === 400) return error.message || 'Validation failed.';
      if (error.status === 403) return error.message || 'Forbidden or read-only operation.';
      if (error.status === 404) return error.message || 'Bundle not found. The list has been refreshed.';
      if (error.status === 409) return error.message || 'Remove the bundle from its policies before deleting it.';
      if (error.status === 503) return 'OpenFGA is unavailable. Bundle authorization changes cannot complete until it recovers.';
    }
    return error.message;
  }

  function ownershipLabel(bundle: BundleView): string {
    return bundle.systemOwned ? 'System' : 'Runtime';
  }

  function ownershipClass(bundle: BundleView): string {
    return bundle.systemOwned
      ? 'border-primary/25 bg-primary/10 text-primary'
      : 'border-success/25 bg-success/10 text-success';
  }

  function catalogGroups(entries: CatalogEntry[] = catalog): CatalogGroup[] {
    const map = new Map<string, CatalogEntry[]>();
    for (const entry of entries) {
      const bucket = map.get(entry.bundle) ?? [];
      bucket.push(entry);
      map.set(entry.bundle, bucket);
    }
    return [...map.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([bundle, groupEntries]) => ({ bundle, entries: groupEntries.sort((a, b) => a.id.localeCompare(b.id)) }));
  }

  function endpointGroups(bundle: BundleView): CatalogGroup[] {
    const endpointSet = new Set(bundle.endpoints);
    const known = catalog.filter((entry) => endpointSet.has(entry.id));
    const groups = catalogGroups(known);
    const knownIds = new Set(known.map((entry) => entry.id));
    const unknown = bundle.endpoints.filter((id) => !knownIds.has(id)).map((id) => ({ id, bundle: 'Uncataloged' }));
    return unknown.length > 0 ? [...groups, { bundle: 'Uncataloged', entries: unknown }] : groups;
  }

  function endpointSectionEntries(bundle: BundleView): CatalogEntry[] {
    const groups = endpointGroups(bundle);
    if (selectedEndpointSection === 'all') return groups.flatMap((group) => group.entries);
    return groups.find((group) => group.bundle === selectedEndpointSection)?.entries ?? groups.flatMap((group) => group.entries);
  }

  function filteredCatalog(): CatalogEntry[] {
    const query = pickerSearch.trim().toLowerCase();
    if (!query) return catalog;
    return catalog.filter((entry) => entry.id.toLowerCase().includes(query) || entry.bundle.toLowerCase().includes(query));
  }

  function toggleEndpoint(endpointId: string) {
    if (cloneBaselineEndpoints().includes(endpointId)) return;
    pickerEndpoints = pickerEndpoints.includes(endpointId)
      ? pickerEndpoints.filter((id) => id !== endpointId)
      : [...pickerEndpoints, endpointId].sort();
  }

  function cloneBaselineEndpoints(): string[] {
    if (pickerMode !== 'create' || !pickerCloneFrom) return [];
    return bundles.find((bundle) => bundle.id === pickerCloneFrom)?.endpoints ?? [];
  }

  function selectedEndpointCount(): number {
    return new Set([...cloneBaselineEndpoints(), ...pickerEndpoints]).size;
  }

  function openCreateModal() {
    pickerMode = 'create';
    pickerBundleId = '';
    pickerBundleSuffix = '';
    pickerCloneFrom = '';
    pickerSearch = '';
    pickerEndpoints = [];
    pickerError = null;
    pickerOpen = true;
  }

  function openEditModal(bundle: BundleView) {
    if (bundle.systemOwned) return;
    pickerMode = 'edit';
    pickerBundleId = bundle.id;
    pickerSearch = '';
    pickerEndpoints = [...bundle.endpoints];
    pickerError = null;
    pickerOpen = true;
  }

  async function submitPicker() {
    const bundleId = pickerMode === 'create' ? `user.${pickerBundleSuffix.trim()}` : pickerBundleId.trim();
    if (pickerMode === 'create' && !pickerBundleSuffix.trim()) {
      pickerError = new Error('Enter a bundle name after the user. prefix.');
      return;
    }
    if (!bundleId) {
      pickerError = new Error('Enter a bundle id.');
      return;
    }

    pickerSaving = true;
    pickerError = null;
    mutationError = null;
    try {
      if (pickerMode === 'create') {
        await createRuntimeBundle({
          id: bundleId,
          name: pickerBundleSuffix.trim(),
          cloneFrom: pickerCloneFrom || null,
          endpoints: pickerEndpoints
        });
      } else {
        await replaceBundleEndpoints(bundleId, pickerEndpoints);
      }
      await reloadBundles(bundleId);
      pickerOpen = false;
    } catch (err) {
      pickerError = err instanceof Error ? err : new Error('Could not save the bundle.');
      if (err instanceof ApiRequestError && err.status === 404) {
        await reloadBundles();
      }
    } finally {
      pickerSaving = false;
    }
  }

  async function deleteSelectedRuntimeBundle() {
    if (!deleteTarget) return;

    deletingBundleId = deleteTarget.id;
    mutationError = null;
    try {
      await deleteRuntimeBundle(deleteTarget.id);
      const previousId = deleteTarget.id;
      deleteTarget = null;
      await reloadBundles(bundles.find((bundle) => bundle.id !== previousId)?.id ?? '');
    } catch (err) {
      mutationError = err instanceof Error ? err : new Error('Could not delete the runtime bundle.');
      if (err instanceof ApiRequestError && err.status === 404) {
        await reloadBundles();
      }
      throw err;
    } finally {
      deletingBundleId = null;
    }
  }
</script>

<section class={cardClass} aria-labelledby="bundle-management-title">
  <div class="flex flex-wrap items-start justify-between gap-3">
    <div class="min-w-0">
      <div class="flex items-center gap-2">
        <Boxes class="h-5 w-5 text-primary" />
        <h2 id="bundle-management-title" class="text-base font-bold text-base-content">Bundle management</h2>
      </div>
      <p class="mt-2 text-sm text-base-content/60">
        Define which endpoints belong to each bundle. Assign users and groups from Policies.
      </p>
    </div>
    <div class="flex flex-wrap gap-2">
      <button class="btn btn-sm btn-neutral" disabled={loading} onclick={() => void loadAll()}>
        <RefreshCw class="mr-1.5 h-3.5 w-3.5" />
        Refresh
      </button>
      <button class="btn btn-sm btn-primary" onclick={openCreateModal}>
        <Plus class="mr-1.5 h-3.5 w-3.5" />
        Create runtime bundle
      </button>
    </div>
  </div>

  {#if openFgaUnavailable}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-warning/60 bg-warning/10 p-3 text-sm text-warning"
      role="alert"
    >
      <Server class="mt-0.5 h-4 w-4 shrink-0" />
      <span>OpenFGA is unavailable. Bundle authorization changes cannot complete until it recovers.</span>
    </div>
  {/if}

  {#if loadError}
    <div class="alert alert-error mt-4 text-sm" role="alert">
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{displayError(loadError, 'Could not load bundle management data.')}</span>
    </div>
  {/if}
  {#if mutationError}
    <div class="alert alert-error mt-4 text-sm" role="alert">
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{displayError(mutationError, 'Bundle operation failed.')}</span>
    </div>
  {/if}

  {#if loading}
    <div class="mt-10 flex justify-center"><span class="loading loading-spinner loading-md"></span></div>
  {:else if bundles.length === 0}
    <div class="mt-5 rounded-xl border border-base-300/80 bg-base-200/30 p-8 text-center">
      <Boxes class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No bundles</p>
      <p class="mt-1 text-sm text-base-content/50">Create a runtime bundle after the catalog is available.</p>
    </div>
  {:else}
    <div class="mt-5 grid gap-5 2xl:grid-cols-[21rem_minmax(0,1fr)]">
      <aside class="min-w-0 rounded-xl border border-base-300 bg-base-200/20" aria-label="Bundles">
        {#snippet bundleRow(bundle: BundleView)}
          <button
            type="button"
            class={[
              'block w-full px-3 py-3 text-left transition',
              selectedBundle?.id === bundle.id ? 'bg-primary/10' : 'hover:bg-base-300/45'
            ]}
            onclick={() => (selectedBundleId = bundle.id)}
          >
            <div class="flex min-w-0 items-center justify-between gap-2">
              <span class="truncate font-mono text-sm font-semibold text-base-content">{bundle.id}</span>
              <span class={['shrink-0 rounded-full border px-2 py-0.5 text-[10px] font-bold', ownershipClass(bundle)]}>
                {ownershipLabel(bundle)}
              </span>
            </div>
            <div class="mt-1 flex flex-wrap gap-2 text-xs text-base-content/50">
              <span>{bundle.endpointCount} endpoint{bundle.endpointCount === 1 ? '' : 's'}</span>
              <span>{bundle.policyCount} {bundle.policyCount === 1 ? 'policy' : 'policies'}</span>
            </div>
          </button>
        {/snippet}

        {#snippet bundleGroup(label: string, items: BundleView[], open: boolean, toggle: () => void)}
          <button
            type="button"
            class="flex w-full items-center justify-between gap-2 border-b border-base-300 px-3 py-2 text-left transition hover:bg-base-300/35"
            aria-expanded={open}
            onclick={toggle}
          >
            <span class="text-xs font-semibold uppercase text-base-content/50">{label}</span>
            <span class="flex shrink-0 items-center gap-1.5 text-xs text-base-content/40">
              {items.length}
              {#if open}
                <ChevronUp class="h-3 w-3" />
              {:else}
                <ChevronDown class="h-3 w-3" />
              {/if}
            </span>
          </button>
          {#if open}
            {#if items.length === 0}
              <div class="border-b border-base-300/80 px-3 py-3 text-xs text-base-content/40">No bundles.</div>
            {:else}
              <div class="divide-y divide-base-300/80 border-b border-base-300/80">
                {#each items as bundle (bundle.id)}
                  {@render bundleRow(bundle)}
                {/each}
              </div>
            {/if}
          {/if}
        {/snippet}

        <div class="max-h-[42rem] overflow-y-auto">
          {@render bundleGroup('System bundles', systemBundles, systemGroupOpen, () => (systemGroupOpen = !systemGroupOpen))}
          {@render bundleGroup('Runtime bundles', runtimeBundles, runtimeGroupOpen, () => (runtimeGroupOpen = !runtimeGroupOpen))}
        </div>
      </aside>

      {#if selectedBundle}
        <div class="min-w-0 space-y-5">
          <section class="rounded-xl border border-base-300 bg-base-200/20 p-4">
            <div class="flex flex-wrap items-start justify-between gap-3">
              <div class="min-w-0">
                <div class="flex min-w-0 flex-wrap items-center gap-2">
                  <h3 class="truncate font-mono text-base font-bold text-base-content">{selectedBundle.id}</h3>
                  <span class={['rounded-full border px-2 py-0.5 text-[10px] font-bold', ownershipClass(selectedBundle)]}>
                    {ownershipLabel(selectedBundle)}
                  </span>
                </div>
                <p class="mt-1 text-xs text-base-content/50">
                  {selectedBundle.endpointCount} endpoints · {selectedBundle.policyCount} {selectedBundle.policyCount === 1 ? 'policy' : 'policies'}
                </p>
              </div>
              {#if !selectedBundle.systemOwned}
                <div class="flex flex-wrap gap-2">
                  <button
                    type="button"
                    class="btn btn-sm btn-neutral text-xs"
                    onclick={() => openEditModal(selectedBundle)}
                  >
                    <Pencil class="mr-1.5 h-4 w-4" />
                    Edit endpoints
                  </button>
                  <button
                    type="button"
                    class="btn btn-sm btn-neutral text-xs"
                    title="Delete bundle"
                    aria-label={`Delete bundle ${selectedBundle.id}`}
                    disabled={deletingBundleId === selectedBundle.id}
                    onclick={() => {
                      deleteTarget = selectedBundle;
                      deleteModalOpen = true;
                    }}
                  >
                    {#if deletingBundleId === selectedBundle.id}
                      <span class="loading loading-spinner loading-xs mr-1.5"></span>
                    {:else}
                      <Trash2 class="mr-1.5 h-4 w-4" />
                    {/if}
                    Delete bundle
                  </button>
                </div>
              {/if}
            </div>
          </section>

          <section class="rounded-xl border border-base-300 bg-base-200/20 p-4" aria-labelledby="bundle-endpoints-title">
            <h3 id="bundle-endpoints-title" class="text-sm font-bold text-base-content">Endpoint membership</h3>
            {#if selectedBundle.endpoints.length === 0}
              <div class="mt-3 rounded-lg border border-base-300 bg-base-200/35 px-3 py-3 text-sm text-base-content/50">
                No endpoints assigned.
              </div>
            {:else}
              <div class="mt-3">
                <div class="flex gap-1 overflow-x-auto border-b border-base-300" role="tablist" aria-label="Endpoint sections">
                  <button
                    type="button"
                    role="tab"
                    aria-selected={selectedEndpointSection === 'all'}
                    class={[
                      'shrink-0 border-b-2 px-3 py-2 text-xs font-semibold transition',
                      selectedEndpointSection === 'all' ? 'border-primary text-primary' : 'border-transparent text-base-content/50 hover:text-base-content/90'
                    ]}
                    onclick={() => (selectedEndpointSection = 'all')}
                  >
                    All endpoints ({selectedBundle.endpoints.length})
                  </button>
                  {#each endpointGroups(selectedBundle) as group (group.bundle)}
                    <button
                      type="button"
                      role="tab"
                      aria-selected={selectedEndpointSection === group.bundle}
                      class={[
                        'shrink-0 border-b-2 px-3 py-2 text-xs font-semibold transition',
                        selectedEndpointSection === group.bundle ? 'border-primary text-primary' : 'border-transparent text-base-content/50 hover:text-base-content/90'
                      ]}
                      onclick={() => (selectedEndpointSection = group.bundle)}
                    >
                      {group.bundle} ({group.entries.length})
                    </button>
                  {/each}
                </div>
                <div class="divide-y divide-base-300/70 overflow-hidden rounded-b-lg border border-t-0 border-base-300 bg-base-100">
                  {#each endpointSectionEntries(selectedBundle) as endpoint (endpoint.id)}
                    <div class="px-3 py-2 font-mono text-xs text-base-content/80">{endpoint.id}</div>
                  {/each}
                </div>
              </div>
            {/if}
          </section>

          <section class="rounded-xl border border-base-300 bg-base-200/20 p-4" aria-labelledby="bundle-policies-title">
            <div class="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 id="bundle-policies-title" class="text-sm font-bold text-base-content">Policy membership</h3>
                <p class="mt-1 max-w-2xl text-xs text-base-content/50">
                  Policies are the only way to assign this bundle to users or groups. Remove the bundle from every policy before deleting it.
                </p>
              </div>
              {#if onManagePolicies}
                <button
                  type="button"
                  onclick={onManagePolicies}
                >
                  Manage policies
                </button>
              {/if}
            </div>

            {#if selectedBundle.memberPolicies.length === 0}
              <div class="mt-4 rounded-lg border border-base-300 bg-base-200/35 px-3 py-3 text-sm text-base-content/50">
                No policies reference this bundle.
              </div>
            {:else}
              <div class="mt-4 divide-y divide-base-300 overflow-hidden rounded-lg border border-base-300">
                {#each selectedBundle.memberPolicies as policy (policy.policyId)}
                  <div class="flex flex-col gap-3 px-3 py-3 sm:flex-row sm:items-center">
                    <div class="flex min-w-0 items-center gap-2">
                      <span class="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-base-300/70 text-primary">
                        <Users class="h-4 w-4" />
                      </span>
                      <div class="min-w-0">
                        <div class="flex flex-wrap items-center gap-2">
                          <span class="truncate text-sm font-semibold text-base-content">{policy.name}</span>
                          <span class={[
                            'rounded-full border px-2 py-0.5 text-[10px] font-bold uppercase',
                            policy.enabled
                              ? 'border-success/25 bg-success/10 text-success'
                              : 'border-base-content/20 bg-base-300/60 text-base-content/60'
                          ]}>
                            {policy.enabled ? 'Enabled' : 'Disabled'}
                          </span>
                        </div>
                        <div class="mt-1 font-mono text-xs text-base-content/50">{policy.policyId}</div>
                      </div>
                    </div>
                    <span class="shrink-0 rounded-full border border-base-content/20 bg-base-200/40 px-2.5 py-1 text-[10px] font-bold uppercase text-base-content/60 sm:ml-auto">
                      {policy.syncStatus}
                    </span>
                  </div>
                {/each}
              </div>
            {/if}
          </section>
        </div>
      {/if}
    </div>
  {/if}
</section>

<Modal bind:open={pickerOpen} title={pickerMode === 'create' ? 'Create runtime bundle' : 'Edit endpoint membership'} size="xl">
  <div class="space-y-4">
    <div>
      <label class="label mb-2 text-sm" for="bundle-picker-id">Bundle id</label>
      {#if pickerMode === 'create'}
        <div class="flex overflow-hidden rounded-lg border border-base-300 bg-base-200/60 focus-within:border-primary focus-within:ring-1 focus-within:ring-primary">
          <span class="flex items-center border-r border-base-300 bg-base-200/80 px-3 font-mono text-sm text-base-content/50 select-none" aria-hidden="true">
            user.
          </span>
          <input
            id="bundle-picker-id"
            type="text"
            bind:value={pickerBundleSuffix}
            placeholder="example"
            autocomplete="off"
            aria-label="Bundle id (after the user. prefix)"
            class="w-full min-w-0 border-0 bg-transparent px-3 py-2.5 text-sm text-base-content/90 placeholder:text-base-content/40 focus:ring-0 focus:outline-none"
          />
        </div>
        <p class="mt-1.5 text-xs text-base-content/50">Runtime bundle ids always start with the fixed user. prefix.</p>
      {:else}
        <input class="input w-full" id="bundle-picker-id" bind:value={pickerBundleId} readonly />
      {/if}
    </div>

    {#if pickerMode === 'create'}
      <div>
        <label class="label mb-2 text-sm" for="bundle-clone-source">Start from a system bundle</label>
        <Select
          id="bundle-clone-source"
          items={[
            { value: '', name: 'Start empty' },
            ...cloneSources.map((bundle) => ({ value: bundle.id, name: `${bundle.id} · ${bundle.endpointCount} endpoints` }))
          ]}
          bind:value={pickerCloneFrom}
          class="text-sm"
        />
        <p class="mt-1.5 text-xs text-base-content/50">
          The baseline is copied when the bundle is created. Add extra endpoints here; edit the new bundle afterward to remove copied endpoints.
        </p>
      </div>
    {/if}

    <div>
      <label class="label mb-2 text-sm" for="endpoint-search">Endpoints</label>
      <input class="input w-full" id="endpoint-search" bind:value={pickerSearch} placeholder="Search endpoint or seeded bundle" />
    </div>

    {#if pickerError}
      <div class="alert alert-error text-sm" role="alert">
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{displayError(pickerError, 'Could not save the bundle.')}</span>
      </div>
    {/if}

    <div class="max-h-[26rem] space-y-3 overflow-y-auto rounded-xl border border-base-300 bg-base-200/20 p-3">
      {#if catalog.length === 0}
        <div class="rounded-lg border border-base-300 bg-base-200/35 px-3 py-3 text-sm text-base-content/50">
          No catalog endpoints are available.
        </div>
      {:else if filteredCatalog().length === 0}
        <div class="rounded-lg border border-base-300 bg-base-200/35 px-3 py-3 text-sm text-base-content/50">
          No endpoints match the search.
        </div>
      {:else}
        {#each catalogGroups(filteredCatalog()) as group (group.bundle)}
          <div class="rounded-lg border border-base-300 bg-base-100">
            <div class="flex items-center justify-between gap-3 border-b border-base-300 px-3 py-2">
              <span class="text-xs font-semibold uppercase text-base-content/50">{group.bundle}</span>
              <span class="text-xs text-base-content/40">{group.entries.length}</span>
            </div>
            <div class="divide-y divide-base-300/70">
              {#each group.entries as endpoint (endpoint.id)}
                <label class="flex cursor-pointer items-center gap-3 px-3 py-2 transition hover:bg-base-300/35">
                  <input
                    type="checkbox"
                    class="checkbox checkbox-sm checkbox-primary"
                    checked={cloneBaselineEndpoints().includes(endpoint.id) || pickerEndpoints.includes(endpoint.id)}
                    disabled={cloneBaselineEndpoints().includes(endpoint.id)}
                    onchange={() => toggleEndpoint(endpoint.id)}
                  />
                  <span class="min-w-0 truncate font-mono text-xs text-base-content/80">{endpoint.id}</span>
                  {#if cloneBaselineEndpoints().includes(endpoint.id)}
                    <span class="ml-auto shrink-0 text-[10px] font-semibold uppercase text-primary">baseline</span>
                  {/if}
                </label>
              {/each}
            </div>
          </div>
        {/each}
      {/if}
    </div>

    <div class="flex flex-wrap items-center gap-2 text-xs text-base-content/50">
      <Check class="h-3.5 w-3.5 text-success" />
      {selectedEndpointCount()} endpoint{selectedEndpointCount() === 1 ? '' : 's'} selected
    </div>
  </div>

  {#snippet footer()}
    <div class="flex w-full flex-wrap justify-end gap-2">
      <button class="btn btn-sm btn-ghost text-xs" disabled={pickerSaving} onclick={() => (pickerOpen = false)}>
        Cancel
      </button>
      <button class="btn btn-sm btn-primary" disabled={pickerSaving} onclick={submitPicker}>
        {#if pickerSaving}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {/if}
        {pickerMode === 'create' ? 'Create bundle' : 'Save endpoints'}
      </button>
    </div>
  {/snippet}
</Modal>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete runtime bundle"
  message={deleteTarget ? `Delete runtime bundle "${deleteTarget.id}"? Its endpoint membership and direct exceptions will be removed.` : ''}
  confirmLabel="Delete bundle"
  onConfirm={deleteSelectedRuntimeBundle}
/>
