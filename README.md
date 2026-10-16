# D2RModelLodDescEditor

A GUI (and headless self-test) tool for **`model_lod_desc.bin`** — the Diablo II: Resurrected
model/LOD descriptor cache — that round-trips the shipped file **byte-for-byte**, edits
individual records, and re-emits a structurally identical replacement.

Everything here is derived from the shipped bytes / game disassembly of build 93847
(`data_93847/hd/model_lod_desc.bin`); nothing is guessed or hard-coded. The
`--selftest` mode (below) re-verifies byte-exactness after every change.

---

## 1. Requirements & build

| Item | Value |
|------|-------|
| SDK  | .NET 10 (`net10.0`), `WinExe` output |
| UI   | Avalonia 12.0.4 + Fluent theme (CommunityToolkit.Mvvm 8.4.1) |
| OS   | Windows (uses `kernel32.dll` `AttachConsole` for the headless report; the GUI is WinExe) |
| Granny | Bundled source (`Granny.Format`, see §7) — plain `.cs` files compiled into the assembly; **no NuGet package, no ProjectReference** |

```bat
cd D2RModelLodDescEditor        REM the project folder containing this README
dotnet build -c Debug           REM or Release
dotnet run --project .          REM launches the GUI
```

Build output: `bin/<config>/net10.0/D2RModelLodDescEditor(.exe)`.
The self-test report is written next to the executable as `selftest-report.txt`.

---

## 2. The `.bin` + `.json` pair

The descriptor never travels alone. A **model** is identified by its manifest key, a
relative path of the form:

```
data/hd/<path>.model
```

The two halves are matched by stem (`model_lod_desc.json` ↔ `model_lod_desc.bin` in the
same folder), via:

- `LodDescDocument.ManifestPathFor(bin)` → same stem, `.json`
- `LodDescDocument.FindPairedBin(json)` → same stem `.bin`, else a sibling `model_lod_desc.bin`
- `LodDescDocument.LoadPair(json)` → loads the bin **and** the manifest, or throws
  `FileNotFoundException` if no `.bin` is present.

The `.json` is a flat object — one property per asset, value `{"lods": <mask>}` — where
the key is the `data/hd/...model` path and `"lods"` is the **raw `lodMask` byte** for that
asset. Re-serializing an untouched pair is **byte-identical** to the shipped file.

---

## 3. GUI usage

### 3.1 Open / save

| Menu | Action |
|------|--------|
| **File → Open manifest .json…** | Loads the `.json` *and* its paired `.bin`. The only open entry point — the editor works on both halves together. |
| **File → Save** (`Ctrl+S`) | Repacks + writes the `.bin`; writes the manifest next to it when one is loaded. |
| **File → Save As…** | Choose a target `.bin` (manifest also paired by stem). |
| **File → Save manifest .json** | Write the in-memory manifest back out (compact single-line JSON, file order, `"lods"` only). Refuses if no manifest was loaded. |
| **File → Exit** | Closes the main window (app shuts down). |

**Only `File → Open manifest .json…` opens a file** — it also loads the paired `.bin`.
There is no separate "open .bin" menu item. (In code, `LodDescDocument.Load(bin)` *can*
load a `.bin` on its own, but without a manifest the assets appear as raw hashes and there
are no LOD / manifest cross-checks.) A manifest opened without a paired `.bin` is
*refused* (`LoadPair` throws `FileNotFoundException`).

### 3.2 Layout

Three linked lists (top → down = asset → slot → mesh), driven by selection:

```
Models[]  ──select──▶  Slots (LODs)  ──select──▶  Meshes
  (14k+)                (ModelInfo)              (ModelCachePerMeshData)
```

- **Search box** — filter by path substring or `0xHASH` (e.g. `0x3F9A…`). `Clear` resets.
- **Models[]** — one row per asset; label is the manifest path when the JSON is loaded,
  otherwise the 8-hex hash. `models[]` is always shown **ascending by hash** (the loader
  binary-searches it).
- **Slots** — one row per LOD the asset actually uses (rank among the set `lodMask` bits).
- **Meshes** — one row per mesh in the selected slot.

### 3.3 Editing a record

Selecting a model/slot/mesh opens an editor on the right. All edits are **write-through**
to the in-memory buffer and flagged dirty; they are only realized in the file on **Save**
(when structural) or **Save manifest** (for manifest-only changes).

Asset fields (`ModelAssetRecord`):

- **hash** — read-only. It is `FNV-1a32(lowercased key)`, so it is *derived from the path*,
  never typed. Editing the path in the manifest updates the hash.
- **path (key)** — the manifest key, resolved from the hash (or "(not in the loaded manifest)").
- **bounds (min/max X/Y/Z)** — the asset AABB, the union of its mesh boxes.
- **LOD checkboxes L0–L6** — toggle bits in `lodMask` (bit `i+1` ⇔ lod `i`).
- **typeMask** — root granny `ExtendedData.modelType`, hex editable.

Slot fields (`ModelInfoRecord`): `isMeshFlipbook`, mesh count, and a read-only rel target.

Mesh fields (`MeshRecord`): bounds min/max, `defaultRenderLayer`, `boneBindingsCount`,
`numBoneInfluences` (4-bit), `hasVertexAlpha`, `isRigid`.

### 3.4 Adding / removing

| Command | Effect |
|---------|--------|
| **+ model…** | Dialog: a **manifest key** (auto-guessed from a picked `.model` file — see §5), optional LOD mask + meshes/slot, or one-or-more `.model` files to import. Adds a *pending* asset (ascending by hash; manifest entry added 1:1 when a manifest is loaded). |
| **− model** | Removes the selected asset **and** its whole slot/mesh run, plus its manifest key. Materializes on save. |
| **Add mesh / Remove mesh** (slot selected) | Append/remove a pending mesh in the selected slot. |
| **Tools → Sort models[] by hash** | Re-sorts `models[]` ascending (safe no-op when already sorted). |
| **Tools → Recompute AABB from meshes** | Recomputes the asset bounds as the union of its mesh boxes (FLT_MAX / −FLT_MAX sentinels when an asset has no meshes). |
| **Tools → Integrity check…** | Runs `LodDescDocument.Integrity()` — region sizes, ascending-hash order, rel-target bounds, and manifest cross-checks (`lodMask` vs `"lods"`, missing paths). |

**Pending records** exist only in a shadow buffer until the next repack; repack grows the
file by exactly `44 + (slots×12) + (meshes×32)` per added asset and restores byte-for-byte
on removal.

### 3.5 Theme

**Theme → System default / Light / Dark** re-themes the app live (`Application.RequestedThemeVariant`).
The Dark palette uses a softer charcoal (`Window:dark`, `Menu:dark`, `ListBox:dark`,
`HeaderedContentControl:dark`) instead of the Fluent-near-black default. The choice is
**not** persisted across launches.

---

## 4. Command-line self-test

The executable is `WinExe` (no console), so the report is mirrored to
`selftest-report.txt` next to it (and also printed if you attach a console, e.g. `dotnet run`).

```bat
D2RModelLodDescEditor --selftest <bin> [json] [modelRoot]
```

| Argument | Meaning |
|----------|---------|
| `bin` | Path to `model_lod_desc.bin` (required). |
| `json` | Optional paired manifest. Enables pair-load, manifest-sync, and import checks. |
| `modelRoot` | Root the sampled `.model` files live under (the **`hd`** root, i.e. `data_93847/hd`). Required only for the import check; ignored without `json`. |

Exit code: `0` = PASS, `2` = bad usage, `>0` = N failures.

Sections run (each prints `ok` / `FAIL`):

1. **parse** — region counts + summary line.
2. **integrity** — structural audit.
3. **repack round-trip** — rebuild and compare **byte-for-byte** (pads included).
4. **manifest cross-check** — every bin hash has a path, every `"lods"` matches `lodMask`, no orphan keys.
5. **structural round-trip** — add/remove a mesh, repack, re-parse clean.
6. **structural keep** — save to disk, reload, field edit write-through.
7. **view-model** — list populated before any search; selection cascade; hash filter.
8. **add / remove model** — duplicate-hash guard, ascending insert, pending, byte-identical restore.
9. **add ordering** — before/middle/after inserts, slot LOD ranks, in-place hash edit re-sort, duplicate-hash refusal.
10. **GuessKey** — manifest key from a file path, incl. the double-`data/` hd-root case.
11. **pair load** — open `.json` pulls the `.bin`; lone-manifest refusal.
12. **manifest sync** — byte-identical re-serialize; add/remove mirror the JSON 1:1.
13. **import check** — parse ~200 sampled `.model` files and compare every derived field
    (typeMask, isMeshFlipbook, mesh counts, renderLayer, boneBindingCount, format word,
    bounds) against the shipped records. Counts/layer/bones/format are verified 100%;
    mesh bounds drift only on a minority of assets (stale extracted models).

Example against the shipped build:

```bat
D2RModelLodDescEditor --selftest data_93847/hd/model_lod_desc.bin ^
                                       data_93847/hd/model_lod_desc.json ^
                                       data_93847/hd
```

---

## 5. Importing from `.model` files

`+ model…` can take one or more granny `.model` files. Each file's LOD comes from its
`_lodN` filename suffix (0–7). `ModelImporter.GuessKey` derives the manifest key by
**anchoring on the *last* `/hd/`** in the picked path (so an extra `data/` layer such as
`…/data_93847/data/data/hd/…` keeps the `data/hd/` prefix) and stripping the `_lodN`
suffix; with no `/hd/` present it falls back to `data/hd/<stem>.model`.

Per mesh, `ModelImporter.ParseFiles` — a direct port of the probe's verified
`RegenEngine` decode path (itself derived from the offline writer) — fills the bin record
from the granny `ExtendedData`:

- bounds `bbMin`/`bbMax` (bitwise),
- `meshRenderLayer` (default 1),
- `granny_mesh.BoneBindingCount`,
- format word: `BoneWeights ArrayWidth | (vertexSize==24)<<4 | (!hasBones)<<5`.

The asset `typeMask` and `isMeshFlipbook` come from the root `ExtendedData`.

---

## 6. Binary format (`model_lod_desc.bin`)

All integers are **little-endian**. Three regions follow a fixed header:

```
┌─────────────────────────────────────────────────────────────┐
│ header  24 B                                                 │
│   0  u32  modelCount        number of ModelAssetRecord        │
│   4  i32  modelsRel         (modelsBase) − 4                  │
│   8  u32  modelInfoCount    number of ModelInfoRecord slots    │
│  12  i32  infoRel           (infoBase)   − 12                 │
│  16  u32  meshCount         number of MeshRecord               │
│  20  i32  meshRel           (meshBase)   − 20                 │
├─────────────────────────────────────────────────────────────┤
│ models[]              44 B each, ascending by hash            │
│ ModelInfo (slots)      12 B each, per-asset runs              │
│ ModelCachePerMeshData  32 B each, per-slot runs               │
└─────────────────────────────────────────────────────────────┘
```

The `rel` fields are **self-relative**: the target address = `fieldOffset + relValue`.
They are anchored at the rel field's own address (header+4 → models, +12 → ModelInfo,
+20 → mesh data), exactly how the loader reads them.

### 6.1 Region offsets (computed by the loader)

```
modelsBase = 24
infoBase   = 24 + modelCount * 44
meshBase   = infoBase + modelInfoCount * 12
```

The shipped build 93847: `modelCount = 14310`, `modelInfoCount = 58931`,
`meshCount = 131802` → `24 + 14310·44 + 58931·12 + 131802·32 = 5 554 500 B`.

### 6.2 `ModelAssetRecord` — 44 B

| Off | Type | Field | Notes |
|-----|------|-------|-------|
| +0  | u32  | `hash` | `FNV-1a32` of the **ASCII-lowercased** manifest key (seed `0x811C9DC5`, prime `0x01000193`). |
| +4  | f32×3 | `boundsMin` | Asset AABB min; `0x7F7FFFFF` (FLT_MAX) sentinel when no meshes. |
| +16 | f32×3 | `boundsMax` | Asset AABB max; `0xFF7FFFFF` (−FLT_MAX) sentinel. |
| +28 | u8   | `lodMask` | LOD set. **bit `i+1` ⇔ lod `i`**; bit 0 is a reserved flag (never set in shipped data). |
| +29 | u8×3 | `pad` | Uninitialized offline-writer heap — **carried verbatim** through repack. |
| +32 | u32  | `lodCount` | `popcount(lodMask)`; the loader asserts slot ordinal `< lodCount`. |
| +36 | i32  | `rel` | → first `ModelInfo` record. **Dead value for zero-LOD assets** (the loader's `lodMask` walk never dereferences it) — repack carries it verbatim. |
| +40 | u32  | `typeMask` | Root granny `ExtendedData.modelType`. |

### 6.3 `ModelInfoRecord` (a LOD slot) — 12 B

| Off | Type | Field | Notes |
|-----|------|-------|-------|
| +0  | u8   | `isMeshFlipbook` | File-level granny `ExtendedData` flag. |
| +1  | u8×3 | `pad` | Uninitialized heap, carried verbatim. |
| +4  | u32  | `meshCount` | Number of meshes in this slot. |
| +8  | i32  | `rel` | → first `ModelCachePerMeshData` record of the slot. |

A zero-`lodMask` asset has **no** `ModelInfo` run; its rel is dead.

### 6.4 `MeshRecord` (`ModelCachePerMeshData`) — 32 B

| Off | Type | Field | Notes |
|-----|------|-------|-------|
| +0  | f32×3 | `boundsMin` | Mesh AABB min. |
| +12 | f32×3 | `boundsMax` | Mesh AABB max. |
| +24 | u32  | `defaultRenderLayer` | granny `ExtendedData.meshRenderLayer` (default 1). |
| +28 | u16  | `boneBindingsCount` | `granny_mesh.BoneBindingCount`. |
| +30 | u16  | `fmt` | `numBoneInfluences:4` (BoneWeights ArrayWidth) \| `hasVertexAlpha:1` (vertexSize == 24) \| `isRigid:1` (!hasBones). |

### 6.5 Chaining & ordering

- **`models[]`** is sorted **ascending by `hash`**; the loader binary-searches it, so the
  editor re-sorts on any hash edit and refuses duplicate hashes.
- **Slots within an asset** are ordered by the *rank* of each set `lodMask` bit: the
  k-th set bit (counting from lod 0) is slot k. So `lodMask = 0x58` (`…0101 1000`)
  → bits 3, 4, 6 → slot ranks 0, 1, 2 serving **LOD 3, 4, 6** respectively.
- **`ModelInfo` runs** and their **mesh runs** are emitted in **manifest (key) order**,
  not `models[]` order. The editor preserves this emission order so an unmodified repack
  stays byte-identical (`LodDescDocument._emitOrder`).
- **`lodCount` is exactly `popcount(lodMask)`** — verified true for all 14 310 shipped
  records. Editing a LOD checkbox updates both together.

### 6.6 Known limits (by format, not bugs)

- **LOD 7 is not representable.** `lodMask` is a single byte and lod `i` maps to bit
  `i+1`: bits 1–7 cover L0–L6, and lod 7 would need bit 8, which `(byte)(1<<8)` truncates
  to 0. The parser/editor cannot see L7 for the same reason. The L7 checkbox is therefore
  disabled (with a tooltip) rather than a silent no-op.
- **Bit 0 of `lodMask` is reserved** — never set in shipped data; don't use it as an LOD.
- **Shipped content tops out at 5 LODs** (L0–L5); the format allows up to L6.

### 6.7 `model_lod_desc.json` (manifest) format

```json
{"data/hd/character/enemy/act2hire/basebody.model":{"lods":42},
 "data/hd/bvt/models/blend_sphere.model":{"lods":2}}
```

- Top level: object, one property per asset.
- Property name = manifest key (`data/hd/...model`, case-insignificant for collision checks).
- `"lods"` = the raw `lodMask` byte (e.g. `42 = 0x2A = bits 1,3,5` → L0, L2, L4).
- First key wins on a duplicate.
- Re-serialization is compact single-line JSON, **file order preserved, `"lods"` only**, and
  is byte-identical to the shipped file when nothing changed.

---

## 7. Layout / source map

All paths below are relative to this project folder.

| File | Responsibility |
|------|----------------|
| `Program.cs` | Entry; `--selftest` dispatch; `WinExe` → `AttachConsole`. |
| `App.axaml(.cs)` | Fluent theme + `:dark` charcoal override; theme runtime switch. |
| `Views/MainWindow.axaml(.cs)` | UI, menus, lists, record editor. |
| `ViewModels/MainWindowViewModel.cs` | MVVM glue, file dialogs, add/remove/import, theme, integrity. |
| `Models/LodDescDocument.cs` | Parse, regions, rel chains, manifest sync, `BuildBytes` repack, integrity. |
| `Models/ModelAssetRecord.cs` | 44-B asset record (write-through + pending shadow). |
| `Models/ModelInfoRecord.cs` | 12-B slot record. |
| `Models/MeshRecord.cs` | 32-B mesh record. |
| `Models/ModelImporter.cs` | `.model` → imported model (Granny `ExtendedData` → bin fields); `GuessKey`. |
| `SelfTest.cs` | Headless format audit (`--selftest`). |
| `Granny/` | **Bundled granny `.model` reader source** — plain `.cs` files in namespace `Granny.Format` (e.g. `GrnImage.cs`, `GrnTypeTree.cs`, `GrnObjectWalker.cs`), compiled **directly into the editor assembly** by the SDK's default `**/*.cs` glob. There is **no NuGet package and no `ProjectReference`**; the original external `Granny.Sharp` dependency lives only in the user's separate probe project and was ported in here as source. |
