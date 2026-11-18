# ADR-003 — Art direction: storybook toon

| Field | Value |
|-------|-------|
| Status | Accepted |
| Date | 2026-11-18 |
| Decided by | Project owner, from the style study in `docs/art/style-study/index.html` |
| Options considered | A storybook toon, B faceted low-poly, C soft painted |

## Context
Everything on screen is still a generated placeholder. T-707 (appearance art) and any art pack need an art direction first, and the client's shading has to match it.
A style study rendered the three first jobs (Wanderer, Warden, Spellbinder) in three candidate styles. It used the game's own camera (50° pitch, 30° vertical field of view) at a close-up and at the default distance of 15.

## Decision
The game uses a **storybook toon** style.

1. **Shading.** Cel shading in three steps (shadow, mid, lit), with one warm directional light and a flat ambient. No PBR on characters. Glowing parts (crystals, runes, effects) are unlit.
2. **Outlines.** A dark outline that is a warm near-black, not pure black. It's drawn only on characters and interactable objects within about 10 cells of the camera target, and never on terrain. Outlines must keep the crowd peak under the §9 limit of 100 draw calls.
3. **Proportions.** Chibi-leaning, with a head about a third of the body height. The face tips slightly toward the camera so it reads at 50° pitch. Silhouettes are told apart by headgear, shoulders and weapon, not by small detail.
4. **Colour.** Bright, slightly saturated base colours from one shared palette per theme. Each job has a two-colour identity:
   - Wanderer: moss green and leather brown;
   - Warden: steel and Lumen blue;
   - Spellbinder: violet and teal, with gold trim.

   Skin and hair stay colour parameters (§7.5.1). Avoid pure black and pure white in materials.
5. **Textures.** Flat colour with at most a light hand-drawn gradient, in **one atlas per rig** (ADR-002 row 179). Equipment shares its rig's atlas, so a dressed character stays one draw call (two with the outline).
6. **World.** Terrain and props use the same three-step ramp without outlines. A soft sky gradient and light distance haze separate the play area from the background.
7. **Readability at game distance comes first.** Every new model is checked at the default zoom on a phone-sized view before it's accepted.

## Consequences
- The client needs a toon material, an outline pass limited by distance, and matching lighting: the toon rendering task (T-222). `themegen` placeholders move to the same look, so tests and screenshots match the target.
- T-707 and any art pack follow this ADR. Its points 4 and 5 are the brief for an artist.
- Faceted low-poly (B) stays a fallback for very low-end phones if on-device measurement (T-803) needs one. Soft painted (C) is not pursued.
- Original names and designs only (CLAUDE.md). The style study's characters are placeholders, not final designs.
