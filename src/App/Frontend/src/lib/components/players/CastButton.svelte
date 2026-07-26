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
  import { Cast } from '@lucide/svelte';
  import { canUseBrowserCast, startBrowserCast } from './browserCast';

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

  $effect(() => {
    castAvailable = canUseBrowserCast();
  });

  async function startCast() {
    castBusy = true;
    castError = null;
    try {
      await startBrowserCast(mediaGuid, title, posterUrl);
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
        ? 'border-error/30 bg-error/10 text-error hover:bg-error/10'
        : 'border-base-300 bg-base-200/70 text-base-content/80 hover:bg-base-300'
    ]}
  >
    <Cast class="h-4 w-4" />
    Cast
  </button>
{/if}
