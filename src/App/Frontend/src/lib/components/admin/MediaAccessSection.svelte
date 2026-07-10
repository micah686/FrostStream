<script lang="ts">
  import { onMount } from 'svelte';
  import { Button, Input, Label, Spinner } from 'flowbite-svelte';
  import {
    ApiKeyOutline,
    CloseOutline,
    ExclamationCircleOutline,
    PlusOutline,
    RefreshOutline,
    ServerOutline,
    TrashBinOutline,
    UsersGroupOutline
  } from 'flowbite-svelte-icons';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import UnderDevelopmentBanner from '$lib/components/admin/UnderDevelopmentBanner.svelte';
  import { ApiRequestError } from '$lib/api/http';
  import {
    addAgePolicyGroup,
    addMediaGroup,
    addProviderGroup,
    clearMediaGroups,
    clearProvider,
    getMediaGroups,
    listAgePolicies,
    listProviderPolicies,
    removeAgePolicyGroup,
    removeMediaGroup,
    removeProviderGroup,
    type AgePolicy,
    type ProviderPolicy
  } from '$lib/api/mediaAccess';

  type AccessTab = 'media' | 'providers' | 'age';

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const outlineButtonClass =
    'border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!';
  const saveButtonClass = 'border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60';

  const tabs: { id: AccessTab; label: string }[] = [
    { id: 'media', label: 'Media items' },
    { id: 'providers', label: 'Providers' },
    { id: 'age', label: 'Age policies' }
  ];

  let activeTab = $state<AccessTab>('media');

  let mediaGuid = $state('');
  let loadedMediaGuid = $state('');
  let mediaGroups = $state<string[]>([]);
  let mediaLoading = $state(false);
  let mediaError = $state<Error | null>(null);
  let mediaGroupInput = $state('');
  let mediaMutation = $state<string | null>(null);
  let clearMediaModalOpen = $state(false);

  let providerPolicies = $state<ProviderPolicy[]>([]);
  let providersLoading = $state(true);
  let providersError = $state<Error | null>(null);
  let providerIdInput = $state('');
  let providerGroupInput = $state('');
  let providerMutation = $state<string | null>(null);
  let clearProviderTarget = $state<ProviderPolicy | null>(null);
  let clearProviderModalOpen = $state(false);

  let agePolicies = $state<AgePolicy[]>([]);
  let ageLoading = $state(true);
  let ageError = $state<Error | null>(null);
  let ageThresholdInput = $state<number | string>('');
  let ageGroupInput = $state('');
  let ageMutation = $state<string | null>(null);

  const anyBridgeUnavailable = $derived(isUnavailable(mediaError) || isUnavailable(providersError) || isUnavailable(ageError));

  onMount(() => {
    void loadProviders();
    void loadAgePolicies();
  });

  function errorMessage(error: Error | null, fallback: string): string {
    if (!error) return fallback;
    if (error instanceof ApiRequestError && error.status === 503) {
      return 'DataBridge is unreachable. Media access changes are routed through DataBridge/NATS.';
    }
    return error.message;
  }

  function isUnavailable(error: Error | null): boolean {
    return error instanceof ApiRequestError && error.status === 503;
  }

  function sortedGroups(groups: string[]): string[] {
    return [...groups].sort((a, b) => a.localeCompare(b));
  }

  function sortProviders(policies: ProviderPolicy[]): ProviderPolicy[] {
    return [...policies]
      .map((policy) => ({ ...policy, groups: sortedGroups(policy.groups ?? []) }))
      .sort((a, b) => a.provider.localeCompare(b.provider));
  }

  function sortAgePolicies(policies: AgePolicy[]): AgePolicy[] {
    return [...policies]
      .map((policy) => ({ ...policy, groups: sortedGroups(policy.groups ?? []) }))
      .sort((a, b) => a.threshold - b.threshold);
  }

  async function loadMediaGroups() {
    const guid = mediaGuid.trim();
    if (!guid) {
      mediaError = new Error('Enter a media GUID.');
      return;
    }

    mediaLoading = true;
    mediaError = null;
    try {
      mediaGroups = sortedGroups(await getMediaGroups(guid));
      loadedMediaGuid = guid;
    } catch (err) {
      mediaError = err instanceof Error ? err : new Error('Could not load media access groups.');
      mediaGroups = [];
      loadedMediaGuid = '';
    } finally {
      mediaLoading = false;
    }
  }

  async function addGroupToMedia() {
    const guid = loadedMediaGuid || mediaGuid.trim();
    const groupName = mediaGroupInput.trim();
    if (!guid || !groupName) return;

    mediaMutation = `add:${groupName}`;
    mediaError = null;
    try {
      await addMediaGroup(guid, groupName);
      mediaGuid = guid;
      mediaGroupInput = '';
      await loadMediaGroups();
    } catch (err) {
      mediaError = err instanceof Error ? err : new Error('Could not add the group.');
    } finally {
      mediaMutation = null;
    }
  }

  async function removeGroupFromMedia(groupName: string) {
    if (!loadedMediaGuid) return;

    mediaMutation = `remove:${groupName}`;
    mediaError = null;
    try {
      await removeMediaGroup(loadedMediaGuid, groupName);
      await loadMediaGroups();
    } catch (err) {
      mediaError = err instanceof Error ? err : new Error('Could not remove the group.');
    } finally {
      mediaMutation = null;
    }
  }

  async function clearLoadedMediaGroups() {
    if (!loadedMediaGuid) return;

    mediaMutation = 'clear';
    mediaError = null;
    try {
      await clearMediaGroups(loadedMediaGuid);
      await loadMediaGroups();
    } catch (err) {
      mediaError = err instanceof Error ? err : new Error('Could not clear media groups.');
      throw err;
    } finally {
      mediaMutation = null;
    }
  }

  async function loadProviders() {
    providersLoading = true;
    providersError = null;
    try {
      providerPolicies = sortProviders(await listProviderPolicies());
    } catch (err) {
      providersError = err instanceof Error ? err : new Error('Could not load provider policies.');
    } finally {
      providersLoading = false;
    }
  }

  async function addGroupToProvider() {
    const provider = providerIdInput.trim();
    const groupName = providerGroupInput.trim();
    if (!provider || !groupName) return;

    providerMutation = `add:${provider}:${groupName}`;
    providersError = null;
    try {
      await addProviderGroup(provider, groupName);
      providerGroupInput = '';
      await loadProviders();
    } catch (err) {
      providersError = err instanceof Error ? err : new Error('Could not add the provider group.');
    } finally {
      providerMutation = null;
    }
  }

  async function removeGroupFromProvider(provider: string, groupName: string) {
    providerMutation = `remove:${provider}:${groupName}`;
    providersError = null;
    try {
      await removeProviderGroup(provider, groupName);
      await loadProviders();
    } catch (err) {
      providersError = err instanceof Error ? err : new Error('Could not remove the provider group.');
    } finally {
      providerMutation = null;
    }
  }

  async function clearProviderGroups() {
    if (!clearProviderTarget) return;

    providerMutation = `clear:${clearProviderTarget.provider}`;
    providersError = null;
    try {
      await clearProvider(clearProviderTarget.provider);
      await loadProviders();
      clearProviderTarget = null;
    } catch (err) {
      providersError = err instanceof Error ? err : new Error('Could not clear the provider policy.');
      throw err;
    } finally {
      providerMutation = null;
    }
  }

  async function loadAgePolicies() {
    ageLoading = true;
    ageError = null;
    try {
      agePolicies = sortAgePolicies(await listAgePolicies());
    } catch (err) {
      ageError = err instanceof Error ? err : new Error('Could not load age policies.');
    } finally {
      ageLoading = false;
    }
  }

  async function addGroupToAgePolicy() {
    const threshold = Number(ageThresholdInput);
    const groupName = ageGroupInput.trim();
    if (!Number.isInteger(threshold) || threshold < 0 || !groupName) {
      ageError = new Error('Enter a non-negative age threshold and group name.');
      return;
    }

    ageMutation = `add:${threshold}:${groupName}`;
    ageError = null;
    try {
      await addAgePolicyGroup(threshold, groupName);
      ageGroupInput = '';
      await loadAgePolicies();
    } catch (err) {
      ageError = err instanceof Error ? err : new Error('Could not add the age policy group.');
    } finally {
      ageMutation = null;
    }
  }

  async function removeGroupFromAgePolicy(threshold: number, groupName: string) {
    ageMutation = `remove:${threshold}:${groupName}`;
    ageError = null;
    try {
      await removeAgePolicyGroup(threshold, groupName);
      await loadAgePolicies();
    } catch (err) {
      ageError = err instanceof Error ? err : new Error('Could not remove the age policy group.');
    } finally {
      ageMutation = null;
    }
  }
</script>

<UnderDevelopmentBanner />

<section class={cardClass} aria-labelledby="media-access-title">
  <div class="flex flex-wrap items-start justify-between gap-3">
    <div class="min-w-0">
      <div class="flex items-center gap-2">
        <ApiKeyOutline class="h-5 w-5 text-blue-400" />
        <h2 id="media-access-title" class="text-base font-bold text-slate-100">Media access</h2>
      </div>
      <p class="mt-2 text-sm text-slate-400">Configure playback-time group restrictions for media, providers, and age thresholds.</p>
    </div>
    <Button
      color="dark"
      class={outlineButtonClass}
      disabled={(activeTab === 'providers' && providersLoading) || (activeTab === 'age' && ageLoading) || (activeTab === 'media' && mediaLoading)}
      onclick={() => {
        if (activeTab === 'providers') void loadProviders();
        if (activeTab === 'age') void loadAgePolicies();
        if (activeTab === 'media' && (loadedMediaGuid || mediaGuid.trim())) void loadMediaGroups();
      }}
    >
      <RefreshOutline class="mr-1.5 h-3.5 w-3.5" />
      Refresh
    </Button>
  </div>

  {#if anyBridgeUnavailable}
    <div
      class="mt-4 flex items-start gap-2 rounded-xl border border-amber-700/60 bg-amber-950/30 p-3 text-sm text-amber-200"
      role="alert"
    >
      <ServerOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>DataBridge is unreachable. Media access operations route through DataBridge/NATS and cannot complete until it recovers.</span>
    </div>
  {/if}

  <div class="mt-5 flex flex-wrap gap-2 border-b border-slate-800 pb-3" role="tablist" aria-label="Media access sections">
    {#each tabs as tab}
      <button
        type="button"
        class={[
          'h-9 rounded-lg px-3 text-xs font-semibold transition',
          activeTab === tab.id ? 'bg-blue-500/18 text-blue-300' : 'text-slate-400 hover:bg-slate-800 hover:text-slate-100'
        ]}
        role="tab"
        aria-selected={activeTab === tab.id}
        onclick={() => (activeTab = tab.id)}
      >
        {tab.label}
      </button>
    {/each}
  </div>

  {#if activeTab === 'media'}
    <div class="mt-5 space-y-5">
      <form
        class="grid gap-3 lg:grid-cols-[minmax(0,1fr)_auto]"
        onsubmit={(event) => {
          event.preventDefault();
          void loadMediaGroups();
        }}
      >
        <div>
          <Label for="media-access-guid" class="mb-2 text-sm font-medium text-slate-300">Media GUID</Label>
          <Input
            id="media-access-guid"
            bind:value={mediaGuid}
            placeholder="00000000-0000-0000-0000-000000000000"
            class={inputClass}
          />
        </div>
        <div class="flex items-end">
          <Button color="blue" type="submit" class={saveButtonClass} disabled={mediaLoading || !mediaGuid.trim()}>
            {#if mediaLoading}
              <Spinner size="4" class="mr-1.5" />
            {/if}
            Load groups
          </Button>
        </div>
      </form>

      {#if mediaError}
        <div class="flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
          <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
          <span>{errorMessage(mediaError, 'Could not load media access groups.')}</span>
        </div>
      {/if}

      {#if loadedMediaGuid}
        <div class="rounded-xl border border-slate-800/80 bg-slate-950/25 p-4">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <div class="min-w-0">
              <p class="text-xs font-semibold uppercase text-slate-500">Loaded media</p>
              <p class="truncate font-mono text-sm text-slate-200">{loadedMediaGuid}</p>
            </div>
            <Button
              color="dark"
              class={outlineButtonClass}
              disabled={mediaGroups.length === 0 || mediaMutation === 'clear'}
              onclick={() => (clearMediaModalOpen = true)}
            >
              {#if mediaMutation === 'clear'}
                <Spinner size="4" class="mr-1.5" />
              {:else}
                <TrashBinOutline class="mr-1.5 h-3.5 w-3.5" />
              {/if}
              Clear all
            </Button>
          </div>

          {#if mediaLoading}
            <div class="mt-8 flex justify-center"><Spinner size="8" /></div>
          {:else if mediaGroups.length === 0}
            <div class="mt-4 rounded-lg border border-slate-800 bg-slate-950/35 px-3 py-3 text-sm text-slate-400">
              <span class="font-semibold text-slate-200">Unrestricted.</span>
              Any user with normal media access can watch it.
            </div>
          {:else}
            <div class="mt-4 flex flex-wrap gap-2">
              {#each mediaGroups as groupName (groupName)}
                <span class="inline-flex max-w-full items-center gap-2 rounded-full border border-slate-700 bg-slate-900/80 px-3 py-1 text-xs font-semibold text-slate-200">
                  <UsersGroupOutline class="h-3.5 w-3.5 shrink-0 text-blue-300" />
                  <span class="truncate">{groupName}</span>
                  <button
                    type="button"
                    class="grid h-5 w-5 shrink-0 place-items-center rounded-full text-slate-400 hover:bg-red-500/15 hover:text-red-200 disabled:opacity-50"
                    aria-label={`Remove group ${groupName}`}
                    disabled={mediaMutation === `remove:${groupName}`}
                    onclick={() => void removeGroupFromMedia(groupName)}
                  >
                    {#if mediaMutation === `remove:${groupName}`}
                      <Spinner size="4" />
                    {:else}
                      <CloseOutline class="h-3 w-3" />
                    {/if}
                  </button>
                </span>
              {/each}
            </div>
          {/if}

          <form
            class="mt-4 grid gap-3 sm:grid-cols-[minmax(0,1fr)_auto]"
            onsubmit={(event) => {
              event.preventDefault();
              void addGroupToMedia();
            }}
          >
            <Input bind:value={mediaGroupInput} placeholder="Group name" class={inputClass} aria-label="Group name" />
            <Button color="blue" type="submit" class={saveButtonClass} disabled={!mediaGroupInput.trim() || mediaMutation?.startsWith('add:')}>
              {#if mediaMutation?.startsWith('add:')}
                <Spinner size="4" class="mr-1.5" />
              {:else}
                <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
              {/if}
              Add group
            </Button>
          </form>
        </div>
      {/if}
    </div>
  {:else if activeTab === 'providers'}
    <div class="mt-5 space-y-5">
      <form
        class="grid gap-3 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]"
        onsubmit={(event) => {
          event.preventDefault();
          void addGroupToProvider();
        }}
      >
        <div>
          <Label for="provider-id" class="mb-2 text-sm font-medium text-slate-300">Provider id</Label>
          <Input id="provider-id" bind:value={providerIdInput} placeholder="provider name" class={inputClass} />
        </div>
        <div>
          <Label for="provider-group" class="mb-2 text-sm font-medium text-slate-300">Group name</Label>
          <Input id="provider-group" bind:value={providerGroupInput} placeholder="group name" class={inputClass} />
        </div>
        <div class="flex items-end">
          <Button color="blue" type="submit" class={saveButtonClass} disabled={!providerIdInput.trim() || !providerGroupInput.trim() || providerMutation?.startsWith('add:')}>
            {#if providerMutation?.startsWith('add:')}
              <Spinner size="4" class="mr-1.5" />
            {:else}
              <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
            {/if}
            Add group
          </Button>
        </div>
      </form>

      {#if providersError}
        <div class="flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
          <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
          <span>{errorMessage(providersError, 'Could not load provider policies.')}</span>
        </div>
      {/if}

      {#if providersLoading}
        <div class="mt-10 flex justify-center"><Spinner size="8" /></div>
      {:else if providerPolicies.length === 0}
        <div class="rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
          <UsersGroupOutline class="mx-auto h-9 w-9 text-slate-700" />
          <p class="mt-4 text-sm font-semibold text-slate-300">No provider restrictions</p>
          <p class="mt-1 text-sm text-slate-500">Providers are unrestricted unless groups are added here.</p>
        </div>
      {:else}
        <div class="overflow-hidden rounded-xl border border-slate-800">
          <div class="grid grid-cols-[minmax(9rem,0.8fr)_minmax(12rem,1fr)_auto] gap-3 border-b border-slate-800 bg-slate-950/40 px-3 py-2 text-xs font-semibold uppercase text-slate-500">
            <span>Provider</span>
            <span>Allowed groups</span>
            <span class="text-right">Actions</span>
          </div>
          <div class="divide-y divide-slate-800/80">
            {#each providerPolicies as policy (policy.provider)}
              <div class="grid gap-3 px-3 py-3 lg:grid-cols-[minmax(9rem,0.8fr)_minmax(12rem,1fr)_auto] lg:items-center">
                <div class="min-w-0">
                  <p class="truncate font-mono text-sm font-semibold text-slate-100">{policy.provider}</p>
                </div>
                <div class="flex min-w-0 flex-wrap gap-2">
                  {#if policy.groups.length === 0}
                    <span class="text-sm text-slate-500">Unrestricted</span>
                  {:else}
                    {#each policy.groups as groupName (groupName)}
                      <span class="inline-flex max-w-full items-center gap-2 rounded-full border border-slate-700 bg-slate-900/80 px-2.5 py-1 text-xs font-semibold text-slate-200">
                        <span class="truncate">{groupName}</span>
                        <button
                          type="button"
                          class="grid h-5 w-5 shrink-0 place-items-center rounded-full text-slate-400 hover:bg-red-500/15 hover:text-red-200 disabled:opacity-50"
                          aria-label={`Remove group ${groupName} from ${policy.provider}`}
                          disabled={providerMutation === `remove:${policy.provider}:${groupName}`}
                          onclick={() => void removeGroupFromProvider(policy.provider, groupName)}
                        >
                          {#if providerMutation === `remove:${policy.provider}:${groupName}`}
                            <Spinner size="4" />
                          {:else}
                            <CloseOutline class="h-3 w-3" />
                          {/if}
                        </button>
                      </span>
                    {/each}
                  {/if}
                </div>
                <div class="flex justify-end">
                  <button
                    type="button"
                    class="inline-flex h-9 min-w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-50"
                    title="Clear provider"
                    aria-label={`Clear provider ${policy.provider}`}
                    disabled={providerMutation === `clear:${policy.provider}`}
                    onclick={() => {
                      clearProviderTarget = policy;
                      clearProviderModalOpen = true;
                    }}
                  >
                    {#if providerMutation === `clear:${policy.provider}`}
                      <Spinner size="4" />
                    {:else}
                      <TrashBinOutline class="h-4 w-4" />
                    {/if}
                  </button>
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/if}
    </div>
  {:else}
    <div class="mt-5 space-y-5">
      <form
        class="grid gap-3 lg:grid-cols-[10rem_minmax(0,1fr)_auto]"
        onsubmit={(event) => {
          event.preventDefault();
          void addGroupToAgePolicy();
        }}
      >
        <div>
          <Label for="age-threshold" class="mb-2 text-sm font-medium text-slate-300">Threshold</Label>
          <Input id="age-threshold" type="number" min={0} bind:value={ageThresholdInput} placeholder="18" class={inputClass} />
        </div>
        <div>
          <Label for="age-group" class="mb-2 text-sm font-medium text-slate-300">Group name</Label>
          <Input id="age-group" bind:value={ageGroupInput} placeholder="group name" class={inputClass} />
        </div>
        <div class="flex items-end">
          <Button color="blue" type="submit" class={saveButtonClass} disabled={!ageGroupInput.trim() || ageMutation?.startsWith('add:')}>
            {#if ageMutation?.startsWith('add:')}
              <Spinner size="4" class="mr-1.5" />
            {:else}
              <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
            {/if}
            Add group
          </Button>
        </div>
      </form>

      {#if ageError}
        <div class="flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300" role="alert">
          <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
          <span>{errorMessage(ageError, 'Could not load age policies.')}</span>
        </div>
      {/if}

      {#if ageLoading}
        <div class="mt-10 flex justify-center"><Spinner size="8" /></div>
      {:else if agePolicies.length === 0}
        <div class="rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
          <UsersGroupOutline class="mx-auto h-9 w-9 text-slate-700" />
          <p class="mt-4 text-sm font-semibold text-slate-300">No age restrictions</p>
          <p class="mt-1 text-sm text-slate-500">Age policies are unrestricted until a threshold group is added.</p>
        </div>
      {:else}
        <div class="space-y-2">
          {#each agePolicies as policy (policy.threshold)}
            <article class="flex flex-col gap-3 rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 sm:flex-row sm:items-center sm:px-4">
              <div class="w-24 shrink-0">
                <span class="rounded-full bg-slate-800 px-2.5 py-1 text-xs font-bold text-blue-300">{policy.threshold}+</span>
              </div>
              <div class="flex min-w-0 flex-1 flex-wrap gap-2">
                {#if policy.groups.length === 0}
                  <span class="text-sm text-slate-500">Unrestricted</span>
                {:else}
                  {#each policy.groups as groupName (groupName)}
                    <span class="inline-flex max-w-full items-center gap-2 rounded-full border border-slate-700 bg-slate-900/80 px-2.5 py-1 text-xs font-semibold text-slate-200">
                      <span class="truncate">{groupName}</span>
                      <button
                        type="button"
                        class="grid h-5 w-5 shrink-0 place-items-center rounded-full text-slate-400 hover:bg-red-500/15 hover:text-red-200 disabled:opacity-50"
                        aria-label={`Remove group ${groupName} from ${policy.threshold}+`}
                        disabled={ageMutation === `remove:${policy.threshold}:${groupName}`}
                        onclick={() => void removeGroupFromAgePolicy(policy.threshold, groupName)}
                      >
                        {#if ageMutation === `remove:${policy.threshold}:${groupName}`}
                          <Spinner size="4" />
                        {:else}
                          <CloseOutline class="h-3 w-3" />
                        {/if}
                      </button>
                    </span>
                  {/each}
                {/if}
              </div>
            </article>
          {/each}
        </div>
      {/if}
    </div>
  {/if}
</section>

<ConfirmDeleteModal
  bind:open={clearMediaModalOpen}
  title="Clear media access groups"
  message={loadedMediaGuid ? `Clear all access groups for media ${loadedMediaGuid}? It will become unrestricted for users with normal media access.` : ''}
  confirmLabel="Clear groups"
  onConfirm={clearLoadedMediaGroups}
/>

<ConfirmDeleteModal
  bind:open={clearProviderModalOpen}
  title="Clear provider policy"
  message={clearProviderTarget ? `Clear all access groups for provider "${clearProviderTarget.provider}"? Media from this provider will be unrestricted by provider policy.` : ''}
  confirmLabel="Clear provider"
  onConfirm={clearProviderGroups}
/>
