/// Every control of smoothdev.ui.fable.webcomponents on one page, wired to show what it sends.
module smoothdev.ui.gallery.Gallery

open Fable.Core
open Fable.Core.JsInterop
open Browser
open Browser.Types
open Firelight
open type Firelight.Lit
open smoothdev.ui
open smoothdev.ui.fable.webcomponents

let palettes =
  [| Colours.palette "ink" [ "background", "#101418"; "stroke", "#e6e1d6"; "fill", "#2b3440"; "accent", "#f0b72f"; "highlight", "#ff6b6b" ]
     Colours.palette "lotus" [ "background", "#fff7ef"; "stroke", "#7a2e3a"; "fill", "#f4c7c3"; "accent", "#d9480f"; "highlight", "#2b8a3e" ]
     Colours.ofTheme Theme.dark |] 

let ringColumns =
  [ Rows.column "petals" "petals" (Rows.NumberKind(3.0, 64.0, 1.0)) (Rows.Number 8.0)
    Rows.column "radius" "radius" (Rows.NumberKind(0.0, 1.0, 0.01)) (Rows.Number 0.5)
    Rows.column "shape" "shape" (Rows.Choice [ "petal"; "circle"; "triangle" ]) (Rows.Text "petal")
    Rows.column "filled" "filled" Rows.FlagKind (Rows.Flag true)
    Rows.column "colour" "colour" Rows.ColourKind (Rows.Colour "#f0b72f") ]

let rings =
  let r = Rows.create ringColumns 1 6 [ Rows.blank ringColumns ]
  r |> Rows.duplicate 0 |> Rows.set 1 "petals" (Rows.Number 16.0) |> Rows.set 1 "radius" (Rows.Number 0.8) |> Rows.set 1 "shape" (Rows.Text "circle")

let tree =
  [ { Tree.node "sites" "sites" [ { Tree.leaf "docs" "docs" with status = Some Tree.Status.Ok; detail = ":8080" }
                                  { Tree.node "app" "playground" [ { Tree.leaf "vite" "vite" with status = Some Tree.Status.Busy; detail = ":5173" }
                                                                   { Tree.leaf "server" "server" with status = Some Tree.Status.Bad; detail = "exited" } ] with status = Some Tree.Status.Warn } ] with detail = "2" }
    { Tree.leaf "others" "other instances" with status = Some Tree.Status.Idle; detail = "3" } ]

let toy =
  { Editor.plain with
      name = "toy"
      tokens = Editor.words [ "let"; "in"; "pulse"; "every" ]
      problems =
        fun text ->
          match text.IndexOf "??" with
          | -1 -> []
          | i -> [ { range = { start = i; finish = i + 2 }; severity = Editor.Severity.Error; message = "a hole to fill" } ]
      hover = fun text at -> if at > 0 && at <= text.Length && text.Substring(0, at).EndsWith "pulse" then Some "pulse: a trigger on every step" else None }

[<AttachMembers>]
type SdGallery() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "said" , PropertyDeclaration<string>(state = true)
      "items", PropertyDeclaration<Sortable.Item array>(state = true)
      "tab"  , PropertyDeclaration<string>(state = true)
    ]

  static member styles =
    css $"""
:host {{ display: block; max-width: 1100px; margin: 0 auto; padding: 20px; }}
h1 {{ font: 600 18px var(--sd-sans); color: var(--sd-bright); margin: 0 0 4px; }}
.said {{ font: 12px var(--sd-mono); color: var(--sd-muted); min-height: 1.5em; margin-bottom: 12px; }}
.grid {{ display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }}
section {{ background: var(--sd-panel); border: 1px solid var(--sd-line); border-radius: 12px; padding: 12px 14px; }}
section.wide {{ grid-column: 1 / -1; }}
h2 {{ font: 600 11px var(--sd-sans); text-transform: uppercase; letter-spacing: .8px; color: var(--sd-muted); margin: 0 0 10px; }}
.toolbar {{ display: flex; gap: 6px; margin-bottom: 8px; }}
button.theme {{ float: right; font: 13px var(--sd-mono); color: var(--sd-ink); background: transparent; border: 1px solid var(--sd-line); border-radius: 6px; cursor: pointer; }}
"""

  member val said = "" with get, set
  member val items: Sortable.Item array = [| for n in [ "bindu"; "petals"; "bhupura" ] -> Sortable.item n n |] with get, set
  member val tab = "vite" with get, set

  override this.connectedCallback() =
    base.connectedCallback ()
    Events.installTheme (Css.stylesheet Css.prefix Theme.dark Theme.light)

  override this.render() =
    let tell (name: string) = fun (e: Event) -> this.said <- $"{name}: {JS.JSON.stringify (Events.detail<obj> e)}"
    let insert (e: Event) =
      let d = Events.detail<Sortable.Insert> e
      this.items <- Sortable.insertAt d.index (Sortable.item (d.text + string this.items.Length) d.text) (List.ofArray this.items) |> Array.ofList
      this.said <- $"sd-insert: {d.text} at {d.index}"
    let reorder (e: Event) =
      this.items <- Events.detail<Sortable.Item array> e
      this.said <- "sd-reorder: " + (this.items |> Array.map _.label |> String.concat ", ")
    html $"""
<button class="theme" title="light / dark" @click={fun (_: Event) -> Events.toggleScheme ()}>◐</button>
<h1>smoothdev.ui</h1>
<div class="said">{if this.said = "" then "events show here" else this.said}</div>
<div class="grid">
  <section class="wide"><h2>scrubber</h2>
    <sd-scrubber duration="12" position="3.4" step="0.1" @sd-play={tell "sd-play"}></sd-scrubber></section>
  <section><h2>tabs</h2>
    <sd-tabs .tabs={[| "vite"; "server"; "nacara" |]} selected={this.tab} @sd-select={fun (e: Event) -> this.tab <- Events.detail<string> e; tell "sd-select" e}></sd-tabs></section>
  <section><h2>sortable list, toolbar</h2>
    <div class="toolbar"><sd-chip text="lotus">lotus</sd-chip><sd-chip text="star">star</sd-chip><sd-chip text="circuit">circuit</sd-chip></div>
    <sd-sortable .items={this.items} @sd-reorder={reorder} @sd-insert={insert}></sd-sortable></section>
  <section><h2>tree</h2>
    <sd-tree .roots={tree} storage-key="gallery" selected="vite" filterable @sd-select={tell "sd-select"}></sd-tree></section>
  <section><h2>colour sets</h2>
    <sd-colour-sets .palettes={palettes} selected="ink" @sd-select={tell "sd-select"} @sd-palette={tell "sd-palette"}></sd-colour-sets></section>
  <section class="wide"><h2>row editor</h2>
    <sd-rows .model={rings} @sd-rows={fun (e: Event) -> this.said <- $"sd-rows: {(Events.detail<Rows.Rows> e).rows.Length} rows"}></sd-rows></section>
  <section class="wide"><h2>editor host</h2>
    <sd-editor .language={toy} value={"let heart = pulse every 1/4\n// a comment\nin heart ?? 2"} @sd-change={fun (_: Event) -> this.said <- "sd-change"}>
      <span slot="toolbar">toolbar slot</span></sd-editor></section>
</div>"""

Elements.register ()
defineElement<SdGallery> "sd-gallery"
