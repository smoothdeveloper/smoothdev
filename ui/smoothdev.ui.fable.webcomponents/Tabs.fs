/// <sd-tabs .tabs=… selected=…>: a row of tabs; click or arrow keys select, `sd-select` tells (detail: the
/// id).
module smoothdev.ui.fable.webcomponents.Tabs

open Fable.Core
open Fable.Core.JsInterop
open Browser.Types
open Firelight
open type Firelight.Lit
open smoothdev.ui

[<AttachMembers>]
type SdTabs() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "tabs"    , PropertyDeclaration<string array>(attribute = !^false)
      "labels"  , PropertyDeclaration<string array>(attribute = !^false)
      "selected", PropertyDeclaration<string>(reflect = true)
    ]

  static member styles =
    css $"""
:host {{ display: block; }}
.tabs {{ display: flex; gap: 4px; flex-wrap: wrap; }}
button {{ font: 12px var(--sd-mono, monospace); color: var(--sd-muted, #888); background: transparent;
  border: 1px solid var(--sd-line, #444); border-radius: 999px; padding: 3px 10px; cursor: pointer; }}
button[aria-selected="true"] {{ color: var(--sd-bright, #fff); border-color: var(--sd-accent, #58a6ff); background: var(--sd-glow, transparent); }}
button:focus-visible {{ outline: 2px solid var(--sd-accent, #58a6ff); outline-offset: 1px; }}
"""

  /// Tab ids, in order.
  member val tabs: string array = [||] with get, set
  /// Labels shown, by position (the ids when absent).
  member val labels: string array = [||] with get, set
  member val selected = "" with get, set

  member this.model =
    let tabs =
      this.tabs |> Array.mapi (fun i id -> Tabs.tab id (if i < this.labels.Length then this.labels[i] else id)) |> List.ofArray
    { Tabs.create tabs with selected = (if this.selected = "" then None else Some this.selected) }

  member this.choose (m: Tabs.Tabs) =
    match Tabs.current m with
    | Some id when id <> this.selected ->
      this.selected <- id
      Events.emit this "sd-select" id
      emitJsStatement this "$0.updateComplete.then(() => $0.renderRoot.querySelector('button[aria-selected=true]')?.focus())"
    | _ -> ()

  override this.render() =
    let m = this.model
    let current = Tabs.current m |> Option.defaultValue ""
    let key (e: KeyboardEvent) =
      match e.key with
      | "ArrowRight" -> e.preventDefault (); this.choose (Tabs.next m)
      | "ArrowLeft" -> e.preventDefault (); this.choose (Tabs.previous m)
      | _ -> ()
    let buttons =
      [ for t in m.tabs ->
          let sel = t.id = current
          html $"""<button role="tab" aria-selected={string sel} tabindex={if sel then "0" else "-1"}
            @click={fun (_: Event) -> this.choose (Tabs.select t.id m)}>{t.label}</button>""" ]
    html $"""<div class="tabs" role="tablist" @keydown={key}>{buttons}</div>"""
