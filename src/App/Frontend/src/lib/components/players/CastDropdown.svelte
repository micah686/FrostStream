<script lang="ts">
  import { ChevronDownOutline } from 'flowbite-svelte-icons';
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
    position = 0
  }: {
    mediaGuid: string;
    title?: string | null;
    posterUrl?: string | null;
    captionLanguages?: CaptionLanguage[];
    position?: number;
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
      await startBrowserCast(mediaGuid, title, posterUrl);
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
        ? 'border-blue-900/60 bg-blue-950/40 text-blue-300 hover:bg-blue-950/60'
        : 'border-slate-800 bg-slate-900/70 text-slate-300 hover:bg-slate-800'
    ]}
  >
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="h-4 w-4" aria-hidden="true">
      <path d="M2 16.1A5 5 0 0 1 5.9 20M2 12.05A9 9 0 0 1 9.95 20M2 8V6a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-6" />
      <circle cx="2" cy="20" r="0.5" fill="currentColor" />
    </svg>
    Cast
    <ChevronDownOutline class="h-3.5 w-3.5" />
  </button>

  {#if open}
    <div class="absolute right-0 z-30 mt-2 w-96 rounded-xl border border-slate-800 bg-slate-950/95 p-3 shadow-2xl shadow-black/50 backdrop-blur">
      {#if error}
        <p class="mb-2 rounded-lg border border-red-900/60 bg-red-950/30 px-3 py-2 text-xs text-red-300">{error}</p>
      {/if}

      {#if protocol === null}
        <div class="space-y-1">
          <button
            type="button"
            onclick={castBrowser}
            disabled={busy || !canUseBrowserCast()}
            class="flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-sm text-slate-200 transition hover:bg-slate-800/70 disabled:opacity-50"
            title={canUseBrowserCast() ? 'Cast from this browser' : 'Browser casting requires HTTPS or localhost'}
          >
            <span>
              <span class="block font-medium">Cast (Browser)</span>
              <span class="block text-[11px] text-slate-500">Google Cast sender SDK in the page</span>
            </span>
          </button>

          <button
            type="button"
            onclick={() => (protocol = 'chromecast')}
            disabled={busy}
            class="flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-sm text-slate-200 transition hover:bg-slate-800/70 disabled:opacity-50"
          >
            <span>
              <span class="block font-medium">Cast (Server)</span>
              <span class="block text-[11px] text-slate-500">Chromecast devices discovered by WebAPI</span>
            </span>
          </button>

          <button
            type="button"
            onclick={() => (protocol = 'fcast')}
            disabled={busy}
            class="flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-sm text-slate-200 transition hover:bg-slate-800/70 disabled:opacity-50"
          >
            <span>
              <span class="block font-medium">FCast</span>
              <span class="block text-[11px] text-slate-500">FCast devices discovered by WebAPI</span>
            </span>
          </button>
        </div>
      {:else}
        <div class="mb-3 flex items-center justify-between gap-2">
          <button
            type="button"
            onclick={() => (protocol = null)}
            class="rounded-md px-2 py-1 text-xs font-semibold text-slate-400 transition hover:bg-slate-800 hover:text-slate-200"
          >
            Back
          </button>
          <p class="truncate text-xs font-semibold tracking-wide text-slate-400 uppercase">
            {protocol === 'chromecast' ? 'Cast (Server)' : 'FCast'}
          </p>
          <button
            type="button"
            onclick={close}
            class="rounded-md px-2 py-1 text-xs font-semibold text-slate-400 transition hover:bg-slate-800 hover:text-slate-200"
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
