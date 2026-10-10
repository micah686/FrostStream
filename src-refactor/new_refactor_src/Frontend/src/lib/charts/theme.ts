export type ChartTheme = {
  primary: string;
  secondary: string;
  accent: string;
  info: string;
  success: string;
  warning: string;
  error: string;
  neutral: string;
  base100: string;
  base200: string;
  base300: string;
  baseContent: string;
};

const semanticKeys = [
  'primary',
  'secondary',
  'accent',
  'info',
  'success',
  'warning',
  'error',
  'neutral'
] as const;

const emptyTheme: ChartTheme = {
  primary: 'transparent',
  secondary: 'transparent',
  accent: 'transparent',
  info: 'transparent',
  success: 'transparent',
  warning: 'transparent',
  error: 'transparent',
  neutral: 'transparent',
  base100: 'transparent',
  base200: 'transparent',
  base300: 'transparent',
  baseContent: 'transparent'
};

export function readChartTheme(): ChartTheme {
  if (typeof document === 'undefined') return emptyTheme;

  return {
    primary: resolveCssColor('--color-primary'),
    secondary: resolveCssColor('--color-secondary'),
    accent: resolveCssColor('--color-accent'),
    info: resolveCssColor('--color-info'),
    success: resolveCssColor('--color-success'),
    warning: resolveCssColor('--color-warning'),
    error: resolveCssColor('--color-error'),
    neutral: resolveCssColor('--color-neutral'),
    base100: resolveCssColor('--color-base-100'),
    base200: resolveCssColor('--color-base-200'),
    base300: resolveCssColor('--color-base-300'),
    baseContent: resolveCssColor('--color-base-content')
  };
}

export function withAlpha(color: string, percentage: number): string {
  if (!color || color === 'transparent' || percentage <= 0) return 'transparent';
  const rgb = parseRgb(color);
  if (!rgb) return color;
  return `rgba(${rgb.r}, ${rgb.g}, ${rgb.b}, ${Math.min(100, percentage) / 100})`;
}

export function categoricalPalette(theme: ChartTheme, count = 12): string[] {
  if (count <= 0) return [];
  const seeds = semanticKeys
    .map((key) => theme[key])
    .filter((color) => color && color !== 'transparent');
  const colors: string[] = [];

  for (const seed of seeds) {
    if (isDistinct(seed, colors)) colors.push(seed);
    if (colors.length >= count) return colors;
  }

  const seedRgb = seeds.map(parseRgb).filter((value): value is Rgb => value !== null);
  for (let index = 0; colors.length < count && index < count * 4; index += 1) {
    const source = seedRgb[index % Math.max(1, seedRgb.length)] ?? { r: 96, g: 165, b: 250 };
    const hsl = rgbToHsl(source);
    const candidate = hslToRgb({
      h: (hsl.h + index * 37 + 17) % 360,
      s: Math.max(0.45, Math.min(0.9, hsl.s + 0.18)),
      l: Math.max(0.35, Math.min(0.72, index % 2 === 0 ? hsl.l + 0.08 : hsl.l - 0.08))
    });
    const color = `rgb(${candidate.r}, ${candidate.g}, ${candidate.b})`;
    if (isDistinct(color, colors)) colors.push(color);
  }

  return colors.slice(0, count);
}

function stateColor(state: string, theme: ChartTheme): string {
  const value = state.toLowerCase();
  if (value.includes('complete') || value.includes('already')) return theme.success;
  if (value.includes('fail') || value.includes('dead')) return theme.error;
  if (value.includes('cancel')) return theme.warning;
  if (value.includes('ignore')) return theme.neutral;
  return theme.info;
}

export function stateColors(states: string[], theme: ChartTheme): string[] {
  return states.map((state) => {
    const value = state.toLowerCase();
    const base = stateColor(state, theme);
    if (value.includes('already')) return variantColor(base, 1);
    if (value.includes('complete')) return base;
    return base;
  });
}

type Rgb = { r: number; g: number; b: number };
type Hsl = { h: number; s: number; l: number };

function resolveCssColor(variable: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(variable).trim();
  if (!value) return 'transparent';

  const probe = document.createElement('span');
  probe.style.color = value;
  document.body.appendChild(probe);
  const computed = getComputedStyle(probe).color;
  probe.remove();

  return canvasColor(computed) ?? value;
}

function canvasColor(value: string): string | null {
  const canvas = document.createElement('canvas');
  canvas.width = 1;
  canvas.height = 1;
  const context = canvas.getContext('2d');
  if (!context) return null;
  context.clearRect(0, 0, 1, 1);
  context.fillStyle = value;
  context.fillRect(0, 0, 1, 1);
  const [r, g, b, a] = context.getImageData(0, 0, 1, 1).data;
  if (a === 0) return 'transparent';
  return `rgb(${r}, ${g}, ${b})`;
}

function parseRgb(value: string): Rgb | null {
  const normalized = value.trim().toLowerCase();
  const hex = normalized.match(/^#([0-9a-f]{3,8})$/i);
  if (hex) {
    const raw = hex[1];
    const expanded = raw.length <= 4 ? raw.split('').map((part) => part + part).join('') : raw;
    if (expanded.length < 6) return null;
    return {
      r: Number.parseInt(expanded.slice(0, 2), 16),
      g: Number.parseInt(expanded.slice(2, 4), 16),
      b: Number.parseInt(expanded.slice(4, 6), 16)
    };
  }

  const rgb = normalized.match(/^rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)/);
  if (!rgb) return null;
  return { r: Math.round(Number(rgb[1])), g: Math.round(Number(rgb[2])), b: Math.round(Number(rgb[3])) };
}

function variantColor(color: string, index: number): string {
  const rgb = parseRgb(color);
  if (!rgb) return color;
  const hsl = rgbToHsl(rgb);
  const variant = hslToRgb({
    h: (hsl.h + index * 34 + 18) % 360,
    s: Math.max(0.48, Math.min(0.92, hsl.s + 0.12)),
    l: Math.max(0.34, Math.min(0.74, hsl.l + (index % 2 === 0 ? 0.06 : -0.06)))
  });
  return `rgb(${variant.r}, ${variant.g}, ${variant.b})`;
}

function isDistinct(color: string, existing: string[]): boolean {
  const candidate = parseRgb(color);
  if (!candidate) return existing.length === 0;
  return existing.every((item) => {
    const current = parseRgb(item);
    if (!current) return true;
    return Math.sqrt(
      (candidate.r - current.r) ** 2 +
      (candidate.g - current.g) ** 2 +
      (candidate.b - current.b) ** 2
    ) >= 26;
  });
}

function rgbToHsl({ r, g, b }: Rgb): Hsl {
  const red = r / 255;
  const green = g / 255;
  const blue = b / 255;
  const max = Math.max(red, green, blue);
  const min = Math.min(red, green, blue);
  const delta = max - min;
  let h = 0;
  const l = (max + min) / 2;
  const s = delta === 0 ? 0 : delta / (1 - Math.abs(2 * l - 1));

  if (delta !== 0) {
    if (max === red) h = 60 * (((green - blue) / delta) % 6);
    else if (max === green) h = 60 * ((blue - red) / delta + 2);
    else h = 60 * ((red - green) / delta + 4);
  }

  return { h: h < 0 ? h + 360 : h, s, l };
}

function hslToRgb({ h, s, l }: Hsl): Rgb {
  const chroma = (1 - Math.abs(2 * l - 1)) * s;
  const segment = h / 60;
  const x = chroma * (1 - Math.abs((segment % 2) - 1));
  const match = l - chroma / 2;
  const [r, g, b] = segment < 1 ? [chroma, x, 0]
    : segment < 2 ? [x, chroma, 0]
      : segment < 3 ? [0, chroma, x]
        : segment < 4 ? [0, x, chroma]
          : segment < 5 ? [x, 0, chroma]
            : [chroma, 0, x];
  return {
    r: Math.round((r + match) * 255),
    g: Math.round((g + match) * 255),
    b: Math.round((b + match) * 255)
  };
}
