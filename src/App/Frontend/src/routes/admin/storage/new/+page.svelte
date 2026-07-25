<script lang="ts">
  import { goto } from '$app/navigation';
  import { Select } from '$lib/components/ui';
  import {
    ArchiveOutline,
    ArrowLeftOutline,
    CloudArrowUpOutline,
    DatabaseOutline,
    ExclamationCircleOutline,
    GlobeOutline,
    LayersOutline,
    PlusOutline
  } from 'flowbite-svelte-icons';
  import {
    createAzureBlobStorage,
    createGoogleCloudStorage,
    createLocalStorage,
    createNetworkStorage,
    createS3CompatibleStorage,
    type AzureBlobCredentialMode,
    type GoogleCloudStorageCredentialMode,
    type NetworkStorageProtocol,
    type S3CompatibleObjectStorageProvider
  } from '$lib/api/storage';

  type IconComponent = typeof DatabaseOutline;
  type TargetType = 'local' | 'network' | 's3' | 'azure' | 'gcs';
  type NetworkAuthMode = 'anonymous' | 'password' | 'privateKey';

  interface TargetOption {
    type: TargetType;
    label: string;
    icon: IconComponent;
    summary: string;
  }

  const targetOptions: TargetOption[] = [
    { type: 'local', label: 'Local', icon: DatabaseOutline, summary: 'Directory on the server filesystem' },
    { type: 'network', label: 'Network', icon: GlobeOutline, summary: 'FTP, FTPS, SFTP, NFS, SMB, or CIFS' },
    { type: 's3', label: 'S3', icon: ArchiveOutline, summary: 'AWS S3, MinIO, or DigitalOcean Spaces' },
    { type: 'azure', label: 'Azure Blob', icon: LayersOutline, summary: 'Azure Blob Storage container' },
    { type: 'gcs', label: 'Google Cloud', icon: CloudArrowUpOutline, summary: 'Google Cloud Storage bucket' }
  ];

  const networkProtocolOptions = [
    { value: 'Sftp', name: 'SFTP' },
    { value: 'Ftp', name: 'FTP' },
    { value: 'Ftps', name: 'FTPS' },
    { value: 'Nfs', name: 'NFS' },
    { value: 'Smb', name: 'SMB' },
    { value: 'Cifs', name: 'CIFS' }
  ];

  const networkAuthOptions = [
    { value: 'anonymous', name: 'None / anonymous' },
    { value: 'password', name: 'Username and password' },
    { value: 'privateKey', name: 'Username and private key' }
  ];

  const s3ProviderOptions = [
    { value: 'AwsS3', name: 'AWS S3' },
    { value: 'MinIo', name: 'MinIO' },
    { value: 'DigitalOceanSpaces', name: 'DigitalOcean Spaces' }
  ];

  const azureCredentialOptions = [
    { value: 'AccountKey', name: 'Account name and key' },
    { value: 'ConnectionString', name: 'Connection string' },
    { value: 'SasUrl', name: 'SAS URL' }
  ];

  const gcsCredentialOptions = [
    { value: 'CredentialsJson', name: 'Service account JSON' },
    { value: 'CredentialsFilePath', name: 'Credentials file on server' },
    { value: 'WorkloadIdentity', name: 'Workload identity' },
    { value: 'DefaultCredentials', name: 'Application default credentials' }
  ];

  let targetType = $state<TargetType>('local');

  let key = $state('');
  let description = $state('');

  // Local
  let localPath = $state('');

  // Network
  let networkProtocol = $state<NetworkStorageProtocol>('Sftp');
  let networkAuthMode = $state<NetworkAuthMode>('password');
  let networkHost = $state('');
  let networkPort = $state('');
  let networkUsername = $state('');
  let networkPassword = $state('');
  let networkPrivateKey = $state('');
  let networkBasePath = $state('');

  // S3-compatible object storage
  let s3Provider = $state<S3CompatibleObjectStorageProvider>('AwsS3');
  let s3Bucket = $state('');
  let s3Region = $state('');
  let s3Endpoint = $state('');
  let s3AccessKeyId = $state('');
  let s3SecretKey = $state('');
  let s3SessionToken = $state('');
  let s3ForcePathStyle = $state(false);
  let s3UseSsl = $state(true);

  // Azure Blob
  let azureCredentialMode = $state<AzureBlobCredentialMode>('AccountKey');
  let azureContainerName = $state('');
  let azureAccountName = $state('');
  let azureAccountKey = $state('');
  let azureConnectionString = $state('');
  let azureSasUrl = $state('');

  // Google Cloud Storage
  let gcsBucket = $state('');
  let gcsCredentialMode = $state<GoogleCloudStorageCredentialMode>('CredentialsJson');
  let gcsCredentialsJsonText = $state('');
  let gcsCredentialsFilePath = $state('');
  let gcsProjectId = $state('');

  let submitting = $state(false);
  let submitError = $state<string | null>(null);

  const activeOption = $derived(targetOptions.find((option) => option.type === targetType) ?? targetOptions[0]);
  const s3RegionRequired = $derived(s3Provider !== 'MinIo');
  const s3EndpointRequired = $derived(s3Provider === 'MinIo');

  async function save(event: SubmitEvent) {
    event.preventDefault();
    submitting = true;
    submitError = null;

    try {
      await createTarget();
      await goto('/admin');
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'Could not register the storage target.';
    } finally {
      submitting = false;
    }
  }

  async function createTarget() {
    const trimmedKey = key.trim();
    const trimmedDescription = description.trim() || null;

    switch (targetType) {
      case 'local':
        await createLocalStorage({
          key: trimmedKey,
          description: trimmedDescription,
          protocol: 'Local',
          path: localPath.trim()
        });
        break;

      case 'network': {
        const port = networkPort.trim() ? Number(networkPort) : null;
        if (port !== null && (!Number.isInteger(port) || port < 1 || port > 65535)) {
          throw new Error('Port must be a whole number from 1 to 65535.');
        }
        await createNetworkStorage({
          key: trimmedKey,
          description: trimmedDescription,
          protocol: networkProtocol,
          host: networkHost.trim(),
          port,
          username: networkAuthMode === 'anonymous' ? null : networkUsername.trim() || null,
          password: networkAuthMode === 'password' ? networkPassword || null : null,
          privateKey: networkAuthMode === 'privateKey' ? networkPrivateKey || null : null,
          publicKey: null,
          basePath: networkBasePath.trim() || null
        });
        break;
      }

      case 's3':
        await createS3CompatibleStorage({
          key: trimmedKey,
          description: trimmedDescription,
          provider: s3Provider,
          bucketName: s3Bucket.trim(),
          region: s3Region.trim() || null,
          endpoint: s3Endpoint.trim() || null,
          accessKeyId: s3AccessKeyId.trim(),
          secretKeyId: s3SecretKey,
          sessionTokenSecretId: s3SessionToken.trim() || null,
          forcePathStyle: s3ForcePathStyle,
          useSsl: s3UseSsl
        });
        break;

      case 'azure':
        await createAzureBlobStorage({
          key: trimmedKey,
          description: trimmedDescription,
          credentialMode: azureCredentialMode,
          containerName: azureContainerName.trim() || null,
          azureAccountName: azureCredentialMode === 'AccountKey' ? azureAccountName.trim() || null : null,
          azureAccountKeySecretId: azureCredentialMode === 'AccountKey' ? azureAccountKey || null : null,
          azureConnectionStringSecretId:
            azureCredentialMode === 'ConnectionString' ? azureConnectionString.trim() || null : null,
          azureSasUrlSecretId: azureCredentialMode === 'SasUrl' ? azureSasUrl.trim() || null : null
        });
        break;

      case 'gcs':
        await createGoogleCloudStorage({
          key: trimmedKey,
          description: trimmedDescription,
          bucketName: gcsBucket.trim(),
          credentialMode: gcsCredentialMode,
          gcpCredentialsJson:
            gcsCredentialMode === 'CredentialsJson' ? parseCredentialsJson(gcsCredentialsJsonText) : null,
          gcpCredentialsJsonIsBase64Encoded: false,
          gcpCredentialsFilePath:
            gcsCredentialMode === 'CredentialsFilePath' ? gcsCredentialsFilePath.trim() || null : null,
          gcpProjectId: gcsProjectId.trim() || null
        });
        break;
    }
  }

  function parseCredentialsJson(value: string): Record<string, unknown> {
    let parsed: unknown;
    try {
      parsed = JSON.parse(value.trim());
    } catch {
      throw new Error('Service account credentials must be valid JSON.');
    }
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      throw new Error('Service account credentials must be a JSON object.');
    }
    return parsed as Record<string, unknown>;
  }

</script>

<svelte:head>
  <title>Register storage · FrostStream</title>
</svelte:head>

<section class="mx-auto min-h-[calc(100vh-7rem)] max-w-4xl" aria-labelledby="register-storage-title">
  <a
    href="/admin"
    class="inline-flex items-center gap-1.5 text-xs font-semibold text-base-content/60 transition hover:text-base-content/90"
  >
    <ArrowLeftOutline class="h-3.5 w-3.5" />
    Back to administration
  </a>

  <h1 id="register-storage-title" class="mt-4 text-2xl font-bold tracking-tight text-base-content">Register storage</h1>
  <p class="mt-2 text-sm text-base-content/60">
    Add a storage target FrostStream can index or write to. Credentials are stored in the secret store and never shown
    again.
  </p>

  <fieldset class="mt-6">
    <legend class="mb-3 text-sm font-medium text-base-content/80">Storage type</legend>
    <div class="grid gap-2 sm:grid-cols-3 lg:grid-cols-5" role="radiogroup" aria-label="Storage type">
      {#each targetOptions as option (option.type)}
        {@const { icon: Icon } = option}
        {@const active = targetType === option.type}
        <button
          type="button"
          role="radio"
          aria-checked={active}
          class={[
            'flex flex-col items-start gap-2 rounded-xl border p-3 text-left transition',
            active
              ? 'border-primary/60 bg-primary/10 text-primary'
              : 'border-base-content/20 bg-base-200/40 text-base-content/80 hover:border-base-content/30 hover:bg-base-300/40'
          ]}
          onclick={() => (targetType = option.type)}
        >
          <Icon class={['h-5 w-5', active ? 'text-primary' : 'text-base-content/50']} />
          <span class="text-sm font-semibold">{option.label}</span>
          <span class="text-xs leading-5 text-base-content/50">{option.summary}</span>
        </button>
      {/each}
    </div>
  </fieldset>

  <form
    onsubmit={save}
    class="mt-6 space-y-5 card border border-base-300 bg-base-100 p-5 sm:p-6"
  >
    <h2 class="text-base font-bold text-base-content">{activeOption.label} storage</h2>

    <div class="grid gap-5 md:grid-cols-2">
      <div>
        <label class="label mb-2 text-sm" for="storage-key">Key</label>
        <input class="input w-full" id="storage-key" required
           pattern={'[a-z0-9-]{2,100}'} minlength={2} maxlength={100} bind:value={key} placeholder="media-cold" />
        <p class="mt-1.5 text-xs text-base-content/40">Lowercase letters, numbers, and hyphens. Referenced by config sets.</p>
      </div>

      <div>
        <label class="label mb-2 text-sm" for="storage-description">Description</label>
        <input class="input w-full" id="storage-description" maxlength={500} bind:value={description} placeholder="optional" />
      </div>
    </div>

    {#if targetType === 'local'}
      <div>
        <label class="label mb-2 text-sm" for="local-path">Path</label>
        <input class="input w-full" id="local-path" required  bind:value={localPath} placeholder="/mnt/media" />
        <p class="mt-1.5 text-xs text-base-content/40">
          Absolute path on the server. All services using this key must see the same filesystem location.
        </p>
      </div>
    {:else if targetType === 'network'}
      <div class="grid gap-5 sm:grid-cols-[10rem_minmax(0,1fr)_7rem]">
        <div>
          <label class="label mb-2 text-sm" for="network-protocol">Protocol</label>
          <Select id="network-protocol" items={networkProtocolOptions} bind:value={networkProtocol} />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="network-host">Host</label>
          <input class="input w-full" id="network-host" required  bind:value={networkHost} placeholder="nas.local" />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="network-port">Port</label>
          <input class="input w-full" id="network-port" type="number" min={1} max={65535} bind:value={networkPort} placeholder="default" />
        </div>
      </div>

      <div>
        <label class="label mb-2 text-sm" for="network-base-path">Base path</label>
        <input class="input w-full" id="network-base-path" bind:value={networkBasePath} placeholder="/volume1/media" />
      </div>

      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <label class="label mb-2 text-sm" for="network-auth">Authentication</label>
          <Select id="network-auth" items={networkAuthOptions} bind:value={networkAuthMode} />
        </div>
        {#if networkAuthMode !== 'anonymous'}
          <div>
            <label class="label mb-2 text-sm" for="network-username">Username</label>
            <input class="input w-full" id="network-username" required  bind:value={networkUsername} />
          </div>
        {/if}
      </div>

      {#if networkAuthMode === 'password'}
        <div>
          <label class="label mb-2 text-sm" for="network-password">Password</label>
          <input class="input w-full" id="network-password" type="password" required  bind:value={networkPassword} />
        </div>
      {:else if networkAuthMode === 'privateKey'}
        <div>
          <label class="label mb-2 text-sm" for="network-private-key">Private key</label>
          <textarea class="textarea w-full font-mono" id="network-private-key" rows={6} required
             bind:value={networkPrivateKey} placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"></textarea>
        </div>
      {/if}
    {:else if targetType === 's3'}
      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <label class="label mb-2 text-sm" for="s3-provider">Provider</label>
          <Select id="s3-provider" items={s3ProviderOptions} bind:value={s3Provider} />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="s3-bucket">Bucket</label>
          <input class="input w-full" id="s3-bucket" required  bind:value={s3Bucket} placeholder="froststream-media" />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="s3-region">
            Region {#if !s3RegionRequired}<span class="font-normal text-base-content/50">(optional)</span>{/if}
          </label>
          <input class="input w-full" id="s3-region" required={s3RegionRequired} bind:value={s3Region} placeholder="us-east-1" />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="s3-endpoint">
            Endpoint {#if !s3EndpointRequired}<span class="font-normal text-base-content/50">(optional)</span>{/if}
          </label>
          <input class="input w-full" id="s3-endpoint" required={s3EndpointRequired} bind:value={s3Endpoint} placeholder="https://minio.local:9000" />
        </div>
      </div>

      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <label class="label mb-2 text-sm" for="s3-access-key">Access key ID</label>
          <input class="input w-full" id="s3-access-key" required  bind:value={s3AccessKeyId} />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="s3-secret-key">Secret access key</label>
          <input class="input w-full" id="s3-secret-key" type="password" required  bind:value={s3SecretKey} />
        </div>
      </div>

      <div>
        <label class="label mb-2 text-sm" for="s3-session-token">
          Session token <span class="font-normal text-base-content/50">(optional)</span>
        </label>
        <input class="input w-full" id="s3-session-token" type="password" bind:value={s3SessionToken} />
      </div>

      <div class="flex flex-wrap gap-x-8 gap-y-3 border-t border-base-300/70 pt-5">
        <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={s3UseSsl} /><span>Use SSL</span></label>
        <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={s3ForcePathStyle} /><span>Force path-style addressing</span></label>
      </div>
    {:else if targetType === 'azure'}
      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <label class="label mb-2 text-sm" for="azure-credential-mode">Authentication</label>
          <Select id="azure-credential-mode" items={azureCredentialOptions} bind:value={azureCredentialMode} />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="azure-container">
            Container <span class="font-normal text-base-content/50">(optional)</span>
          </label>
          <input class="input w-full" id="azure-container" bind:value={azureContainerName} placeholder="froststream-media" />
        </div>
      </div>

      {#if azureCredentialMode === 'AccountKey'}
        <div class="grid gap-5 sm:grid-cols-2">
          <div>
            <label class="label mb-2 text-sm" for="azure-account-name">Account name</label>
            <input class="input w-full" id="azure-account-name" required  bind:value={azureAccountName} />
          </div>
          <div>
            <label class="label mb-2 text-sm" for="azure-account-key">Account key</label>
            <input class="input w-full" id="azure-account-key" type="password" required  bind:value={azureAccountKey} />
          </div>
        </div>
      {:else if azureCredentialMode === 'ConnectionString'}
        <div>
          <label class="label mb-2 text-sm" for="azure-connection-string">Connection string</label>
          <input class="input w-full" id="azure-connection-string" type="password" required
             bind:value={azureConnectionString} placeholder="DefaultEndpointsProtocol=https;AccountName=..." />
        </div>
      {:else if azureCredentialMode === 'SasUrl'}
        <div>
          <label class="label mb-2 text-sm" for="azure-sas-url">SAS URL</label>
          <input class="input w-full" id="azure-sas-url" type="password" required
             bind:value={azureSasUrl} placeholder="https://account.blob.core.windows.net/container?sv=..." />
        </div>
      {/if}
    {:else if targetType === 'gcs'}
      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <label class="label mb-2 text-sm" for="gcs-bucket">Bucket</label>
          <input class="input w-full" id="gcs-bucket" required  bind:value={gcsBucket} placeholder="froststream-media" />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="gcs-project">
            Project ID <span class="font-normal text-base-content/50">(optional)</span>
          </label>
          <input class="input w-full" id="gcs-project" bind:value={gcsProjectId} placeholder="my-project-123" />
        </div>
      </div>

      <div>
        <label class="label mb-2 text-sm" for="gcs-credential-mode">Credentials</label>
        <Select id="gcs-credential-mode" items={gcsCredentialOptions} bind:value={gcsCredentialMode} />
      </div>

      {#if gcsCredentialMode === 'CredentialsJson'}
        <div>
          <label class="label mb-2 text-sm" for="gcs-credentials-json">Service account JSON</label>
          <textarea class="textarea w-full font-mono" id="gcs-credentials-json" rows={8} required
             bind:value={gcsCredentialsJsonText} placeholder={'{\n  "type": "service_account",\n  ...\n}'}></textarea>
        </div>
      {:else if gcsCredentialMode === 'CredentialsFilePath'}
        <div>
          <label class="label mb-2 text-sm" for="gcs-credentials-path">Credentials file path</label>
          <input class="input w-full" id="gcs-credentials-path" required
             bind:value={gcsCredentialsFilePath} placeholder="/etc/froststream/gcs-credentials.json" />
          <p class="mt-1.5 text-xs text-base-content/40">Path on the server running FrostStream services.</p>
        </div>
      {/if}
    {/if}

    {#if submitError}
      <div
        class="flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
        role="alert"
      >
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{submitError}</span>
      </div>
    {/if}

    <div class="flex items-center gap-3 border-t border-base-300/70 pt-5">
      <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={submitting}>
        {#if submitting}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {:else}
          <PlusOutline class="mr-1.5 h-4 w-4" />
        {/if}
        Register storage
      </button>
      <a class="btn btn-sm btn-ghost text-xs" href="/admin">
        Cancel
      </a>
    </div>
  </form>
</section>
