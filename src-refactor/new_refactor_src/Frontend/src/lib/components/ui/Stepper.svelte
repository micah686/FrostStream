<script module lang="ts">
  export interface Step {
    id: number;
    label: string;
    description?: string;
  }
</script>

<script lang="ts">
  interface Props {
    steps: Step[];
    current: number;
    clickable?: boolean;
    onStepClick?: (event: { current: number }) => void;
    class?: string;
  }

  let { steps, current, clickable = false, onStepClick, class: klass = '' }: Props = $props();
</script>

<!-- daisyUI's `steps` gives the numbered markers and connector rail; the label
     and description stack is ours, since `step` carries a single line only. -->
<ul class={['steps w-full', klass]}>
  {#each steps as step (step.id)}
    <li
      class={['step', step.id <= current && 'step-primary']}
      data-content={step.id < current ? '✓' : String(step.id)}
    >
      {#if clickable}
        <button
          type="button"
          class="cursor-pointer px-2 text-center hover:opacity-80"
          onclick={() => onStepClick?.({ current: step.id })}
        >
          <span class={['block text-sm font-semibold', step.id === current ? 'text-primary' : '']}>
            {step.label}
          </span>
          {#if step.description}
            <span class="mt-0.5 block text-xs text-base-content/50">{step.description}</span>
          {/if}
        </button>
      {:else}
        <span class="block px-2 text-center">
          <span class={['block text-sm font-semibold', step.id === current ? 'text-primary' : '']}>
            {step.label}
          </span>
          {#if step.description}
            <span class="mt-0.5 block text-xs text-base-content/50">{step.description}</span>
          {/if}
        </span>
      {/if}
    </li>
  {/each}
</ul>
