<script lang="ts">
  import { goto } from '$app/navigation';
  import { Select } from '$lib/components/ui';
  import {
    Archive,
    ArrowLeft,
    CircleAlert,
    CloudUpload,
    Database,
    Globe,
    Eye,
    EyeOff,
    Layers,
    Plus
  } from '@lucide/svelte';
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

  type IconComponent = typeof Database;
  type TargetType = 'local' | 'network' | 's3' | 'azure' | 'gcs';
  type NetworkAuthMode = 'anonymous' | 'password' | 'privateKey';

  interface TargetOption {
    type: TargetType;
    label: string;
    icon: IconComponent;
    summary: string;
  }

  const targetOptions: TargetOption[] = [
    { type: 'local', label: 'Local', icon: Database, summary: 'Directory on the server filesystem' },
    { type: 'network', label: 'Network', icon: Globe, summary: 'FTP, FTPS, SFTP, NFS, SMB, or CIFS' },
    { type: 's3', label: 'S3', icon: Archive, summary: 'AWS S3, MinIO, or DigitalOcean Spaces' },
    { type: 'azure', label: 'Azure Blob', icon: Layers, summary: 'Azure Blob Storage container' },
    { type: 'gcs', label: 'Google Cloud', icon: CloudUpload, summary: 'Google Cloud Storage bucket' }
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

  const passwordNetworkAuthOptions = networkAuthOptions.filter((option) => option.value !== 'privateKey');

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
  let networkShareName = $state('');
  let networkDomain = $state('');
  let networkExportPath = $state('');
  let networkNfsUserId = $state('65534');
  let networkNfsGroupId = $state('65534');

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

  let visibleSecrets = $state<Record<string, boolean>>({});

  let submitting = $state(false);
  let submitError = $state<string | null>(null);

  const activeOption = $derived(targetOptions.find((option) => option.type === targetType) ?? targetOptions[0]);
  const s3RegionRequired = $derived(s3Provider !== 'MinIo');
  const s3EndpointRequired = $derived(s3Provider === 'MinIo');
  const isNfs = $derived(networkProtocol === 'Nfs');
  const isSmb = $derived(networkProtocol === 'Smb' || networkProtocol === 'Cifs');
  const activeNetworkAuthOptions = $derived(networkProtocol === 'Sftp' ? networkAuthOptions : passwordNetworkAuthOptions);

  $effect(() => {
    if (networkProtocol === 'Nfs') {
      networkAuthMode = 'anonymous';
    } else if (networkProtocol !== 'Sftp' && networkAuthMode === 'privateKey') {
      networkAuthMode = 'password';
    }
  });

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
        const nfsUserId = isNfs ? parsePosixId(networkNfsUserId, 'NFS user ID') : null;
        const nfsGroupId = isNfs ? parsePosixId(networkNfsGroupId, 'NFS group ID') : null;
        await createNetworkStorage({
          key: trimmedKey,
          description: trimmedDescription,
          protocol: networkProtocol,
          host: networkHost.trim(),
          port: isNfs ? null : port,
          username: isNfs || networkAuthMode === 'anonymous' ? null : networkUsername.trim() || null,
          password: !isNfs && networkAuthMode === 'password' ? networkPassword || null : null,
          privateKey: !isNfs && networkAuthMode === 'privateKey' ? networkPrivateKey || null : null,
          publicKey: null,
          basePath: networkBasePath.trim() || null,
          shareName: isSmb ? networkShareName.trim() || null : null,
          domain: isSmb ? networkDomain.trim() || null : null,
          exportPath: isNfs ? networkExportPath.trim() || null : null,
          nfsUserId,
          nfsGroupId
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

  function parsePosixId(value: string, label: string): number {
    const parsed = Number(value);
    if (!Number.isInteger(parsed) || parsed < 0 || parsed > 2147483647) {
      throw new Error(`${label} must be a non-negative whole number.`);
    }
    return parsed;
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

  function secretVisible(id: string): boolean {
    return visibleSecrets[id] ?? false;
  }

  function toggleSecret(id: string): void {
    visibleSecrets[id] = !secretVisible(id);
  }

</script>

<svelte:head>
  <title>Register storage · FrostStream</title>
</svelte:head>

<section class="mx-auto min-h-[calc(100vh-7rem)] max-w-4xl" aria-labelledby="register-storage-title">
  <h1 id="register-storage-title" class="text-2xl font-bold tracking-tight text-base-content">Register storage</h1>
  <p class="mt-2 text-sm text-base-content/60">
    Add a storage target FrostStream can index or write to. Credentials are stored in the secret store and never shown
    again.
  </p>

  <form
    onsubmit={save}
    class="mt-6 space-y-5 card border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6"
  >
    <h2 class="text-base font-bold text-base-content">{activeOption.label} storage</h2>

    <fieldset>
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
              'flex flex-col items-start gap-2 rounded-box border p-3 text-left transition',
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

    <div class="grid gap-5 md:grid-cols-2">
      <div>
        <label class="label mb-2 text-sm" for="storage-key">Key</label>
        <input class="input w-full" id="storage-key" required
           pattern={'[a-z0-9\\-]{2,100}'} minlength={2} maxlength={100} bind:value={key} placeholder="media-cold" />
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
        {#if !isNfs}<div>
          <label class="label mb-2 text-sm" for="network-port">Port</label>
          <input class="input w-full" id="network-port" type="number" min={1} max={65535} bind:value={networkPort} placeholder="default" />
        </div>{/if}
      </div>

      {#if isSmb}
        <div class="grid gap-5 sm:grid-cols-2">
          <div>
            <label class="label mb-2 text-sm" for="network-share">Share name</label>
            <input class="input w-full" id="network-share" required bind:value={networkShareName} placeholder="media" />
            <p class="mt-1.5 text-xs text-base-content/40">The share name only, without the server prefix.</p>
          </div>
          <div>
            <label class="label mb-2 text-sm" for="network-domain">Domain / workgroup <span class="font-normal text-base-content/50">(optional)</span></label>
            <input class="input w-full" id="network-domain" bind:value={networkDomain} placeholder="WORKGROUP" />
          </div>
        </div>
      {:else if isNfs}
        <div>
          <label class="label mb-2 text-sm" for="network-export">Export path</label>
          <input class="input w-full" id="network-export" required bind:value={networkExportPath} placeholder="/volume1/media" />
          <p class="mt-1.5 text-xs text-base-content/40">NFSv3 export path advertised by the server. The export must allow unprivileged source ports (the Linux <code>insecure</code> export option).</p>
        </div>
        <div class="grid gap-5 sm:grid-cols-2">
          <div>
            <label class="label mb-2 text-sm" for="network-nfs-uid">User ID (UID)</label>
            <input class="input w-full" id="network-nfs-uid" type="number" min={0} required bind:value={networkNfsUserId} />
          </div>
          <div>
            <label class="label mb-2 text-sm" for="network-nfs-gid">Group ID (GID)</label>
            <input class="input w-full" id="network-nfs-gid" type="number" min={0} required bind:value={networkNfsGroupId} />
          </div>
          <p class="sm:col-span-2 -mt-3 text-xs text-base-content/40">NFSv3 uses AUTH_UNIX IDs. They must match ownership and permissions on the export.</p>
        </div>
      {/if}

      <div>
        <label class="label mb-2 text-sm" for="network-base-path">Base path <span class="font-normal text-base-content/50">(optional)</span></label>
        <input class="input w-full" id="network-base-path" bind:value={networkBasePath} placeholder={isNfs || isSmb ? '/library' : '/volume1/media'} />
        {#if isNfs || isSmb}<p class="mt-1.5 text-xs text-base-content/40">Folder inside the configured export or share.</p>{/if}
      </div>

      {#if !isNfs}<div class="grid gap-5 sm:grid-cols-2">
        <div>
          <label class="label mb-2 text-sm" for="network-auth">Authentication</label>
          <Select id="network-auth" items={activeNetworkAuthOptions} bind:value={networkAuthMode} />
        </div>
        {#if networkAuthMode !== 'anonymous'}
          <div>
            <label class="label mb-2 text-sm" for="network-username">Username</label>
            <input class="input w-full" id="network-username" required  bind:value={networkUsername} />
          </div>
        {/if}
      </div>{/if}

      {#if !isNfs && networkAuthMode === 'password'}
        <div>
          <label class="label mb-2 text-sm" for="network-password">Password</label>
          <div class="relative">
            <input class="input w-full pr-10" id="network-password" type={secretVisible('network-password') ? 'text' : 'password'} required bind:value={networkPassword} />
            <button type="button" class="absolute right-2 top-1/2 -translate-y-1/2 text-base-content/50 hover:text-base-content" onclick={() => toggleSecret('network-password')} aria-label={secretVisible('network-password') ? 'Hide password' : 'Show password'}>
              {#if secretVisible('network-password')}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
            </button>
          </div>
          <p class="mt-1.5 text-xs text-base-content/40">Stored securely in the secret store and never shown again.</p>
        </div>
      {:else if !isNfs && networkAuthMode === 'privateKey'}
        <div>
          <label class="label mb-2 text-sm" for="network-private-key">Private key</label>
          <div class="relative">
            <textarea class={['textarea w-full pr-10 font-mono', !secretVisible('network-private-key') && 'text-transparent caret-transparent select-none']} id="network-private-key" rows={6} required
             bind:value={networkPrivateKey} placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"></textarea>
            <button type="button" class="absolute right-2 top-3 text-base-content/50 hover:text-base-content" onclick={() => toggleSecret('network-private-key')} aria-label={secretVisible('network-private-key') ? 'Hide private key' : 'Show private key'}>
              {#if secretVisible('network-private-key')}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
            </button>
          </div>
          <p class="mt-1.5 text-xs text-base-content/40">Stored securely in the secret store and never shown again.</p>
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
          <div class="relative">
            <input class="input w-full pr-10" id="s3-secret-key" type={secretVisible('s3-secret-key') ? 'text' : 'password'} required bind:value={s3SecretKey} />
            <button type="button" class="absolute right-2 top-1/2 -translate-y-1/2 text-base-content/50 hover:text-base-content" onclick={() => toggleSecret('s3-secret-key')} aria-label={secretVisible('s3-secret-key') ? 'Hide secret access key' : 'Show secret access key'}>
              {#if secretVisible('s3-secret-key')}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
            </button>
          </div>
          <p class="mt-1.5 text-xs text-base-content/40">Stored securely in the secret store and never shown again.</p>
        </div>
      </div>

      <div>
        <label class="label mb-2 text-sm" for="s3-session-token">
          Session token <span class="font-normal text-base-content/50">(optional)</span>
        </label>
        <div class="relative">
          <input class="input w-full pr-10" id="s3-session-token" type={secretVisible('s3-session-token') ? 'text' : 'password'} bind:value={s3SessionToken} />
          <button type="button" class="absolute right-2 top-1/2 -translate-y-1/2 text-base-content/50 hover:text-base-content" onclick={() => toggleSecret('s3-session-token')} aria-label={secretVisible('s3-session-token') ? 'Hide session token' : 'Show session token'}>
            {#if secretVisible('s3-session-token')}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
          </button>
        </div>
        <p class="mt-1.5 text-xs text-base-content/40">Stored securely in the secret store and never shown again.</p>
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
            <div class="relative">
              <input class="input w-full pr-10" id="azure-account-key" type={secretVisible('azure-account-key') ? 'text' : 'password'} required bind:value={azureAccountKey} />
              <button type="button" class="absolute right-2 top-1/2 -translate-y-1/2 text-base-content/50 hover:text-base-content" onclick={() => toggleSecret('azure-account-key')} aria-label={secretVisible('azure-account-key') ? 'Hide account key' : 'Show account key'}>
                {#if secretVisible('azure-account-key')}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
              </button>
            </div>
            <p class="mt-1.5 text-xs text-base-content/40">Stored securely in the secret store and never shown again.</p>
          </div>
        </div>
      {:else if azureCredentialMode === 'ConnectionString'}
        <div>
          <label class="label mb-2 text-sm" for="azure-connection-string">Connection string</label>
          <div class="relative">
            <input class="input w-full pr-10" id="azure-connection-string" type={secretVisible('azure-connection-string') ? 'text' : 'password'} required
             bind:value={azureConnectionString} placeholder="DefaultEndpointsProtocol=https;AccountName=..." />
            <button type="button" class="absolute right-2 top-1/2 -translate-y-1/2 text-base-content/50 hover:text-base-content" onclick={() => toggleSecret('azure-connection-string')} aria-label={secretVisible('azure-connection-string') ? 'Hide connection string' : 'Show connection string'}>
              {#if secretVisible('azure-connection-string')}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
            </button>
          </div>
          <p class="mt-1.5 text-xs text-base-content/40">Stored securely in the secret store and never shown again.</p>
        </div>
      {:else if azureCredentialMode === 'SasUrl'}
        <div>
          <label class="label mb-2 text-sm" for="azure-sas-url">SAS URL</label>
          <div class="relative">
            <input class="input w-full pr-10" id="azure-sas-url" type={secretVisible('azure-sas-url') ? 'text' : 'password'} required
             bind:value={azureSasUrl} placeholder="https://account.blob.core.windows.net/container?sv=..." />
            <button type="button" class="absolute right-2 top-1/2 -translate-y-1/2 text-base-content/50 hover:text-base-content" onclick={() => toggleSecret('azure-sas-url')} aria-label={secretVisible('azure-sas-url') ? 'Hide SAS URL' : 'Show SAS URL'}>
              {#if secretVisible('azure-sas-url')}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
            </button>
          </div>
          <p class="mt-1.5 text-xs text-base-content/40">Stored securely in the secret store and never shown again.</p>
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
          <div class="relative">
            <textarea class={['textarea w-full pr-10 font-mono', !secretVisible('gcs-credentials-json') && 'text-transparent caret-transparent select-none']} id="gcs-credentials-json" rows={8} required
             bind:value={gcsCredentialsJsonText} placeholder={'{\n  "type": "service_account",\n  ...\n}'}></textarea>
            <button type="button" class="absolute right-2 top-3 text-base-content/50 hover:text-base-content" onclick={() => toggleSecret('gcs-credentials-json')} aria-label={secretVisible('gcs-credentials-json') ? 'Hide service account JSON' : 'Show service account JSON'}>
              {#if secretVisible('gcs-credentials-json')}<EyeOff class="h-4 w-4" />{:else}<Eye class="h-4 w-4" />{/if}
            </button>
          </div>
          <p class="mt-1.5 text-xs text-base-content/40">Stored securely in the secret store and never shown again.</p>
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
        class="alert alert-error text-sm"
        role="alert"
      >
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{submitError}</span>
      </div>
    {/if}

    <div class="flex flex-col-reverse gap-3 border-t border-base-300/70 pt-5 sm:flex-row sm:items-center sm:justify-between">
      <a class="btn btn-sm btn-neutral text-xs" href="/admin/storage">
        <ArrowLeft class="mr-1.5 h-4 w-4" />
        Back
      </a>
      <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={submitting}>
        {#if submitting}
          <span class="loading loading-spinner loading-xs mr-1.5"></span>
        {:else}
          <Plus class="mr-1.5 h-4 w-4" />
        {/if}
        Save changes
      </button>
    </div>
  </form>
</section>
