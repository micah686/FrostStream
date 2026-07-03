<script lang="ts">
  import { onMount } from 'svelte';
  import { Badge, Button, Checkbox, Input, Label, Select, Spinner, Textarea } from 'flowbite-svelte';
  import {
    ApiKeyOutline,
    BellOutline,
    BellRingOutline,
    CheckCircleOutline,
    EditOutline,
    ExclamationCircleOutline,
    FlaskOutline,
    LockOutline,
    PaperPlaneOutline,
    PlusOutline,
    RefreshOutline,
    TrashBinOutline
  } from 'flowbite-svelte-icons';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import {
    NOTIFICATION_PROVIDER_KEY_PATTERN,
    NOTIFICATION_PROVIDER_KINDS,
    NOTIFICATION_SECRET_NAME_PATTERN,
    deleteNotificationProvider,
    deleteNotificationProviderSecret,
    getNotificationProvider,
    listNotificationProviders,
    sendTestNotification,
    upsertNotificationProvider,
    upsertNotificationProviderSecrets,
    type NotificationProvider
  } from '$lib/api/notifications';

  interface SelectItem {
    value: string;
    name: string;
  }

  const providerKindItems: SelectItem[] = NOTIFICATION_PROVIDER_KINDS.map((kind) => ({ value: kind, name: kind }));
  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const cardClass = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const outlineButtonClass =
    'border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800! disabled:opacity-60';
  const rowActionClass =
    'inline-flex h-9 items-center justify-center gap-1.5 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200 disabled:opacity-50';

  let providers = $state<NotificationProvider[]>([]);
  let selectedProviderKey = $state('');
  let loading = $state(true);
  let loadingProviderKey = $state<string | null>(null);
  let loadError = $state<string | null>(null);
  let mutationError = $state<string | null>(null);
  let statusMessage = $state<string | null>(null);

  let editorMode = $state<'create' | 'update'>('create');
  let providerKey = $state('');
  let providerKind = $state('email');
  let providerEnabled = $state(true);
  let displayName = $state('');
  let defaultTo = $state('');
  let notifyConfigText = $state('{}');
  let savingProvider = $state(false);

  let secretName = $state('');
  let secretValue = $state('');
  let savingSecret = $state(false);
  let deletingSecret = $state(false);

  let testSubject = $state('FrostStream notification test');
  let testBody = $state('This is a test notification from FrostStream.');
  let testingProvider = $state(false);

  let deleteModalOpen = $state(false);
  let deleteTarget = $state<NotificationProvider | null>(null);
  let deletingProviderKey = $state<string | null>(null);

  const selectedProvider = $derived(
    providers.find((provider) => provider.providerKey === selectedProviderKey) ?? null
  );

  onMount(() => {
    void loadProviders();
  });

  async function loadProviders(selectKey = selectedProviderKey) {
    loading = true;
    loadError = null;
    try {
      providers = sortProviders(await listNotificationProviders());
      const nextKey = providers.some((provider) => provider.providerKey === selectKey)
        ? selectKey
        : (providers[0]?.providerKey ?? '');
      if (nextKey) {
        await selectProvider(nextKey);
      } else {
        startCreate();
      }
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load notification providers.';
    } finally {
      loading = false;
    }
  }

  async function selectProvider(key: string) {
    selectedProviderKey = key;
    loadingProviderKey = key;
    mutationError = null;
    statusMessage = null;
    try {
      const provider = await getNotificationProvider(key);
      providers = sortProviders([
        provider,
        ...providers.filter((item) => item.providerKey !== provider.providerKey)
      ]);
      applyProvider(provider);
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not load the notification provider.';
    } finally {
      loadingProviderKey = null;
    }
  }

  function startCreate() {
    editorMode = 'create';
    selectedProviderKey = '';
    providerKey = '';
    providerKind = 'email';
    providerEnabled = true;
    displayName = '';
    defaultTo = '';
    notifyConfigText = '{}';
    secretName = '';
    secretValue = '';
    mutationError = null;
    statusMessage = null;
  }

  function applyProvider(provider: NotificationProvider) {
    editorMode = 'update';
    providerKey = provider.providerKey;
    providerKind = provider.providerKind;
    providerEnabled = provider.enabled;
    displayName = provider.displayName ?? '';
    defaultTo = provider.defaultTo ?? '';
    notifyConfigText = formatJson(provider.notifyConfig);
    secretName = '';
    secretValue = '';
  }

  async function saveProvider(event: SubmitEvent) {
    event.preventDefault();
    mutationError = null;
    statusMessage = null;

    const key = providerKey.trim();
    if (!NOTIFICATION_PROVIDER_KEY_PATTERN.test(key)) {
      mutationError = 'Provider key must match ^[a-z0-9-]{2,100}$.';
      return;
    }

    let notifyConfig: Record<string, unknown>;
    try {
      notifyConfig = parseJsonObject(notifyConfigText, 'Notify config');
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Notify config must be a JSON object.';
      return;
    }

    savingProvider = true;
    try {
      const provider = await upsertNotificationProvider(key, {
        providerKey: key,
        providerKind,
        enabled: providerEnabled,
        displayName: displayName.trim() || null,
        defaultTo: defaultTo.trim() || null,
        notifyConfig
      });
      providers = sortProviders([
        provider,
        ...providers.filter((item) => item.providerKey !== provider.providerKey)
      ]);
      selectedProviderKey = provider.providerKey;
      applyProvider(provider);
      statusMessage = 'Notification provider saved.';
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not save the notification provider.';
    } finally {
      savingProvider = false;
    }
  }

  async function removeProvider(provider: NotificationProvider) {
    deletingProviderKey = provider.providerKey;
    mutationError = null;
    statusMessage = null;
    try {
      const preferences = await deleteNotificationProvider(provider.providerKey);
      providers = sortProviders(preferences.providers ?? []);
      deleteTarget = null;
      deleteModalOpen = false;
      const nextKey = providers[0]?.providerKey ?? '';
      if (nextKey) {
        await selectProvider(nextKey);
      } else {
        startCreate();
      }
      statusMessage = 'Notification provider deleted.';
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not delete the notification provider.';
    } finally {
      deletingProviderKey = null;
    }
  }

  async function saveSecret(event: SubmitEvent) {
    event.preventDefault();
    mutationError = null;
    statusMessage = null;

    if (!selectedProviderKey) {
      mutationError = 'Save the provider before storing secrets.';
      return;
    }
    if (!NOTIFICATION_SECRET_NAME_PATTERN.test(secretName.trim())) {
      mutationError = 'Secret names must match ^[A-Za-z0-9_.-]{1,100}$.';
      return;
    }
    if (!secretValue) {
      mutationError = 'Secret value is required.';
      return;
    }

    savingSecret = true;
    try {
      await upsertNotificationProviderSecrets(selectedProviderKey, {
        secrets: { [secretName.trim()]: secretValue }
      });
      statusMessage = `Secret ${secretName.trim()} saved.`;
      secretValue = '';
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not save the provider secret.';
    } finally {
      savingSecret = false;
    }
  }

  async function removeSecret() {
    mutationError = null;
    statusMessage = null;

    if (!selectedProviderKey) {
      mutationError = 'Select a provider before deleting secrets.';
      return;
    }
    if (!NOTIFICATION_SECRET_NAME_PATTERN.test(secretName.trim())) {
      mutationError = 'Secret name must match ^[A-Za-z0-9_.-]{1,100}$.';
      return;
    }

    deletingSecret = true;
    try {
      await deleteNotificationProviderSecret(selectedProviderKey, secretName.trim());
      statusMessage = `Secret ${secretName.trim()} deleted.`;
      secretValue = '';
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not delete the provider secret.';
    } finally {
      deletingSecret = false;
    }
  }

  async function sendTest() {
    mutationError = null;
    statusMessage = null;

    if (!selectedProviderKey) {
      mutationError = 'Select a provider before sending a test notification.';
      return;
    }

    testingProvider = true;
    try {
      await sendTestNotification({
        providerKey: selectedProviderKey,
        subject: testSubject.trim() || null,
        body: testBody.trim() || null
      });
      statusMessage = 'Test notification sent.';
    } catch (err) {
      mutationError = err instanceof Error ? err.message : 'Could not send the test notification.';
    } finally {
      testingProvider = false;
    }
  }

  function sortProviders(items: NotificationProvider[]): NotificationProvider[] {
    return [...items].sort((a, b) => a.providerKey.localeCompare(b.providerKey));
  }

  function parseJsonObject(value: string, label: string): Record<string, unknown> {
    const trimmed = value.trim();
    if (!trimmed) {
      return {};
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(trimmed);
    } catch {
      throw new Error(`${label} must be valid JSON.`);
    }
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      throw new Error(`${label} must be a JSON object.`);
    }
    return parsed as Record<string, unknown>;
  }

  function formatJson(value: Record<string, unknown> | null | undefined): string {
    return JSON.stringify(value ?? {}, null, 2);
  }

  function providerLabel(provider: NotificationProvider): string {
    return provider.displayName?.trim() || provider.providerKey;
  }

  function providerSummary(provider: NotificationProvider): string {
    return [
      provider.providerKind,
      provider.defaultTo ? `to ${provider.defaultTo}` : null,
      provider.enabled ? 'enabled' : 'disabled'
    ].filter(Boolean).join(' · ');
  }
</script>

<section class={cardClass} aria-labelledby="notifications-title">
  <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 id="notifications-title" class="text-base font-bold text-slate-100">Notifications</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
        Manage user-scoped notification providers, write-only provider secrets, and delivery tests.
      </p>
    </div>
    <div class="flex shrink-0 gap-2">
      <Button color="dark" class={outlineButtonClass} disabled={loading} onclick={() => void loadProviders()}>
        <RefreshOutline class="mr-1.5 h-3.5 w-3.5" />
        Refresh
      </Button>
      <Button color="dark" class={outlineButtonClass} onclick={startCreate}>
        <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
        New provider
      </Button>
    </div>
  </div>

  {#if loadError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{loadError}</span>
    </div>
  {/if}

  {#if mutationError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{mutationError}</span>
    </div>
  {/if}

  {#if statusMessage}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-emerald-900/60 bg-emerald-950/30 p-3 text-sm text-emerald-300"
      role="status"
    >
      <CheckCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{statusMessage}</span>
    </div>
  {/if}

  {#if loading}
    <div class="mt-10 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else}
    <div class="mt-5 grid gap-5 2xl:grid-cols-[minmax(18rem,0.8fr)_minmax(0,1.2fr)]">
      <div class="min-w-0 rounded-xl border border-slate-800/80 bg-slate-950/20 p-3">
        {#if providers.length === 0}
          <div class="p-8 text-center">
            <BellOutline class="mx-auto h-9 w-9 text-slate-700" />
            <p class="mt-4 text-sm font-semibold text-slate-300">No notification providers yet</p>
            <p class="mt-1 text-sm text-slate-500">Create one to enable delivery through a Notify channel.</p>
          </div>
        {:else}
          <div class="space-y-2">
            {#each providers as provider (provider.providerKey)}
              {@const active = provider.providerKey === selectedProviderKey}
              <article
                class={[
                  'flex min-h-[4rem] flex-col gap-3 rounded-lg border px-3 py-3 transition sm:flex-row sm:items-center',
                  active
                    ? 'border-blue-500/50 bg-blue-500/10'
                    : 'border-slate-700/70 bg-[#151a26] hover:border-slate-600 hover:bg-slate-800/30'
                ]}
              >
                <button
                  type="button"
                  class="flex min-w-0 flex-1 items-center gap-3 text-left"
                  onclick={() => void selectProvider(provider.providerKey)}
                >
                  <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-400">
                    {#if loadingProviderKey === provider.providerKey}
                      <Spinner size="4" />
                    {:else if provider.enabled}
                      <BellRingOutline class="h-4.5 w-4.5" />
                    {:else}
                      <BellOutline class="h-4.5 w-4.5" />
                    {/if}
                  </span>
                  <span class="min-w-0">
                    <span class="flex min-w-0 flex-wrap items-center gap-2">
                      <span class="truncate text-sm font-semibold text-slate-100">{providerLabel(provider)}</span>
                      <Badge rounded color="gray" class="bg-slate-800! px-2! py-0.5! text-[10px]! text-slate-400!">
                        {provider.providerKey}
                      </Badge>
                    </span>
                    <span class="mt-0.5 block truncate text-xs text-slate-400">{providerSummary(provider)}</span>
                  </span>
                </button>

                <div class="flex shrink-0 gap-2 sm:ml-auto">
                  <button
                    type="button"
                    class={rowActionClass}
                    aria-label={`Edit notification provider ${provider.providerKey}`}
                    onclick={() => void selectProvider(provider.providerKey)}
                  >
                    <EditOutline class="h-4 w-4" />
                    Edit
                  </button>
                  <button
                    type="button"
                    class="inline-flex h-9 min-w-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-2.5 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-50"
                    title="Delete provider"
                    aria-label={`Delete notification provider ${provider.providerKey}`}
                    disabled={deletingProviderKey === provider.providerKey}
                    onclick={() => {
                      deleteTarget = provider;
                      deleteModalOpen = true;
                    }}
                  >
                    {#if deletingProviderKey === provider.providerKey}
                      <Spinner size="4" />
                    {:else}
                      <TrashBinOutline class="h-4 w-4" />
                    {/if}
                  </button>
                </div>
              </article>
            {/each}
          </div>
        {/if}
      </div>

      <div class="min-w-0 space-y-5">
        <form
          onsubmit={saveProvider}
          class="rounded-xl border border-slate-800/80 bg-slate-950/20 p-4"
          aria-labelledby="notification-provider-editor-title"
        >
          <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
            <div>
              <h3 id="notification-provider-editor-title" class="text-sm font-bold text-slate-100">
                {editorMode === 'create' ? 'New provider' : 'Provider settings'}
              </h3>
              {#if selectedProvider}
                <p class="mt-1 text-xs text-slate-500">{providerSummary(selectedProvider)}</p>
              {/if}
            </div>
            <Badge rounded color="gray" class="w-fit bg-slate-800! px-2.5! py-1! text-[10px]! text-slate-400!">
              {editorMode}
            </Badge>
          </div>

          <div class="mt-4 grid gap-4 md:grid-cols-2">
            <div>
              <Label for="notification-provider-key" class="mb-2 text-sm font-medium text-slate-300">Provider key</Label>
              <Input
                id="notification-provider-key"
                required
                pattern={'[a-z0-9-]{2,100}'}
                minlength={2}
                maxlength={100}
                disabled={editorMode === 'update'}
                bind:value={providerKey}
                placeholder="alerts"
                class={inputClass}
              />
            </div>
            <div>
              <Label for="notification-provider-kind" class="mb-2 text-sm font-medium text-slate-300">Provider kind</Label>
              <Select
                id="notification-provider-kind"
                items={providerKindItems}
                bind:value={providerKind}
                class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! focus:border-blue-500! focus:ring-blue-500!"
              />
            </div>
          </div>

          <div class="mt-4 grid gap-4 md:grid-cols-2">
            <div>
              <Label for="notification-display-name" class="mb-2 text-sm font-medium text-slate-300">Display name</Label>
              <Input
                id="notification-display-name"
                maxlength={255}
                bind:value={displayName}
                placeholder="Operations alerts"
                class={inputClass}
              />
            </div>
            <div>
              <Label for="notification-default-to" class="mb-2 text-sm font-medium text-slate-300">Default recipient</Label>
              <Input
                id="notification-default-to"
                maxlength={512}
                bind:value={defaultTo}
                placeholder="#ops-alerts"
                class={inputClass}
              />
            </div>
          </div>

          <div class="mt-4">
            <Label for="notification-config" class="mb-2 text-sm font-medium text-slate-300">Notify config JSON</Label>
            <Textarea
              id="notification-config"
              rows={10}
              bind:value={notifyConfigText}
              placeholder={'{\n  "webhookUrl": "secret://alerts/webhookUrl"\n}'}
              class="font-mono! border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
            />
          </div>

          <div class="mt-4 flex flex-col-reverse gap-3 border-t border-slate-800/70 pt-4 sm:flex-row sm:items-center sm:justify-between">
            <Checkbox bind:checked={providerEnabled} class="text-sm text-slate-300">Enabled</Checkbox>
            <Button
              type="submit"
              color="blue"
              disabled={savingProvider}
              class="border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60"
            >
              {#if savingProvider}
                <Spinner size="4" class="mr-1.5" />
              {:else}
                <CheckCircleOutline class="mr-1.5 h-4 w-4" />
              {/if}
              Save provider
            </Button>
          </div>
        </form>

        <section class="grid gap-5 xl:grid-cols-2">
          <form
            onsubmit={saveSecret}
            class="rounded-xl border border-slate-800/80 bg-slate-950/20 p-4"
            aria-labelledby="notification-secrets-title"
          >
            <h3 id="notification-secrets-title" class="flex items-center gap-2 text-sm font-bold text-slate-100">
              <LockOutline class="h-4 w-4 text-blue-400" />
              Provider secrets
            </h3>

            <div class="mt-4">
              <Label for="notification-secret-name" class="mb-2 text-sm font-medium text-slate-300">Secret name</Label>
              <Input
                id="notification-secret-name"
                maxlength={100}
                bind:value={secretName}
                placeholder="webhookUrl"
                class={inputClass}
              />
            </div>
            <div class="mt-4">
              <Label for="notification-secret-value" class="mb-2 text-sm font-medium text-slate-300">Secret value</Label>
              <Input
                id="notification-secret-value"
                type="password"
                bind:value={secretValue}
                placeholder="write-only"
                class={inputClass}
              />
            </div>

            <div class="mt-4 flex flex-col gap-2 sm:flex-row">
              <Button
                type="submit"
                color="blue"
                disabled={savingSecret || !selectedProviderKey}
                class="border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60"
              >
                {#if savingSecret}
                  <Spinner size="4" class="mr-1.5" />
                {:else}
                  <ApiKeyOutline class="mr-1.5 h-4 w-4" />
                {/if}
                Store secret
              </Button>
              <Button
                type="button"
                color="dark"
                disabled={deletingSecret || !selectedProviderKey}
                class="border-slate-700! bg-transparent! px-4! py-2! text-xs! font-semibold! text-red-200! hover:border-red-500/60! hover:bg-red-500/10! disabled:opacity-60"
                onclick={removeSecret}
              >
                {#if deletingSecret}
                  <Spinner size="4" class="mr-1.5" />
                {:else}
                  <TrashBinOutline class="mr-1.5 h-4 w-4" />
                {/if}
                Delete secret
              </Button>
            </div>
          </form>

          <section class="rounded-xl border border-slate-800/80 bg-slate-950/20 p-4" aria-labelledby="notification-test-title">
            <h3 id="notification-test-title" class="flex items-center gap-2 text-sm font-bold text-slate-100">
              <FlaskOutline class="h-4 w-4 text-blue-400" />
              Test delivery
            </h3>

            <div class="mt-4">
              <Label for="notification-test-subject" class="mb-2 text-sm font-medium text-slate-300">Subject</Label>
              <Input id="notification-test-subject" bind:value={testSubject} class={inputClass} />
            </div>
            <div class="mt-4">
              <Label for="notification-test-body" class="mb-2 text-sm font-medium text-slate-300">Body</Label>
              <Textarea
                id="notification-test-body"
                rows={5}
                bind:value={testBody}
                class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!"
              />
            </div>

            <Button
              type="button"
              color="blue"
              disabled={testingProvider || !selectedProviderKey}
              class="mt-4 border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60"
              onclick={sendTest}
            >
              {#if testingProvider}
                <Spinner size="4" class="mr-1.5" />
              {:else}
                <PaperPlaneOutline class="mr-1.5 h-4 w-4" />
              {/if}
              Send test
            </Button>
          </section>
        </section>
      </div>
    </div>
  {/if}
</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete notification provider?"
  message={deleteTarget
    ? `Delete provider "${providerLabel(deleteTarget)}"? Matching provider secrets will also be removed.`
    : 'Delete this notification provider?'}
  confirmLabel="Delete provider"
  onConfirm={async () => {
    if (deleteTarget) {
      await removeProvider(deleteTarget);
    }
  }}
/>
