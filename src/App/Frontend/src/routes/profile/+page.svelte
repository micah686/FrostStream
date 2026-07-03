<script lang="ts">
  import { onMount } from 'svelte';
  import { Badge, Button, Spinner } from 'flowbite-svelte';
  import {
    AdjustmentsHorizontalOutline,
    ArchiveOutline,
    ArrowRightToBracketOutline,
    BellOutline,
    CogOutline,
    CookieSolid,
    ExclamationCircleOutline,
    EyeOutline,
    ListMusicOutline,
    MusicAltOutline,
    PlusOutline,
    TrashBinOutline,
    UserOutline,
    UsersGroupOutline
  } from 'flowbite-svelte-icons';
  import {
    deleteDownloadConfigSet,
    listDownloadConfigSets,
    type DownloadConfigSet
  } from '$lib/api/downloadConfigSets';
  import CookieManagementSection from '$lib/components/profile/CookieManagementSection.svelte';

  type IconComponent = typeof UserOutline;

  interface ProfileSection {
    label: string;
    icon: IconComponent;
  }

  let { data } = $props();

  let configSets = $state<DownloadConfigSet[]>([]);
  let configSetsLoading = $state(true);
  let configSetsError = $state<string | null>(null);
  let deletingKey = $state<string | null>(null);

  const authLabel = $derived(data.singleUser ? 'Owner' : 'Signed in');
  const sessionLabel = $derived(data.singleUser ? 'local profile' : 'FrostStream account');
  const expiresLabel = $derived(data.expiresAt ? new Date(data.expiresAt).toLocaleString() : null);

  const sections: ProfileSection[] = [
    { label: 'Overview', icon: UserOutline },
    { label: 'Config sets', icon: AdjustmentsHorizontalOutline },
    { label: 'Cookie management', icon: CookieSolid },
    { label: 'Notifications', icon: BellOutline },
    { label: 'Playlists', icon: ListMusicOutline }
  ];

  let activeSection = $state('Config sets');

  onMount(() => {
    void loadConfigSets();
  });

  async function loadConfigSets() {
    configSetsLoading = true;
    configSetsError = null;
    try {
      configSets = await listDownloadConfigSets();
    } catch (err) {
      configSetsError = err instanceof Error ? err.message : 'Could not load config sets.';
    } finally {
      configSetsLoading = false;
    }
  }

  async function deleteConfigSet(config: DownloadConfigSet) {
    const confirmed = window.confirm(`Delete config set "${config.name}"? This will not affect existing jobs.`);
    if (!confirmed) {
      return;
    }

    deletingKey = config.key;
    configSetsError = null;
    try {
      await deleteDownloadConfigSet(config.key);
      configSets = configSets.filter((item) => item.key !== config.key);
    } catch (err) {
      configSetsError = err instanceof Error ? err.message : 'Could not delete the config set.';
    } finally {
      deletingKey = null;
    }
  }

  function configSummary(config: DownloadConfigSet): string {
    return [
      config.storageKey ?? 'default storage',
      config.cookieProfileKey ? `cookie ${config.cookieProfileKey}` : null,
      `priority ${config.priority}`,
      config.encodeForPlaylist ? `${config.audioFormat} playlist audio` : null,
      config.fetchComments ? 'fetch comments' : null,
      config.ignoreKeywords.length > 0 ? `${config.ignoreKeywords.length} ignore ${config.ignoreKeywords.length === 1 ? 'keyword' : 'keywords'}` : null
    ].filter(Boolean).join(' · ');
  }

  function configIcon(config: DownloadConfigSet): IconComponent {
    return config.encodeForPlaylist ? MusicAltOutline : config.ytDlpOptions ? AdjustmentsHorizontalOutline : ArchiveOutline;
  }
</script>

<svelte:head>
  <title>Profile · FrostStream</title>
</svelte:head>

<section class="min-h-[calc(100vh-7rem)]" aria-labelledby="profile-title">
  <div class="flex flex-col gap-5 sm:flex-row sm:items-start sm:justify-between">
    <div class="min-w-0">
      <h1 id="profile-title" class="text-2xl font-bold tracking-tight text-slate-100">Your profile</h1>
      <p class="mt-2 text-sm text-slate-400">
        {data.user.name} · {authLabel} · Signed in to FrostStream
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      {#if !data.singleUser}
        <Button
          href="/auth/logout"
          color="dark"
          class="border-slate-700! bg-[#0f1420]! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!"
        >
          <ArrowRightToBracketOutline class="mr-1.5 h-4 w-4" />
          Sign out
        </Button>
      {/if}
      <Button
        color="dark"
        class="border-slate-700! bg-[#0f1420]! px-4! py-2! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!"
      >
        <CogOutline class="mr-1.5 h-4 w-4" />
        Edit profile
      </Button>
    </div>
  </div>

  <div class="mt-6 grid gap-6 xl:grid-cols-[14rem_minmax(0,1fr)]">
    <aside class="xl:pt-1" aria-label="Profile sections">
      <nav class="flex gap-2 overflow-x-auto pb-1 xl:block xl:space-y-2 xl:overflow-visible xl:pb-0">
        {#each sections as section}
          {@const { icon: Icon } = section}
          <button
            type="button"
            class={[
              'flex h-10 shrink-0 items-center gap-3 rounded-lg px-4 text-sm font-medium transition xl:w-full',
              section.label === activeSection
                ? 'bg-blue-500/18 text-blue-400'
                : 'text-slate-400 hover:bg-slate-800/70 hover:text-slate-100'
            ]}
            aria-current={section.label === activeSection ? 'page' : undefined}
            onclick={() => (activeSection = section.label)}
          >
            <Icon class="h-4.5 w-4.5 shrink-0" />
            <span>{section.label}</span>
          </button>
        {/each}
      </nav>
    </aside>

    <div class="min-w-0 space-y-5">
      {#if activeSection === 'Config sets'}
      <section class="rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6">
        <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <h2 class="text-base font-bold text-slate-100">Config sets</h2>
            <p class="mt-2 max-w-3xl text-sm leading-6 text-slate-400">
              Named presets for download and transcode options. Pick one when starting a new download, or set a default.
            </p>
          </div>
          <Badge rounded color="gray" class="w-fit bg-slate-800! px-2.5! py-1! text-[10px]! text-slate-400!">
            {sessionLabel}
          </Badge>
        </div>

        {#if configSetsError}
          <div
            class="mt-5 flex items-start gap-2 rounded-xl border border-red-900/60 bg-red-950/35 p-3 text-sm text-red-300"
            role="alert"
          >
            <ExclamationCircleOutline class="mt-0.5 h-4 w-4 shrink-0" />
            <span>{configSetsError}</span>
          </div>
        {/if}

        {#if configSetsLoading}
          <div class="mt-10 flex justify-center">
            <Spinner size="8" />
          </div>
        {:else if configSets.length === 0}
          <div class="mt-5 rounded-xl border border-slate-800/80 bg-slate-950/30 p-8 text-center">
            <AdjustmentsHorizontalOutline class="mx-auto h-9 w-9 text-slate-700" />
            <p class="mt-4 text-sm font-semibold text-slate-300">No config sets yet</p>
            <p class="mt-1 text-sm text-slate-500">Create one to reuse download and playlist settings.</p>
          </div>
        {:else}
          <div class="mt-5 space-y-2">
            {#each configSets as config (config.key)}
              {@const Icon = configIcon(config)}
              <article
                class="flex min-h-[3.95rem] flex-col gap-3 rounded-lg border border-slate-700/70 bg-[#151a26] px-3 py-3 transition hover:border-slate-600 hover:bg-slate-800/30 sm:flex-row sm:items-center sm:px-4"
              >
                <div class="flex min-w-0 items-center gap-3">
                  <span class="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-slate-800/70 text-blue-400">
                    <Icon class="h-4.5 w-4.5" />
                  </span>
                  <div class="min-w-0">
                    <div class="flex min-w-0 flex-wrap items-center gap-2">
                      <h3 class="truncate text-sm font-semibold text-slate-100">{config.name}</h3>
                      <span class="rounded-full bg-slate-800 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
                        {config.key}
                      </span>
                    </div>
                    <p class="mt-0.5 truncate text-xs text-slate-400">
                      {config.description || configSummary(config)}
                    </p>
                  </div>
                </div>

                <div class="flex shrink-0 gap-2 sm:ml-auto">
                  <a
                    href={`/profile/config-sets/${encodeURIComponent(config.key)}`}
                    class="inline-flex h-10 min-w-24 items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-xs font-semibold text-slate-200 transition hover:border-blue-500/60 hover:bg-blue-500/10 hover:text-blue-200"
                    aria-label={`View config set ${config.name}`}
                  >
                    <EyeOutline class="h-4 w-4" />
                    View
                  </a>
                  <button
                    type="button"
                    class="inline-flex h-10 min-w-10 items-center justify-center rounded-lg border border-slate-700 bg-slate-900/70 px-3 text-slate-300 transition hover:border-red-500/60 hover:bg-red-500/10 hover:text-red-200 disabled:opacity-50"
                    title="Delete config set"
                    aria-label={`Delete config set ${config.name}`}
                    disabled={deletingKey === config.key}
                    onclick={() => deleteConfigSet(config)}
                  >
                    {#if deletingKey === config.key}
                      <Spinner size="4" />
                    {:else}
                      <TrashBinOutline class="h-4 w-4" />
                    {/if}
                  </button>
                </div>
              </article>
            {/each}
          </div>
        {/if}

        <div class="mt-4">
          <Button
            href="/profile/config-sets/new"
            color="dark"
            class="border-slate-700! bg-transparent! px-3! py-1.5! text-xs! font-semibold! text-slate-200! hover:border-slate-600! hover:bg-slate-800!"
          >
            <PlusOutline class="mr-1.5 h-3.5 w-3.5" />
            New config set
          </Button>
        </div>
      </section>
      {:else if activeSection === 'Cookie management'}
        <CookieManagementSection />
      {:else if activeSection !== 'Overview'}
        <section class="rounded-2xl border border-slate-800 bg-[#151a26] p-8 text-center shadow-xl shadow-black/15">
          <p class="text-sm font-semibold text-slate-300">{activeSection}</p>
          <p class="mt-1 text-sm text-slate-500">This section is not available yet.</p>
        </section>
      {/if}

      <section class="grid gap-3 lg:grid-cols-3" aria-label="Account details">
        {#if data.user.email}
          <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
            <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Email</p>
            <p class="mt-1 truncate text-sm text-slate-300">{data.user.email}</p>
          </div>
        {/if}
        {#if expiresLabel}
          <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
            <p class="text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">Session expires</p>
            <p class="mt-1 text-sm text-slate-300">{expiresLabel}</p>
          </div>
        {/if}
        <div class="rounded-xl border border-slate-800/80 bg-slate-900/40 p-4">
          <p class="flex items-center gap-1.5 text-[10px] font-bold uppercase tracking-[0.08em] text-slate-600">
            <UsersGroupOutline class="h-3.5 w-3.5" />
            Groups
          </p>
          <div class="mt-2 flex flex-wrap gap-1.5">
            {#each data.user.groups as group}
              <Badge rounded color="gray" class="bg-slate-800! px-2.5! py-0.5! text-xs! text-slate-300!">
                {group}
              </Badge>
            {:else}
              <span class="text-sm text-slate-500">No groups</span>
            {/each}
          </div>
        </div>
      </section>
    </div>
  </div>
</section>
