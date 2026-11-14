# FrostStream frontend style guide for AI contributors

Apply this guide to `src/Frontend`. Use Svelte 5, Tailwind CSS 4 and daisyUI 5. The goal is a consistent interface that follows the active theme. Existing code is not automatically a precedent: prefer these rules when adding or modifying UI, and avoid unrelated restyling.

## Local daisyUI skills: required workflow

The repository includes the daisyUI 5.7.x skill at `.agents/skills/daisyui/`. Read the files, rather than relying on memory or copying an existing page, before making frontend styling changes:

1. Start with [daisyUI SKILL.md](.agents/skills/daisyui/SKILL.md) for any HTML, Svelte markup, Tailwind or UI design work. Identify the component by its intended behavior, not just its name.
2. Read [usage/SKILL.md](.agents/skills/daisyui/usage/SKILL.md) before applying daisyUI classes and [colors/SKILL.md](.agents/skills/daisyui/colors/SKILL.md) before using colors or themes.
3. Read the applicable guide in [components/](.agents/skills/daisyui/components/) before implementing a component. If multiple components could fit, compare their guides before choosing; if a component was explicitly requested, read its guide first. Follow its required structure, parts and modifiers. Check the shared Svelte wrappers before building another implementation.
4. Read [config/SKILL.md](.agents/skills/daisyui/config/SKILL.md) when changing plugin configuration or theme registration. Read [install/SKILL.md](.agents/skills/daisyui/install/SKILL.md) only for installation or migration work; daisyUI is already installed here.

Use daisyUI component classes first, followed by Tailwind utilities for layout and necessary adjustments. The skill's component guides are the reference for daisyUI syntax; this document adds FrostStream's theme and placement conventions. Explicit user requirements take precedence over either guide. When changing a shared theme or component, consider all enabled themes and the application's existing user customization feature.

## Where styles belong

- **Theme definitions and global defaults:** `src/Frontend/src/app.css`. This file enables all built-in daisyUI themes and defines the default `froststream` theme, its semantic colors, radii, sizing, border, depth and noise. Set shared theme values here rather than in individual pages. The global font, focus outline, selection and scrollbar rules also live here; change them only for an app-wide need.
- **Theme selection and user customization:** `src/Frontend/src/lib/stores/theme.ts` and the existing settings UI. The store manages the selected theme, persistence and optional user CSS. Do not create a separate theme switch or overwrite user choices. A component should inherit its theme; do not set `data-theme` locally just to fix its colors.
- **Reusable controls:** check `src/Frontend/src/lib/components/ui/` first, including `Modal`, `Drawer`, `Select` and `Stepper`. Extend a shared component when the same behavior or appearance is needed in multiple places; keep domain-specific composition in the relevant feature component.
- **Page composition:** use daisyUI component classes in Svelte markup, with Tailwind utilities for spacing, responsive layout, sizing and typography. Keep one-off layout decisions close to the markup. Use Svelte state for behavior, not for theme colors.
- **Exceptional CSS:** use scoped component CSS only when daisyUI and Tailwind cannot express the required behavior. Use global CSS only for genuinely global rules. Explain the need in a short comment and use theme variables. Avoid new global element selectors, specificity overrides, `!important`, or a parallel component library.

## Colors and themes

Use semantic daisyUI colors everywhere for application UI. Never hardcode hex, RGB, named colors, `gray-*`, `slate-*`, `white` or `black` for UI surfaces, text, borders, icons, hover states or focus rings. Do not use `dark:` to choose between fixed palettes: the active daisyUI theme supplies the colors. Pair a filled semantic background with its matching `*-content` foreground.

| Purpose | Classes or tokens |
| --- | --- |
| App canvas, surfaces, borders | `base-200`, `base-100`, `base-300`; use `base-content` for text |
| Main action | `btn btn-primary`, with `primary-content` on custom primary surfaces |
| Secondary and accent actions | `secondary` / `secondary-content`, `accent` / `accent-content` |
| Informational, success, warning, error | `info`, `success`, `warning`, `error` and their corresponding `*-content` colors |
| Muted text or subtle borders | semantic colors with opacity, for example `text-base-content/70` or `border-base-300` |

Choose colors by role rather than assuming a theme maps them to a particular hue or brightness. Use the default daisyUI component variant unless a primary, accent or status role is appropriate, or an existing product decision explicitly calls for one. Do not rely on color alone to convey status. Check that text, icons, borders and focus indicators remain discernible against their actual backgrounds in light and dark themes; opacity can reduce contrast.

Literal colors are allowed in the central theme definition, and where an external media specification or data visualization requires a fixed identity. Examples include externally supplied chat colors and media imagery; these are not UI palette choices. Keep such exceptions isolated, document their purpose, and ensure surrounding UI remains theme aware. For charts, resolve semantic theme variables at runtime and update them when the theme changes; use distinct labels or patterns as well as color. User-provided CSS is an intentional customization feature, not a pattern for application styling.

## Components and layout

- Use `btn` with daisyUI variants and sizes for actions; use real buttons for actions and links for navigation. Give icon-only controls accessible names. Preserve visible keyboard focus and disabled states.
- Use `card` and its parts for grouped content; use `bg-base-*`, `border-base-300` and `rounded-box` when a surface needs explicit styling. Use `rounded-field` for fields and buttons, and `rounded-selector` for selectors. Avoid arbitrary per-page radii, shadows and fixed border colors.
- Use `input`, `select`, `textarea`, `checkbox`, `radio`, `toggle`, `fieldset` and `label` as appropriate. Associate labels and errors with controls. Reuse the shared Select wrapper where it fits.
- Use `tabs`, `tabs-border`, `tab` and `tab-active` for related view or filter navigation; use correct accessible semantics for the behavior. Use `menu`, `breadcrumbs`, `dropdown`, `collapse`, `modal`, `alert`, `badge`, `progress`, `loading` and `table` for their respective purposes rather than rebuilding them with custom CSS.
- Use the shared modal and drawer components where suitable. Preserve their keyboard, focus and dismissal behavior. Do not replace native accessibility with clickable `div` elements.
- Use Tailwind layout utilities for responsive `flex`, `grid`, gaps, padding and width. Keep controls usable on small screens, allow long text to wrap, and contain wide tables with horizontal scrolling. Use Lucide icons consistently and keep labels visible where useful.
- Use semantic status variants for feedback and loading, empty and error states. Avoid assigning meaning through visual treatment alone. Prefer theme radius and size tokens to fixed pixel values for component styling.

## Existing inconsistencies and migration

The frontend currently mixes daisyUI components with custom utility combinations, scoped CSS and some fixed colors. For example, `src/Frontend/src/lib/components/RangeSlider.svelte` has scoped styling and `src/Frontend/src/lib/api/liveChat.ts` contains fixed external chat colors. Treat each case according to its purpose: replace UI styling with daisyUI or semantic tokens when touching it; preserve externally meaningful colors where needed. Do not copy arbitrary existing styling into new components or perform a broad visual rewrite as part of an unrelated change.  
Don't touch the src/Frontend/src/lib/api/liveChat.ts without a specific reason, as the colors there have been picked for a specific use case that's outside the normal styling.

Before introducing a new selector or custom rule, check the installed daisyUI 5 component and its available variants, then the shared UI components. Prefer an existing component, followed by semantic Tailwind utilities. If neither works, scope the smallest necessary exception and explain why.

## Review checklist

1. Does each surface, text color, border and state use a semantic theme color and a readable foreground/background pairing?
2. Does each control use the appropriate daisyUI component and an existing shared wrapper where available?
3. Are theme definitions centralized in `app.css`, with theme selection handled by the existing store?
4. Are responsive behavior, keyboard focus, labels, disabled states and status meaning preserved?
5. For visual changes, inspect the affected UI in at least one light and one dark theme, including hover, focus and disabled states, plus a narrow viewport. Check representative built-in and custom themes when changing shared components or tokens.
6. Run `pnpm check` from `src/Frontend`; run relevant existing tests when behavior changes. Record any checks that could not be run.
