<script lang="ts">
  /**
   * Chromecast sender button. Loads the Google Cast sender SDK, and when a cast device is
   * available casts the progressive stream URL authenticated with a short-lived cast token
   * (the device has no session cookies, so the token rides the URL). The page origin must be
   * reachable from the cast device; override with PUBLIC_CAST_BASE_URL when it is not.
   *
   * Requires a secure context (HTTPS or localhost) — the Cast SDK does not initialize on
   * plain-HTTP origins, in which case the button simply stays hidden.
   */
  import { env } from '$env/dynamic/public';

  let {
    mediaGuid,
    title = null,
    posterUrl = null
  }: {
    mediaGuid: string;
    title?: string | null;
    posterUrl?: string | null;
  } = $props();

  let castAvailable = $state(false);
  let castBusy = $state(false);
  let castError = $state<string | null>(null);

  const SDK_URL = 'https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1';

  /* eslint-disable @typescript-eslint/no-explicit-any */
  function castFramework(): any {
    return (window as any).cast?.framework ?? null;
  }

  function chromeCast(): any {
    return (window as any).chrome?.cast ?? null;
  }

  $effect(() => {
    if (typeof window === 'undefined' || !window.isSecureContext) {
      return;
    }

    (window as any).__onGCastApiAvailable = (isAvailable: boolean) => {
      if (!isAvailable) {
        return;
      }
      const framework = castFramework();
      framework.CastContext.getInstance().setOptions({
        receiverApplicationId: chromeCast().media.DEFAULT_MEDIA_RECEIVER_APP_ID,
        autoJoinPolicy: chromeCast().AutoJoinPolicy.ORIGIN_SCOPED
      });
      castAvailable = true;
    };

    if (!document.querySelector(`script[src="${SDK_URL}"]`)) {
      const script = document.createElement('script');
      script.src = SDK_URL;
      script.async = true;
      document.head.appendChild(script);
    } else if (castFramework()) {
      castAvailable = true;
    }
  });

  async function startCast() {
    castBusy = true;
    castError = null;
    try {
      const tokenResponse = await fetch(`/api/watch/${mediaGuid}/cast-token`, { method: 'POST' });
      if (!tokenResponse.ok) {
        throw new Error(`Cast token request failed (${tokenResponse.status}).`);
      }
      const { token } = (await tokenResponse.json()) as { token: string };

      const base = env.PUBLIC_CAST_BASE_URL || window.location.origin;
      const mediaUrl = `${base}/api/watch/${mediaGuid}?castToken=${encodeURIComponent(token)}`;

      // The device streams the original file; probe its content type through the session.
      let contentType = 'video/mp4';
      try {
        const head = await fetch(`/api/watch/${mediaGuid}`, { method: 'HEAD' });
        contentType = head.headers.get('content-type') ?? contentType;
      } catch {
        // Fall back to video/mp4; the default receiver sniffs most formats anyway.
      }

      const context = castFramework().CastContext.getInstance();
      if (!context.getCurrentSession()) {
        await context.requestSession();
      }
      const session = context.getCurrentSession();
      if (!session) {
        return; // User dismissed the device picker.
      }

      const media = chromeCast().media;
      const mediaInfo = new media.MediaInfo(mediaUrl, contentType);
      mediaInfo.metadata = new media.GenericMediaMetadata();
      if (title) {
        mediaInfo.metadata.title = title;
      }
      if (posterUrl) {
        mediaInfo.metadata.images = [
          { url: `${base}${posterUrl}?castToken=${encodeURIComponent(token)}` }
        ];
      }

      await session.loadMedia(new media.LoadRequest(mediaInfo));
    } catch (error) {
      castError = error instanceof Error ? error.message : 'Casting failed.';
    } finally {
      castBusy = false;
    }
  }
</script>

{#if castAvailable}
  <button
    type="button"
    onclick={startCast}
    disabled={castBusy}
    title={castError ?? 'Cast to a device'}
    class={[
      'flex items-center gap-1.5 rounded-lg border px-4 py-2 text-xs font-semibold transition disabled:opacity-60',
      castError
        ? 'border-red-900/60 bg-red-950/30 text-red-300 hover:bg-red-950/50'
        : 'border-slate-800 bg-slate-900/70 text-slate-300 hover:bg-slate-800'
    ]}
  >
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="h-4 w-4" aria-hidden="true">
      <path d="M2 16.1A5 5 0 0 1 5.9 20M2 12.05A9 9 0 0 1 9.95 20M2 8V6a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-6" />
      <circle cx="2" cy="20" r="0.5" fill="currentColor" />
    </svg>
    Cast
  </button>
{/if}
