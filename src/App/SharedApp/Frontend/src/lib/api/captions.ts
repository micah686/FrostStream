import { getJson } from '$lib/api/http';

export interface CaptionTrack {
  languageCode: string;
  captionType: string;
  name?: string | null;
  format: string;
  /** 'native' for WebVTT/SRT, 'jassub' for ASS/SSA. */
  renderer: 'native' | 'jassub';
  /** Watch-authorized URL for the caption sidecar (converted to WebVTT when needed). */
  url: string;
  /** Original ASS/SSA source, used by JASSUB to preserve styling and positioning. */
  sourceUrl?: string | null;
}

/** Lists durable caption sidecars directly from the playback API, independently of Typesense. */
export async function listCaptionTracks(
  mediaGuid: string,
  fetchImpl: typeof fetch = fetch
): Promise<CaptionTrack[]> {
  return getJson<CaptionTrack[]>(`/api/media/watch/${encodeURIComponent(mediaGuid)}/captions`, fetchImpl);
}
