<script lang="ts">
  import { onDestroy } from 'svelte';
  import { Virtualizer, type VirtualizerHandle } from 'virtua/svelte';
  import { MessageSquare } from '@lucide/svelte';
  import type { ChatMessage } from '$lib/api/liveChat';
  import { ChatPlaybackController } from './ChatPlaybackController.svelte';
  import ChatMessageRow from './ChatMessageRow.svelte';

  let {
    mediaGuid,
    positionSeconds,
    heightPx,
    onSeek
  }: {
    mediaGuid: string;
    /** Current playback position; drives message reveal and seek detection. */
    positionSeconds: number;
    /** Measured height (in px) so the panel's bottom lines up with the video column's action row.
     * Falls back to a fixed viewport-relative height until the parent has measured it. */
    heightPx?: number | null;
    onSeek?: (offsetSeconds: number) => void;
  } = $props();

  /** Distance from the bottom, in px, still counted as "at the bottom". */
  const FOLLOW_THRESHOLD_PX = 40;
  /** How long after a scroll gesture its scroll events are still attributed to the viewer. */
  const GESTURE_WINDOW_MS = 400;
  /** A jump larger than this means the video was seeked rather than played through. */
  const SEEK_THRESHOLD_SECONDS = 2;

  let virtualizer = $state<VirtualizerHandle | undefined>();
  let scrollElement = $state<HTMLDivElement | undefined>();
  /** False while the viewer is reading scrollback; suppresses auto-scroll until they return. */
  let following = $state(true);
  let controller = $state<ChatPlaybackController | null>(null);
  // Whether a scroll event came from the viewer cannot be read off the event itself: virtua's
  // scrollToIndex is async and iterative (it scrolls, awaits measurement of unmeasured rows, then
  // scrolls again), so following the playhead emits its own scroll events across several frames.
  // Tracking the input gestures instead is what separates "the viewer scrolled back" from "we
  // advanced the list".
  let lastGestureAt = 0;
  let pointerDown = false;
  let lastPositionSeconds = 0;

  const messages = $derived<ChatMessage[]>(controller?.visible ?? []);

  // One controller per media item; recreated (and the old one aborted) when the video changes.
  $effect(() => {
    const next = new ChatPlaybackController(mediaGuid);
    controller = next;
    following = true;
    return () => {
      next.dispose();
      controller = null;
    };
  });

  $effect(() => {
    const position = Math.max(0, positionSeconds);
    // Seeking is an explicit "take me to this moment", so it resumes following even if the
    // viewer had scrolled back to read history before jumping.
    if (Math.abs(position - lastPositionSeconds) > SEEK_THRESHOLD_SECONDS) {
      following = true;
    }
    lastPositionSeconds = position;
    controller?.tick(position * 1000);
  });

  // Keep the newest message in view while following. This runs on every reveal (once per frame on
  // a busy chat), so it pins scrollTop directly rather than going through scrollToIndex: that call
  // is async and iterative — it scrolls, awaits measurement of unmeasured rows, then scrolls again
  // — so at this cadence each call would still be settling when the next one started, and the view
  // would visibly trail the playhead. Assigning scrollTop is synchronous and self-correcting.
  $effect(() => {
    const count = messages.length;
    if (!following || count === 0 || !scrollElement) {
      return;
    }
    scrollElement.scrollTop = scrollElement.scrollHeight;
  });

  function distanceFromBottom(): number {
    if (!scrollElement) {
      return 0;
    }
    return scrollElement.scrollHeight - scrollElement.scrollTop - scrollElement.clientHeight;
  }

  /** True while scroll events can still be attributed to a gesture the viewer is making. */
  function viewerIsScrolling(): boolean {
    return pointerDown || Date.now() - lastGestureAt < GESTURE_WINDOW_MS;
  }

  function noteGesture() {
    lastGestureAt = Date.now();
  }

  function handleKeyDown(event: KeyboardEvent) {
    if (
      ['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' '].includes(event.key)
    ) {
      noteGesture();
    }
  }

  function handleScroll() {
    if (distanceFromBottom() < FOLLOW_THRESHOLD_PX) {
      // Back at the bottom — resume following regardless of who scrolled us here.
      following = true;
    } else if (viewerIsScrolling()) {
      // Only the viewer's own gesture parks the list in the scrollback.
      following = false;
    }
  }

  function resumeFollowing() {
    following = true;
    if (virtualizer && messages.length > 0) {
      virtualizer.scrollToIndex(messages.length - 1, { align: 'end' });
    }
  }

  function seekTo(offsetMs: number) {
    onSeek?.(offsetMs / 1000);
  }

  onDestroy(() => controller?.dispose());
</script>

<div
  class={['flex flex-col overflow-hidden rounded-box border-[length:var(--border)] border-base-300/80 bg-base-300', heightPx === null || heightPx === undefined ? 'h-[32rem] xl:h-[calc(100vh-16rem)]' : '']}
  style={heightPx ? `height: ${heightPx}px;` : undefined}
>
  <div class="flex items-center gap-2 border-b-[length:var(--border)] border-base-300/80 px-4 py-2.5">
    <MessageSquare class="h-4 w-4 opacity-70" />
    <h2 class="text-sm font-semibold">Live chat replay</h2>
    {#if controller?.loading}
      <span class="loading loading-spinner loading-xs ml-auto opacity-60"></span>
    {/if}
  </div>

  {#if controller?.error}
    <div class="border-b-[length:var(--border)] border-base-300/80 px-4 py-2 text-xs text-warning">
      {controller.error}
    </div>
  {/if}

  <div class="relative min-h-0 flex-1">
    <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
    <div
      bind:this={scrollElement}
      role="log"
      aria-label="Live chat messages"
      class="h-full overflow-y-auto"
      onscroll={handleScroll}
      onwheel={noteGesture}
      ontouchmove={noteGesture}
      onpointerdown={() => {
        pointerDown = true;
        noteGesture();
      }}
      onpointerup={() => (pointerDown = false)}
      onpointercancel={() => (pointerDown = false)}
      onkeydown={handleKeyDown}
    >
      {#if messages.length === 0}
        <p class="px-4 py-6 text-center text-sm opacity-60">
          {controller?.loading ? 'Loading chat…' : 'No chat messages at this point in the video.'}
        </p>
      {:else}
        <Virtualizer
          bind:this={virtualizer}
          data={messages}
          getKey={(message: ChatMessage) => message.messageId}
        >
          {#snippet children(message: ChatMessage)}
            <ChatMessageRow {message} onSeek={seekTo} />
          {/snippet}
        </Virtualizer>
      {/if}
    </div>

    {#if !following && messages.length > 0}
      <button
        type="button"
        class="btn btn-primary btn-sm absolute inset-x-0 bottom-3 mx-auto w-fit shadow-lg"
        onclick={resumeFollowing}
      >
        Return to current chat
      </button>
    {/if}
  </div>
</div>
