import { env } from '$env/dynamic/public';

const SDK_URL = 'https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1';

type CastGlobal = {
  cast?: {
    framework?: {
      CastContext: {
        getInstance(): {
          setOptions(options: {
            receiverApplicationId: string;
            autoJoinPolicy: string;
          }): void;
          getCurrentSession(): {
            loadMedia(request: unknown): Promise<void>;
          } | null;
          requestSession(): Promise<void>;
        };
      };
    };
  };
  chrome?: {
    cast?: {
      media: {
        DEFAULT_MEDIA_RECEIVER_APP_ID: string;
        MediaInfo: new (url: string, contentType: string) => {
          metadata: {
            title?: string;
            images?: { url: string }[];
          };
        };
        GenericMediaMetadata: new () => { title?: string; images?: { url: string }[] };
        LoadRequest: new (mediaInfo: unknown) => unknown;
      };
      AutoJoinPolicy: {
        ORIGIN_SCOPED: string;
      };
    };
  };
  __onGCastApiAvailable?: (isAvailable: boolean) => void;
};

function globals(): CastGlobal {
  return window as unknown as CastGlobal;
}

function castFramework() {
  return globals().cast?.framework ?? null;
}

function chromeCast() {
  return globals().chrome?.cast ?? null;
}

function initializeCastContext() {
  const framework = castFramework();
  const cast = chromeCast();
  if (!framework || !cast) {
    return false;
  }

  framework.CastContext.getInstance().setOptions({
    receiverApplicationId: cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID,
    autoJoinPolicy: cast.AutoJoinPolicy.ORIGIN_SCOPED
  });
  return true;
}

async function waitForCastApi(): Promise<void> {
  if (initializeCastContext()) {
    return;
  }

  await new Promise<void>((resolve, reject) => {
    const timeout = window.setTimeout(() => {
      reject(new Error('The Google Cast SDK did not initialize.'));
    }, 10_000);

    globals().__onGCastApiAvailable = (isAvailable: boolean) => {
      window.clearTimeout(timeout);
      if (!isAvailable) {
        reject(new Error('The Google Cast SDK is unavailable.'));
        return;
      }

      if (!initializeCastContext()) {
        reject(new Error('The Google Cast SDK failed to initialize.'));
        return;
      }
      resolve();
    };

    if (!document.querySelector(`script[src="${SDK_URL}"]`)) {
      const script = document.createElement('script');
      script.src = SDK_URL;
      script.async = true;
      script.onerror = () => {
        window.clearTimeout(timeout);
        reject(new Error('Failed to load the Google Cast SDK.'));
      };
      document.head.appendChild(script);
    }
  });
}

export function canUseBrowserCast(): boolean {
  return typeof window !== 'undefined' && window.isSecureContext;
}

export async function startBrowserCast(
  mediaGuid: string,
  title: string | null,
  posterUrl: string | null
): Promise<void> {
  if (!canUseBrowserCast()) {
    throw new Error('Browser casting requires a secure context.');
  }

  await waitForCastApi();
  const tokenResponse = await fetch(`/api/watch/${mediaGuid}/cast-token`, { method: 'POST' });
  if (!tokenResponse.ok) {
    throw new Error(`Cast token request failed (${tokenResponse.status}).`);
  }

  const { token } = (await tokenResponse.json()) as { token: string };
  const base = env.PUBLIC_CAST_BASE_URL || window.location.origin;
  const mediaUrl = `${base}/api/watch/${mediaGuid}?castToken=${encodeURIComponent(token)}`;

  let contentType = 'video/mp4';
  try {
    const head = await fetch(`/api/watch/${mediaGuid}`, { method: 'HEAD' });
    contentType = head.headers.get('content-type') ?? contentType;
  } catch {
    // Fall back to video/mp4; the default receiver sniffs most formats anyway.
  }

  const framework = castFramework();
  const cast = chromeCast();
  if (!framework || !cast) {
    throw new Error('The Google Cast SDK failed to initialize.');
  }

  const session = framework.CastContext.getInstance().getCurrentSession() ?? (await requestSession(framework));
  if (!session) {
    return;
  }

  const media = cast.media;
  const mediaInfo = new media.MediaInfo(mediaUrl, contentType);
  mediaInfo.metadata = new media.GenericMediaMetadata();
  if (title) {
    mediaInfo.metadata.title = title;
  }
  if (posterUrl) {
    mediaInfo.metadata.images = [{ url: `${base}${posterUrl}?castToken=${encodeURIComponent(token)}` }];
  }

  await session.loadMedia(new media.LoadRequest(mediaInfo));
}

async function requestSession(framework: NonNullable<ReturnType<typeof castFramework>>) {
  await framework.CastContext.getInstance().requestSession();
  return framework.CastContext.getInstance().getCurrentSession();
}
