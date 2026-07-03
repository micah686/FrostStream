<script lang="ts">
  import { Label, Select } from 'flowbite-svelte';
  import { triStateItems, type TriState } from './ytDlpPresetOptions';

  interface Props {
    id: string;
    label: string;
    hint?: string;
    value?: TriState;
  }

  let { id, label, hint = '', value = $bindable('default') }: Props = $props();
</script>

<div>
  <Label for={id} class="mb-2 text-sm font-medium text-slate-300">{label}</Label>
  <Select
    {id}
    items={triStateItems}
    bind:value
    class="border-slate-800! bg-slate-950/60! text-sm! text-slate-200! focus:border-blue-500! focus:ring-blue-500!"
  />
  {#if hint}
    <p class="mt-1.5 text-xs text-slate-600">{hint}</p>
  {/if}
</div>
