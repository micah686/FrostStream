import { formatBytes } from '$lib/media';

export function normalizeState(state: string): string {
  return state.toLowerCase();
}

export function isQueued(state: string): boolean {
  return ['queued', 'downloadqueued'].includes(normalizeState(state));
}

export function isActive(state: string): boolean {
  // DownloadedTemp and Uploaded are mid-flow checkpoints (bytes on worker disk / bytes in
  // storage): the job still has uploads and/or the final DB commit ahead, so they render as
  // active, never as done.
  return [
    'metadatapending',
    'metadataresolved',
    'downloadpending',
    'downloadedtemp',
    'uploadpending',
    'uploaded',
    'commitpending',
    'compensating',
    'cancelling'
  ].includes(normalizeState(state));
}

export function isFailed(state: string): boolean {
  return ['failedtransient', 'failedpermanent', 'deadlettered', 'providerhalted'].includes(normalizeState(state));
}

export function isDone(state: string): boolean {
  // Only true terminal success states — matches the backend's StateGroup.Done filter. The green
  // bar/pill is reserved for the very end of the flow.
  return ['completed', 'alreadydownloaded'].includes(normalizeState(state));
}

export function isCancelled(state: string): boolean {
  return ['cancelled', 'ignored'].includes(normalizeState(state));
}

export function formatOptionalBytes(bytes: number | null | undefined): string {
  return bytes === null || bytes === undefined ? '-' : formatBytes(bytes);
}
