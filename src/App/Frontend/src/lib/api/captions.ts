import { getJson } from '$lib/api/http';

export interface CaptionTrack {
  languageCode: string;
  captionType: string;
  name?: string | null;
  /** Watch-authorized URL for the caption sidecar (converted to WebVTT when needed). */
  url: string;
}

/** Lists durable caption sidecars directly from the playback API, independently of Typesense. */
export async function listCaptionTracks(
  mediaGuid: string,
  fetchImpl: typeof fetch = fetch
): Promise<CaptionTrack[]> {
  return getJson<CaptionTrack[]>(`/api/media/watch/${encodeURIComponent(mediaGuid)}/captions`, fetchImpl);
}
