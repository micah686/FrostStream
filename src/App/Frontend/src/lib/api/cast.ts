import { getJson, sendJson, sendEmpty, ApiRequestError } from './http';

export interface CastDevice {
  id: string;
  protocol: string;
  name: string;
  host: string;
  port: number;
  model?: string | null;
  version?: string | null;
  status?: string | null;
}

export interface CastSessionSnapshot {
  playerState: string;
  currentTime: number;
  durationSeconds?: number | null;
  volumeLevel?: number | null;
  muted?: boolean | null;
  updatedAt: string;
}

export interface CastSession {
  deviceId: string;
  deviceName: string;
  mediaGuid: string;
  title: string;
  snapshot: CastSessionSnapshot;
  tokenExpiresAt: string;
}

export interface StartCastSessionBody {
  mediaGuid: string;
  audioOnly?: boolean;
  format?: string;
  subtitleLanguage?: string | null;
  captionType?: string | null;
  startPositionSeconds?: number | null;
}

/** 202 from session start: the audio rendition is still being prepared server-side. */
export interface CastPreparing {
  preparing: true;
  status: string;
}

export function listCastDevices(refresh = false): Promise<CastDevice[]> {
  return getJson<CastDevice[]>(`/api/cast/devices${refresh ? '?refresh=true' : ''}`);
}

export function listCastSessions(): Promise<CastSession[]> {
  return getJson<CastSession[]>('/api/cast/sessions');
}

export function getCastSession(deviceId: string): Promise<CastSession> {
  return getJson<CastSession>(`/api/cast/sessions/${encodeURIComponent(deviceId)}`);
}

export async function startCastSession(
  deviceId: string,
  body: StartCastSessionBody
): Promise<CastSession | CastPreparing> {
  const response = await fetch(`/api/cast/devices/${encodeURIComponent(deviceId)}/session`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body)
  });

  if (response.status === 202) {
    const details = (await response.json()) as { status?: string };
    return { preparing: true, status: details.status ?? 'preparing' };
  }

  if (!response.ok) {
    const text = await response.text();
    throw new ApiRequestError(text || `Starting the cast session failed (${response.status}).`, response.status);
  }

  return (await response.json()) as CastSession;
}

export function castPlay(deviceId: string): Promise<CastSession> {
  return sendJson<CastSession>(`/api/cast/sessions/${encodeURIComponent(deviceId)}/play`, 'POST', undefined);
}

export function castPause(deviceId: string): Promise<CastSession> {
  return sendJson<CastSession>(`/api/cast/sessions/${encodeURIComponent(deviceId)}/pause`, 'POST', undefined);
}

export function castStop(deviceId: string): Promise<CastSession> {
  return sendJson<CastSession>(`/api/cast/sessions/${encodeURIComponent(deviceId)}/stop`, 'POST', undefined);
}

export function castSeek(deviceId: string, seconds: number): Promise<CastSession> {
  return sendJson<CastSession>(`/api/cast/sessions/${encodeURIComponent(deviceId)}/seek`, 'POST', { seconds });
}

export function castVolume(
  deviceId: string,
  volume: { level?: number; muted?: boolean }
): Promise<CastSession> {
  return sendJson<CastSession>(`/api/cast/sessions/${encodeURIComponent(deviceId)}/volume`, 'POST', volume);
}

export function endCastSession(deviceId: string): Promise<void> {
  return sendEmpty(`/api/cast/sessions/${encodeURIComponent(deviceId)}`, 'DELETE');
}
