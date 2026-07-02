<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { Button, Spinner } from 'flowbite-svelte';
  import { ArrowLeftOutline, ExclamationCircleOutline, TrashBinOutline } from 'flowbite-svelte-icons';
  import {
    deleteStorage,
    getStorage,
    storageMethodLabel,
    type StorageConfig
  } from '$lib/api/storage';

  interface SettingEntry {
    label: string;
    value: string;
    mono?: boolean;
  }

  let { data } = $props();

  let storage = $state<StorageConfig | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let deleting = $state(false);

  onMount(async () => {
    try {
      storage = await getStorage(data.key);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not load the storage target.';
    } finally {
      loading = false;
    }
  });

  async function removeStorage() {
    if (!storage) {
      return;
    }
    const confirmed = window.confirm(
      `Delete storage target "${storage.key}"? Media already stored there will no longer be reachable through this key.`
    );
    if (!confirmed) {
      return;
    }

    deleting = true;
    error = null;
    try {
      await deleteStorage(storage.key);
      await goto('/admin');
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not delete the storage target.';
    } finally {
      deleting = false;
    }
  }

  function formatInstant(value: string | null): string {
    return value ? new Date(value).toLocaleString() : '—';
  }

  function yesNo(value: boolean | null): string {
    return value === null ? 'Default' : value ? 'Yes' : 'No';
  }

  const settings = $derived.by<SettingEntry[]>(() => {
    if (!storage) {
      return [];
    }

    if (storage.local) {
      return [{ label: 'Path', value: storage.local.path, mono: true }];
    }

    if (storage.network) {
      const network = storage.network;
      return [
        { label: 'Protocol', value: network.protocol.toUpperCase() },
        { label: 'Host', value: network.host, mono: true },
        { label: 'Port', value: network.port?.toString() ?? 'Default' },
        { label: 'Username', value: network.username ?? 'Anonymous' },
        { label: 'Base path', value: network.basePath ?? '—', mono: true }
      ];
    }

    if (storage.objectS3Compatible) {
      const s3 = storage.objectS3Compatible;
      return [
        { label: 'Bucket', value: s3.bucketName, mono: true },
        { label: 'Region', value: s3.region ?? '—' },
        { label: 'Endpoint', value: s3.endpoint ?? '—', mono: true },
        { label: 'Session token', value: s3.hasSessionToken ? 'Configured' : 'Not used' },
        { label: 'Force path style', value: yesNo(s3.forcePathStyle) },
        { label: 'Use SSL', value: yesNo(s3.useSsl) }
      ];
    }

    if (storage.objectAzureBlob) {
      const azure = storage.objectAzureBlob;
      return [
        { label: 'Credential mode', value: azure.credentialMode },
        { label: 'Container', value: azure.containerName ?? '—', mono: true },
        { label: 'Account name', value: azure.azureAccountName ?? '—' }
      ];
    }

    if (storage.objectGoogleCloudStorage) {
      const gcs = storage.objectGoogleCloudStorage;
      return [
        { label: 'Bucket', value: gcs.bucketName, mono: true },
        { label: 'Credential mode', value: gcs.credentialMode },
        { label: 'Credentials file', value: gcs.gcpCredentialsFilePath ?? '—', mono: true },
        { label: 'Project ID', value: gcs.gcpProjectId ?? '—' }
      ];
    }

    return [];
  });
</script>

<svelte:head>
  <title>{data.key} · Storage · FrostStream</title>
</svelte:head>

<section class="mx-auto min-h-[calc(100vh-7rem)] max-w-4xl" aria-labelledby="storage-detail-title">
  <a
    href="/admin"
    class="inline-flex items-center gap-1.5 text-xs font-semibold text-slate-400 transition hover:text-slate-200"
  >
    <ArrowLeftOutline class="h-3.5 w-3.5" />
    Back to administration
  </a>

  {#if loading}
    <div class="mt-16 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if !storage}
    <div
      class="mt-6 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{error ?? 'Storage target not found.'}</span>
    </div>
  {:else}
    <div class="mt-4 flex flex-col gap-5 sm:flex-row sm:items-start sm:justify-between">
      <div class="min-w-0">
        <div class="flex flex-wrap items-center gap-2.5">
          <h1 id="storage-detail-title" class="truncate text-2xl font-bold tracking-tight text-slate-100">
            {storage.key}
          </h1>
          <span class="rounded-full bg-slate-800 px-2.5 py-0.5 text-[10px] font-semibold text-slate-400">
            {storageMethodLabel(storage)}
          </span>
        </div>
        <p class="mt-2 text-sm text-slate-400">{storage.description || 'No description'}</p>
      </div>

      <Button
        color="dark"
        disabled={deleting}
        class="shrink-0 border-slate-700! bg-[#0f1420]! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:border-red-500/60! hover:bg-red-500/10! hover:text-red-200! disabled:opacity-60"
        onclick={removeStorage}
      >
        {#if deleting}
          <Spinner size="4" class="mr-1.5" />
        {:else}
          <TrashBinOutline class="mr-1.5 h-4 w-4" />
        {/if}
        Delete
      </Button>
    </div>

    {#if error}
      <div
        class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
        role="alert"
      >
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{error}</span>
      </div>
    {/if}

    <section
      class="mt-6 rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6"
      aria-label="Storage settings"
    >
      <h2 class="text-base font-bold text-slate-100">Settings</h2>
      <p class="mt-2 text-sm text-slate-400">
        Credentials are stored in the secret store and are not shown here.
      </p>

      <dl class="mt-5 grid gap-3 sm:grid-cols-2">
        {#each settings as entry (entry.label)}
          <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
            <dt class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">{entry.label}</dt>
            <dd class={['mt-1 break-all text-sm text-slate-300', entry.mono && 'font-mono']}>{entry.value}</dd>
          </div>
        {/each}
      </dl>
    </section>

    <section class="mt-5 grid gap-3 sm:grid-cols-3" aria-label="Storage metadata">
      <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
        <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Created</p>
        <p class="mt-1 text-sm text-slate-300">{formatInstant(storage.createdAt)}</p>
      </div>
      <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
        <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Last updated</p>
        <p class="mt-1 text-sm text-slate-300">{formatInstant(storage.lastUpdated)}</p>
      </div>
      <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
        <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Worker tag</p>
        <p class="mt-1 text-sm text-slate-300">{storage.workerTag ?? 'Any worker'}</p>
      </div>
    </section>
  {/if}
</section>
