export type StorageMethod = 'Local' | 'Network' | 'ObjectStorage';
export type LocalStorageProtocol = 'Local';
export type NetworkStorageProtocol = 'Ftp' | 'Ftps' | 'Sftp' | 'Nfs' | 'Smb' | 'Cifs';
export type S3CompatibleObjectStorageProvider = 'AwsS3' | 'MinIo' | 'DigitalOceanSpaces';
export type AzureBlobCredentialMode = 'AccountKey' | 'ConnectionString' | 'SasUrl';
export type GoogleCloudStorageCredentialMode =
  | 'CredentialsJson'
  | 'CredentialsFilePath'
  | 'WorkloadIdentity'
  | 'DefaultCredentials';

export interface LocalStorageStored {
  protocol: LocalStorageProtocol;
  path: string;
}

export interface NetworkStorageStored {
  protocol: NetworkStorageProtocol;
  host: string;
  port: number | null;
  username: string | null;
  basePath: string | null;
}

export interface S3CompatibleStorageStored {
  provider: S3CompatibleObjectStorageProvider;
  bucketName: string;
  region: string | null;
  endpoint: string | null;
  hasSessionToken: boolean;
  forcePathStyle: boolean;
  useSsl: boolean | null;
}

export interface AzureBlobStorageStored {
  credentialMode: AzureBlobCredentialMode;
  containerName: string | null;
  azureAccountName: string | null;
}

export interface GoogleCloudStorageStored {
  bucketName: string;
  credentialMode: GoogleCloudStorageCredentialMode;
  gcpCredentialsFilePath: string | null;
  gcpProjectId: string | null;
}

export interface StorageConfig {
  id: number;
  key: string;
  method: StorageMethod;
  description: string | null;
  workerTag: string | null;
  createdAt: string;
  lastUpdated: string | null;
  local: LocalStorageStored | null;
  network: NetworkStorageStored | null;
  objectS3Compatible: S3CompatibleStorageStored | null;
  objectAzureBlob: AzureBlobStorageStored | null;
  objectGoogleCloudStorage: GoogleCloudStorageStored | null;
}

export interface LocalStorageRequest {
  description: string | null;
  protocol: LocalStorageProtocol;
  path: string;
}

export interface NetworkStorageRequest {
  description: string | null;
  protocol: NetworkStorageProtocol;
  host: string;
  port: number | null;
  username: string | null;
  password: string | null;
  privateKey: string | null;
  publicKey: string | null;
  basePath: string | null;
}

export interface S3CompatibleStorageRequest {
  description: string | null;
  provider: S3CompatibleObjectStorageProvider;
  bucketName: string;
  region: string | null;
  endpoint: string | null;
  accessKeyId: string;
  secretKeyId: string;
  sessionTokenSecretId: string | null;
  forcePathStyle: boolean;
  useSsl: boolean | null;
}

export interface AzureBlobStorageRequest {
  description: string | null;
  credentialMode: AzureBlobCredentialMode;
  containerName: string | null;
  azureAccountName: string | null;
  azureAccountKeySecretId: string | null;
  azureConnectionStringSecretId: string | null;
  azureSasUrlSecretId: string | null;
}

export interface GoogleCloudStorageRequest {
  description: string | null;
  bucketName: string;
  credentialMode: GoogleCloudStorageCredentialMode;
  gcpCredentialsJson: Record<string, unknown> | null;
  gcpCredentialsJsonIsBase64Encoded: boolean;
  gcpCredentialsFilePath: string | null;
  gcpProjectId: string | null;
}

export type Keyed<T> = T & { key: string };

const BASE = '/api/storage';

export async function listStorage(fetchImpl: typeof fetch = fetch): Promise<StorageConfig[]> {
  return getJson<StorageConfig[]>(`${BASE}/list`, fetchImpl);
}

export async function getStorage(key: string, fetchImpl: typeof fetch = fetch): Promise<StorageConfig> {
  return getJson<StorageConfig>(`${BASE}/${encodeURIComponent(key)}`, fetchImpl);
}

export async function deleteStorage(key: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const url = `${BASE}/delete/${encodeURIComponent(key)}`;
  const response = await fetchImpl(url, { method: 'DELETE', credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `DELETE ${url} failed with status ${response.status}.`));
  }
}

export async function createLocalStorage(request: Keyed<LocalStorageRequest>, fetchImpl: typeof fetch = fetch) {
  return sendJson(`${BASE}/local/create`, 'POST', request, fetchImpl);
}

export async function updateLocalStorage(key: string, request: LocalStorageRequest, fetchImpl: typeof fetch = fetch) {
  return sendJson(`${BASE}/local/update/${encodeURIComponent(key)}`, 'PUT', request, fetchImpl);
}

export async function createNetworkStorage(request: Keyed<NetworkStorageRequest>, fetchImpl: typeof fetch = fetch) {
  return sendJson(`${BASE}/network/create`, 'POST', request, fetchImpl);
}

export async function updateNetworkStorage(key: string, request: NetworkStorageRequest, fetchImpl: typeof fetch = fetch) {
  return sendJson(`${BASE}/network/update/${encodeURIComponent(key)}`, 'PUT', request, fetchImpl);
}

export async function createS3CompatibleStorage(
  request: Keyed<S3CompatibleStorageRequest>,
  fetchImpl: typeof fetch = fetch
) {
  return sendJson(`${BASE}/object/s3-compatible/create`, 'POST', request, fetchImpl);
}

export async function updateS3CompatibleStorage(
  key: string,
  request: S3CompatibleStorageRequest,
  fetchImpl: typeof fetch = fetch
) {
  return sendJson(`${BASE}/object/s3-compatible/update/${encodeURIComponent(key)}`, 'PUT', request, fetchImpl);
}

export async function createAzureBlobStorage(request: Keyed<AzureBlobStorageRequest>, fetchImpl: typeof fetch = fetch) {
  return sendJson(`${BASE}/object/azure-blob/create`, 'POST', request, fetchImpl);
}

export async function updateAzureBlobStorage(
  key: string,
  request: AzureBlobStorageRequest,
  fetchImpl: typeof fetch = fetch
) {
  return sendJson(`${BASE}/object/azure-blob/update/${encodeURIComponent(key)}`, 'PUT', request, fetchImpl);
}

export async function createGoogleCloudStorage(
  request: Keyed<GoogleCloudStorageRequest>,
  fetchImpl: typeof fetch = fetch
) {
  return sendJson(`${BASE}/object/google-cloud-storage/create`, 'POST', request, fetchImpl);
}

export async function updateGoogleCloudStorage(
  key: string,
  request: GoogleCloudStorageRequest,
  fetchImpl: typeof fetch = fetch
) {
  return sendJson(`${BASE}/object/google-cloud-storage/update/${encodeURIComponent(key)}`, 'PUT', request, fetchImpl);
}

export function storageMethodLabel(storage: StorageConfig): string {
  if (storage.objectS3Compatible) {
    const provider = storage.objectS3Compatible.provider;
    return provider === 'AwsS3' ? 'AWS S3' : provider === 'MinIo' ? 'MinIO' : 'DigitalOcean Spaces';
  }
  if (storage.objectAzureBlob) {
    return 'Azure Blob';
  }
  if (storage.objectGoogleCloudStorage) {
    return 'Google Cloud Storage';
  }
  if (storage.network) {
    return `Network (${storage.network.protocol.toUpperCase()})`;
  }
  return 'Local filesystem';
}

/**
 * The default local target stores its path relative to the server-side
 * FROSTSTREAM_STORAGE_ROOT environment variable. The API never resolves the
 * token, so render it as a readable name instead of the raw placeholder.
 */
const STORAGE_ROOT_TOKEN = '${FROSTSTREAM_STORAGE_ROOT}';

export function displayLocalPath(path: string): string {
  return path.replaceAll(STORAGE_ROOT_TOKEN, '<storage root>');
}

export function storageSummary(storage: StorageConfig): string {
  if (storage.local) {
    return displayLocalPath(storage.local.path);
  }
  if (storage.network) {
    const { protocol, host, port, basePath } = storage.network;
    return `${protocol.toLowerCase()}://${host}${port ? `:${port}` : ''}${basePath ?? ''}`;
  }
  if (storage.objectS3Compatible) {
    const { bucketName, region, endpoint } = storage.objectS3Compatible;
    return [bucketName, region ?? endpoint].filter(Boolean).join(' · ');
  }
  if (storage.objectAzureBlob) {
    const { containerName, azureAccountName } = storage.objectAzureBlob;
    return [azureAccountName, containerName].filter(Boolean).join(' · ') || 'Azure Blob container';
  }
  if (storage.objectGoogleCloudStorage) {
    const { bucketName, gcpProjectId } = storage.objectGoogleCloudStorage;
    return [bucketName, gcpProjectId].filter(Boolean).join(' · ');
  }
  return storage.method;
}

async function getJson<T>(url: string, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
}

async function sendJson<T = unknown>(url: string, method: string, body: unknown, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, {
    method,
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body)
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `${method} ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
}

async function describeError(response: Response, fallback: string): Promise<string> {
  const text = await response.text();
  if (!text) {
    return fallback;
  }

  try {
    const problem = JSON.parse(text) as { title?: string; detail?: string; error?: string; errors?: Record<string, string[]> };
    const validation = problem.errors ? Object.values(problem.errors).flat().join(' ') : '';
    return [problem.title, problem.detail, problem.error, validation].filter(Boolean).join(' - ') || text || fallback;
  } catch {
    return text;
  }
}
