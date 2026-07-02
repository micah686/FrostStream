<script lang="ts">
  import { goto } from '$app/navigation';
  import { Button, Checkbox, Input, Label, Select, Spinner, Textarea } from 'flowbite-svelte';
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

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
</script>

<svelte:head>
  <title>Register storage · FrostStream</title>
</svelte:head>

<section class="mx-auto min-h-[calc(100vh-7rem)] max-w-4xl" aria-labelledby="register-storage-title">
  <a
    href="/admin"
    class="inline-flex items-center gap-1.5 text-xs font-semibold text-slate-400 transition hover:text-slate-200"
  >
    <ArrowLeftOutline class="h-3.5 w-3.5" />
    Back to administration
  </a>

  <h1 id="register-storage-title" class="mt-4 text-2xl font-bold tracking-tight text-slate-100">Register storage</h1>
  <p class="mt-2 text-sm text-slate-400">
    Add a storage target FrostStream can index or write to. Credentials are stored in the secret store and never shown
    again.
  </p>

  <fieldset class="mt-6">
    <legend class="mb-3 text-sm font-medium text-slate-300">Storage type</legend>
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
              ? 'border-blue-500/60 bg-blue-500/10 text-blue-200'
              : 'border-slate-700/70 bg-slate-900/40 text-slate-300 hover:border-slate-600 hover:bg-slate-800/40'
          ]}
          onclick={() => (targetType = option.type)}
        >
          <Icon class={['h-5 w-5', active ? 'text-blue-400' : 'text-slate-500']} />
          <span class="text-sm font-semibold">{option.label}</span>
          <span class="text-xs leading-5 text-slate-500">{option.summary}</span>
        </button>
      {/each}
    </div>
  </fieldset>

  <form
    onsubmit={save}
    class="mt-6 space-y-5 rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6"
  >
    <h2 class="text-base font-bold text-slate-100">{activeOption.label} storage</h2>

    <div class="grid gap-5 md:grid-cols-2">
      <div>
        <Label for="storage-key" class="mb-2 text-sm font-medium text-slate-300">Key</Label>
        <Input
          id="storage-key"
          required
          pattern={'[a-z0-9-]{2,100}'}
          minlength={2}
          maxlength={100}
          bind:value={key}
          placeholder="media-cold"
          class={inputClass}
        />
        <p class="mt-1.5 text-xs text-slate-600">Lowercase letters, numbers, and hyphens. Referenced by config sets.</p>
      </div>

      <div>
        <Label for="storage-description" class="mb-2 text-sm font-medium text-slate-300">Description</Label>
        <Input
          id="storage-description"
          maxlength={500}
          bind:value={description}
          placeholder="optional"
          class={inputClass}
        />
      </div>
    </div>

    {#if targetType === 'local'}
      <div>
        <Label for="local-path" class="mb-2 text-sm font-medium text-slate-300">Path</Label>
        <Input id="local-path" required bind:value={localPath} placeholder="/mnt/media" class={inputClass} />
        <p class="mt-1.5 text-xs text-slate-600">
          Absolute path on the server. All services using this key must see the same filesystem location.
        </p>
      </div>
    {:else if targetType === 'network'}
      <div class="grid gap-5 sm:grid-cols-[10rem_minmax(0,1fr)_7rem]">
        <div>
          <Label for="network-protocol" class="mb-2 text-sm font-medium text-slate-300">Protocol</Label>
          <Select id="network-protocol" items={networkProtocolOptions} bind:value={networkProtocol} class={inputClass} />
        </div>
        <div>
          <Label for="network-host" class="mb-2 text-sm font-medium text-slate-300">Host</Label>
          <Input id="network-host" required bind:value={networkHost} placeholder="nas.local" class={inputClass} />
        </div>
        <div>
          <Label for="network-port" class="mb-2 text-sm font-medium text-slate-300">Port</Label>
          <Input id="network-port" type="number" min={1} max={65535} bind:value={networkPort} placeholder="default" class={inputClass} />
        </div>
      </div>

      <div>
        <Label for="network-base-path" class="mb-2 text-sm font-medium text-slate-300">Base path</Label>
        <Input id="network-base-path" bind:value={networkBasePath} placeholder="/volume1/media" class={inputClass} />
      </div>

      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <Label for="network-auth" class="mb-2 text-sm font-medium text-slate-300">Authentication</Label>
          <Select id="network-auth" items={networkAuthOptions} bind:value={networkAuthMode} class={inputClass} />
        </div>
        {#if networkAuthMode !== 'anonymous'}
          <div>
            <Label for="network-username" class="mb-2 text-sm font-medium text-slate-300">Username</Label>
            <Input id="network-username" required bind:value={networkUsername} class={inputClass} />
          </div>
        {/if}
      </div>

      {#if networkAuthMode === 'password'}
        <div>
          <Label for="network-password" class="mb-2 text-sm font-medium text-slate-300">Password</Label>
          <Input id="network-password" type="password" required bind:value={networkPassword} class={inputClass} />
        </div>
      {:else if networkAuthMode === 'privateKey'}
        <div>
          <Label for="network-private-key" class="mb-2 text-sm font-medium text-slate-300">Private key</Label>
          <Textarea
            id="network-private-key"
            rows={6}
            required
            bind:value={networkPrivateKey}
            placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
            class={`font-mono! ${inputClass}`}
          />
        </div>
      {/if}
    {:else if targetType === 's3'}
      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <Label for="s3-provider" class="mb-2 text-sm font-medium text-slate-300">Provider</Label>
          <Select id="s3-provider" items={s3ProviderOptions} bind:value={s3Provider} class={inputClass} />
        </div>
        <div>
          <Label for="s3-bucket" class="mb-2 text-sm font-medium text-slate-300">Bucket</Label>
          <Input id="s3-bucket" required bind:value={s3Bucket} placeholder="froststream-media" class={inputClass} />
        </div>
        <div>
          <Label for="s3-region" class="mb-2 text-sm font-medium text-slate-300">
            Region {#if !s3RegionRequired}<span class="font-normal text-slate-500">(optional)</span>{/if}
          </Label>
          <Input
            id="s3-region"
            required={s3RegionRequired}
            bind:value={s3Region}
            placeholder="us-east-1"
            class={inputClass}
          />
        </div>
        <div>
          <Label for="s3-endpoint" class="mb-2 text-sm font-medium text-slate-300">
            Endpoint {#if !s3EndpointRequired}<span class="font-normal text-slate-500">(optional)</span>{/if}
          </Label>
          <Input
            id="s3-endpoint"
            required={s3EndpointRequired}
            bind:value={s3Endpoint}
            placeholder="https://minio.local:9000"
            class={inputClass}
          />
        </div>
      </div>

      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <Label for="s3-access-key" class="mb-2 text-sm font-medium text-slate-300">Access key ID</Label>
          <Input id="s3-access-key" required bind:value={s3AccessKeyId} class={inputClass} />
        </div>
        <div>
          <Label for="s3-secret-key" class="mb-2 text-sm font-medium text-slate-300">Secret access key</Label>
          <Input id="s3-secret-key" type="password" required bind:value={s3SecretKey} class={inputClass} />
        </div>
      </div>

      <div>
        <Label for="s3-session-token" class="mb-2 text-sm font-medium text-slate-300">
          Session token <span class="font-normal text-slate-500">(optional)</span>
        </Label>
        <Input id="s3-session-token" type="password" bind:value={s3SessionToken} class={inputClass} />
      </div>

      <div class="flex flex-wrap gap-x-8 gap-y-3 border-t border-slate-800/70 pt-5">
        <Checkbox bind:checked={s3UseSsl} class="text-sm text-slate-300">Use SSL</Checkbox>
        <Checkbox bind:checked={s3ForcePathStyle} class="text-sm text-slate-300">Force path-style addressing</Checkbox>
      </div>
    {:else if targetType === 'azure'}
      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <Label for="azure-credential-mode" class="mb-2 text-sm font-medium text-slate-300">Authentication</Label>
          <Select
            id="azure-credential-mode"
            items={azureCredentialOptions}
            bind:value={azureCredentialMode}
            class={inputClass}
          />
        </div>
        <div>
          <Label for="azure-container" class="mb-2 text-sm font-medium text-slate-300">
            Container <span class="font-normal text-slate-500">(optional)</span>
          </Label>
          <Input id="azure-container" bind:value={azureContainerName} placeholder="froststream-media" class={inputClass} />
        </div>
      </div>

      {#if azureCredentialMode === 'AccountKey'}
        <div class="grid gap-5 sm:grid-cols-2">
          <div>
            <Label for="azure-account-name" class="mb-2 text-sm font-medium text-slate-300">Account name</Label>
            <Input id="azure-account-name" required bind:value={azureAccountName} class={inputClass} />
          </div>
          <div>
            <Label for="azure-account-key" class="mb-2 text-sm font-medium text-slate-300">Account key</Label>
            <Input id="azure-account-key" type="password" required bind:value={azureAccountKey} class={inputClass} />
          </div>
        </div>
      {:else if azureCredentialMode === 'ConnectionString'}
        <div>
          <Label for="azure-connection-string" class="mb-2 text-sm font-medium text-slate-300">Connection string</Label>
          <Input
            id="azure-connection-string"
            type="password"
            required
            bind:value={azureConnectionString}
            placeholder="DefaultEndpointsProtocol=https;AccountName=..."
            class={inputClass}
          />
        </div>
      {:else if azureCredentialMode === 'SasUrl'}
        <div>
          <Label for="azure-sas-url" class="mb-2 text-sm font-medium text-slate-300">SAS URL</Label>
          <Input
            id="azure-sas-url"
            type="password"
            required
            bind:value={azureSasUrl}
            placeholder="https://account.blob.core.windows.net/container?sv=..."
            class={inputClass}
          />
        </div>
      {/if}
    {:else if targetType === 'gcs'}
      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <Label for="gcs-bucket" class="mb-2 text-sm font-medium text-slate-300">Bucket</Label>
          <Input id="gcs-bucket" required bind:value={gcsBucket} placeholder="froststream-media" class={inputClass} />
        </div>
        <div>
          <Label for="gcs-project" class="mb-2 text-sm font-medium text-slate-300">
            Project ID <span class="font-normal text-slate-500">(optional)</span>
          </Label>
          <Input id="gcs-project" bind:value={gcsProjectId} placeholder="my-project-123" class={inputClass} />
        </div>
      </div>

      <div>
        <Label for="gcs-credential-mode" class="mb-2 text-sm font-medium text-slate-300">Credentials</Label>
        <Select id="gcs-credential-mode" items={gcsCredentialOptions} bind:value={gcsCredentialMode} class={inputClass} />
      </div>

      {#if gcsCredentialMode === 'CredentialsJson'}
        <div>
          <Label for="gcs-credentials-json" class="mb-2 text-sm font-medium text-slate-300">Service account JSON</Label>
          <Textarea
            id="gcs-credentials-json"
            rows={8}
            required
            bind:value={gcsCredentialsJsonText}
            placeholder={'{\n  "type": "service_account",\n  ...\n}'}
            class={`font-mono! ${inputClass}`}
          />
        </div>
      {:else if gcsCredentialMode === 'CredentialsFilePath'}
        <div>
          <Label for="gcs-credentials-path" class="mb-2 text-sm font-medium text-slate-300">Credentials file path</Label>
          <Input
            id="gcs-credentials-path"
            required
            bind:value={gcsCredentialsFilePath}
            placeholder="/etc/froststream/gcs-credentials.json"
            class={inputClass}
          />
          <p class="mt-1.5 text-xs text-slate-600">Path on the server running FrostStream services.</p>
        </div>
      {/if}
    {/if}

    {#if submitError}
      <div
        class="flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/40 p-3 text-sm text-red-300"
        role="alert"
      >
        <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{submitError}</span>
      </div>
    {/if}

    <div class="flex items-center gap-3 border-t border-slate-800/70 pt-5">
      <Button
        type="submit"
        disabled={submitting}
        color="blue"
        class="border-0! bg-blue-500! px-4! py-2! text-xs! font-semibold! hover:bg-blue-400! disabled:opacity-60"
      >
        {#if submitting}
          <Spinner size="4" class="mr-1.5" />
        {:else}
          <PlusOutline class="mr-1.5 h-4 w-4" />
        {/if}
        Register storage
      </Button>
      <Button
        href="/admin"
        color="dark"
        class="border-slate-700! bg-transparent! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!"
      >
        Cancel
      </Button>
    </div>
  </form>
</section>
