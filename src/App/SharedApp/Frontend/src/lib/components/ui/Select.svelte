<script module lang="ts">
  export interface SelectItem<T extends string | number = string> {
    value: T;
    name: string;
    disabled?: boolean;
  }
</script>

<script lang="ts" generics="T extends string | number">
  import type { HTMLSelectAttributes } from 'svelte/elements';

  interface Props extends Omit<HTMLSelectAttributes, 'value' | 'class'> {
    items: SelectItem<T>[];
    value?: T;
    class?: string;
  }

  let { items, value = $bindable(), class: klass = '', ...rest }: Props = $props();
</script>

<select class={['select w-full', klass]} bind:value {...rest}>
  {#each items as item (item.value)}
    <option value={item.value} disabled={item.disabled}>{item.name}</option>
  {/each}
</select>
