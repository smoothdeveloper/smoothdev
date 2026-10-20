/// <sd-rows .model=…>: a table of rows with a cell editor per column (number, text, choice, flag, colour),
/// and per row move up, move down, duplicate, remove; add at the end. `sd-rows` tells the new model.
module smoothdev.ui.fable.webcomponents.Rows

open Fable.Core
open Fable.Core.JsInterop
open Browser.Types
open Firelight
open type Firelight.Lit
open smoothdev.ui
open smoothdev.ui.Rows

[<AttachMembers>]
type SdRows() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "model", PropertyDeclaration<Rows>(attribute = !^false)
    ]

  static member styles =
    css $"""
:host {{ display: block; font: 12px var(--sd-sans, sans-serif); color: var(--sd-ink, #ccc); }}
table {{ border-collapse: collapse; width: 100%%; }}
th {{ text-align: left; font-weight: 600; color: var(--sd-muted, #888); padding: 4px 6px; border-bottom: 1px solid var(--sd-line, #444); }}
td {{ padding: 3px 6px; border-bottom: 1px solid var(--sd-line, #444); }}
td.index {{ color: var(--sd-muted, #888); font-family: var(--sd-mono, monospace); width: 2em; }}
input, select {{ font: 12px var(--sd-mono, monospace); color: var(--sd-ink, #ccc); background: var(--sd-bg, #111);
  border: 1px solid var(--sd-line, #444); border-radius: 4px; padding: 2px 4px; }}
input[type=number] {{ width: 6em; }}
input[type=color] {{ width: 32px; padding: 0; height: 22px; }}
button {{ font: 12px var(--sd-mono, monospace); color: var(--sd-ink, #ccc); background: transparent; border: 1px solid var(--sd-line, #444);
  border-radius: 4px; padding: 1px 6px; cursor: pointer; }}
button:hover:not(:disabled) {{ border-color: var(--sd-accent, #58a6ff); }}
button:disabled {{ opacity: .35; cursor: default; }}
.actions {{ white-space: nowrap; text-align: right; }}
.add {{ margin-top: 6px; }}
.count {{ color: var(--sd-muted, #888); margin-left: 8px; }}
"""

  member val model: Rows = create [] 0 0 [] with get, set

  member this.change (f: Rows -> Rows) =
    this.model <- f this.model
    Events.emit this "sd-rows" this.model

  member this.cell (i: int) (c: Column) =
    let v = Map.tryFind c.key this.model.rows[i] |> Option.defaultValue c.initial
    let value (e: Event) : string = (e.target :?> HTMLInputElement).value
    let put (cell: Cell) = this.change (set i c.key cell)
    match c.kind, v with
    | NumberKind(lo, hi, step), Number n ->
      html $"""<input type="number" min={string lo} max={string hi} step={if step > 0.0 then string step else "any"} .value={string n}
        @change={fun (e: Event) -> match System.Double.TryParse(value e) with | true, x -> put (Number x) | _ -> this.requestUpdate ()}>"""
    | Choice options, Text t ->
      let opts = [ for o in options -> html $"""<option value={o} ?selected={o = t}>{o}</option>""" ]
      html $"""<select @change={fun (e: Event) -> put (Text(value e))}>{opts}</select>"""
    | FlagKind, Flag b ->
      html $"""<input type="checkbox" .checked={b} @change={fun (e: Event) -> put (Flag((e.target :?> HTMLInputElement).``checked``))}>"""
    | ColourKind, Colour col -> html $"""<input type="color" .value={col} @input={fun (e: Event) -> put (Colour(value e))}>"""
    | _, Text t -> html $"""<input type="text" .value={t} @change={fun (e: Event) -> put (Text(value e))}>"""
    | _, other -> html $"""<span>{string other}</span>"""

  override this.render() =
    let m = this.model
    let n = m.rows.Length
    let header = [ for c in m.columns -> html $"""<th>{c.label}</th>""" ]
    let row (i: int) =
      let cells = [ for c in m.columns -> html $"""<td>{this.cell i c}</td>""" ]
      html $"""<tr><td class="index">{string (i + 1)}</td>{cells}<td class="actions">
  <button title="move up" ?disabled={i = 0} @click={fun (_: Event) -> this.change (move i (i - 1))}>↑</button>
  <button title="move down" ?disabled={i = n - 1} @click={fun (_: Event) -> this.change (move i (i + 1))}>↓</button>
  <button title="duplicate" ?disabled={not (canAdd m)} @click={fun (_: Event) -> this.change (duplicate i)}>⧉</button>
  <button title="remove" ?disabled={not (canRemove m)} @click={fun (_: Event) -> this.change (remove i)}>✕</button>
</td></tr>"""
    html $"""
<table><thead><tr><th></th>{header}<th></th></tr></thead><tbody>{[ for i in 0 .. n - 1 -> row i ]}</tbody></table>
<div class="add"><button ?disabled={not (canAdd m)} @click={fun (_: Event) -> this.change (add true)}>+ row</button>
<span class="count">{string n} of {string m.minRows}..{string m.maxRows}</span></div>"""
