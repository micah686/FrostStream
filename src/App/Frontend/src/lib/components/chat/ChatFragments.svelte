<script lang="ts">
  import { emoteImageUrl, type ChatFragment } from '$lib/api/liveChat';

  let { fragments }: { fragments: ChatFragment[] } = $props();

  function hideBrokenEmote(event: Event) {
    if (event.currentTarget instanceof HTMLImageElement) {
      event.currentTarget.hidden = true;
    }
  }
</script>

<span class="break-words">
  {#each fragments as fragment, index (index)}
    {#if fragment.type === 'text'}
      {fragment.text}
    {:else if fragment.type === 'emoji'}
      {fragment.value}
    {:else if fragment.type === 'link'}
      <a
        class="link link-primary"
        href={fragment.url}
        target="_blank"
        rel="noopener noreferrer nofollow ugc">{fragment.text}</a
      >
    {:else if fragment.type === 'emote'}
      {@const src = emoteImageUrl(fragment)}
      {#if src}
        <img
          class="inline-block h-5 w-5 align-text-bottom"
          {src}
          alt={fragment.name}
          title={fragment.name}
          loading="lazy"
          onerror={hideBrokenEmote}
        />
      {:else}
        <span class="opacity-70">{fragment.name}</span>
      {/if}
    {/if}
  {/each}
</span>
