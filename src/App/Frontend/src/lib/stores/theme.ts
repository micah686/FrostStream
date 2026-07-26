import { writable } from 'svelte/store';
import { browser } from '$app/environment';

export const themes = ['froststream', 'froststream-light'] as const;
export type Theme = (typeof themes)[number];

export const themeLabels: Record<Theme, string> = {
  froststream: 'Dark',
  'froststream-light': 'Light'
};

const themeKey = 'froststream:theme';
const defaultTheme: Theme = 'froststream';

function isTheme(value: string | null): value is Theme {
  return (themes as readonly string[]).includes(value ?? '');
}

function readStored(): Theme {
  if (!browser) return defaultTheme;
  const saved = localStorage.getItem(themeKey);
  return isTheme(saved) ? saved : defaultTheme;
}

export const theme = writable<Theme>(readStored());

// Applies the current theme to the document and persists changes. Called once
// from the root layout's onMount; the subscription lives for the app's session,
// so later setTheme() calls stay in sync without every consumer re-wiring it.
export function initTheme(): void {
  theme.subscribe((value) => {
    if (browser) {
      document.documentElement.dataset.theme = value;
      localStorage.setItem(themeKey, value);
    }
  });
}

export function setTheme(value: Theme): void {
  theme.set(value);
}
