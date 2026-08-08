<script lang="ts">
  import { goto } from '$app/navigation';
  import { ArrowLeft, CircleAlert } from '@lucide/svelte';
  import { COOKIE_PROFILE_KEY_PATTERN, upsertCookieProfile } from '$lib/api/cookies';

  let profileKey = $state('');
  let displayName = $state('');
  let site = $state('');
  let content = $state('');
  let saving = $state(false);
  let error = $state<string | null>(null);

  const keyValid = $derived(COOKIE_PROFILE_KEY_PATTERN.test(profileKey.trim()));
  const formValid = $derived(keyValid && content.trim().length > 0);

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!formValid || saving) return;

    saving = true;
    error = null;
    try {
      await upsertCookieProfile(profileKey.trim(), {
        content,
        site: site.trim() || null,
        displayName: displayName.trim() || null
      });
      await goto('/profile/cookie-management');
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not save the cookie profile.';
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

<svelte:head><title>New cookie profile · FrostStream</title></svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="cookie-profile-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Profile</p>
    <h1 id="cookie-profile-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">New cookie profile</h1>
    <p class="mt-2 text-sm text-base-content/60">
      Store Netscape-formatted cookies for sites that need a signed-in session to download.
    </p>
  </div>

  <form onsubmit={save} class="card space-y-5 border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6">
    {#if error}
      <div class="alert alert-error text-sm" role="alert">
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{error}</span>
      </div>
    {/if}

    <div class="grid gap-5 sm:grid-cols-2">
      <div>
        <label class="label mb-2 text-sm" for="cookie-profile-key">Key</label>
        <input class="input w-full" id="cookie-profile-key" required pattern={'[a-z0-9\\-]{2,100}'} minlength={2} maxlength={100} bind:value={profileKey} placeholder="youtube-main" />
        <p class="mt-1.5 text-xs text-base-content/40">Lowercase letters, numbers, and hyphens.</p>
      </div>
      <div>
        <label class="label mb-2 text-sm" for="cookie-display-name">Display name (optional)</label>
        <input class="input w-full" id="cookie-display-name" maxlength={255} bind:value={displayName} placeholder="YouTube main account" />
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
      <p class="mt-1.5 text-xs text-base-content/40">Paste a Netscape-format export. It is stored write-only and never shown again.</p>
    </div>

    <div class="flex flex-wrap justify-between gap-2">
      <a class="btn btn-sm btn-neutral" href="/profile/cookie-management"><ArrowLeft class="mr-1.5 h-4 w-4" />Back</a>
      <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={saving || !formValid}>
        {#if saving}<span class="loading loading-spinner loading-xs mr-1.5"></span>{/if}Create cookie profile
      </button>
    </div>
  </form>
</section>
