/// Registers every element once: <sd-tabs>, <sd-scrubber>, <sd-colour-sets>, <sd-rows>, <sd-sortable>,
/// <sd-chip>, <sd-tree>, <sd-editor>.
module smoothdev.ui.fable.webcomponents.Elements

open Fable.Core.JsInterop
open Firelight
open type Firelight.Lit

let private defined (name: string) : bool = emitJsExpr name "customElements.get($0) !== undefined"

let register () =
  if not (defined "sd-tabs") then defineElement<Tabs.SdTabs> "sd-tabs"
  if not (defined "sd-scrubber") then defineElement<Scrubber.SdScrubber> "sd-scrubber"
  if not (defined "sd-colour-sets") then defineElement<ColourSets.SdColourSets> "sd-colour-sets"
  if not (defined "sd-rows") then defineElement<Rows.SdRows> "sd-rows"
  if not (defined "sd-sortable") then defineElement<Sortable.SdSortable> "sd-sortable"
  if not (defined "sd-chip") then defineElement<Sortable.SdChip> "sd-chip"
  if not (defined "sd-tree") then defineElement<Tree.SdTree> "sd-tree"
  if not (defined "sd-editor") then defineElement<Editor.SdEditor> "sd-editor"
