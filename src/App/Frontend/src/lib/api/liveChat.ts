import { getJson } from '$lib/api/http';

/** One renderable piece of a chat message; archived chats from any platform share this shape. */
export type ChatFragment =
  | { type: 'text'; text: string }
  | { type: 'emoji'; value: string }
  | { type: 'emote'; id: string; name: string; path?: string | null; url?: string | null }
  | { type: 'link'; text: string; url: string };

export type ChatMessageType = 'message' | 'superchat' | 'membership' | 'sticker' | 'system';

/** Wire shape returned by `/api/media/watch/{guid}/chat`. */
interface ChatMessageWire {
  messageId: string;
  videoOffsetMs: number;
  publishedAtUnixMs?: number | null;
  type: string;
  authorExternalId: string;
  authorName: string;
  badges: string[];
  fragmentsJson: string;
  amountText?: string | null;
  currency?: string | null;
  headerColor?: number | null;
  bodyColor?: number | null;
}

export interface ChatMessage {
  messageId: string;
  videoOffsetMs: number;
  publishedAtUnixMs: number | null;
  type: ChatMessageType;
  authorExternalId: string;
  authorName: string;
  badges: string[];
  fragments: ChatFragment[];
  amountText: string | null;
  headerColor: number | null;
  bodyColor: number | null;
}

/** Jump to a playback position after a seek. */
export interface ChatAroundQuery {
  around: number;
  before?: number;
  after?: number;
}

/** Page forward sequentially while playing. */
export interface ChatRangeQuery {
  from: number;
  to: number;
  limit?: number;
}

export type ChatWindowQuery = ChatAroundQuery | ChatRangeQuery;

const KNOWN_TYPES: ChatMessageType[] = ['message', 'superchat', 'membership', 'sticker', 'system'];

/** Builds the watch-authorized URL for an archived custom emote image. */
export function emoteImageUrl(fragment: Extract<ChatFragment, { type: 'emote' }>): string | null {
  if (!fragment.path) {
    return null;
  }
  const fileName = fragment.path.split('/').pop();
  return fileName ? `/api/media/watch/chat/emotes/${encodeURIComponent(fileName)}` : null;
}

/**
 * Fetches one window of chat replay. Pass an AbortSignal so an in-flight window can be dropped
 * when the viewer seeks again before it lands.
 */
export async function fetchChatWindow(
  mediaGuid: string,
  query: ChatWindowQuery,
  signal?: AbortSignal
): Promise<ChatMessage[]> {
  const params = new URLSearchParams();
  if ('around' in query) {
    params.set('around', String(Math.max(0, Math.round(query.around))));
    if (query.before !== undefined) params.set('before', String(query.before));
    if (query.after !== undefined) params.set('after', String(query.after));
  } else {
    params.set('from', String(Math.max(0, Math.round(query.from))));
    params.set('to', String(Math.max(0, Math.round(query.to))));
    if (query.limit !== undefined) params.set('limit', String(query.limit));
  }

  const url = `/api/media/watch/${encodeURIComponent(mediaGuid)}/chat?${params}`;
  const wire = await getJson<ChatMessageWire[]>(url, (input, init) =>
    fetch(input, { ...init, signal })
  );
  return wire.map(toChatMessage);
}

function toChatMessage(wire: ChatMessageWire): ChatMessage {
  return {
    messageId: wire.messageId,
    videoOffsetMs: wire.videoOffsetMs,
    publishedAtUnixMs: wire.publishedAtUnixMs ?? null,
    type: KNOWN_TYPES.includes(wire.type as ChatMessageType)
      ? (wire.type as ChatMessageType)
      : 'message',
    authorExternalId: wire.authorExternalId,
    authorName: wire.authorName,
    badges: wire.badges ?? [],
    fragments: parseFragments(wire.fragmentsJson),
    amountText: wire.amountText ?? null,
    headerColor: wire.headerColor ?? null,
    bodyColor: wire.bodyColor ?? null
  };
}

function parseFragments(json: string): ChatFragment[] {
  if (!json) {
    return [];
  }
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? (parsed as ChatFragment[]) : [];
  } catch {
    return [];
  }
}

/** Renders an ARGB colour from ClickHouse as a CSS colour, or null when unset. */
export function argbToCss(color: number | null, alpha?: number): string | null {
  if (!color) {
    return null;
  }
  const a = alpha ?? ((color >>> 24) & 0xff) / 255;
  const r = (color >>> 16) & 0xff;
  const g = (color >>> 8) & 0xff;
  const b = color & 0xff;
  return `rgba(${r}, ${g}, ${b}, ${a.toFixed(3)})`;
}

/** A membership author badge, resolved from its YouTube tooltip text (e.g. "Member (1 year)"). */
export interface MembershipBadge {
  months: number;
  /** Matches the colour YouTube uses for the loyalty badge at this milestone. */
  color: string;
}

/** Ascending milestone thresholds (in months) and the loyalty-badge colour YouTube pairs with each. */
const MEMBERSHIP_TIERS: { minMonths: number; color: string }[] = [
  { minMonths: 48, color: '#38bdf8' }, // 4+ years: diamond blue
  { minMonths: 36, color: '#eab308' }, // 3 years: yellow / gold
  { minMonths: 24, color: '#cd7f32' }, // 2 years: orange / bronze
  { minMonths: 12, color: '#ef4444' }, // 1 year: red
  { minMonths: 6, color: '#ec4899' }, // 6 months: pink
  { minMonths: 2, color: '#a855f7' }, // 2 months: purple
  { minMonths: 1, color: '#3b82f6' }, // 1 month: blue
  { minMonths: 0, color: '#22c55e' } // new member (under 1 month): green
];

/** Parses a YouTube author-badge tooltip and returns its membership milestone, or null if the
 * badge isn't a membership badge (e.g. "Moderator", "Verified"). */
export function parseMembershipBadge(tooltip: string): MembershipBadge | null {
  const lower = tooltip.toLowerCase();
  if (!lower.includes('member')) {
    return null;
  }
  const years = Number(lower.match(/(\d+)\s*year/)?.[1] ?? 0);
  const months = Number(lower.match(/(\d+)\s*month/)?.[1] ?? 0);
  const totalMonths = years * 12 + months;
  const tier = MEMBERSHIP_TIERS.find((t) => totalMonths >= t.minMonths)!;
  return { months: totalMonths, color: tier.color };
}

/** Formats a video offset as H:MM:SS / M:SS, matching the player's timestamp style. */
export function formatOffset(offsetMs: number): string {
  const total = Math.max(0, Math.floor(offsetMs / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`
    : `${minutes}:${String(seconds).padStart(2, '0')}`;
}
