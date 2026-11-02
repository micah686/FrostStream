<script lang="ts">
  import { sendEmpty } from '#lib/api/http.js';

  let busy = $state(false);
  let message = $state<string | null>(null);
  let failed = $state(false);

  async function update() {
    busy = true;
    message = null;
    failed = false;
    try {
      await sendEmpty('/api/admin/media-tools/update', 'POST');
      message = 'Media tools updated successfully. The worker now has the latest yt-dlp, Deno, FFmpeg and ffprobe binaries.';
    } catch (error) {
      failed = true;
      message = error instanceof Error ? error.message : 'Unable to update media tools.';
    } finally {
      busy = false;
    }
  }
</script>

<svelte:head><title>Media tools · Administration · FrostStream</title></svelte:head>

<section class="max-w-3xl space-y-5" aria-labelledby="media-tools-title">
  <div>
    <h2 id="media-tools-title" class="text-xl font-bold text-base-content">Media tools</h2>
    <p class="mt-2 text-sm text-base-content/60">Update the binaries used by download workers. This refreshes yt-dlp and Deno, and updates FFmpeg and ffprobe when they are managed by FrostStream.</p>
  </div>
  <div class="card space-y-4 border-[length:var(--border)] border-base-300 bg-base-100 p-5">
    <p class="text-sm text-base-content/70">The update can take several minutes. Existing downloads keep running; start this when the worker has enough disk space and network access to fetch release files.</p>
    <div>
      <button class="btn btn-primary" type="button" disabled={busy} onclick={() => void update()}>
        {#if busy}<span class="loading loading-spinner loading-sm mr-2"></span>{/if}
        {busy ? 'Updating media tools…' : 'Update binaries'}
      </button>
    </div>
    {#if message}<div class:alert-error={failed} class:alert-success={!failed} class="alert" role="status">{message}</div>{/if}
  </div>
</section>
