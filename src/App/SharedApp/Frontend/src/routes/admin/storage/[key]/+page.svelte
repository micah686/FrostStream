<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { ArrowLeft, CircleAlert } from '@lucide/svelte';
  import {
    displayLocalPath,
    getStorage,
    storageMethodLabel,
    type StorageConfig
  } from '$lib/api/storage';

  interface SettingEntry {
    label: string;
    value: string;
    mono?: boolean;
  }

  let storage = $state<StorageConfig | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);

  onMount(async () => {
    try {
      storage = await getStorage(page.params.key ?? '');
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not load the storage target.';
    } finally {
      loading = false;
    }
  });

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
      return [{ label: 'Path', value: displayLocalPath(storage.local.path), mono: true }];
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
  <title>{page.params.key} · Storage · FrostStream</title>
</svelte:head>

<section class="mx-auto min-h-[calc(100vh-7rem)] max-w-4xl" aria-labelledby="storage-detail-title">
  {#if loading}
    <div class="mt-16 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if !storage}
    <div
      class="alert alert-error mt-6 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{error ?? 'Storage target not found.'}</span>
    </div>
  {:else}
    <div class="mt-4 flex flex-col gap-5 sm:flex-row sm:items-start sm:justify-between">
      <div class="min-w-0">
        <div class="flex flex-wrap items-center gap-2.5">
          <h1 id="storage-detail-title" class="truncate text-2xl font-bold tracking-tight text-base-content">
            {storage.key}
          </h1>
          <span class="badge badge-primary badge-sm text-[10px] font-semibold">
            {storageMethodLabel(storage)}
          </span>
        </div>
        <p class="mt-2 text-sm text-base-content/60">{storage.description || 'No description'}</p>
      </div>

    </div>

    {#if error}
      <div
        class="alert alert-error mt-5 text-sm"
        role="alert"
      >
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{error}</span>
      </div>
    {/if}

    <section
      class="mt-6 card border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6"
      aria-label="Storage view"
    >
      <h2 class="text-base font-bold text-base-content">View</h2>
      <p class="mt-2 text-sm text-base-content/60">
        Credentials are stored in the secret store and are not shown here.
      </p>

      <dl class="mt-5 grid gap-3 sm:grid-cols-2">
        {#each settings as entry (entry.label)}
          <div class="card border-[length:var(--border)] border-base-300 bg-base-100 p-4">
            <dt class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">{entry.label}</dt>
            <dd class={['mt-1 break-all text-sm text-base-content/80', entry.mono && 'font-mono']}>{entry.value}</dd>
          </div>
        {/each}
      </dl>
    </section>

    <section class="mt-5 grid gap-4 sm:grid-cols-3" aria-label="Storage metadata">
      <div class="card border-[length:var(--border)] border-base-300 bg-base-100 p-4">
        <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Created</p>
        <p class="mt-1 text-sm text-base-content/80">{formatInstant(storage.createdAt)}</p>
      </div>
      <div class="card border-[length:var(--border)] border-base-300 bg-base-100 p-4">
        <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Last updated</p>
        <p class="mt-1 text-sm text-base-content/80">{formatInstant(storage.lastUpdated)}</p>
      </div>
      <div class="card border-[length:var(--border)] border-base-300 bg-base-100 p-4">
        <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-base-content/40">Worker tag</p>
        <p class="mt-1 text-sm text-base-content/80">{storage.workerTag ?? 'Any worker'}</p>
      </div>
    </section>
    <div class="mt-5 border-t border-base-300/70 pt-5">
      <a class="btn btn-sm btn-neutral text-xs" href="/admin/storage">
        <ArrowLeft class="mr-1.5 h-4 w-4" />
        Back
      </a>
    </div>
  {/if}

</section>
