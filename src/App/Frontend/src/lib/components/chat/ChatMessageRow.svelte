<script lang="ts">
  import { BadgeCheck, CheckCheck, Shield, UserStar } from '@lucide/svelte';
  import {
    argbToCss,
    formatOffset,
    parseMembershipBadge,
    NEW_MEMBER_COLOR,
    type ChatMessage
  } from '$lib/api/liveChat';
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
  const isOwner = $derived(message.badges.some(isOwnerBadge));
  const headerStyle = $derived(buildStyle(argbToCss(message.headerColor)));
  const bodyStyle = $derived(buildStyle(argbToCss(message.bodyColor)));

  function buildStyle(color: string | null): string | undefined {
    return color ? `background-color: ${color};` : undefined;
  }

  function isOwnerBadge(badge: string): boolean {
    return badge.trim().toLowerCase() === 'owner';
  }

  function isVerifiedBadge(badge: string): boolean {
    return badge.trim().toLowerCase() === 'verified';
  }

  function isModeratorBadge(badge: string): boolean {
    return badge.trim().toLowerCase() === 'moderator';
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
    <div class="rounded-box px-3 py-2 text-black" style={`background-color: ${NEW_MEMBER_COLOR};`}>
      <span class="font-semibold text-black">{message.authorName}</span>
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
              <span class="mr-1 inline-flex h-3.5 w-3.5 shrink-0 align-middle" title={badge}>
                <UserStar class="h-3.5 w-3.5" style={`color: ${membership.color};`} />
              </span>
            {:else if isVerifiedBadge(badge)}
              <span
                class="mr-1 inline-flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded-full align-middle bg-accent text-accent-content"
                title={badge}
              >
                <CheckCheck class="h-2.5 w-2.5" />
              </span>
            {:else if isModeratorBadge(badge)}
              <span
                class="mr-1 inline-flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded-full align-middle bg-secondary text-secondary-content"
                title={badge}
              >
                <Shield class="h-2.5 w-2.5" />
              </span>
            {:else if isOwnerBadge(badge)}
              <!-- The owner marker is rendered around the author name below. -->
            {:else}
              <span class="badge badge-ghost badge-xs mr-1 align-middle" title={badge}>
                {badge.slice(0, 1)}
              </span>
            {/if}
          {/each}
        {/if}
        <span
          class={isOwner
            ? 'badge badge-primary badge-sm mr-1 align-middle font-semibold text-primary-content'
            : 'mr-1 font-semibold opacity-80'}
          title={isOwner ? 'Owner' : undefined}
        >{#if isOwner}<BadgeCheck class="h-3.5 w-3.5" />{/if}{message.authorName}</span>
        <ChatFragments fragments={message.fragments} />
      </div>
    </div>
  {/if}
</div>
