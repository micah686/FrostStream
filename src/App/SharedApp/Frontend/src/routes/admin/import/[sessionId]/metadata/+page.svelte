<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { ArrowLeft } from '@lucide/svelte';
  import { Select } from '$lib/components/ui';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import ImportWizardStepper from '$lib/components/admin/ImportWizardStepper.svelte';
  import { listCookieProfiles, type CookieProfile } from '$lib/api/cookies';
  import { enrichImportSession, listAllImportSessionItems, patchImportSessionItem, refreshImportSessionMetadata, type ImportSessionItem, type ImportYtDlpOptions } from '$lib/api/imports';

  const card = 'card border-[length:var(--border)] border-base-300 bg-base-100 p-5 sm:p-6';
  const sessionId = $derived(page.params.sessionId ?? '');
  let items = $state<ImportSessionItem[]>([]); let sourceUrls = $state<Record<string, string>>({});
  let cookieProfiles = $state<CookieProfile[]>([]); let cookieProfileKey = $state('');
  let loading = $state(false); let busy = $state(false); let error = $state<string | null>(null); let notice = $state<string | null>(null); let timer: ReturnType<typeof setTimeout> | undefined;
  let proxyUrl = $state(''); let username = $state(''); let password = $state(''); let twoFactorCode = $state(''); let videoPassword = $state(''); let skipCertificateChecks = $state(false); let allowLegacyConnections = $state(false); let headers = $state(''); let sleepSeconds = $state(3);

  onMount(() => { void load(); void loadCookieProfiles(); return () => { if (timer) clearTimeout(timer); }; });
  async function loadCookieProfiles() { try { cookieProfiles = await listCookieProfiles(); } catch { cookieProfiles = []; } }
  async function load() {
    loading = true; error = null;
    try {
      items = await listAllImportSessionItems(sessionId, { included: true });
      const next = { ...sourceUrls }; for (const item of items) if (!(item.itemId in next)) next[item.itemId] = item.sourceUrl ?? ''; sourceUrls = next;
      if (items.some((x) => x.metadataFetchState === 'queued')) timer = setTimeout(() => void load(), 1800);
    } catch (err) { error = err instanceof Error ? err.message : 'Could not load metadata items.'; }
    finally { loading = false; }
  }
  function options(): ImportYtDlpOptions { return { cookieProfileKey: cookieProfileKey || undefined, proxyUrl: proxyUrl.trim() || undefined, username: username.trim() || undefined, password: password || undefined, twoFactorCode: twoFactorCode.trim() || undefined, videoPassword: videoPassword || undefined, skipCertificateChecks, allowLegacyConnections, extraHttpHeaders: headers.split('\n').map((x) => x.trim()).filter(Boolean), sleepBetweenRequestsSeconds: Math.max(3, Number(sleepSeconds) || 3) }; }
  async function saveUrl(item: ImportSessionItem, quiet = false) {
    const response = await patchImportSessionItem(sessionId, item.itemId, { sourceUrl: sourceUrls[item.itemId]?.trim() || undefined });
    if (response.item) items = items.map((x) => x.itemId === item.itemId ? response.item! : x);
    if (!quiet) notice = `Source URL saved for ${item.fileName}.`;
  }
  async function fetchMetadata(target?: ImportSessionItem) {
    const targets = target ? [target] : items.filter((x) => sourceUrls[x.itemId]?.trim() && x.metadataFetchState !== 'succeeded' && x.metadataSource !== 'manualMapping');
    if (!targets.length) { error = 'Add at least one source URL that has not already fetched metadata.'; return; }
    busy = true; error = null; notice = null;
    try {
      await Promise.all(targets.map((item) => saveUrl(item, true)));
      const response = await enrichImportSession(sessionId, targets.map((x) => x.itemId), options());
      notice = `${response.queuedCount} metadata fetch${response.queuedCount === 1 ? '' : 'es'} queued.`;
      await load();
    } catch (err) { error = err instanceof Error ? err.message : 'Could not queue metadata fetching.'; }
    finally { busy = false; }
  }
  async function refreshSidecars() {
    busy = true; error = null; notice = null;
    try {
      const response = await refreshImportSessionMetadata(sessionId);
      notice = `Found local info.json for ${response.foundCount} of ${response.checkedCount} selected file${response.checkedCount === 1 ? '' : 's'}.`;
      await load();
    } catch (err) { error = err instanceof Error ? err.message : 'Could not refresh local metadata sidecars.'; }
    finally { busy = false; }
  }
  function localInfoJsonFound(item: ImportSessionItem) {
    return item.hasInfoJson;
  }
  function stateLabel(item: ImportSessionItem) {
    if (item.metadataFetchState === 'queued') return '● Checking';
    if (localInfoJsonFound(item)) return 'info.json found';
    return 'no metadata';
  }
  function stateClass(item: ImportSessionItem) {
    if (item.metadataFetchState === 'failed') return 'badge-error';
    if (localInfoJsonFound(item)) return 'badge-success';
    return 'badge-neutral';
  }
  function updateSourceUrl(itemId: string, event: Event) { sourceUrls = { ...sourceUrls, [itemId]: (event.currentTarget as HTMLInputElement).value }; }
</script>

<section class={card}>
  <ImportWizardStepper current={3} {sessionId} />
  <div class="flex flex-wrap items-start gap-3"><div><h1 class="text-xl font-bold text-base-content">Metadata download</h1><p class="mt-2 text-sm text-base-content/60">Attach a source URL to each selected file, then let yt-dlp write its info.json without downloading media.</p></div><div class="ml-auto flex gap-2"><button class="btn btn-sm btn-neutral" disabled={busy} onclick={refreshSidecars}>{#if busy}<span class="loading loading-spinner loading-xs mr-2"></span>{/if}Refresh local info.json</button><button class="btn btn-sm btn-primary" disabled={busy} onclick={() => fetchMetadata()}>{#if busy}<span class="loading loading-spinner loading-xs mr-2"></span>{/if}Fetch metadata</button></div></div>
  <div class="mt-5"><ImportNotice {error} {notice} /></div>
  <details class="card collapse mb-5 border-[length:var(--border)] border-base-300 bg-base-100 p-4">
    <summary class="collapse-title cursor-pointer list-none px-0 py-0 text-sm font-semibold text-base-content [&::-webkit-details-marker]:hidden">yt-dlp options</summary>
    <div class="collapse-content px-0 pb-0">
      <div class="mt-4 grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        <div><label class="label mb-1 text-xs" for="cookie-profile">Cookie profile</label><Select id="cookie-profile" items={[{ value: '', name: 'No cookie profile' }, ...cookieProfiles.map((profile) => ({ value: profile.profileKey, name: profile.displayName ? `${profile.displayName} (${profile.profileKey})` : profile.profileKey }))]} bind:value={cookieProfileKey} /></div>
        <div><label class="label mb-1 text-xs" for="proxy">Proxy URL</label><input class="input w-full" id="proxy" bind:value={proxyUrl} /></div>
        <div><label class="label mb-1 text-xs" for="user">Username</label><input class="input w-full" id="user" bind:value={username} /></div>
        <div><label class="label mb-1 text-xs" for="pass">Password</label><input class="input w-full" id="pass" type="password" bind:value={password} /></div>
        <div><label class="label mb-1 text-xs" for="twofactor">Two-factor code</label><input class="input w-full" id="twofactor" bind:value={twoFactorCode} /></div>
        <div><label class="label mb-1 text-xs" for="video-pass">Video password</label><input class="input w-full" id="video-pass" type="password" bind:value={videoPassword} /></div>
        <div><label class="label mb-1 text-xs" for="sleep">Sleep between requests (seconds)</label><input class="input w-full" id="sleep" type="number" min="3" bind:value={sleepSeconds} /></div>
        <div class="md:col-span-2"><label class="label mb-1 text-xs" for="headers">Extra HTTP headers (one FIELD:VALUE per line)</label><textarea id="headers" bind:value={headers} rows="3" class="textarea w-full text-sm"></textarea></div>
        <div class="space-y-3 pt-5"><label class="label inline-flex cursor-pointer items-center gap-2"><input type="checkbox" class="toggle toggle-primary" bind:checked={skipCertificateChecks} /><span>Skip certificate checks</span></label><label class="label inline-flex cursor-pointer items-center gap-2"><input type="checkbox" class="toggle toggle-primary" bind:checked={allowLegacyConnections} /><span>Allow legacy connections</span></label></div>
      </div>
    </div>
  </details>
  <div class="overflow-x-auto rounded-box border-[length:var(--border)] border-base-300"><table class="min-w-full text-left text-sm"><thead class="bg-base-200/60 text-xs uppercase text-base-content/50"><tr><th class="px-4 py-3">File</th><th class="min-w-80 px-4 py-3">Source URL</th><th class="px-4 py-3">Status</th><th class="px-4 py-3"></th></tr></thead><tbody class="divide-y divide-base-300">{#each items as item (item.itemId)}<tr><td class="max-w-72 px-4 py-3"><p class="truncate font-medium text-base-content/90">{item.fileName}</p><p class="truncate text-xs text-base-content/50" title={item.relativePath}>{item.relativePath}</p></td><td class="px-4 py-3"><input class="input w-full" value={sourceUrls[item.itemId] ?? ''} oninput={(event) => updateSourceUrl(item.itemId, event)} placeholder="https://…" /></td><td class="px-4 py-3"><span class={`badge badge-sm whitespace-nowrap ${stateClass(item)}`}>{stateLabel(item)}</span>{#if item.metadataFetchMessage}<p class="mt-1 max-w-60 truncate text-xs text-base-content/50" title={item.metadataFetchMessage}>{item.metadataFetchMessage}</p>{/if}</td><td class="px-4 py-3 text-right">{#if item.metadataFetchState === 'failed'}<button class="btn btn-sm btn-neutral text-xs" onclick={() => fetchMetadata(item)} disabled={busy}>Retry</button>{/if}</td></tr>{:else}<tr><td colspan="4" class="p-8 text-center text-base-content/50">No selected files.</td></tr>{/each}</tbody></table></div>
  <details class="card collapse mt-5 border-[length:var(--border)] border-base-300 bg-base-100 p-4"><summary class="collapse-title cursor-pointer list-none px-0 py-0 text-sm font-semibold text-base-content [&::-webkit-details-marker]:hidden">Metadata activity log</summary><div class="collapse-content px-0 pb-0"><div class="mt-3 space-y-2 font-mono text-xs">{#each items.filter((x) => x.metadataFetchState !== 'notAttempted' || localInfoJsonFound(x)) as item (item.itemId)}<p class="text-base-content/60"><span class="text-base-content/40">{new Date(item.updatedAt).toLocaleTimeString()}</span> {item.fileName}: {item.metadataFetchMessage || stateLabel(item)}</p>{:else}<p class="text-base-content/40">No metadata activity yet.</p>{/each}</div></div></details>
  <div class="mt-6 flex justify-between"><a class="btn btn-sm btn-neutral text-xs" href={`/admin/import/${sessionId}/files`}><ArrowLeft class="mr-1.5 h-4 w-4" />Back</a><a class="btn btn-sm btn-primary text-xs" href={`/admin/import/${sessionId}/mapping`}>Next: manual mapping</a></div>
</section>
