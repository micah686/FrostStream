/**
 * JASSUB only needs `queryRemoteFonts` from lfa-ponyfill. Its dependency's lazy JSON import is
 * not understood by Vite's module worker in development, so this focused resolver asks Google
 * Fonts directly for the requested family instead. It intentionally does not query local fonts.
 */
class GoogleFont {
  constructor(
    private readonly family: string,
    private readonly weight: number
  ) {}

  async blob(): Promise<Blob> {
    const cssUrl = new URL('https://fonts.googleapis.com/css2');
    cssUrl.searchParams.set('family', `${this.family}:wght@${this.weight}`);
    const cssResponse = await fetch(cssUrl, { mode: 'cors' });
    if (!cssResponse.ok) throw new Error(`Google Fonts did not provide ${this.family}.`);

    const css = await cssResponse.text();
    const fontUrl = css.match(/url\(([^)]+)\)/)?.[1];
    if (!fontUrl) throw new Error(`Google Fonts did not return a font file for ${this.family}.`);

    const fontResponse = await fetch(fontUrl, { mode: 'cors' });
    if (!fontResponse.ok) throw new Error(`Google Fonts font download failed for ${this.family}.`);
    return fontResponse.blob();
  }
}

function toFamilyAndWeight(postscriptName: string): { family: string; weight: number } {
  const normalized = postscriptName.replace(/[-_]/g, ' ').trim();
  const match = normalized.match(/^(.*?)(?:\s+(thin|extralight|light|regular|medium|semibold|bold|extrabold|black))?$/i);
  const family = match?.[1]?.trim() || normalized;
  const name = match?.[2]?.toLowerCase();
  const weight = name === 'thin' ? 100 : name === 'extralight' ? 200 : name === 'light' ? 300 :
    name === 'medium' ? 500 : name === 'semibold' ? 600 : name === 'bold' ? 700 :
    name === 'extrabold' ? 800 : name === 'black' ? 900 : 400;
  return { family, weight };
}

export async function queryRemoteFonts({ postscriptNames = [] }: { postscriptNames?: string[] } = {}) {
  return postscriptNames
    .filter((name) => name.trim().length > 0)
    .map((name) => {
      const { family, weight } = toFamilyAndWeight(name);
      return new GoogleFont(family, weight);
    });
}

export default queryRemoteFonts;
