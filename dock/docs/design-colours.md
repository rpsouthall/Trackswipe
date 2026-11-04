# Windows App Colour Specification

A reusable light and dark colour system for Windows desktop applications. It follows the Windows 11 material model: neutral surfaces, system-provided semantic colours, and an optional accent tint. Use semantic token names in application code; the fixed values below are fallback values, not replacements for Windows accessibility or personalization settings.

## Principles

- Follow the Windows light/dark preference by default. Let a user explicitly choose Light, Dark, or Use system setting.
- Use neutral greys for app chrome and let the Windows accent provide personality and state.
- Prefer `ThemeResource` / `DynamicResource` equivalents for text, controls, focus, and interaction states. These resources adapt to personalization and accessibility settings.
- High Contrast always takes precedence. Do not apply custom tints, opacity, or fixed foreground colours while High Contrast is active.
- Treat translucency as enhancement only. Every translucent material must have an opaque fallback.

## Core Tokens

| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| `Surface.Base` | `#F3F3F3` | `#202020` | Opaque window, dock, popup, or acrylic fallback surface. |
| `Surface.Tint` | `#F2F2F2` | `#1C1C1C` | Neutral acrylic tint colour. |
| `Surface.TintOpacity` | `55%` | `55%` | Strength of `Surface.Tint` over the backdrop. |
| `Surface.LuminosityOpacity` | `88%` | `88%` | Frosted material opacity. |
| `Surface.Rim` | `#F3F3F3` | `#202020` | Window frame/rim colour when the platform requires an explicit value. |
| `Accent.Default` | Windows accent | Windows accent | Links, selection, focus indication, and optional material tinting. |
| `Accent.Fallback` | `#0078D4` | `#0078D4` | Use only when the Windows accent cannot be read. |

## Material Recipes

### Light Material

| Property | Value |
| --- | --- |
| Fallback surface | `#F3F3F3` |
| Acrylic tint | `#F2F2F2` |
| Tint opacity | `0.55` |
| Luminosity opacity | `0.88` |
| Window rim | `#F3F3F3` |

### Dark Material

| Property | Value |
| --- | --- |
| Fallback surface | `#202020` |
| Acrylic tint | `#1C1C1C` |
| Tint opacity | `0.55` |
| Luminosity opacity | `0.88` |
| Window rim | `#202020` |

For a non-translucent application, use `Surface.Base` directly. For a Windows composition material, use the respective tint and opacity values. The 88% luminosity opacity gives the material a stable, taskbar-like appearance without relying on the wallpaper for contrast.

## Semantic Colours

Do not hard-code values for these roles in normal UI. Use the platform's current theme resources so they track Windows updates, contrast settings, and the selected accent.

| Role | Windows/WinUI resource | Usage |
| --- | --- | --- |
| Primary text | `TextFillColorPrimaryBrush` | Page titles, normal body text, primary commands. |
| Secondary text | `TextFillColorSecondaryBrush` | Supporting copy, metadata, descriptions. |
| Disabled text | `TextFillColorDisabledBrush` | Disabled labels and commands. |
| Subtle hover fill | `SubtleFillColorSecondaryBrush` | Hover background for icon buttons and compact list items. |
| Subtle pressed fill | `SubtleFillColorTertiaryBrush` | Pressed background for icon buttons and compact list items. |
| Accent fill | `AccentFillColorDefaultBrush` | Selected controls and high-emphasis actions. |
| Focus visual | Platform focus visual resource | Keyboard focus only; retain the platform default whenever possible. |
| Error, warning, success | Platform semantic status resources | Validation and status; do not infer these from the accent colour. |

## Accent-Tinted Material

Accent tint is optional and should be a user preference, not the default. It may tint glass/chrome, but it should not replace semantic selection, focus, or status colours.

Start with the current Windows accent and mix it toward a neutral endpoint:

| Theme | Formula | Result |
| --- | --- | --- |
| Dark | `accent * 0.45 + black * 0.55` | A restrained dark accent tint. |
| Light | `accent * 0.40 + white * 0.60` | A restrained light accent tint. |

In channel form, for each RGB channel $c$:

$$
\text{darkTint} = \operatorname{round}(0.45c)
$$

$$
\text{lightTint} = \operatorname{round}(0.40c + 153)
$$

If the app is following the active Windows theme, refresh this material whenever Windows changes its theme, background, or accent colour.

## WinUI 3 Mapping

```xaml
<Grid Background="{ThemeResource ApplicationPageBackgroundThemeBrush}">
    <Button
        Background="Transparent"
        BorderThickness="0"
        Foreground="{ThemeResource TextFillColorPrimaryBrush}">
        <Button.Resources>
            <!-- Keep the normal state transparent. -->
        </Button.Resources>
    </Button>
</Grid>
```

For a custom compact-button template, set the hover background to `{ThemeResource SubtleFillColorSecondaryBrush}` and the pressed background to `{ThemeResource SubtleFillColorTertiaryBrush}`. Keep `UseSystemFocusVisuals="True"` so keyboard focus remains visible and theme-aware.

For a composition acrylic surface:

```csharp
var material = isDark
    ? new AcrylicRecipe("#1C1C1C", 0.55, 0.88, "#202020")
    : new AcrylicRecipe("#F2F2F2", 0.55, 0.88, "#F3F3F3");
```

`AcrylicRecipe` is illustrative; map the four values to the material API used by the target application.

## Accessibility Rules

- Never convey state with colour alone; pair it with text, iconography, position, or another non-colour cue.
- Keep text and essential icons at or above $4.5:1$ contrast against their effective background. Large text may use $3:1$.
- Do not calculate a fixed foreground colour over acrylic. Use the platform text brush, and supply an opaque fallback surface.
- Preserve Windows focus visuals. Avoid custom focus rings unless they meet contrast requirements in both themes.
- When High Contrast is enabled, request the system/default theme and let Windows supply the colours. Do not force Light or Dark.

## Implementation Checklist

- [ ] Implement `Light`, `Dark`, and `System` theme choices; default to `System` for a new app.
- [ ] Bind all normal text, control, focus, and status colours to theme resources.
- [ ] Use `#F3F3F3` and `#202020` as opaque fallbacks for light and dark material surfaces.
- [ ] Apply optional accent tint only after the user enables it.
- [ ] Re-evaluate material and rim colours after a theme or accent change.
- [ ] Verify Light, Dark, System, and High Contrast modes with keyboard focus and disabled states.
