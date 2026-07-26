<script lang="ts">
  import {
    ArrowDown,
    BadgeCheck,
    Heart,
    MapPin,
    ThumbsDown,
    ThumbsUp
  } from '@lucide/svelte';
  import { accentFor, formatCount, formatRelativeDate, initialsFor } from '$lib/media';
  import WatchComment from './WatchComment.svelte';

  export interface WatchCommentNode {
    commentId: string;
    parentCommentId?: string | null;
    text: string;
    commentTimestamp: string;
    likeCount?: number | null;
    dislikeCount?: number | null;
    isFavorited: boolean;
    isPinned: boolean;
    isUploader: boolean;
    account: {
      accountId: number;
      accountName: string;
      accountHandle: string;
      avatarStoragePath?: string | null;
    };
    replies: WatchCommentNode[];
  }

  let { comment, depth = 0 } = $props<{ comment: WatchCommentNode; depth?: number }>();

  let expanded = $state(false);

  const hasReplies = $derived(comment.replies.length > 0);
  const authorHandle = $derived(formatHandle(comment.account.accountHandle || comment.account.accountName));
  const displayName = $derived(comment.account.accountName?.trim() || authorHandle);
  const avatarUrl = $derived(
    comment.account.avatarStoragePath ? `/api/media/watch/accounts/${comment.account.accountId}/avatar` : null
  );

  function formatHandle(value: string): string {
    return value.startsWith('@') ? value : `@${value}`;
  }

  function handleAvatarError(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }
</script>

<article class={['space-y-3', depth > 0 ? 'ml-8 border-l border-base-300/70 pl-4' : '']}>
  <div class="flex gap-3">
    <span
      class={`relative mt-0.5 grid h-9 w-9 shrink-0 place-items-center overflow-hidden rounded-full bg-gradient-to-br ${accentFor(displayName)} text-[10px] font-bold text-white shadow-lg shadow-black/20`}
      aria-hidden="true"
    >
      {initialsFor(displayName)}
      {#if avatarUrl}
        <img
          src={avatarUrl}
          alt=""
          loading="lazy"
          decoding="async"
          class="absolute inset-0 h-full w-full object-cover"
          onerror={handleAvatarError}
        />
      {/if}
    </span>

    <div class="min-w-0 flex-1">
      {#if comment.isPinned}
        <p class="mb-1 flex flex-wrap items-center gap-1.5 text-[11px] font-medium text-base-content/50">
          <MapPin class="h-3 w-3 text-base-content/50" />
          <span>Pinned by {authorHandle}</span>
        </p>
      {/if}

      <p class="flex flex-wrap items-center gap-2 text-xs">
        {#if comment.isUploader}
          <span class="inline-flex items-center gap-1.5 rounded-full border border-primary/40 bg-primary/20 px-3 py-1 font-semibold text-primary shadow-sm shadow-primary/30">
            <span>{displayName}</span>
            <BadgeCheck class="h-3.5 w-3.5 shrink-0 text-primary" />
          </span>
        {:else}
          <span class="font-semibold text-base-content/90">{authorHandle}</span>
        {/if}
        <span class="text-base-content/40">{formatRelativeDate(comment.commentTimestamp)}</span>
      </p>

      <p class="mt-1 whitespace-pre-line text-sm leading-6 text-base-content/80">{comment.text}</p>

      <div class="mt-2 flex flex-wrap items-center gap-3">
        <div class="flex items-center gap-2 text-xs text-base-content/50">
          {#if comment.likeCount != null}
            <span class="inline-flex items-center gap-1">
              <ThumbsUp class="h-3.5 w-3.5" />
              {formatCount(comment.likeCount)}
            </span>
          {/if}
          {#if comment.dislikeCount != null}
            <span class="inline-flex items-center gap-1">
              <ThumbsDown class="h-3.5 w-3.5" />
              {formatCount(comment.dislikeCount)}
            </span>
          {/if}

          {#if comment.isFavorited}
            <span class="relative ml-1 grid h-7 w-7 place-items-center rounded-full ring-1 ring-rose-500/30">
              <span
                class={`absolute inset-0 rounded-full bg-gradient-to-br ${accentFor(displayName)} overflow-hidden`}
                aria-hidden="true"
              >
                <span class="absolute inset-0 grid place-items-center text-[9px] font-bold text-base-content">
                  {initialsFor(displayName)}
                </span>
                {#if avatarUrl}
                  <img
                    src={avatarUrl}
                    alt=""
                    loading="lazy"
                    decoding="async"
                    class="absolute inset-0 h-full w-full object-cover"
                    onerror={handleAvatarError}
                  />
                {/if}
              </span>
              <Heart class="absolute -bottom-0.5 -right-0.5 h-3.5 w-3.5 rounded-full bg-base-200 p-0.5 text-rose-400 shadow" />
            </span>
          {/if}
        </div>
      </div>

      {#if hasReplies}
        <button
          type="button"
          onclick={() => (expanded = !expanded)}
          class="mt-3 inline-flex items-center gap-1 text-xs font-semibold text-base-content/50 transition hover:text-base-content/80"
        >
          <ArrowDown class={['h-3.5 w-3.5 transition-transform', expanded ? 'rotate-180' : '']} />
          {comment.replies.length} {comment.replies.length === 1 ? 'reply' : 'replies'}
        </button>
      {/if}
    </div>
  </div>

  {#if expanded}
    <div class="space-y-5">
      {#each comment.replies as reply (reply.commentId)}
        <WatchComment comment={reply} depth={depth + 1} />
      {/each}
    </div>
  {/if}
</article>
