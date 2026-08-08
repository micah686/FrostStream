<script lang="ts">
  import { onMount } from 'svelte';
  import {
    CircleAlert,
    Cookie,
    Pen,
    Plus,
    Trash2
  } from '@lucide/svelte';
  import ConfirmDeleteModal from '$lib/components/admin/ConfirmDeleteModal.svelte';
  import {
    deleteCookieProfile,
    listCookieProfiles,
    type CookieProfile
  } from '$lib/api/cookies';


  let profiles = $state<CookieProfile[]>([]);
  let loading = $state(true);
  let listError = $state<string | null>(null);

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

  function formatDate(value: string | null): string {
    if (!value) {
      return 'unknown';
    }
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? 'unknown' : date.toLocaleString();
  }
</script>

<section class="card border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6" aria-labelledby="cookie-management-title">
  <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h2 id="cookie-management-title" class="text-base font-bold text-base-content">Cookie management</h2>
      <p class="mt-2 max-w-3xl text-sm leading-6 text-base-content/60">
        Store Netscape-formatted cookies for sites that need a signed-in session to download. Cookie contents are
        write-only: once saved they are kept in the secret store and can never be viewed again, only replaced or
        deleted.
      </p>
    </div>
    <a class="btn btn-sm btn-neutral" href="/profile/cookie-management/new">
      <Plus class="mr-1.5 h-3.5 w-3.5" />
      New cookie profile
    </a>
  </div>

  {#if listError}
    <div
      class="alert alert-error mt-5 text-sm"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{listError}</span>
    </div>
  {/if}

  {#if loading}
    <div class="mt-10 flex justify-center">
      <span class="loading loading-spinner loading-md"></span>
    </div>
  {:else if profiles.length === 0}
    <div class="mt-5 rounded-box border-[length:var(--border)] border-base-300/80 bg-base-200/30 p-8 text-center">
      <Cookie class="mx-auto h-9 w-9 text-base-content/30" />
      <p class="mt-4 text-sm font-semibold text-base-content/80">No cookie profiles yet</p>
      <p class="mt-1 text-sm text-base-content/50">Add one to download from sites that require a signed-in session.</p>
    </div>
  {:else if profiles.length > 0}
    <div class="mt-5 space-y-2">
      {#each profiles as profile (profile.profileKey)}
        <article
          class="card flex min-h-[3.95rem] flex-col gap-3 border-[length:var(--border)] border-base-300 bg-base-100 p-3 transition hover:border-base-content/30 hover:bg-base-300/30 sm:flex-row sm:items-center sm:px-4"
        >
          <div class="flex min-w-0 items-center gap-3">
            <span class="grid h-9 w-9 shrink-0 place-items-center rounded-field bg-base-300/70 text-primary">
              <Cookie class="h-4.5 w-4.5" />
            </span>
            <div class="min-w-0">
              <div class="flex min-w-0 flex-wrap items-center gap-2">
                <h3 class="truncate text-sm font-semibold text-base-content">
                  {profile.displayName || profile.profileKey}
                </h3>
                <span class="badge badge-sm badge-accent text-[10px] text-accent-content">
                  {profile.profileKey}
                </span>
                {#if profile.site}
                  <span class="badge badge-sm badge-ghost rounded-full text-[10px]">
                    {profile.site}
                  </span>
                {/if}
              </div>
              <p class="mt-0.5 truncate text-xs text-base-content/60">
                Updated {formatDate(profile.lastUpdated ?? profile.createdAt)}
              </p>
            </div>
          </div>

          <div class="flex shrink-0 gap-2 sm:ml-auto">
            <a
              href={`/profile/cookie-management/${encodeURIComponent(profile.profileKey)}`}
              class="btn btn-sm btn-neutral text-xs"
              aria-label={`Replace cookies for ${profile.profileKey}`}
            >
              <Pen class="h-4 w-4" />
              Replace
            </a>
            <button
              type="button"
              class="btn btn-sm btn-neutral text-xs"
              title="Delete cookie profile"
              aria-label={`Delete cookie profile ${profile.profileKey}`}
              onclick={() => requestDelete(profile)}
            >
              <Trash2 class="mr-1.5 h-4 w-4" />
              Delete
            </button>
          </div>
        </article>
      {/each}
    </div>
  {/if}

</section>

<ConfirmDeleteModal
  bind:open={deleteModalOpen}
  title="Delete cookie profile"
  message={`Delete cookie profile "${deleteTarget?.profileKey ?? ''}"? The stored cookies are removed permanently, and downloads that reference this profile will run without them.`}
  confirmLabel="Delete profile"
  onConfirm={confirmDelete}
/>
