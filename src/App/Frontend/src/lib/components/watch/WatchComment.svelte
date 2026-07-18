<script lang="ts">
  import {
    ArrowDownOutline,
    BadgeCheckSolid,
    HeartSolid,
    MapPinSolid,
    ThumbsDownOutline,
    ThumbsUpOutline
  } from 'flowbite-svelte-icons';
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

<article class={['space-y-3', depth > 0 ? 'ml-8 border-l border-slate-800/70 pl-4' : '']}>
  <div class="flex gap-3">
    <span
      class={`relative mt-0.5 grid h-9 w-9 shrink-0 place-items-center overflow-hidden rounded-full bg-gradient-to-br ${accentFor(comment.account.accountName)} text-[10px] font-bold text-white shadow-lg shadow-black/20`}
      aria-hidden="true"
    >
      {initialsFor(comment.account.accountName)}
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
        <p class="mb-1 flex flex-wrap items-center gap-1.5 text-[11px] font-medium text-slate-500">
          <MapPinSolid class="h-3 w-3 text-slate-500" />
          <span>Pinned by {authorHandle}</span>
        </p>
      {/if}

      <p class="flex flex-wrap items-center gap-2 text-xs">
        {#if comment.isUploader}
          <span class="inline-flex items-center gap-1.5 rounded-full border border-blue-400/40 bg-blue-500/20 px-3 py-1 font-semibold text-blue-50 shadow-sm shadow-blue-950/30">
            <span>{comment.account.accountName}</span>
            <BadgeCheckSolid class="h-3.5 w-3.5 shrink-0 text-blue-200" />
          </span>
        {:else}
          <span class="font-semibold text-slate-200">{authorHandle}</span>
        {/if}
        <span class="text-slate-600">{formatRelativeDate(comment.commentTimestamp)}</span>
      </p>

      <p class="mt-1 whitespace-pre-line text-sm leading-6 text-slate-300">{comment.text}</p>

      <div class="mt-2 flex flex-wrap items-center gap-3">
        <div class="flex items-center gap-2 text-xs text-slate-500">
          {#if comment.likeCount != null}
            <span class="inline-flex items-center gap-1">
              <ThumbsUpOutline class="h-3.5 w-3.5" />
              {formatCount(comment.likeCount)}
            </span>
          {/if}
          {#if comment.dislikeCount != null}
            <span class="inline-flex items-center gap-1">
              <ThumbsDownOutline class="h-3.5 w-3.5" />
              {formatCount(comment.dislikeCount)}
            </span>
          {/if}

          {#if comment.isFavorited}
            <span class="relative ml-1 grid h-7 w-7 place-items-center rounded-full ring-1 ring-rose-500/30">
              <span
                class={`absolute inset-0 rounded-full bg-gradient-to-br ${accentFor(comment.account.accountName)} overflow-hidden`}
                aria-hidden="true"
              >
                <span class="absolute inset-0 grid place-items-center text-[9px] font-bold text-white">
                  {initialsFor(comment.account.accountName)}
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
              <HeartSolid class="absolute -bottom-0.5 -right-0.5 h-3.5 w-3.5 rounded-full bg-slate-950 p-0.5 text-rose-400 shadow" />
            </span>
          {/if}
        </div>
      </div>

      {#if hasReplies}
        <button
          type="button"
          onclick={() => (expanded = !expanded)}
          class="mt-3 inline-flex items-center gap-1 text-xs font-semibold text-slate-500 transition hover:text-slate-300"
        >
          <ArrowDownOutline class={['h-3.5 w-3.5 transition-transform', expanded ? 'rotate-180' : '']} />
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
