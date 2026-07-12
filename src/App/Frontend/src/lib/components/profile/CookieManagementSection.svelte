<script lang="ts">
  import { onMount } from 'svelte';
  import { Badge, Button, Input, Label, Spinner, Textarea } from 'flowbite-svelte';
  import {
    CookieSolid,
    ExclamationCircleOutline,
    PenOutline,
    PlusOutline,
    TrashBinOutline
  } from 'flowbite-svelte-icons';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import {
    COOKIE_PROFILE_KEY_PATTERN,
    deleteCookieProfile,
    listCookieProfiles,
    upsertCookieProfile,
    type CookieProfile
  } from '$lib/api/cookies';

  const inputClass =
    'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600! focus:border-blue-500! focus:ring-blue-500!';
  const outlineButtonClass =
    'border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!';

  let profiles = $state<CookieProfile[]>([]);
  let loading = $state(true);
  let listError = $state<string | null>(null);

  // Upsert form
  let formOpen = $state(false);
  let formIsUpdate = $state(false);
  let formKey = $state('');
  let formDisplayName = $state('');
  let formSite = $state('');
  let formContent = $state('');
  let formBusy = $state(false);
  let formError = $state<string | null>(null);
  let formMessage = $state<string | null>(null);

  const formKeyValid = $derived(COOKIE_PROFILE_KEY_PATTERN.test(formKey.trim()));
  const formValid = $derived(formKeyValid && formContent.trim().length > 0);

  // Delete
  let deleteTarget = $state<CookieProfile | null>(null);
  let deleteModalOpen = $state(false);

  onMount(() => {
    void load();
  });

  async function load() {
    loading = true;
    listError = null;
    try {
      profiles = await listCookieProfiles();
    } catch (err) {
      listError = err instanceof Error ? err.message : 'Could not load cookie profiles.';
    } finally {
      loading = false;
    }
  }

  function openCreateForm() {
    formIsUpdate = false;
    formKey = '';
    formDisplayName = '';
    formSite = '';
    formContent = '';
    formError = null;
    formMessage = null;
    formOpen = true;
  }

  function openReplaceForm(profile: CookieProfile) {
    formIsUpdate = true;
    formKey = profile.profileKey;
    formDisplayName = profile.displayName ?? '';
    formSite = profile.site ?? '';
    formContent = '';
    formError = null;
    formMessage = null;
    formOpen = true;
  }

  async function save(event: SubmitEvent) {
    event.preventDefault();
    if (!formValid) {
      return;
    }

    formBusy = true;
    formError = null;
    formMessage = null;
    try {
      const saved = await upsertCookieProfile(formKey.trim(), {
        content: formContent,
        site: formSite.trim() || null,
        displayName: formDisplayName.trim() || null
      });
      profiles = [...profiles.filter((item) => item.profileKey !== saved.profileKey), saved].sort((a, b) =>
        a.profileKey.localeCompare(b.profileKey)
      );
      formMessage = `Cookie profile "${saved.profileKey}" saved. The cookie content is stored securely and cannot be viewed again.`;
      formOpen = false;
      formContent = '';
    } catch (err) {
      formError = err instanceof Error ? err.message : 'Could not save the cookie profile.';
    } finally {
      formBusy = false;
    }
  }

  function requestDelete(profile: CookieProfile) {
    deleteTarget = profile;
    deleteModalOpen = true;
  }

  async function confirmDelete() {
    if (!deleteTarget) {
      return;
    }
    const key = deleteTarget.profileKey;
    await deleteCookieProfile(key);
    profiles = profiles.filter((item) => item.profileKey !== key);
    deleteTarget = null;
  }

  async function importFile(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }
    formContent = await file.text();
    input.value = '';
  }

  function formatDate(value: string | null): string {
    if (!value) {
      return 'unknown';
    }
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? 'unknown' : date.toLocaleString();
  }
</script>

<section class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6" aria-labelledby="cookie-management-title">
  <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 id="cookie-management-title" class="text-base font-bold text-slate-100">Cookie management</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
        Store Netscape-formatted cookies for sites that need a signed-in session to download. Cookie contents are
        write-only: once saved they are kept in the secret store and can never be viewed again, only replaced or
        deleted.
      </p>
    </div>
    {#if !formOpen}
      <Button color="dark" class={outlineButtonClass} onclick={openCreateForm}>
        <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
        New cookie profile
      </Button>
    {/if}
  </div>

  {#if listError}
    <div
      class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
      role="alert"
    >
      <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{listError}</span>
    </div>
  {/if}

  {#if formMessage}
    <div class="mt-5 rounded-xl border border-emerald-900/60 bg-emerald-950/35 p-3 text-sm text-emerald-300" role="status">
      {formMessage}
    </div>
  {/if}

  {#if loading}
    <div class="mt-10 flex justify-center">
      <Spinner size="8" />
    </div>
  {:else if profiles.length === 0 && !formOpen}
    <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
      <CookieSolid class="mx-auto h-9 w-9 text-slate-700" />
      <p class="mt-4 text-sm font-semibold text-slate-300">No cookie profiles yet</p>
      <p class="mt-1 text-sm text-slate-500">Add one to download from sites that require a signed-in session.</p>
    </div>
  {:else if profiles.length > 0}
    <div class="mt-5 space-y-2">
      {#each profiles as profile (profile.profileKey)}
        <article
          class="flex min-h-[3.95rem] flex-col gap-3 rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 transition hover:border-slate-600 hover:bg-slate-800/30 sm:flex-row sm:items-center sm:px-4"
        >
          <div class="flex min-w-0 items-center gap-3">
            <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-amber-400">
              <CookieSolid class="h-4.5 w-4.5" />
            </span>
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <h3 class="truncate text-sm font-semibold text-slate-100">
                  {profile.displayName || profile.profileKey}
                </h3>
                <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
                  {profile.profileKey}
                </span>
                {#if profile.site}
                  <Badge rounded color="gray" class="bg-slate-800! px-2! py-0.5! text-[10px]! text-slate-400!">
                    {profile.site}
                  </Badge>
                {/if}
              </div>
              <p class="mt-0.5 truncate text-xs text-slate-400">
                Updated {formatDate(profile.lastUpdated ?? profile.createdAt)}
              </p>
            </div>
          </div>

          <div class="flex shrink-0 gap-2 sm:ml-auto">
            <button
              type="button"
              class="inline-flex h-10 min-w-24 items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200"
              aria-label={`Replace cookies for ${profile.profileKey}`}
              onclick={() => openReplaceForm(profile)}
            >
              <PenOutline class="h-4 w-4" />
              Replace
            </button>
            <button
              type="button"
              class="inline-flex h-10 min-w-10 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200"
              title="Delete cookie profile"
              aria-label={`Delete cookie profile ${profile.profileKey}`}
              onclick={() => requestDelete(profile)}
            >
              <TrashBinOutline class="h-4 w-4" />
            </button>
          </div>
        </article>
      {/each}
    </div>
  {/if}

  {#if formOpen}
    <form onsubmit={save} class="mt-5 space-y-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-4 sm:p-5">
      <h3 class="text-sm font-bold text-slate-100">
        {formIsUpdate ? `Replace cookies for "${formKey}"` : 'New cookie profile'}
      </h3>

      {#if formError}
        <div
          class="flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
          role="alert"
        >
          <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
          <span>{formError}</span>
        </div>
      {/if}

      <div class="grid gap-5 sm:grid-cols-2">
        <div>
          <Label for="cookie-profile-key" class="mb-2 text-sm font-medium text-slate-300">Profile key</Label>
          <Input
            id="cookie-profile-key"
            required
            pattern={'[a-z0-9-]{2,100}'}
            minlength={2}
            maxlength={100}
            disabled={formIsUpdate}
            bind:value={formKey}
            placeholder="youtube-main"
            class="{inputClass} disabled:opacity-60"
          />
          <p class="mt-1.5 text-xs text-slate-600">Lowercase letters, numbers, and hyphens.</p>
        </div>
        <div>
          <Label for="cookie-display-name" class="mb-2 text-sm font-medium text-slate-300">Display name (optional)</Label>
          <Input
            id="cookie-display-name"
            maxlength={255}
            bind:value={formDisplayName}
            placeholder="YouTube main account"
            class={inputClass}
          />
        </div>
      </div>

      <div>
        <Label for="cookie-site" class="mb-2 text-sm font-medium text-slate-300">Site (optional)</Label>
        <Input id="cookie-site" maxlength={255} bind:value={formSite} placeholder="youtube.com" class={inputClass} />
      </div>

      <div>
        <div class="mb-2 flex items-center justify-between">
          <Label for="cookie-content" class="text-sm font-medium text-slate-300">Cookie content</Label>
          <label
            class="cursor-pointer rounded-lg border border-slate-700 px-2.5 py-1 text-[11px] font-semibold text-slate-300 transition hover:border-slate-600 hover:bg-slate-800"
          >
            Import cookies.txt
            <input type="file" accept=".txt,text/plain" class="hidden" onchange={importFile} />
          </label>
        </div>
        <Textarea
          id="cookie-content"
          required
          rows={8}
          bind:value={formContent}
          placeholder={'# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t...'}
          class="{inputClass} font-mono text-xs!"
        />
        <p class="mt-1.5 text-xs text-slate-600">
          Paste the Netscape-format export (e.g. from a "Get cookies.txt" browser extension). It is stored write-only
          and never shown again.
        </p>
      </div>

      <div class="flex flex-wrap justify-end gap-2">
        <Button color="dark" class={outlineButtonClass} disabled={formBusy} onclick={() => (formOpen = false)}>
          Cancel
        </Button>
        <Button
          type="submit"
          color="blue"
          class="border-0! px-4! py-2! text-xs! font-semibold! disabled:opacity-60"
          disabled={formBusy || !formValid}
        >
          {#if formBusy}
            <Spinner size="4" class="mr-1.5" />
          {/if}
          {formIsUpdate ? 'Replace cookies' : 'Save cookie profile'}
        </Button>
      </div>
    </form>
  {/if}
</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete cookie profile"
  message={`Delete cookie profile "${deleteTarget?.profileKey ?? ''}"? The stored cookies are removed permanently, and downloads that reference this profile will run without them.`}
  confirmLabel="Delete profile"
  onConfirm={confirmDelete}
/>
