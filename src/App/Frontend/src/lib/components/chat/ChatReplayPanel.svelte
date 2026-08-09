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
    onSeek
  }: {
    mediaGuid: string;
    /** Current playback position; drives message reveal and seek detection. */
    positionSeconds: number;
    onSeek?: (offsetSeconds: number) => void;
  } = $props();

  let virtualizer = $state<VirtualizerHandle | undefined>();
  let scrollElement = $state<HTMLDivElement | undefined>();
  /** False while the viewer is reading scrollback; suppresses auto-scroll until they return. */
  let following = $state(true);
  let controller = $state<ChatPlaybackController | null>(null);

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
    controller?.tick(Math.max(0, positionSeconds) * 1000);
  });

  // Keep the newest message in view while following.
  $effect(() => {
    const count = messages.length;
    if (!following || count === 0 || !virtualizer) {
      return;
    }
    virtualizer.scrollToIndex(count - 1, { align: 'end' });
  });

  function handleScroll() {
    if (!virtualizer || !scrollElement) {
      return;
    }
    const distanceFromBottom =
      virtualizer.getScrollSize() - virtualizer.getScrollOffset() - virtualizer.getViewportSize();
    following = distanceFromBottom < 40;
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

<div class="flex h-[32rem] flex-col overflow-hidden rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/40 xl:h-[calc(100vh-16rem)]">
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
    <div bind:this={scrollElement} class="h-full overflow-y-auto" onscroll={handleScroll}>
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
