<script lang="ts">
  import { Cast, ChevronDown } from '@lucide/svelte';
  import ServerCastMenu from './ServerCastMenu.svelte';
  import { canUseBrowserCast, startBrowserCast } from './browserCast';

  interface CaptionLanguage {
    languageCode: string;
    captionType: string;
    name?: string | null;
  }

  let {
    mediaGuid,
    title = null,
    posterUrl = null,
    captionLanguages = [],
    position = 0,
    storageKey = null,
    version = null
  }: {
    mediaGuid: string;
    title?: string | null;
    posterUrl?: string | null;
    captionLanguages?: CaptionLanguage[];
    position?: number;
    storageKey?: string | null;
    version?: number | null;
  } = $props();

  let open = $state(false);
  let protocol = $state<'chromecast' | 'fcast' | null>(null);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let container = $state<HTMLDivElement | null>(null);

  $effect(() => {
    if (!open) {
      protocol = null;
      return;
    }

    const onPointerDown = (event: PointerEvent) => {
      if (container && event.target instanceof Node && !container.contains(event.target)) {
        close();
      }
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        close();
      }
    };

    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  });

  function toggle() {
    open = !open;
    if (!open) {
      protocol = null;
      error = null;
    }
  }

  function close() {
    open = false;
    protocol = null;
    error = null;
  }

  async function castBrowser() {
    busy = true;
    error = null;
    try {
      await startBrowserCast(mediaGuid, title, posterUrl, storageKey, version);
      close();
    } catch (cause) {
      error = cause instanceof Error ? cause.message : 'Casting failed.';
    } finally {
      busy = false;
    }
  }
</script>

<div class="relative" bind:this={container}>
  <button
    type="button"
    onclick={toggle}
    aria-haspopup="menu"
    aria-expanded={open}
    class={[
      'flex items-center gap-1.5 rounded-lg border px-4 py-2 text-xs font-semibold transition',
      open || protocol
        ? 'border-primary/30 bg-primary/10 text-primary hover:bg-primary/10'
        : 'border-base-300 bg-base-200/70 text-base-content/80 hover:bg-base-300'
    ]}
  >
    <Cast class="h-4 w-4" />
    Cast
    <ChevronDown class="h-3.5 w-3.5" />
  </button>

  {#if open}
    <div class="absolute right-0 z-30 mt-2 w-96 rounded-xl border border-base-300 bg-base-200/95 p-3 shadow-2xl shadow-black/50 backdrop-blur">
      {#if error}
        <p class="alert alert-error mb-2 text-xs" role="alert">{error}</p>
      {/if}

      <div class="mb-2 rounded-lg border border-warning/60 bg-warning/10 px-3 py-2 text-xs text-warning">
        <p class="font-semibold uppercase tracking-wide text-warning">Under development</p>
        <p class="mt-1 leading-5 text-warning">
          Cast (Browser) and Cast (Server) are still in progress.
        </p>
      </div>

      {#if protocol === null}
        <div class="space-y-1">
          <button
            type="button"
            onclick={castBrowser}
            disabled={busy || !canUseBrowserCast()}
            class="flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-sm text-base-content/90 transition hover:bg-base-300/70 disabled:opacity-50"
            title={canUseBrowserCast() ? 'Cast from this browser' : 'Browser casting requires HTTPS or localhost'}
          >
            <span>
              <span class="block font-medium">Cast (Browser)</span>
              <span class="block text-[11px] text-base-content/50">Google Cast sender SDK in the page</span>
            </span>
          </button>

          <button
            type="button"
            onclick={() => (protocol = 'chromecast')}
            disabled={busy}
            class="flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-sm text-base-content/90 transition hover:bg-base-300/70 disabled:opacity-50"
          >
            <span>
              <span class="block font-medium">Cast (Server)</span>
              <span class="block text-[11px] text-base-content/50">Chromecast devices discovered by WebAPI</span>
            </span>
          </button>

          <button
            type="button"
            onclick={() => (protocol = 'fcast')}
            disabled={busy}
            class="flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-sm text-base-content/90 transition hover:bg-base-300/70 disabled:opacity-50"
          >
            <span>
              <span class="block font-medium">FCast</span>
              <span class="block text-[11px] text-base-content/50">FCast devices discovered by WebAPI</span>
            </span>
          </button>
        </div>
      {:else}
        <div class="mb-3 flex items-center justify-between gap-2">
          <button
            type="button"
            onclick={() => (protocol = null)}
            class="rounded-md px-2 py-1 text-xs font-semibold text-base-content/60 transition hover:bg-base-300 hover:text-base-content/90"
          >
            Back
          </button>
          <p class="truncate text-xs font-semibold tracking-wide text-base-content/60 uppercase">
            {protocol === 'chromecast' ? 'Cast (Server)' : 'FCast'}
          </p>
          <button
            type="button"
            onclick={close}
            class="rounded-md px-2 py-1 text-xs font-semibold text-base-content/60 transition hover:bg-base-300 hover:text-base-content/90"
          >
            Close
          </button>
        </div>

        <ServerCastMenu
          embedded
          {mediaGuid}
          {title}
          {captionLanguages}
          {position}
          {storageKey}
          {version}
          protocolId={protocol}
          triggerLabel={protocol === 'chromecast' ? 'Cast (Server)' : 'FCast'}
          panelLabel={protocol === 'chromecast' ? 'Cast (Server)' : 'FCast'}
          emptyMessage={
            protocol === 'chromecast'
              ? "No Chromecast devices found on the server's network."
              : "No FCast devices found on the server's network."
          }
        />
      {/if}
    </div>
  {/if}
</div>
