<script lang="ts">
  import { onMount } from 'svelte';
  import { ArrowLeft, Check } from '@lucide/svelte';
  import { customCss, setCustomCss, setCustomCssEnabled, themes, themeLabels } from '$lib/stores/theme';

  type Field = { key: string; label: string; color?: boolean };

  const colorFields: Field[] = [
    ['base-100', 'Base 100'], ['base-200', 'Base 200'], ['base-300', 'Base 300'], ['base-content', 'Base content'],
    ['primary', 'Primary'], ['primary-content', 'Primary content'], ['secondary', 'Secondary'], ['secondary-content', 'Secondary content'],
    ['accent', 'Accent'], ['accent-content', 'Accent content'], ['neutral', 'Neutral'], ['neutral-content', 'Neutral content'],
    ['info', 'Info'], ['info-content', 'Info content'], ['success', 'Success'], ['success-content', 'Success content'],
    ['warning', 'Warning'], ['warning-content', 'Warning content'], ['error', 'Error'], ['error-content', 'Error content']
  ].map(([key, label]) => ({ key: `--color-${key}`, label, color: true }));
  const valueFields: Field[] = [
    { key: '--radius-selector', label: 'Radius selector' }, { key: '--radius-field', label: 'Radius field' },
    { key: '--radius-box', label: 'Radius box' }, { key: '--size-selector', label: 'Size selector' },
    { key: '--size-field', label: 'Size field' }, { key: '--border', label: 'Border' },
    { key: '--depth', label: 'Depth' }, { key: '--noise', label: 'Noise' }, { key: '--color-scheme', label: 'Color scheme' }
  ];
  const defaults: Record<string, string> = {
    '--color-base-100': 'oklch(100% 0 0)', '--color-base-200': 'oklch(98% 0 0)', '--color-base-300': 'oklch(95% 0 0)',
    '--color-base-content': 'oklch(21% 0.006 285.885)', '--color-primary': 'oklch(45% 0.24 277.023)', '--color-primary-content': 'oklch(93% 0.034 272.788)',
    '--color-secondary': 'oklch(65% 0.241 354.308)', '--color-secondary-content': 'oklch(94% 0.028 342.258)', '--color-accent': 'oklch(77% 0.152 181.912)',
    '--color-accent-content': 'oklch(38% 0.063 188.416)', '--color-neutral': 'oklch(14% 0.005 285.823)', '--color-neutral-content': 'oklch(92% 0.004 286.32)',
    '--color-info': 'oklch(74% 0.16 232.661)', '--color-info-content': 'oklch(29% 0.066 243.157)', '--color-success': 'oklch(76% 0.177 163.223)',
    '--color-success-content': 'oklch(37% 0.077 168.94)', '--color-warning': 'oklch(82% 0.189 84.429)', '--color-warning-content': 'oklch(41% 0.112 45.904)',
    '--color-error': 'oklch(71% 0.194 13.428)', '--color-error-content': 'oklch(27% 0.105 12.094)', '--radius-selector': '0.5rem', '--radius-field': '0.25rem',
    '--radius-box': '0.5rem', '--size-selector': '0.25rem', '--size-field': '0.25rem', '--border': '1px', '--depth': '1', '--noise': '0', '--color-scheme': 'light'
  };

  let tab = $state<'designer' | 'css'>('designer');
  let rawCss = $state('');
  let designer = $state<Record<string, string>>({ ...defaults });
  const themeName = 'froststream-custom-theme';
  let themeDefault = $state(false);
  let prefersDark = $state(false);
  let saved = $state(false);

  onMount(() => {
    rawCss = $customCss;
  });

  function updateValue(key: string, event: Event) {
    designer[key] = (event.currentTarget as HTMLInputElement).value;
    saved = false;
  }

  function copyFromTheme(event: Event) {
    const source = (event.currentTarget as HTMLSelectElement).value;
    if (!source || typeof document === 'undefined') return;
    const sample = document.createElement('div');
    sample.setAttribute('data-theme', source);
    sample.style.position = 'absolute';
    sample.style.visibility = 'hidden';
    document.body.appendChild(sample);
    const computed = getComputedStyle(sample);
    const next = { ...designer };
    [...colorFields, ...valueFields].forEach((field) => {
      const value = computed.getPropertyValue(field.key).trim();
      if (value) next[field.key] = value;
    });
    sample.remove();
    designer = next;
    saved = false;
  }

  function buildCss(): string {
    const lines = [...colorFields, ...valueFields.filter((field) => field.key !== '--color-scheme')]
      .map((field) => `  ${field.key}: ${designer[field.key]};`);
    return `/* FrostStream custom theme: name: ${themeName}; default: ${themeDefault}; prefersdark: ${prefersDark} */\n:root {\n  color-scheme: ${designer['--color-scheme']};\n${lines.join('\n')}\n}`;
  }

  function apply() {
    rawCss = tab === 'designer' ? buildCss() : rawCss;
    setCustomCss(rawCss);
    setCustomCssEnabled(true);
    saved = true;
  }
</script>

<svelte:head><title>Custom CSS · Appearance · FrostStream</title></svelte:head>

<section class="mx-auto max-w-4xl" aria-labelledby="custom-css-title">
  <div class="mb-6">
    <p class="text-xs font-semibold uppercase tracking-[0.08em] text-primary">Appearance</p>
    <h1 id="custom-css-title" class="mt-2 text-2xl font-bold tracking-tight text-base-content">Custom CSS</h1>
    <p class="mt-2 text-sm text-base-content/60">Custom CSS is stored in this browser’s local storage. Changing devices or clearing browser storage will clear it.</p>
  </div>

  <div class="card border border-base-300 bg-base-100 p-5 sm:p-6">
    <div class="tabs tabs-lift mb-5" role="tablist" aria-label="Custom CSS editor tabs">
      <button type="button" class={['tab gap-2 text-sm font-semibold', tab === 'designer' ? 'tab-active text-primary' : '']} role="tab" aria-selected={tab === 'designer'} onclick={() => (tab = 'designer')}>Designer</button>
      <button type="button" class={['tab gap-2 text-sm font-semibold', tab === 'css' ? 'tab-active text-primary' : '']} role="tab" aria-selected={tab === 'css'} onclick={() => (tab = 'css')}>CSS</button>
    </div>

    {#if tab === 'designer'}
      <div class="mt-5 space-y-5">
        <div class="grid gap-4 sm:grid-cols-2">
          <label class="label flex-col items-start gap-1 text-sm">Theme name<input class="input w-full text-sm" value={themeName} disabled /></label>
          <label class="label flex-col items-start gap-1 text-sm">Copy from theme<select class="select w-full text-sm" onchange={copyFromTheme}>
            <option value="">Choose a theme…</option>
            {#each themes as source (source)}<option value={source}>{themeLabels[source]}</option>{/each}
          </select></label>
          <div class="flex items-center gap-4 sm:col-span-2">
            <label class="label flex-row items-center gap-2 text-sm">Default<input type="checkbox" class="toggle toggle-primary" bind:checked={themeDefault} /></label>
            <label class="label flex-row items-center gap-2 text-sm">Prefers dark<input type="checkbox" class="toggle toggle-primary" bind:checked={prefersDark} /></label>
          </div>
        </div>
        <div>
          <h2 class="mb-3 text-sm font-semibold text-base-content">Colors</h2>
          <div class="grid gap-3 sm:grid-cols-2">
            {#each colorFields as field (field.key)}
              <label class="flex items-center gap-2 text-xs font-semibold text-base-content/80">
                <span class="h-5 w-5 shrink-0 rounded border border-base-300" style={`background-color: ${designer[field.key]}`}></span>
                <span class="min-w-28">{field.label}</span>
                <input class="input min-w-0 flex-1 font-mono text-sm" value={designer[field.key]} oninput={(event) => updateValue(field.key, event)} />
              </label>
            {/each}
          </div>
        </div>
        <div>
          <h2 class="mb-3 text-sm font-semibold text-base-content">Theme values</h2>
          <div class="grid gap-3 sm:grid-cols-2">
            {#each valueFields as field (field.key)}
              <label class="label flex-col items-start gap-1 text-sm">{field.label}<input class="input w-full font-mono text-sm" value={designer[field.key]} oninput={(event) => updateValue(field.key, event)} /></label>
            {/each}
          </div>
        </div>
      </div>
    {:else}
      <div class="mt-5">
        <label class="label mb-2 text-sm" for="custom-css">CSS</label>
        <textarea id="custom-css" class="textarea min-h-96 w-full font-mono text-sm" bind:value={rawCss} placeholder={`:root {\n  --color-primary: #…;\n}`}></textarea>
        <p class="mt-2 text-xs text-base-content/50">Paste exact CSS here. It is applied to this browser only.</p>
      </div>
    {/if}

    <div class="mt-5 flex flex-col-reverse gap-3 border-t border-base-300/70 pt-5 sm:flex-row sm:items-center sm:justify-between">
      <a class="btn btn-sm btn-neutral text-xs" href="/profile/appearance"><ArrowLeft class="mr-1.5 h-4 w-4" />Back</a>
      <div class="flex items-center gap-3">
        {#if saved}<span class="flex items-center gap-1.5 text-xs font-semibold text-success"><Check class="h-3.5 w-3.5" />Saved</span>{/if}
        <button class="btn btn-sm btn-primary text-xs" type="button" onclick={apply}>Apply CSS</button>
      </div>
    </div>
  </div>
</section>
