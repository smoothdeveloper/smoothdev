---
title: UI controls
order: 2
---

Controls that several projects need (a scrubber, colour sets, a row editor, a tree, tabs, a code editor
host, theme tokens) live here once, in two libraries:

| library | what | depends on |
|---|---|---|
| `smoothdev.ui` | the controls' models: state, messages and rules, as plain F# values; theme tokens as data | FSharp.Core |
| `smoothdev.ui.fable.webcomponents` | the controls as Lit custom elements written in F# (Firelight), and the tokens as CSS custom properties | `smoothdev.ui`, Fable.Core, Firelight |

The split keeps every rule (where a drop lands, what a key does in a tree, how a value fits its column,
when playback stops) in `smoothdev.ui`, tested on .NET and compiled by Fable alike. A target only draws
the model and turns its events into messages, so other targets (Avalonia, a terminal UI, Slint) can come
later as `smoothdev.ui.<target>` beside the web one, without touching the models. The patching layer
(ports, cables, patches) is not here: it is [Sutradhara](https://github.com/smoothdeveloper/sutradhara),
which a patch bay element will build on.

## The controls

| control | model (`smoothdev.ui`) | element |
|---|---|---|
| theme tokens | `Theme`: named values for a dark and a light scheme, `extend` for an application's own | `Css.stylesheet` → `--sd-*` on `:root`, light under `data-theme="light"` |
| scrubber and transport | `Scrubber`: position, duration, playing, looping, rate, step; `update` | `<sd-scrubber>`: restart, step, play, loop, track, time; keys space ← → Home |
| colour sets | `Colours`: palettes of roles, hex parsing, mix, WCAG contrast, sets | `<sd-colour-sets>`: pick a palette, edit a role's colour |
| row editor | `Rows`: columns (number, text, choice, flag, colour), rows within a min and a max; add, duplicate, move, remove, fit | `<sd-rows>` |
| sortable list | `Sortable`: drop index, move, insert from outside | `<sd-sortable>` and `<sd-chip>` (drag from a toolbar) |
| tree | `Tree`: nodes with a status and a detail, expanded, selected, filter, keys, reveal, save and restore | `<sd-tree>`: carets, status dots, keyboard, filter, remembered |
| tabs | `Tabs`: select, next and previous (wrapping), close, replace | `<sd-tabs>` |
| editor host | `Editor`: a `Language` (tokens, problems, hover) the owner plugs in, spans, lines and columns | `<sd-editor>`: coloured text, toolbar slot, problems, hover at the cursor |

Elements send custom events that bubble out of shadow roots (`sd-select`, `sd-position`, `sd-play`,
`sd-palette`, `sd-rows`, `sd-reorder`, `sd-insert`, `sd-change`), with F# values as the detail. Every
element styles itself with the `--sd-*` tokens and falls back to neutral values without them.

```fsharp
open smoothdev.ui
open smoothdev.ui.fable.webcomponents

Elements.register ()
Events.installTheme (Css.stylesheet Css.prefix Theme.dark Theme.light)
// html $"""<sd-tabs .tabs={[| "vite"; "server" |]} @sd-select={pick}></sd-tabs>"""
```

The smoothdev-web console uses the tokens (with its own colours per kind of process added by
`Theme.extend`) and `<sd-tabs>` for its logs. `ui/gallery` shows every control on one page:
`pnpm install && pnpm dev` there.

## Names and versions

- **Namespaces** are lowercase and dotted, one segment per layer: `smoothdev.ui`,
  `smoothdev.ui.fable.webcomponents`, `smoothdev.web` for the tool. Modules inside keep their capitals
  (`smoothdev.ui.Tree`). .NET's own guidelines capitalise namespaces; F# accepts either, and lowercase keeps
  the name the same as the package and the folder.
- **Package ids** are the namespaces (`smoothdev.ui`), as the tool's `smoothdev.web` already is. NuGet ids
  are case-insensitive, and nuget.org shows them as written.
- **Local versions** are `0.0.0.1-alpha.g<short sha>` until a release: `dotnet fsi scripts/pack_local.fsx`
  packs the committed `HEAD` (from a temporary clone) into `~/.nuget/local-feed`, with the F# sources in
  the package's `fable/` folder for Fable. NuGet sorts prerelease labels alphabetically, so a sha does not
  say which pack is newer: consumers pin the exact version, never a range.
- When a consumer sits outside this repository, Vite resolves the components' `lit` import from the
  consumer: add `resolve: { dedupe: ['lit', 'lit-html', 'lit-element', '@lit/reactive-element'] }` when the
  sources are referenced from another folder.
