/// <sd-colour-sets .palettes=… selected=…>: pick a palette by name, see its roles as swatches, edit a
/// colour. `sd-palette` tells the edited palette, `sd-select` the chosen name.
module smoothdev.ui.fable.webcomponents.ColourSets

open Fable.Core
open Fable.Core.JsInterop
open Browser.Types
open Firelight
open type Firelight.Lit
open smoothdev.ui

[<AttachMembers>]
type SdColourSets() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "palettes", PropertyDeclaration<Colours.Palette array>(attribute = !^false)
      "selected", PropertyDeclaration<string>(reflect = true)
      "editable", PropertyDeclaration<bool>()
    ]

  static member styles =
    css $"""
:host {{ display: block; font: 12px var(--sd-sans, sans-serif); color: var(--sd-ink, #ccc); }}
.names {{ display: flex; gap: 6px; flex-wrap: wrap; margin-bottom: 8px; }}
.name {{ display: flex; align-items: center; gap: 6px; padding: 4px 8px; border: 1px solid var(--sd-line, #444); border-radius: 8px;
  background: transparent; color: inherit; font: inherit; cursor: pointer; }}
.name[aria-pressed="true"] {{ border-color: var(--sd-accent, #58a6ff); background: var(--sd-glow, transparent); color: var(--sd-bright, #fff); }}
.strip {{ display: inline-flex; border-radius: 4px; overflow: hidden; border: 1px solid var(--sd-line, #444); }}
.strip i {{ width: 10px; height: 14px; }}
.roles {{ display: grid; grid-template-columns: repeat(auto-fill, minmax(120px, 1fr)); gap: 6px; }}
label {{ display: flex; align-items: center; gap: 6px; padding: 4px; border: 1px solid var(--sd-line, #444); border-radius: 8px; }}
input[type=color] {{ width: 28px; height: 22px; border: none; padding: 0; background: transparent; }}
.role {{ display: flex; flex-direction: column; }}
.hex {{ font: 11px var(--sd-mono, monospace); color: var(--sd-muted, #888); }}
.bad {{ color: var(--sd-bad, #f85149); }}
"""

  member val palettes: Colours.Palette array = [||] with get, set
  member val selected = "" with get, set
  member val editable = true with get, set

  member this.sets : Colours.Sets = { palettes = List.ofArray this.palettes; selected = this.selected }

  override this.render() =
    let sets = this.sets
    let current = Colours.current sets
    let currentName = current |> Option.map _.name |> Option.defaultValue ""
    let pick (name: string) (_: Event) =
      this.selected <- name
      Events.emit this "sd-select" name
    let names =
      [ for p in sets.palettes ->
          let strip = [ for (_, c) in p.roles -> html $"""<i style={"background:" + c}></i>""" ]
          html $"""<button class="name" aria-pressed={string (p.name = currentName)} @click={pick p.name}>
  <span class="strip">{strip}</span>{p.name}</button>""" ]
    let edit (role: string) (e: Event) =
      let value: string = (e.target :?> HTMLInputElement).value
      let updated = Colours.update (Colours.setRole role value) sets
      this.palettes <- Array.ofList updated.palettes
      match Colours.current updated with
      | Some p -> Events.emit this "sd-palette" p
      | None -> ()
    let roles =
      match current with
      | None -> []
      | Some p ->
        [ for (role, colour) in p.roles ->
            let valid = (Colours.parse colour).IsSome
            let swatch =
              if this.editable && valid then html $"""<input type="color" .value={colour} @input={edit role}>"""
              else html $"""<i style={"display:inline-block;width:28px;height:22px;border-radius:4px;background:" + colour}></i>"""
            html $"""<label>{swatch}<span class="role">{role}<span class={if valid then "hex" else "hex bad"}>{colour}</span></span></label>""" ]
    html $"""<div class="names">{names}</div><div class="roles">{roles}</div>"""
