<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { ArrowLeft, CircleAlert } from '@lucide/svelte';
  import { getCookieProfile, upsertCookieProfile, type CookieProfile } from '$lib/api/cookies';

  let { params } = $props();
  let profile = $state<CookieProfile | null>(null);
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let displayName = $state('');
  let site = $state('');
  let content = $state('');
  let saving = $state(false);
  let error = $state<string | null>(null);

  const formValid = $derived(content.trim().length > 0);

  onMount(() => void load());

  async function load() {
    loading = true;
    loadError = null;
    try {
      profile = await getCookieProfile(params.key);
      displayName = profile.displayName ?? '';
      site = profile.site ?? '';
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'Could not load the cookie profile.';
    } finally {
      loading = false;
    }
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!profile || !formValid || saving) return;

    saving = true;
    error = null;
    try {
      await upsertCookieProfile(profile.profileKey, {
        content,
        site: site.trim() || null,
        displayName: displayName.trim() || null
      });
      await goto('/profile/cookie-management');
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not replace the cookie profile.';
    } finally {
      saving = false;
    }
  }

  async function importFile(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    content = await file.text();
    input.value = '';
  }
</script>

<svelte:head><title>{profile?.displayName ?? 'Replace cookies'} · FrostStream</title></svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="cookie-profile-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Profile</p>
    <h1 id="cookie-profile-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">Replace cookie profile</h1>
    <p class="mt-2 text-sm text-base-content/60">Replace the write-only cookies stored for this profile.</p>
  </div>

  {#if loading}
    <div class="mt-16 flex justify-center"><span class="loading loading-spinner loading-md"></span></div>
  {:else if loadError}
    <div class="alert alert-error text-sm" role="alert"><CircleAlert class="mt-0.5 h-4 w-4 shrink-0" /><span>{loadError}</span><a class="btn btn-sm btn-neutral mt-4 text-xs" href="/profile/cookie-management">Back to profile</a></div>
  {:else if profile}
    <form onsubmit={save} class="card space-y-5 border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6">
      {#if error}
        <div class="alert alert-error text-sm" role="alert"><CircleAlert class="mt-0.5 h-4 w-4 shrink-0" /><span>{error}</span></div>
      {/if}
      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <label class="label mb-2 text-sm" for="cookie-profile-key">Key</label>
          <input class="input w-full" id="cookie-profile-key" disabled value={profile.profileKey} />
        </div>
        <div>
          <label class="label mb-2 text-sm" for="cookie-display-name">Display name (optional)</label>
          <input class="input w-full" id="cookie-display-name" maxlength={255} bind:value={displayName} />
        </div>
      </div>
      <div>
        <label class="label mb-2 text-sm" for="cookie-site">Site (optional)</label>
        <input class="input w-full" id="cookie-site" maxlength={255} bind:value={site} placeholder="youtube.com" />
      </div>
      <div>
        <div class="mb-2 flex items-center justify-between">
          <label class="label text-sm" for="cookie-content">Cookie content</label>
          <label class="btn btn-sm btn-neutral text-xs">Import cookies.txt<input type="file" accept=".txt,text/plain" class="hidden" onchange={importFile} /></label>
        </div>
        <textarea class="textarea w-full font-mono text-xs" id="cookie-content" required rows={8} bind:value={content} placeholder={'# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t...'}></textarea>
        <p class="mt-1.5 text-xs text-base-content/40">Paste a Netscape-format export. It replaces the stored cookies and is never shown again.</p>
      </div>
      <div class="flex flex-wrap justify-between gap-2 border-t border-base-300/70 pt-5">
        <a class="btn btn-sm btn-neutral text-xs" href="/profile/cookie-management"><ArrowLeft class="mr-1.5 h-4 w-4" />Back</a>
        <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={saving || !formValid}>{#if saving}<span class="loading loading-spinner loading-xs mr-1.5"></span>{/if}Replace cookies</button>
      </div>
    </form>
  {/if}
</section>
