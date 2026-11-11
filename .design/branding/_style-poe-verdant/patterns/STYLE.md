# PoE Verdant — PoeFree UI style

> Phase: patterns | Brand: PoeFree | Generated: 2026-10-20

---

## Intensity

| Dial | Value | Meaning |
|------|-------|---------|
| Variance | 3 | Strict, symmetric panels — like the in-game Options window |
| Motion | 2 | Only hover glows and fast state fades; no flourish |
| Density | 7 | A settings tool: many options per screen, compact but legible |

## Philosophy

The interface borrows Path of Exile 1's visual grammar — near-black stone panels,
thin bronze/gold frames with a black inner line, Fontin SmallCaps everywhere, ornate
dividers with a center diamond — and swaps the game's signature red-orange accent for
a deep "verdant" green. Gold stays as the frame/heading metal; green marks everything
that is *active*: primary buttons, checked boxes, the selected tab, progress.

## Tokens

| Token | Value | Use |
|-------|-------|-----|
| void | `#070806` | Window edges, inner frame line |
| background | `#0B0C09` | Window |
| panel | `#11130E` → `#0C0D0A` | Framed panels (vertical gradient) |
| card | `#0E100B` | Sub-panels inside a panel |
| frame-dark | `#2B261A` | Quiet borders, list separators |
| frame-bronze | `#5E4E31` | Default frame / button border |
| frame-gold | `#8F7A4E` | Hover frame, ornaments |
| gold-light | `#C8B27D` | Headings, button text |
| gold-bright | `#E8D9A8` | Hovered / selected text |
| text | `#C9C2AE` | Body (parchment) |
| muted | `#8A8470` | Descriptions, hints |
| green-deep | `#0F2A16` | Primary button bottom, tab fill |
| green | `#1F5C2C` | Primary button top |
| green-mid | `#2F8040` | Accent (Fluent palette), borders of active things |
| green-bright | `#5DBE5A` | Checked glyph, OK status, focus |
| green-glow | `#9BEA86` | Hover glow on primary |
| amber | `#D7A54A` | Warnings, experimental badge |
| blood | `#D2554A` | Errors, broken status |
| magic-blue | `#8F9BFF` | Update banner (PoE "magic" blue) |

Font: `Fontin SmallCaps, Fontin, Palatino Linotype, Book Antiqua, Georgia, serif`
(Fontin is used when installed; it is not redistributed — its license is not OFL).
Mono: `Cascadia Mono, Consolas`.

Shape: radius 0 everywhere (1–2 px only on popups). Frames are 1 px bronze + 1 px
black inset line, which gives the PoE "double rule".

## Patterns

| Component | Spec |
|-----------|------|
| Button (secondary) | Dark bronze gradient, bronze 1px frame, black outer rule, gold-light Fontin 14; hover → gold frame + faint gold glow; pressed → darker, text shifts 1px |
| Button.primary | Green gradient `#1F5C2C → #0F2A16`, green-mid frame, gold-bright text; hover → green-bright frame + green glow |
| CheckBox | 18px sunken square, bronze frame; checked → green-bright check glyph + green frame glow |
| Tabs | Stash-tab style: bronze-framed tabs above a framed content panel; selected tab green-filled with gold-bright text |
| Panel header | Centered gold small-caps title over a horizontally fading green band, ornament divider below |
| Inputs | Sunken near-black field, bronze frame, green-bright frame on focus, radius 0 |
| Banners | Dark tinted fill + colored 1px frame (info blue, warn amber, danger blood) |

## Constraints

- **Never** use red/orange as an accent — that is the game's color, ours is green.
- **Never** round corners on frames or buttons.
- **Never** use bright fills for large areas; brightness is reserved for text and glows.
- **Always** keep gold for headings/frames and green for "active/on" states.
- **Always** keep body text ≥ 13px in Fontin (small caps have a low x-height).

## Effects

| State | Treatment |
|-------|-----------|
| Hover (button) | Frame → gold (secondary) / green-bright (primary), outer glow 0 0 10 |
| Pressed | Fill darkens, content offset 0,1 |
| Focus | 1px green-bright frame |
| Disabled | Opacity 0.4 |
| Transition | 120 ms brush/opacity |
