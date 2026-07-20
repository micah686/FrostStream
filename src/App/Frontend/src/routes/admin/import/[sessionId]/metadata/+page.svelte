<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/state';
  import { Button, Input, Label, Spinner, Toggle } from 'flowbite-svelte';
  import ImportNotice from '$lib/components/admin/ImportNotice.svelte';
  import ImportWizardStepper from '$lib/components/admin/ImportWizardStepper.svelte';
  import { enrichImportSession, listAllImportSessionItems, patchImportSessionItem, refreshImportSessionMetadata, type ImportSessionItem, type ImportYtDlpOptions } from '$lib/api/imports';

  const card = 'rounded-2xl border border-slate-800 bg-[#151a26] p-5 shadow-xl shadow-black/15 sm:p-6';
  const field = 'border-slate-800! bg-slate-950/60! text-sm! text-slate-200! placeholder:text-slate-600!';
  const sessionId = $derived(page.params.sessionId ?? '');
  let items = $state<ImportSessionItem[]>([]); let sourceUrls = $state<Record<string, string>>({});
  let loading = $state(false); let busy = $state(false); let error = $state<string | null>(null); let notice = $state<string | null>(null); let timer: ReturnType<typeof setTimeout> | undefined;
  let proxyUrl = $state(''); let username = $state(''); let password = $state(''); let twoFactorCode = $state(''); let videoPassword = $state(''); let skipCertificateChecks = $state(false); let allowLegacyConnections = $state(false); let headers = $state(''); let sleepSeconds = $state(3);

  onMount(() => { void load(); return () => { if (timer) clearTimeout(timer); }; });
  async function load() {
    loading = true; error = null;
    try {
      items = await listAllImportSessionItems(sessionId, { included: true });
      const next = { ...sourceUrls }; for (const item of items) if (!(item.itemId in next)) next[item.itemId] = item.sourceUrl ?? ''; sourceUrls = next;
      if (items.some((x) => x.metadataFetchState === 'queued')) timer = setTimeout(() => void load(), 1800);
    } catch (err) { error = err instanceof Error ? err.message : 'Could not load metadata items.'; }
    finally { loading = false; }
  }
  function options(): ImportYtDlpOptions { return { proxyUrl: proxyUrl.trim() || undefined, username: username.trim() || undefined, password: password || undefined, twoFactorCode: twoFactorCode.trim() || undefined, videoPassword: videoPassword || undefined, skipCertificateChecks, allowLegacyConnections, extraHttpHeaders: headers.split('\n').map((x) => x.trim()).filter(Boolean), sleepBetweenRequestsSeconds: Math.max(3, Number(sleepSeconds) || 3) }; }
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
    if (item.metadataFetchState === 'queued') return 'bg-blue-950/50 text-blue-300';
    if (localInfoJsonFound(item)) return 'bg-emerald-950/50 text-emerald-300';
    return 'bg-slate-900 text-slate-400';
  }
  function updateSourceUrl(itemId: string, event: Event) { sourceUrls = { ...sourceUrls, [itemId]: (event.currentTarget as HTMLInputElement).value }; }
</script>

<ImportWizardStepper current={3} {sessionId} />
<section class={card}>
  <div class="flex flex-wrap items-start gap-3"><div><h1 class="text-xl font-bold text-white">Metadata download</h1><p class="mt-2 text-sm text-slate-400">Attach a source URL to each selected file, then let yt-dlp write its info.json without downloading media.</p></div><div class="ml-auto flex gap-2"><Button color="dark" class="border-slate-700! bg-slate-900! font-semibold!" disabled={busy} onclick={refreshSidecars}>{#if busy}<Spinner size="4" class="mr-2" />{/if}Refresh local info.json</Button><Button color="blue" class="border-0! font-semibold!" disabled={busy} onclick={() => fetchMetadata()}>{#if busy}<Spinner size="4" class="mr-2" />{/if}Fetch metadata</Button></div></div>
  <div class="mt-5"><ImportNotice {error} {notice} /></div>
  <details class="mb-5 rounded-xl border border-slate-800 bg-slate-950/30 p-4"><summary class="cursor-pointer text-sm font-semibold text-slate-300">yt-dlp options</summary><div class="mt-4 grid gap-4 md:grid-cols-2 xl:grid-cols-3"><div><Label for="proxy" class="mb-1 text-xs text-slate-400">Proxy URL</Label><Input id="proxy" bind:value={proxyUrl} class={field} /></div><div><Label for="user" class="mb-1 text-xs text-slate-400">Username</Label><Input id="user" bind:value={username} class={field} /></div><div><Label for="pass" class="mb-1 text-xs text-slate-400">Password</Label><Input id="pass" type="password" bind:value={password} class={field} /></div><div><Label for="twofactor" class="mb-1 text-xs text-slate-400">Two-factor code</Label><Input id="twofactor" bind:value={twoFactorCode} class={field} /></div><div><Label for="video-pass" class="mb-1 text-xs text-slate-400">Video password</Label><Input id="video-pass" type="password" bind:value={videoPassword} class={field} /></div><div><Label for="sleep" class="mb-1 text-xs text-slate-400">Sleep between requests (seconds)</Label><Input id="sleep" type="number" min="3" bind:value={sleepSeconds} class={field} /></div><div class="md:col-span-2"><Label for="headers" class="mb-1 text-xs text-slate-400">Extra HTTP headers (one FIELD:VALUE per line)</Label><textarea id="headers" bind:value={headers} rows="3" class="w-full rounded-lg border border-slate-800 bg-slate-950/60 p-2.5 text-sm text-slate-200"></textarea></div><div class="space-y-3 pt-5"><Toggle bind:checked={skipCertificateChecks}>Skip certificate checks</Toggle><Toggle bind:checked={allowLegacyConnections}>Allow legacy connections</Toggle></div></div></details>
  <div class="overflow-x-auto rounded-xl border border-slate-800"><table class="min-w-full text-left text-sm"><thead class="bg-slate-950/60 text-xs uppercase text-slate-500"><tr><th class="px-4 py-3">File</th><th class="min-w-80 px-4 py-3">Source URL</th><th class="px-4 py-3">Status</th><th class="px-4 py-3"></th></tr></thead><tbody class="divide-y divide-slate-800">{#each items as item (item.itemId)}<tr><td class="max-w-72 px-4 py-3"><p class="truncate font-medium text-slate-200">{item.fileName}</p><p class="truncate text-xs text-slate-500" title={item.relativePath}>{item.relativePath}</p></td><td class="px-4 py-3"><Input value={sourceUrls[item.itemId] ?? ''} oninput={(event) => updateSourceUrl(item.itemId, event)} placeholder="https://…" class={field} /></td><td class="px-4 py-3"><span class={`inline-flex whitespace-nowrap rounded-full px-2.5 py-1 text-xs font-semibold ${stateClass(item)}`}>{stateLabel(item)}</span>{#if item.metadataFetchMessage}<p class="mt-1 max-w-60 truncate text-xs text-slate-500" title={item.metadataFetchMessage}>{item.metadataFetchMessage}</p>{/if}</td><td class="px-4 py-3 text-right"><div class="flex justify-end gap-2"><Button color="dark" class="border-slate-700! bg-slate-900! text-xs!" onclick={() => saveUrl(item)} disabled={busy}>Save</Button>{#if item.metadataFetchState === 'failed'}<Button color="dark" class="border-red-900! bg-red-950/30! text-xs! text-red-200!" onclick={() => fetchMetadata(item)} disabled={busy}>Retry</Button>{/if}</div></td></tr>{:else}<tr><td colspan="4" class="p-8 text-center text-slate-500">No selected files.</td></tr>{/each}</tbody></table></div>
  <details class="mt-5 rounded-xl border border-slate-800 bg-slate-950/30 p-4"><summary class="cursor-pointer text-sm font-semibold text-slate-300">Metadata activity log</summary><div class="mt-3 space-y-2 font-mono text-xs">{#each items.filter((x) => x.metadataFetchState !== 'notAttempted' || localInfoJsonFound(x)) as item (item.itemId)}<p class="text-slate-400"><span class="text-slate-600">{new Date(item.updatedAt).toLocaleTimeString()}</span> {item.fileName}: {item.metadataFetchMessage || stateLabel(item)}</p>{:else}<p class="text-slate-600">No metadata activity yet.</p>{/each}</div></details>
  <div class="mt-6 flex justify-between"><a class="rounded-lg px-4 py-2.5 text-sm font-semibold text-slate-400 hover:text-white" href={`/admin/import/${sessionId}/files`}>Back</a><a class="rounded-lg bg-blue-600 px-6 py-2.5 text-sm font-semibold text-white hover:bg-blue-500" href={`/admin/import/${sessionId}/mapping`}>Next: manual mapping</a></div>
</section>
