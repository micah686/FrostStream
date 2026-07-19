import { formatBytes } from '$lib/media';

export function normalizeStatus(status: string): string {
  return status.toLowerCase();
}

export function isQueued(status: string): boolean {
  return normalizeStatus(status) === 'queued';
}

export function isActive(status: string): boolean {
  return ['running', 'stopping', 'compensating'].includes(normalizeStatus(status));
}

export function isFailed(status: string): boolean {
  return normalizeStatus(status) === 'failed';
}

export function isDone(status: string): boolean {
  return ['completed', 'completedwithwarnings', 'alreadydownloaded', 'ignored'].includes(normalizeStatus(status));
}

export function isStopped(status: string): boolean {
  return normalizeStatus(status) === 'stopped';
}

export function isTerminal(status: string): boolean {
  return isDone(status) || isFailed(status) || isStopped(status);
}

export function canStart(status: string): boolean {
  return isFailed(status) || isStopped(status);
}

export function canStop(status: string): boolean {
  return isQueued(status) || normalizeStatus(status) === 'running' || normalizeStatus(status) === 'compensating';
}

export function humanizeDownloadName(value: string): string {
  if (!value || value.toLowerCase() === 'none') {
    return 'Not started';
  }

  return value
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z])([A-Z][a-z])/g, '$1 $2')
    .replace(/Json/g, 'JSON');
}

export function formatOptionalBytes(bytes: number | null | undefined): string {
  return bytes === null || bytes === undefined ? '-' : formatBytes(bytes);
}
