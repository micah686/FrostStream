import { ApiRequestError, getJson, sendEmpty, sendJson } from './http';

export type NoteTargetType = 'video' | 'playlist' | 'channel';

export interface UserNote {
  targetType: NoteTargetType;
  targetId: string;
  note: string;
  targetTitle: string | null;
  targetSubtitle: string | null;
  createdAt: string | null;
  updatedAt: string | null;
}

interface RawUserNote {
  targetType?: string | null;
  targetId?: string | number | null;
  note?: string | null;
  text?: string | null;
  content?: string | null;
  targetTitle?: string | null;
  title?: string | null;
  targetSubtitle?: string | null;
  subtitle?: string | null;
  createdAt?: string | null;
  updatedAt?: string | null;
}

interface RawNotesSearch {
  items?: RawUserNote[];
  notes?: RawUserNote[];
  page?: number;
  totalCount?: number;
  hasMore?: boolean;
}

export interface NotesSearchPage {
  items: UserNote[];
  page: number;
  totalCount: number;
  hasMore: boolean;
}

export interface NotesSearchOptions {
  query?: string;
  targetType?: NoteTargetType | 'all';
  page?: number;
  pageSize?: number;
}

export async function getNote(
  targetType: NoteTargetType,
  targetId: string,
  fetchImpl: typeof fetch = fetch
): Promise<UserNote | null> {
  try {
    const raw = await getJson<RawUserNote | null>(noteUrl(targetType, targetId), fetchImpl);
    return raw ? normalizeNote(raw, targetType, targetId) : null;
  } catch (err) {
    // The API answers 404 when no note exists for the target; that is a normal empty result.
    if (err instanceof ApiRequestError && err.status === 404) {
      return null;
    }
    throw err;
  }
}

export async function saveNote(
  targetType: NoteTargetType,
  targetId: string,
  note: string,
  fetchImpl: typeof fetch = fetch
): Promise<UserNote> {
  const raw = await sendJson<RawUserNote>(noteUrl(targetType, targetId), 'PUT', { note }, fetchImpl);
  return normalizeNote(raw, targetType, targetId);
}

export async function deleteNote(
  targetType: NoteTargetType,
  targetId: string,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  await sendEmpty(noteUrl(targetType, targetId), 'DELETE', fetchImpl);
}

export async function searchNotes(
  options: NotesSearchOptions = {},
  fetchImpl: typeof fetch = fetch
): Promise<NotesSearchPage> {
  const params = new URLSearchParams();
  const query = options.query?.trim();
  if (query) params.set('q', query);
  if (options.targetType && options.targetType !== 'all') params.set('targetType', options.targetType);
  const page = Math.max(1, options.page ?? 1);
  const pageSize = Math.max(1, options.pageSize ?? 50);
  params.set('pageSize', String(pageSize));
  params.set('pageOffset', String((page - 1) * pageSize));

  const endpoint = query ? '/api/user/notes/search' : '/api/user/notes';
  const raw = await getJson<RawNotesSearch | RawUserNote[]>(`${endpoint}?${params}`, fetchImpl);
  if (Array.isArray(raw)) {
    return {
      items: raw.map((item) => normalizeNote(item)),
      page,
      totalCount: raw.length,
      hasMore: false
    };
  }

  const items = raw.items ?? raw.notes ?? [];
  return {
    items: items.map((item) => normalizeNote(item)),
    page: raw.page ?? page,
    totalCount: raw.totalCount ?? items.length,
    hasMore: raw.hasMore ?? false
  };
}

function noteUrl(targetType: NoteTargetType, targetId: string): string {
  return `/api/user/notes/${encodeURIComponent(targetType)}/${encodeURIComponent(targetId)}`;
}

function normalizeNote(raw: RawUserNote, fallbackType: NoteTargetType = 'video', fallbackId = ''): UserNote {
  const targetType = normalizeTargetType(raw.targetType, fallbackType);
  return {
    targetType,
    targetId: raw.targetId == null ? fallbackId : String(raw.targetId),
    note: raw.note ?? raw.text ?? raw.content ?? '',
    targetTitle: raw.targetTitle ?? raw.title ?? null,
    targetSubtitle: raw.targetSubtitle ?? raw.subtitle ?? null,
    createdAt: raw.createdAt ?? null,
    updatedAt: raw.updatedAt ?? null
  };
}

function normalizeTargetType(value: string | null | undefined, fallback: NoteTargetType): NoteTargetType {
  const normalized = value?.toLowerCase();
  return normalized === 'playlist' || normalized === 'channel' || normalized === 'video' ? normalized : fallback;
}
