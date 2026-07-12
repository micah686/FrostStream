// Shared formatting and placeholder helpers for media cards and the watch page.

export const placeholderAccents = [
  'from-slate-800 to-blue-950',
  'from-purple-950 to-violet-700',
  'from-red-950 to-orange-900',
  'from-blue-950 to-slate-800',
  'from-emerald-950 to-teal-800',
  'from-fuchsia-950 to-pink-800'
];

export function hashOf(value: string): number {
  let hash = 0;
  for (let i = 0; i < value.length; i += 1) {
    hash = (hash * 31 + value.charCodeAt(i)) | 0;
  }
  return Math.abs(hash);
}

export function accentFor(key: string): string {
  return placeholderAccents[hashOf(key) % placeholderAccents.length];
}

export function initialsFor(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) {
    return '?';
  }
  return (words[0][0] + (words.length > 1 ? words[words.length - 1][0] : '')).toUpperCase();
}

export function formatDuration(seconds: number | null | undefined): string | null {
  if (!seconds || seconds <= 0) {
    return null;
  }
  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0
    ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
    : `${m}:${String(s).padStart(2, '0')}`;
}

export function formatCount(count: number | null | undefined): string | null {
  if (count === null || count === undefined) {
    return null;
  }
  if (count >= 1_000_000) {
    return `${(count / 1_000_000).toFixed(1)}M`;
  }
  if (count >= 1_000) {
    return `${(count / 1_000).toFixed(1)}K`;
  }
  return String(count);
}

export function formatViews(count: number | null | undefined): string | null {
  const compact = formatCount(count);
  return compact === null ? null : `${compact} views`;
}

export function formatRelativeDate(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return null;
  }
  const ms = Date.now() - date.getTime();
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 1) {
    return 'just now';
  }
  if (minutes < 60) {
    return `${minutes}m ago`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }
  const days = Math.floor(hours / 24);
  if (days === 1) {
    return 'Yesterday';
  }
  if (days < 30) {
    return `${days} days ago`;
  }
  return date.toLocaleDateString();
}

export function formatBytes(bytes: number): string {
  if (bytes >= 1_099_511_627_776) {
    return `${(bytes / 1_099_511_627_776).toFixed(1)} TB`;
  }
  if (bytes >= 1_073_741_824) {
    return `${(bytes / 1_073_741_824).toFixed(1)} GB`;
  }
  if (bytes >= 1_048_576) {
    return `${(bytes / 1_048_576).toFixed(0)} MB`;
  }
  return `${bytes} B`;
}
