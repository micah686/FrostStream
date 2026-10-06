import { writable } from 'svelte/store';
import { browser } from '$app/environment';

export const themes = [
  'froststream',
  'light',
  'dark',
  'cupcake',
  'bumblebee',
  'emerald',
  'corporate',
  'synthwave',
  'retro',
  'cyberpunk',
  'valentine',
  'halloween',
  'garden',
  'forest',
  'aqua',
  'lofi',
  'pastel',
  'fantasy',
  'wireframe',
  'black',
  'luxury',
  'dracula',
  'cmyk',
  'autumn',
  'business',
  'acid',
  'lemonade',
  'night',
  'coffee',
  'winter',
  'dim',
  'nord',
  'sunset',
  'caramellatte',
  'abyss',
  'silk'
] as const;
export type Theme = (typeof themes)[number];

export const themeLabels: Record<Theme, string> = {
  froststream: 'FrostStream (default)',
  light: 'Light',
  dark: 'Dark',
  cupcake: 'Cupcake',
  bumblebee: 'Bumblebee',
  emerald: 'Emerald',
  corporate: 'Corporate',
  synthwave: 'Synthwave',
  retro: 'Retro',
  cyberpunk: 'Cyberpunk',
  valentine: 'Valentine',
  halloween: 'Halloween',
  garden: 'Garden',
  forest: 'Forest',
  aqua: 'Aqua',
  lofi: 'Lofi',
  pastel: 'Pastel',
  fantasy: 'Fantasy',
  wireframe: 'Wireframe',
  black: 'Black',
  luxury: 'Luxury',
  dracula: 'Dracula',
  cmyk: 'CMYK',
  autumn: 'Autumn',
  business: 'Business',
  acid: 'Acid',
  lemonade: 'Lemonade',
  night: 'Night',
  coffee: 'Coffee',
  winter: 'Winter',
  dim: 'Dim',
  nord: 'Nord',
  sunset: 'Sunset',
  caramellatte: 'Caramellatte',
  abyss: 'Abyss',
  silk: 'Silk'
};

const themeKey = 'froststream:theme';
const customCssKey = 'froststream:custom-css';
const customCssEnabledKey = 'froststream:custom-css-enabled';
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
export const customCss = writable<string>(browser ? localStorage.getItem(customCssKey) ?? '' : '');
export const customCssEnabled = writable<boolean>(browser ? localStorage.getItem(customCssEnabledKey) === 'true' : false);

let appliedCustomCss = '';
let customCssIsEnabled = false;

function runtimeCustomCss(value: string): string {
  const pluginPattern = /@plugin\s+["']daisyui\/theme["']\s*\{([\s\S]*?)\}/g;
  return value.replace(pluginPattern, (_match, body: string) => {
    const declarations = body.match(/(?:--[\w-]+|color-scheme)\s*:\s*[^;]+;/g) ?? [];
    return `:root {\n  ${declarations.join('\n  ')}\n}`;
  });
}

function applyCustomCss(): void {
  if (!browser) return;
  let style = document.getElementById('froststream-custom-css') as HTMLStyleElement | null;
  if (!style) {
    style = document.createElement('style');
    style.id = 'froststream-custom-css';
    document.head.appendChild(style);
  }
  style.textContent = customCssIsEnabled ? runtimeCustomCss(appliedCustomCss) : '';
  localStorage.setItem(customCssKey, appliedCustomCss);
  localStorage.setItem(customCssEnabledKey, String(customCssIsEnabled));
}

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
  customCss.subscribe((value) => {
    appliedCustomCss = value;
    applyCustomCss();
  });
  customCssEnabled.subscribe((value) => {
    customCssIsEnabled = value;
    applyCustomCss();
  });
}

export function setTheme(value: Theme): void {
  theme.set(value);
  customCssEnabled.set(false);
}

export function setCustomCss(value: string): void {
  customCss.set(value);
}

export function setCustomCssEnabled(value: boolean): void {
  customCssEnabled.set(value);
}
