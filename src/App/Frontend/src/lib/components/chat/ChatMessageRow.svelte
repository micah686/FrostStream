<script lang="ts">
  import { CircleStar } from '@lucide/svelte';
  import { argbToCss, formatOffset, parseMembershipBadge, type ChatMessage } from '$lib/api/liveChat';
  import ChatFragments from './ChatFragments.svelte';

  let {
    message,
    onSeek
  }: {
    message: ChatMessage;
    onSeek?: (offsetMs: number) => void;
  } = $props();

  const isPaid = $derived(message.type === 'superchat' || message.type === 'sticker');
  const isMembership = $derived(message.type === 'membership');
  const isSystem = $derived(message.type === 'system');
  const headerStyle = $derived(buildStyle(argbToCss(message.headerColor)));
  const bodyStyle = $derived(buildStyle(argbToCss(message.bodyColor)));

  function buildStyle(color: string | null): string | undefined {
    return color ? `background-color: ${color};` : undefined;
  }
</script>

<div class="px-3 py-1 text-sm leading-6">
  {#if isPaid}
    <div class="overflow-hidden rounded-box border-[length:var(--border)] border-base-300/70">
      <div class="flex items-center justify-between gap-2 px-3 py-1.5" style={headerStyle}>
        <span class="truncate font-semibold">{message.authorName}</span>
        {#if message.amountText}
          <span class="shrink-0 font-bold">{message.amountText}</span>
        {/if}
      </div>
      {#if message.fragments.length > 0}
        <div class="px-3 py-2" style={bodyStyle}>
          <ChatFragments fragments={message.fragments} />
        </div>
      {/if}
    </div>
  {:else if isMembership}
    <div class="rounded-box bg-success/15 px-3 py-2">
      <span class="font-semibold text-success">{message.authorName}</span>
      {#if message.fragments.length > 0}
        <span class="ml-1"><ChatFragments fragments={message.fragments} /></span>
      {/if}
    </div>
  {:else if isSystem}
    <div class="rounded-box bg-base-300/40 px-3 py-2 text-xs opacity-80">
      <ChatFragments fragments={message.fragments} />
    </div>
  {:else}
    <div class="flex gap-2">
      {#if onSeek}
        <button
          type="button"
          class="shrink-0 pt-0.5 font-mono text-xs tabular-nums opacity-50 hover:opacity-100"
          title="Jump to this moment"
          onclick={() => onSeek?.(message.videoOffsetMs)}
        >
          {formatOffset(message.videoOffsetMs)}
        </button>
      {/if}
      <div class="min-w-0">
        {#if message.badges.length > 0}
          {#each message.badges as badge (badge)}
            {@const membership = parseMembershipBadge(badge)}
            {#if membership}
              <span
                class="mr-1 inline-flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded-full align-middle text-black"
                style={`background-color: ${membership.color};`}
                title={badge}
              >
                <CircleStar class="h-3.5 w-3.5" />
              </span>
            {:else}
              <span class="badge badge-ghost badge-xs mr-1 align-middle" title={badge}>
                {badge.slice(0, 1)}
              </span>
            {/if}
          {/each}
        {/if}
        <span class="mr-1 font-semibold opacity-80">{message.authorName}</span>
        <ChatFragments fragments={message.fragments} />
      </div>
    </div>
  {/if}
</div>
