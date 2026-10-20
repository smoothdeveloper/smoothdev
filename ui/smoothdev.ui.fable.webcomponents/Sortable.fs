/// <sd-sortable .items=…>: a list reordered by dragging (`sd-reorder`, detail: the items in their new
/// order) that also takes text dragged in from elsewhere, such as an <sd-chip> in a toolbar (`sd-insert`,
/// detail: `{ index, text }`; the host adds the item it stands for).
module smoothdev.ui.fable.webcomponents.Sortable

open Fable.Core
open Fable.Core.JsInterop
open Browser.Types
open Firelight
open type Firelight.Lit
open smoothdev.ui

type Item = { id: string; label: string }

let item (id: string) (label: string) : Item = { id = id; label = label }

type Insert = { index: int; text: string }

let private internalType = "application/x-sd-sortable"

[<AttachMembers>]
type SdSortable() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "items" , PropertyDeclaration<Item array>(attribute = !^false)
      "marker", PropertyDeclaration<int>(state = true)
    ]

  static member styles =
    css $"""
:host {{ display: block; font: 12px var(--sd-sans, sans-serif); color: var(--sd-ink, #ccc); }}
ol {{ list-style: none; margin: 0; padding: 4px; border: 1px dashed var(--sd-line, #444); border-radius: 8px; min-height: 28px; }}
li {{ display: flex; align-items: center; gap: 8px; padding: 5px 8px; margin: 2px 0; background: var(--sd-panel-2, #222);
  border: 1px solid var(--sd-line, #444); border-radius: 6px; cursor: grab; }}
li .grip {{ color: var(--sd-muted, #888); }}
li.before {{ box-shadow: 0 -2px 0 var(--sd-accent, #58a6ff); }}
ol.after-last {{ box-shadow: inset 0 -2px 0 var(--sd-accent, #58a6ff); }}
"""

  member val items: Item array = [||] with get, set
  member val marker = -1 with get, set

  member this.dropIndex (e: DragEvent) =
    let lis: HTMLElement array = emitJsExpr this "Array.from($0.renderRoot.querySelectorAll('li'))"
    let middles = lis |> Array.map (fun li -> let r = li.getBoundingClientRect () in r.top + r.height / 2.0) |> List.ofArray
    Sortable.dropIndex middles e.clientY

  override this.render() =
    let over (e: DragEvent) =
      e.preventDefault ()
      this.marker <- this.dropIndex e
    let leave (_: DragEvent) = this.marker <- -1
    let dropped (e: DragEvent) =
      e.preventDefault ()
      let before = this.dropIndex e
      this.marker <- -1
      let from: string = e.dataTransfer.getData internalType
      if from <> "" then
        let items = Sortable.moveBefore (int from) before (List.ofArray this.items) |> Array.ofList
        if items <> this.items then
          this.items <- items
          Events.emit this "sd-reorder" items
      else
        match e.dataTransfer.getData "text/plain" with
        | "" -> ()
        | text -> Events.emit this "sd-insert" { index = before; text = text }
    let start (i: int) (e: DragEvent) =
      e.dataTransfer.setData (internalType, string i) |> ignore
      e.dataTransfer.effectAllowed <- "move"
    let lis =
      [ for i, it in Array.indexed this.items ->
          html $"""<li draggable="true" class={if this.marker = i then "before" else ""} @dragstart={start i}><span class="grip">⠿</span>{it.label}</li>""" ]
    let cls = if this.marker >= this.items.Length then "after-last" else ""
    html $"""<ol class={cls} @dragover={over} @dragleave={leave} @drop={dropped}>{lis}</ol>"""

/// <sd-chip text=…>: something to drag into an <sd-sortable> (or anywhere that reads text/plain).
[<AttachMembers>]
type SdChip() =
  inherit LitElement()

  static member properties = PropertyDeclarations.create [ "text", PropertyDeclaration<string>() ]

  static member styles =
    css $"""
:host {{ display: inline-block; }}
span {{ display: inline-block; padding: 3px 10px; font: 12px var(--sd-mono, monospace); color: var(--sd-bright, #fff);
  background: var(--sd-glow, transparent); border: 1px solid var(--sd-accent, #58a6ff); border-radius: 999px; cursor: grab; }}
"""

  member val text = "" with get, set

  override this.render() =
    let start (e: DragEvent) =
      e.dataTransfer.setData ("text/plain", this.text) |> ignore
      e.dataTransfer.effectAllowed <- "copy"
    html $"""<span draggable="true" @dragstart={start}><slot>{this.text}</slot></span>"""
