<script lang="ts">
  import { Select } from '$lib/components/ui';
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
  <label class="label mb-2 text-sm" for={id}>{label}</label>
  <Select {id} items={triStateItems} bind:value
 class="text-sm" />
  {#if hint}
    <p class="mt-1.5 text-xs text-base-content/40">{hint}</p>
  {/if}
</div>
