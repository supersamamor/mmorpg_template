# Themes: the contract, variants and the import tool

A **theme** is a folder `themes/<theme>/` with an `asset-manifest.json` that maps asset keys to files (ARCHITECTURE §8). This repository is a template: it ships
only the generated placeholder theme `default` (and the small test theme `test`). A game made from it is a fork that adds its own theme. This page says exactly
what a theme must provide, and how `tools/themeimport` turns an art pack into one. [FORKING.md](FORKING.md) is the step-by-step guide for a fork.

A theme may be **partial**: the client looks a key up in the active theme, then in `default` (§7.7). So a fork can replace the monsters first and leave the rest
to the placeholders. `validate --theme <theme>` checks the whole chain.

## 1. The contract

Rules marked **checked** are checked by `validate` (`src/Game.Content/Validation/ThemeCheck.cs`) and by `tools/themeimport`, with one finding per failure that
names the manifest, the key, the reason and the fix. Rules marked *by eye* cannot be measured from the files; check them in the game at the default zoom on a
phone-sized view (ADR-003 point 7).

### 1.1 Every model (`model.*`)

| Rule | Value | Check |
|------|-------|-------|
| Format | glTF 2.0 binary (`.glb`), one buffer, textures embedded | **checked** (a file that is not glTF is an error; `themeimport` also reads `.gltf` and embeds its files) |
| Mesh names | The mesh node is `Body_LOD0`; a cheaper `Body_LOD1` is optional | **checked** (`theme.model`) |
| Materials | 1 material per model (one draw call per character, two with the outline; §9) | **checked** (`theme.budget`) |
| Texture size | Each texture ≤ 1024 × 1024 (§9) | **checked** (`theme.budget`) |
| Atlas | A character's parts share one atlas image, so a dressed character merges into one surface (ADR-003 point 5, §7.5, §1.9) | **checked** as a warning for every character part (§1.9) |
| Scale | 1 cell = 1 world unit. A living model is 0.1 to 8 units tall; a worn piece or prop is at most 4 units across | **checked** (`theme.model`, with the `scale` to set) |
| Origin | On the ground under the model: its lowest point within 35 % of its height of the origin | **checked** as a warning (`warn.theme_look`) |
| Facing | Faces **-Z** (north at yaw 0) | **checked** for the humanoid rig (its right hand must be on +X); *by eye* for other rigs |
| Opaque | Materials are opaque (`alphaMode` `OPAQUE`). The toon look and the outline leave blended and cut-out materials alone | **checked** as a warning |

### 1.2 Living models (characters, monsters, NPCs: entries with `anims`)

| Rule | Value | Check |
|------|-------|-------|
| LOD0 budget | ≤ 5,000 triangles (the nearest 8 characters; §9) | **checked** |
| LOD1 budget | ≤ 2,000 triangles (everyone else). Without `Body_LOD1`, far characters draw LOD0, so LOD0 must then be ≤ 2,000 | **checked** |
| Skin | A model with `rig` has a skin, and the skeleton sits under a node named `Armature` (Godot imports it as `Armature/Skeleton3D`) | **checked** (`themeimport` renames or adds the node) |
| Same bones as its clips | Every bone its animation library moves exists in the model, by name | **checked** (`theme.anim`) |
| Library on the same rig | The model's `anims` library has the model's `rig` | **checked** |
| Outline directions | Each vertex carries its outline direction as glTF `TANGENT` (the normal averaged over the vertices at the same place) so the outline hull stays closed at hard edges | **checked** as a warning (`themeimport` writes them) |
| Proportions and palette | Chibi-leaning, head about a third of the height; the theme palette (ADR-003 points 3 and 4) | *by eye* |

### 1.3 The shared humanoid rig

Every character body, face, hairstyle and garment uses **one** rig named `humanoid`, so gear and appearance attach to any character (§7.5, §7.5.1), and, when textured, **one** atlas image (§1.9), so they merge into one draw call. Its bones
must include these names (more bones, such as fingers, are fine):

`hips` (the root, and the default attach point), `spine`, `head`, `arm_l`, `hand_l`, `arm_r`, `hand_r`, `leg_l`, `leg_r` — **checked**.

Other rigs (`blob`, `sprout`, `insect`, `quadruped` in the placeholders, or whatever a pack has) are theme-internal: any names, as long as each model and its
library agree. A pack's own bone names (`mixamorig:RightHand`, `Hand.R`) are mapped once per rig in the import mapping (§3).

### 1.4 Attachment points

| Kind | Manifest | Modelled in | Check |
|------|----------|-------------|-------|
| Rigid piece (weapon, shield, headgear, hairstyle) | `"attach": "<bone>"`, an attach point of VOCABULARY ("Attach points") | The bone's own space: origin at the bone, facing -Z | **checked**: the bone exists in the rig of every body model of `appearance.yaml` |
| Garment (armour, clothes, footgear) | `"skinned": "<rig>"` | The body's mesh space, skinned to the body's own bones | **checked**: the rig exists, and the garment uses no bone the rig lacks |
| Face | `"rig": "humanoid"` (no `anims`) | Skinned to the body's head | **checked** with the appearance rule (§7.5.1) |

The placeholders use `hand_r` (weapons), `hand_l` (shield) and `head` (caps, hair). VOCABULARY also names `face`, `mouth` and `back`: a rig that has those
bones may use them; the check stops an item that names a bone the rig lacks.

### 1.5 Animation libraries (`anim.*`)

| Rule | Check |
|------|-------|
| The entry names its `rig` | **checked** |
| `clips` maps the animation ids **idle, walk, attack, hit, die** to clip names in the file (cast, sit and pick_up are optional) | **checked**: each missing id is an error that lists the file's clips |
| Every mapped clip name exists in the file | **checked** |
| Every clip id is an animation id (VOCABULARY) or `<animation id>.<weapon type>` (`attack.staff`) | **checked** |
| The clips move only bones the rig has (same names as the models) | **checked** |
| Loops: **idle** and **walk** loop, the rest play once and the die clip holds its last frame | Not needed in the file: the client sets the loop mode itself, so a pack's loop flags do not matter |
| `impact_delay`: seconds from the start of the attack clip to the blow (on the library, or per model) | Optional; without it the client default (0.2 s) applies |

A pack that ships animations inside each model file is fine: point the `anim.*` entry at the same source file as the model.

### 1.6 The toon look on imported models (T-222, ADR-003)

The client draws real art the same way as the placeholders, with no code change:

- Every opaque, untextured or textured `BaseMaterial3D` the glTF importer makes becomes a **toon material** with the same colour and texture (three cel steps
  from one warm light). Vertex colours multiply the base colour. A vertex colour with alpha 0 is drawn unlit (glowing parts: crystals, runes).
- Blended or cut-out materials, unshaded (`KHR_materials_unlit`) materials and shader materials are left alone: they draw without the toon ramp and without the outline.
- The **outline** is an inverted hull drawn over living models and ground items within 10 cells of the camera target, at most 6 at a time. It pushes each vertex
  along its `TANGENT`, else its normal; without proper outline directions it opens at hard edges, so `themeimport` writes them. Normal maps are not used by the
  toon material, so overwriting a pack's tangents loses nothing in the toon look.
- Terrain and props get the toon ramp without outlines. Lighting, sky and haze come from the theme's `scene.environment`; a theme that has none uses `default`'s.
- To restyle the look itself, a theme provides its own `mat.toon`, `mat.outline` or `scene.environment` (entries tagged `"render"`, VOCABULARY "Render roles").

### 1.7 Appearance: bodies, faces, hairstyles (T-706)

A character is one mesh made of its gender's body, its face, its hairstyle, its job's pieces and its equipment (ARCHITECTURE §7.5.1). `content/appearance.yaml` names the body, face and hairstyle keys and the palettes; a
theme supplies the models. The character's colours are **vertex colours**: paint what should change with the colours below, and the client replaces them.

| Colour role | Takes | Painted with (the contract; sRGB of glTF `COLOR_0`) | On |
|-------------|-------|------------------------------------------------------|----|
| `skin` | The skin tone | `#f1c7a1`, and shades that follow it: `#e9a08a` (cheek) | Body, face |
| `hair` | The hair colour | A hairstyle as a whole, whatever its paint (its most common colour becomes the hair colour, lighter and darker strands keep their relation). On a body or face (a scalp, brows): `#7a4a2a`, `#c9b79a`, `#3a3040`, `#b5643a`, each on its own | Hairstyle, body, face |
| `cloth` | The job's `look.cloth_color` | `#efe3c8`, and its shade `#bbb19c` | Body |

- **Other paint:** name a model's own in its manifest entry: `"recolor": { "skin": ["#ffccaa", "#dd9977"], "cloth": [] }`. The first colour of a list becomes the character's, the others keep their relation to it; an empty
  list means nothing on this model takes that colour. On a hairstyle, naming `hair` colours only that paint, so a ribbon can stay. Names and values are **checked** (`theme.appearance`).
- **Textured art:** a vertex colour multiplies the texture, so leave skin and cloth white (or grey shading) in the atlas and paint them by vertex colour. Faces and hairstyles go on the body's atlas (§1.9); one with
  an image of its own stays a second surface, one more draw call per character (**checked** as a warning).
- **Body without paint:** a body whose skin paint no vertex has shows no skin tone, and one without cloth paint shows no job (**checked** as warnings).
- **Budgets:** the body with its largest face and hairstyle must be inside 5,000 triangles near and 2,000 far (**checked**, `theme.budget`); faces and hairstyles may have a `Body_LOD1`, and need one when the far sum is too big.
- **A hairstyle that headgear hides:** `"hides_hair": true` on the worn piece's entry (**checked**: a worn piece, true or false). The hairstyle is then not drawn at all, so the body needs a scalp under it (the placeholder body
  has one, painted `hair`). `default` has one example, `model.equip.leather_cap`.
- **Job pieces** (`look.pieces` in `content/jobs/*.yaml`) are worn pieces: exactly one of `attach` and `skinned` (**checked**), and gear in the piece's slot is drawn instead of it.
- *By eye*, on the creation screen (`run-e2e.ps1 -Run appearance -Windowed -Screenshot shot.png`): every hairstyle on every face, the darkest and lightest tones, and headgear over long hair.

### 1.8 Outfits (T-708)

An outfit's pieces (`content/outfits/*.yaml`, ARCHITECTURE §7.5.2) are **worn pieces**, like equipment: each `model.*` key needs exactly one of `attach` and `skinned` (**checked**), and is merged into the
character's one mesh in place of the gear of the slots it covers. Model a piece just outside the body (a coat over the torso and the hips, a hood over the head), on the shared `humanoid` rig.
- **One per gender** where the body differs: give the outfit a piece per gender for that slot (the template's Rain Cloak has two coats).
- **Colours:** a piece keeps its own vertex colours, unless the outfit lists `takes_colors`; then the parts painted with a role's colours (the contract's, §1.7, or the entry's `recolor`) take the
  character's. The template's coat is painted with the cloth paint (`#efe3c8`, `#bbb19c`), so it takes the job's colour. An outfit that takes a colour none of its pieces is painted with is **checked** as a warning.
- **`hides_hair`** on a head piece takes the hairstyle off while it shows (the template's hood); keep a scalp on the body.
- **Budgets:** a piece is a worn piece (1,000 triangles at most, **checked**), and the body with its outfit is one mesh: keep a full outfit in the size of the gear it replaces.
- Never a texture of its own: put it on the character's atlas (§1.9), or it is one more draw call per character wearing it (**checked** as a warning).

### 1.9 One draw call a character: the atlas rule (T-711)

The client merges a character's body, face, hairstyle, job pieces, outfit pieces and worn equipment into one mesh. Parts become **one surface** (one draw call; two with the outline) when their materials match:

- **The same image.** Every `.glb` embeds its own copy of a texture, so the client tells images apart by **content**: the bytes of the image as imported, hashed once per texture at load (`ImageIdentity`).
  The same atlas exported into twenty files is one image; one changed pixel is another. A part without a texture matches only parts without a texture.
- **The same material settings:** opaque, the base colour texture only (no normal, emission, occlusion or metallic-roughness map), the same roughness, metallic, sides and shading (unlit or not).
  The base colour *factor* may differ: it is baked into the vertex colours like the vertex-coloured art's.

**What an artist does for one draw call per character:** one atlas image per theme for all character parts (by asset key: every `model.char.body_*`, face, hairstyle, job piece, `model.outfit.*` and worn
`model.equip.*` a body can wear), at most 1024 × 1024, with one material on each model that points at it and the same settings everywhere. Paint skin, hair and cloth by vertex colour over white
or grey in the atlas (§1.7), so skin tones, hair colours and job colours still show. Or go without textures altogether, as `default` does: vertex colours only, which is also one image ("none").

**What costs a second draw call:** each part whose image or settings differ from its body's (a hairstyle on its own texture, an untextured weapon on a textured body, a cape with a normal map, a
see-through veil). It still draws, as one more surface on every character that has it at that moment, so 40 characters with one such part cost 40 draw calls more (§9: 100 in a scene).

**What `validate --theme <theme>` checks** (warnings, `warn.theme_look`, at the part's manifest line): for each gender, every part above whose `Body_LOD0` materials differ from its body's, naming the
part's key, the bodies it splits from, and what differs (the image, by its hash; "no texture"; the settings; "a material of its own"). It compares the image's bytes in the file, so the same
pixels encoded twice differently are a warning the client does not need (it compares the imported pixels); the same bytes are never missed. A body with more than one material is the budget's error (§1.1). An untextured part on an untextured body is not checked: other settings there (a metal blade) cost the surface they did before T-711, and this rule is about the atlas.

**Why a rule and not an atlas built at load:** packing the parts' textures into one atlas on the phone would need a texture as big as all of them (up to 1024² per part, so 4 MB each uncompressed,
more than the 1024² §9 allows a character), re-written texture coordinates in every part, mipmaps that bleed between neighbours, and the GPU-compressed formats (ETC2, ASTC) decompressed and
compressed again, at every start, on a worker; a texture that changes with every look also defeats the cache shared by every character. An artist packs the atlas once, in Blender, at no cost on the phone.

**Testing it:** `dotnet run --project tools/themegen -- --textured <name> [--other key,key]` writes `themes/<name>`, a textured copy of `default`'s character parts (one atlas in every file, another
image for the keys after `--other`), which ignores itself in Git and is left out of exports; `validate --theme <name>` shows the warnings, `--set theme=<name>` draws it, and `run-e2e.ps1 -Run textured`
checks it in the engine.

### 1.10 Minimap pictures (T-812)

Every map needs a picture for the minimap, under the key `minimap.<map id>` (`minimap.map.starter_meadow`). The key follows from the map's id and is never written in content; `validate` fails a map whose
key is not in the manifest (`asset.missing`, path `minimap`) — **checked**. The map export (`client/export-maps.ps1`) draws it from the baked grid into `maps/<name>.minimap.png` beside the map's scenes, so
a fork's map gets one by exporting it and adding the manifest line beside the scene key; `export-maps.ps1 -Check` fails a missing or stale one. Its colours (walkable, blocked, blocks line of sight, with
alpha) and pixels per cell are the plugin's `config.json` `minimap` section, so a theme's look is a config change and an export.

**Own art instead** (a painted map): point the key at any PNG that covers the whole map, north at the top, with the map's aspect ratio (its width in pixels divided by the map's width in cells is the
pixels per cell, the same both ways), at most 2048 pixels a side. The client draws the part around the player from it and never renders the world a second time. It is shown over a dark square, so
transparent pixels read as "nowhere to walk". `-Check` then reports it as stale (it is not the export's), so keep the export's picture in `default` and put the painted one in the fork's theme, which the
client looks up first (§7.7). Like any texture, it must show only what every player may see: no spawn areas, no secret rooms the grid does not have (hard rule 11).

## 2. Variants

One model can serve several keys. A variant entry names another entry's key in `variant_of` and draws that model, optionally changed:

```json
"model.monster.gel_slime_elite": { "variant_of": "model.monster.gel_slime", "tint": "#ffb070", "scale": 1.4, "outline": "#5a2410" }
```

| Field | Meaning |
|-------|---------|
| `variant_of` | The base key, looked up through the theme chain like any key, so a fork that redraws the base redraws its variants too |
| `tint` | `#rrggbb` the model's material colours are multiplied by (one extra material per variant, shared by every instance) |
| `scale` | A uniform scale on the whole model, 0.1 to 4; the origin stays on the ground |
| `outline` | `false` for no outline, or an outline colour `#rrggbb` |
| `impact_delay` | Optional; else the base's |

The variant takes the base's file, rig, animations and clips; it must not have `file`, `rig`, `anims`, `attach`, `skinned` or `clips` of its own. A variant may
stand on another variant (tints and scales multiply, the nearest outline override wins), at most 4 deep. Only whole models can be variants or bases
(`model.monster.*`, `model.npc.*`, `model.char.*` job models, `model.prop.*`): worn pieces merge into their character's mesh, where a tint or scale cannot apply.
A plain alias (`variant_of` alone) is the cheap way to let several monsters share one model while a pack has fewer models than the game has monsters.

All of it is **checked** (`theme.variant`): the base exists in the chain and has a file, no loop, no field that comes from the base, a valid tint, scale and
outline. `default` has one example, `model.monster.gel_slime_elite`, which no content uses yet: a fork's elite monster can point at it.

## 3. The import tool

`tools/themeimport` turns a pack folder into a theme folder from a small mapping file:

```powershell
dotnet run --project tools/themeimport -- --mapping theme-import/my-theme.yaml
```

It reads the mapping, writes `themes/<theme>/` (each file under the themegen layout: `models/monster/gel_slime.glb`, `anims/humanoid.glb`, `icons/item/x.png`,
`audio/...`) and its `asset-manifest.json`, runs the content boot and ThemeCheck for that theme and prints a report:

- **done**: each file written or unchanged, with the fixes made (bones renamed, mesh renamed, `Armature` added, scale, turn, outline directions);
- **missing**: the keys content uses that the theme does not list (the placeholders stand in), grouped by kind;
- **rules**: every finding, with its fix; the last line is `OK` or `FAILED`.

It is **idempotent**: the output depends only on the mapping and the pack, and a file is written only when its bytes change, so a second run reports every file
`unchanged`. `--check` writes nothing and exits 1 when a file would change. Files in the theme that the mapping no longer produces are reported as stale, not
deleted. It refuses to write `default` and `test` (they are generated by `tools/themegen`). Options: `--pack <dir>`, `--themes <dir>`, `--content <dir>`. Exit code
0 when the theme meets the contract (warnings allowed), 1 when not, 2 when the mapping cannot be used.

### 3.1 Mapping format

```yaml
theme: eval-meadow-pack        # the theme folder to write; eval-* is git-ignored, for trials
pack: ../packs/meadow-pack     # the pack folder, relative to this file (never committed: packs/ is git-ignored)

rigs:                          # fixes for every file on a rig: models, garments and the library alike
  humanoid:
    bones:                     # the pack's bone name: the contract's
      "mixamorig:Hips": hips
      "mixamorig:Spine": spine
      "mixamorig:Head": head
      "mixamorig:LeftArm": arm_l
      "mixamorig:LeftHand": hand_l
      "mixamorig:RightArm": arm_r
      "mixamorig:RightHand": hand_r
      "mixamorig:LeftUpLeg": leg_l
      "mixamorig:RightUpLeg": leg_r

assets:
  model.char.wanderer:
    source: characters/ranger.glb    # a file in the pack (.glb or .gltf for models and animations)
    rig: humanoid                    # manifest fields, as in ARCHITECTURE §8
    anims: anim.humanoid
    scale: 0.01                      # centimetres to cells
    facing: +z                       # the way the source faces: -z (default), +z, +x or -x; the tool turns it to -Z
    meshes: { lod0: Ranger, lod1: Ranger_low }   # only when the mesh nodes are not already Body_LOD0 / Body_LOD1
  anim.humanoid:
    source: characters/ranger.glb    # the clips may live in a model file
    rig: humanoid
    scale: 0.01
    facing: +z
    clips:                           # animation renames: animation id: the clip's name in the source
      idle: Idle_Loop
      walk: Walk_Fwd
      attack: Sword_Slash
      hit: Hit_React
      die: Death_Back
      attack.staff: Staff_Swing
    impact_delay: 0.3
  model.equip.iron_blade:
    source: weapons/sword.glb
    attach: hand_r                   # the attachment bone
    scale: 0.01
  model.monster.gel_slime:
    source: monsters/blob.gltf
    rig: blob
    anims: anim.blob
    facing: +x
  model.monster.gel_slime_elite:
    variant: { of: model.monster.gel_slime, tint: "#ffb070", scale: 1.4, outline: "#5a2410" }   # outline: "false" for none
  icon.item.iron_blade:
    source: icons/sword.png          # any other key: the file is copied as it is
  sfx.monster.die.generic:
    source: audio/splat.ogg
    extra: { bus: sfx, volume_db: -3 }   # more manifest fields, copied as they are

fallbacks:                           # optional: the manifest's fallbacks table
  missing: model.missing
```

Per asset: `source`, `scale` (default 1), `facing` (default `-z`), `rig`, `anims`, `attach`, `skinned`, `clips`, `impact_delay`, `meshes` (`lod0`, `lod1`),
`outline_directions` (default true), `variant` (`of`, `tint`, `scale`, `outline`) instead of a source, and `extra`. An unknown field is an error with its line.

### 3.2 What the tool fixes and what it does not

| It fixes | How |
|----------|-----|
| Bone names | `rigs.<rig>.bones` renames joints in every file on that rig |
| Mesh names | `meshes`, or a file's only mesh node becomes `Body_LOD0` |
| The `Armature` node | The node that holds the root bone is renamed, or a new one is put above a root bone that is a scene root |
| Scale and facing | Composed into the scene roots' transforms (a root that a clip moves gets a new parent instead) |
| Outline directions | Written as `TANGENT` on every primitive |
| Clip names | Recorded in the manifest's `clips`; the file keeps its own names |

It does not decimate meshes, merge several meshes or materials into one, bake an atlas, move an origin or convert FBX: those need Blender, and the report says
which rule failed and what to do.

## 4. Testing without downloads

`tests/Game.Tests/Tools/ThemeImportTests.cs` writes a small synthetic pack in code (`SyntheticPack.cs`: a humanoid in centimetres facing +Z with exporter bone,
mesh and clip names; a slime whose root bone has no `Armature`; a sword; an icon), imports it, and checks that the fixed mapping meets the contract, that a
second run changes nothing, and that without the fixes each rule fails with its fix. No pack is downloaded by the tests. A trial with a real pack needs the
project owner's approval first (licence, size): put it in the git-ignored `packs/` folder and write the theme as `eval-<name>`.
