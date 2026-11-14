<script lang="ts">
  interface Props {
    id?: string;
    min?: number;
    max?: number;
    step?: number;
    value: number;
    disabled?: boolean;
  }

  let { id, min = 0, max = 100, step = 1, value = $bindable(), disabled = false }: Props = $props();

</script>

<input
  {id}
  type="range"
  {min}
  {max}
  {step}
  {disabled}
  bind:value
  class="range range-primary range-sm w-full"
/>
