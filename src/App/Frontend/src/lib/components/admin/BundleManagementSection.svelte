<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Input, Label, Modal, Spinner } from 'flowbite-svelte';
  import {
    CheckOutline,
    ChevronDownOutline,
    ChevronUpOutline,
    CubesStackedOutline,
    EditOutline,
    ExclamationCircleOutline,
    PlusOutline,
    RefreshOutline,
    ServerOutline,
    TrashBinOutline,
    UsersGroupOutline
  } from 'flowbite-svelte-icons';
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

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const outlineButtonClass =
    'border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!';
  const saveButtonClass = 'border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60';
  const systemTooltip = 'System-owned bundles are seeded by the server and cannot be modified.';

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
      ? 'border-blue-500/25 bg-blue-500/10 text-blue-200'
      : 'border-emerald-500/25 bg-emerald-500/10 text-emerald-200';
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
        <CubesStackedOutline class="h-5 w-5 text-blue-400" />
        <h2 id="bundle-management-title" class="text-base font-bold text-slate-100">Bundle management</h2>
      </div>
      <p class="mt-2 text-sm text-slate-400">
        Define which endpoints belong to each bundle. Assign users and groups from Policies.
      </p>
    </div>
    <div class="flex flex-wrap gap-2">
      <Button color="dark" class={outlineButtonClass} disabled={loading} onclick={() => void loadAll()}>
        <RefreshOutline class="mr-1.5 h-3.5 w-3.5" />
        Refresh
      </Button>
      <Button color="blue" class={saveButtonClass} onclick={openCreateModal}>
        <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
        Create runtime bundle
      </Button>
    </div>
  </div>

  {#if openFgaUnavailable}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-amber-700/60 bg-amber-950/30 p-3 text-sm text-amber-200"
      role="alert"
    >
      <ServerOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>OpenFGA is unavailable. Bundle authorization changes cannot complete until it recovers.</span>
    </div>
  {/if}

  {#if loadError}
    <div class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{displayError(loadError, 'Could not load bundle management data.')}</span>
    </div>
  {/if}
  {#if mutationError}
    <div class="mt-4 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{displayError(mutationError, 'Bundle operation failed.')}</span>
    </div>
  {/if}

  {#if loading}
    <div class="mt-10 flex justify-center"><Spinner size="8" /></div>
  {:else if bundles.length === 0}
    <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <CubesStackedOutline class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No bundles</p>
      <p class="mt-1 text-sm text-slate-500">Create a runtime bundle after the catalog is available.</p>
    </div>
  {:else}
    <div class="mt-5 grid gap-5 2xl:grid-cols-[21rem_minmax(0,1fr)]">
      <aside class="min-w-0 rounded-xl border border-slate-800 bg-slate-950/20" aria-label="Bundles">
        {#snippet bundleRow(bundle: BundleView)}
          <button
            type="button"
            class={[
              'block w-full px-3 py-3 text-left transition',
              selectedBundle?.id === bundle.id ? 'bg-blue-500/10' : 'hover:bg-slate-800/45'
            ]}
            onclick={() => (selectedBundleId = bundle.id)}
          >
            <div class="flex min-w-0 items-center justify-between gap-2">
              <span class="truncate font-mono text-sm font-semibold text-slate-100">{bundle.id}</span>
              <span class={['shrink-0 rounded-full border px-2 py-0.5 text-[10px] font-bold', ownershipClass(bundle)]}>
                {ownershipLabel(bundle)}
              </span>
            </div>
            <div class="mt-1 flex flex-wrap gap-2 text-xs text-slate-500">
              <span>{bundle.endpointCount} endpoint{bundle.endpointCount === 1 ? '' : 's'}</span>
              <span>{bundle.policyCount} {bundle.policyCount === 1 ? 'policy' : 'policies'}</span>
            </div>
          </button>
        {/snippet}

        {#snippet bundleGroup(label: string, items: BundleView[], open: boolean, toggle: () => void)}
          <button
            type="button"
            class="flex w-full items-center justify-between gap-2 border-b border-slate-800 px-3 py-2 text-left transition hover:bg-slate-800/35"
            aria-expanded={open}
            onclick={toggle}
          >
            <span class="text-xs font-semibold uppercase text-slate-500">{label}</span>
            <span class="flex shrink-0 items-center gap-1.5 text-xs text-slate-600">
              {items.length}
              {#if open}
                <ChevronUpOutline class="h-3 w-3" />
              {:else}
                <ChevronDownOutline class="h-3 w-3" />
              {/if}
            </span>
          </button>
          {#if open}
            {#if items.length === 0}
              <div class="border-b border-slate-800/80 px-3 py-3 text-xs text-slate-600">No bundles.</div>
            {:else}
              <div class="divide-y divide-slate-800/80 border-b border-slate-800/80">
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
          <section class="rounded-xl border border-slate-800 bg-slate-950/20 p-4">
            <div class="flex flex-wrap items-start justify-between gap-3">
              <div class="min-w-0">
                <div class="flex min-w-0 flex-wrap items-center gap-2">
                  <h3 class="truncate font-mono text-base font-bold text-slate-100">{selectedBundle.id}</h3>
                  <span class={['rounded-full border px-2 py-0.5 text-[10px] font-bold', ownershipClass(selectedBundle)]}>
                    {ownershipLabel(selectedBundle)}
                  </span>
                </div>
                <p class="mt-1 text-xs text-slate-500">
                  {selectedBundle.endpointCount} endpoints · {selectedBundle.policyCount} {selectedBundle.policyCount === 1 ? 'policy' : 'policies'}
                </p>
              </div>
              <div class="flex flex-wrap gap-2">
                <button
                  type="button"
                  class="inline-flex h-9 min-w-9 items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200 disabled:cursor-not-allowed disabled:opacity-45"
                  title={selectedBundle.systemOwned ? systemTooltip : 'Edit endpoints'}
                  disabled={selectedBundle.systemOwned}
                  onclick={() => openEditModal(selectedBundle)}
                >
                  <EditOutline class="h-4 w-4" />
                  Edit endpoints
                </button>
                <button
                  type="button"
                  class="inline-flex h-9 min-w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:cursor-not-allowed disabled:opacity-45"
                  title={selectedBundle.systemOwned ? systemTooltip : 'Delete runtime bundle'}
                  aria-label={`Delete bundle ${selectedBundle.id}`}
                  disabled={selectedBundle.systemOwned || deletingBundleId === selectedBundle.id}
                  onclick={() => {
                    deleteTarget = selectedBundle;
                    deleteModalOpen = true;
                  }}
                >
                  {#if deletingBundleId === selectedBundle.id}
                    <Spinner size="4" />
                  {:else}
                    <TrashBinOutline class="h-4 w-4" />
                  {/if}
                </button>
              </div>
            </div>
          </section>

          <section class="rounded-xl border border-slate-800 bg-slate-950/20 p-4" aria-labelledby="bundle-endpoints-title">
            <h3 id="bundle-endpoints-title" class="text-sm font-bold text-slate-100">Endpoint membership</h3>
            {#if selectedBundle.endpoints.length === 0}
              <div class="mt-3 rounded-lg border border-slate-800 bg-slate-950/35 px-3 py-3 text-sm text-slate-500">
                No endpoints assigned.
              </div>
            {:else}
              <div class="mt-3 space-y-3">
                {#each endpointGroups(selectedBundle) as group (group.bundle)}
                  <div class="rounded-lg border border-slate-800 bg-[#151a26]">
                    <div class="border-b border-slate-800 px-3 py-2 text-xs font-semibold uppercase text-slate-500">{group.bundle}</div>
                    <div class="divide-y divide-slate-800/70">
                      {#each group.entries as endpoint (endpoint.id)}
                        <div class="px-3 py-2 font-mono text-xs text-slate-300">{endpoint.id}</div>
                      {/each}
                    </div>
                  </div>
                {/each}
              </div>
            {/if}
          </section>

          <section class="rounded-xl border border-slate-800 bg-slate-950/20 p-4" aria-labelledby="bundle-policies-title">
            <div class="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 id="bundle-policies-title" class="text-sm font-bold text-slate-100">Policy membership</h3>
                <p class="mt-1 max-w-2xl text-xs text-slate-500">
                  Policies are the only way to assign this bundle to users or groups. Remove the bundle from every policy before deleting it.
                </p>
              </div>
              {#if onManagePolicies}
                <button
                  type="button"
                  class={outlineButtonClass}
                  onclick={onManagePolicies}
                >
                  Manage policies
                </button>
              {/if}
            </div>

            {#if selectedBundle.memberPolicies.length === 0}
              <div class="mt-4 rounded-lg border border-slate-800 bg-slate-950/35 px-3 py-3 text-sm text-slate-500">
                No policies reference this bundle.
              </div>
            {:else}
              <div class="mt-4 divide-y divide-slate-800 overflow-hidden rounded-lg border border-slate-800">
                {#each selectedBundle.memberPolicies as policy (policy.policyId)}
                  <div class="flex flex-col gap-3 px-3 py-3 sm:flex-row sm:items-center">
                    <div class="flex min-w-0 items-center gap-2">
                      <span class="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-300">
                        <UsersGroupOutline class="h-4 w-4" />
                      </span>
                      <div class="min-w-0">
                        <div class="flex flex-wrap items-center gap-2">
                          <span class="truncate text-sm font-semibold text-slate-100">{policy.name}</span>
                          <span class={[
                            'rounded-full border px-2 py-0.5 text-[10px] font-bold uppercase',
                            policy.enabled
                              ? 'border-emerald-500/25 bg-emerald-500/10 text-emerald-200'
                              : 'border-slate-700 bg-slate-800/60 text-slate-400'
                          ]}>
                            {policy.enabled ? 'Enabled' : 'Disabled'}
                          </span>
                        </div>
                        <div class="mt-1 font-mono text-xs text-slate-500">{policy.policyId}</div>
                      </div>
                    </div>
                    <span class="shrink-0 rounded-full border border-slate-700 bg-slate-950/40 px-2.5 py-1 text-[10px] font-bold uppercase text-slate-400 sm:ml-auto">
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

<Modal bind:open={pickerOpen} title={pickerMode === 'create' ? 'Create runtime bundle' : 'Edit endpoint membership'} size="xl" class="z-50">
  <div class="space-y-4">
    <div>
      <Label for="bundle-picker-id" class="mb-2 text-sm font-medium text-slate-300">Bundle id</Label>
      {#if pickerMode === 'create'}
        <div class="flex overflow-hidden rounded-lg border border-slate-800 bg-slate-950/60 focus-within:border-blue-500 focus-within:ring-1 focus-within:ring-blue-500">
          <span class="flex items-center border-r border-slate-800 bg-slate-900/80 px-3 font-mono text-sm text-slate-500 select-none" aria-hidden="true">
            user.
          </span>
          <input
            id="bundle-picker-id"
            type="text"
            bind:value={pickerBundleSuffix}
            placeholder="example"
            autocomplete="off"
            aria-label="Bundle id (after the user. prefix)"
            class="w-full min-w-0 border-0 bg-transparent px-3 py-2.5 text-sm text-slate-200 placeholder:text-slate-600 focus:ring-0 focus:outline-none"
          />
        </div>
        <p class="mt-1.5 text-xs text-slate-500">Runtime bundle ids always start with the fixed user. prefix.</p>
      {:else}
        <Input id="bundle-picker-id" bind:value={pickerBundleId} readonly class={inputClass} />
      {/if}
    </div>

    {#if pickerMode === 'create'}
      <div>
        <Label for="bundle-clone-source" class="mb-2 text-sm font-medium text-slate-300">Start from a system bundle</Label>
        <select
          id="bundle-clone-source"
          bind:value={pickerCloneFrom}
          class="w-full rounded-lg border border-slate-800 bg-slate-950/60 px-3 py-2.5 text-sm text-slate-200 outline-none focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
        >
          <option value="">Start empty</option>
          {#each cloneSources as bundle (bundle.id)}
            <option value={bundle.id}>{bundle.id} · {bundle.endpointCount} endpoints</option>
          {/each}
        </select>
        <p class="mt-1.5 text-xs text-slate-500">
          The baseline is copied when the bundle is created. Add extra endpoints here; edit the new bundle afterward to remove copied endpoints.
        </p>
      </div>
    {/if}

    <div>
      <Label for="endpoint-search" class="mb-2 text-sm font-medium text-slate-300">Endpoints</Label>
      <Input id="endpoint-search" bind:value={pickerSearch} placeholder="Search endpoint or seeded bundle" class={inputClass} />
    </div>

    {#if pickerError}
      <div class="flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{displayError(pickerError, 'Could not save the bundle.')}</span>
      </div>
    {/if}

    <div class="max-h-[26rem] space-y-3 overflow-y-auto rounded-xl border border-slate-800 bg-slate-950/20 p-3">
      {#if catalog.length === 0}
        <div class="rounded-lg border border-slate-800 bg-slate-950/35 px-3 py-3 text-sm text-slate-500">
          No catalog endpoints are available.
        </div>
      {:else if filteredCatalog().length === 0}
        <div class="rounded-lg border border-slate-800 bg-slate-950/35 px-3 py-3 text-sm text-slate-500">
          No endpoints match the search.
        </div>
      {:else}
        {#each catalogGroups(filteredCatalog()) as group (group.bundle)}
          <div class="rounded-lg border border-slate-800 bg-[#151a26]">
            <div class="flex items-center justify-between gap-3 border-b border-slate-800 px-3 py-2">
              <span class="text-xs font-semibold uppercase text-slate-500">{group.bundle}</span>
              <span class="text-xs text-slate-600">{group.entries.length}</span>
            </div>
            <div class="divide-y divide-slate-800/70">
              {#each group.entries as endpoint (endpoint.id)}
                <label class="flex cursor-pointer items-center gap-3 px-3 py-2 transition hover:bg-slate-800/35">
                  <input
                    type="checkbox"
                    class="h-4 w-4 rounded border-slate-700 bg-slate-950 text-blue-600 focus:ring-blue-600"
                    checked={cloneBaselineEndpoints().includes(endpoint.id) || pickerEndpoints.includes(endpoint.id)}
                    disabled={cloneBaselineEndpoints().includes(endpoint.id)}
                    onchange={() => toggleEndpoint(endpoint.id)}
                  />
                  <span class="min-w-0 truncate font-mono text-xs text-slate-300">{endpoint.id}</span>
                  {#if cloneBaselineEndpoints().includes(endpoint.id)}
                    <span class="ml-auto shrink-0 text-[10px] font-semibold uppercase text-blue-400">baseline</span>
                  {/if}
                </label>
              {/each}
            </div>
          </div>
        {/each}
      {/if}
    </div>

    <div class="flex flex-wrap items-center gap-2 text-xs text-slate-500">
      <CheckOutline class="h-3.5 w-3.5 text-emerald-400" />
      {selectedEndpointCount()} endpoint{selectedEndpointCount() === 1 ? '' : 's'} selected
    </div>
  </div>

  {#snippet footer()}
    <div class="flex w-full flex-wrap justify-end gap-2">
      <Button
        color="dark"
        class="border-slate-700! bg-transparent! px-4! py-2! text-xs! font-semibold! text-slate-300! hover:bg-slate-800!"
        disabled={pickerSaving}
        onclick={() => (pickerOpen = false)}
      >
        Cancel
      </Button>
      <Button color="blue" class={saveButtonClass} disabled={pickerSaving} onclick={submitPicker}>
        {#if pickerSaving}
          <Spinner size="4" class="mr-1.5" />
        {/if}
        {pickerMode === 'create' ? 'Create bundle' : 'Save endpoints'}
      </Button>
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
