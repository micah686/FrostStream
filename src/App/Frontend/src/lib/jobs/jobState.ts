import { formatBytes } from '$lib/media';

export function normalizeState(state: string): string {
  return state.toLowerCase();
}

export function isQueued(state: string): boolean {
  return ['queued', 'downloadqueued'].includes(normalizeState(state));
}

export function isActive(state: string): boolean {
  return [
    'metadatapending',
    'metadataresolved',
    'downloadpending',
    'uploadpending',
    'commitpending',
    'compensating',
    'cancelling'
  ].includes(normalizeState(state));
}

export function isFailed(state: string): boolean {
  return ['failedtransient', 'failedpermanent', 'deadlettered', 'providerhalted'].includes(normalizeState(state));
}

export function isDone(state: string): boolean {
  return ['uploaded', 'completed', 'alreadydownloaded'].includes(normalizeState(state));
}

export function isCancelled(state: string): boolean {
  return ['cancelled', 'ignored'].includes(normalizeState(state));
}

export function formatOptionalBytes(bytes: number | null | undefined): string {
  return bytes === null || bytes === undefined ? '-' : formatBytes(bytes);
}
