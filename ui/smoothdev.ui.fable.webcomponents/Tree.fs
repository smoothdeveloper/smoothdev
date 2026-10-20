/// <sd-tree .roots=… storage-key=… filterable>: a tree with carets, status dots and details; arrows, Home,
/// End, Enter as in any tree; an optional filter; expanded nodes remembered in localStorage under the
/// storage key. `sd-select` tells the selected id; set `selected` to follow a node (its ancestors open).
module smoothdev.ui.fable.webcomponents.Tree

open Fable.Core
open Fable.Core.JsInterop
open Browser
open Browser.Types
open Firelight
open type Firelight.Lit
open smoothdev.ui

[<AttachMembers>]
type SdTree() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "roots"     , PropertyDeclaration<Tree.Node list>(attribute = !^false)
      "selected"  , PropertyDeclaration<string>()
      "storageKey", PropertyDeclaration<string>(attribute = !^"storage-key")
      "filterable", PropertyDeclaration<bool>()
      "state"     , PropertyDeclaration<Tree.Tree>(state = true)
    ]

  static member styles =
    css $"""
:host {{ display: block; font: 12px var(--sd-sans, sans-serif); color: var(--sd-ink, #ccc); }}
input {{ width: 100%%; box-sizing: border-box; margin-bottom: 6px; font: 12px var(--sd-mono, monospace); color: var(--sd-ink, #ccc);
  background: var(--sd-bg, #111); border: 1px solid var(--sd-line, #444); border-radius: 6px; padding: 4px 8px; }}
ul {{ list-style: none; margin: 0; padding: 0; outline: none; }}
li {{ display: flex; align-items: center; gap: 6px; padding: 3px 6px; border-radius: 6px; cursor: pointer; white-space: nowrap; }}
li:hover {{ background: var(--sd-panel-2, #222); }}
li[aria-selected="true"] {{ background: var(--sd-glow, transparent); color: var(--sd-bright, #fff); }}
ul:focus-visible li[aria-selected="true"] {{ outline: 1px solid var(--sd-accent, #58a6ff); }}
.caret {{ width: 12px; color: var(--sd-muted, #888); text-align: center; }}
.dot {{ width: 7px; height: 7px; border-radius: 50%%; background: var(--sd-muted, #888); }}
.dot.ok {{ background: var(--sd-ok, #3fb950); }} .dot.busy {{ background: var(--sd-accent, #58a6ff); }}
.dot.warn {{ background: var(--sd-warn, #d29922); }} .dot.bad {{ background: var(--sd-bad, #f85149); }}
.detail {{ color: var(--sd-muted, #888); font-family: var(--sd-mono, monospace); margin-left: auto; padding-left: 12px; }}
"""

  member val roots: Tree.Node list = [] with get, set
  member val selected = "" with get, set
  member val storageKey = "" with get, set
  member val filterable = false with get, set
  member val state: Tree.Tree = Tree.create [] with get, set
  member val private restored = false with get, set

  member this.current =
    let s = { this.state with roots = this.roots }
    if not this.restored && this.storageKey <> "" then
      this.restored <- true
      match (try window.localStorage.getItem ("sd-tree:" + this.storageKey) with _ -> null) with
      | null -> s
      | text -> Tree.restore text s
    else s

  member this.commit (t: Tree.Tree) =
    let before = this.state.selected
    this.state <- t
    if this.storageKey <> "" then
      try window.localStorage.setItem ("sd-tree:" + this.storageKey, Tree.save t) with _ -> ()
    match t.selected with
    | Some id when t.selected <> before ->
      this.selected <- id
      Events.emit this "sd-select" id
    | _ -> ()

  override this.willUpdate(changed) =
    if changed.ContainsKey "selected" && this.selected <> "" && this.state.selected <> Some this.selected then
      this.state <- Tree.reveal this.selected this.current

  override this.render() =
    let t = this.current
    let key (e: KeyboardEvent) =
      let k =
        match e.key with
        | "ArrowUp" -> Some Tree.Up
        | "ArrowDown" -> Some Tree.Down
        | "ArrowLeft" -> Some Tree.Left
        | "ArrowRight" -> Some Tree.Right
        | "Home" -> Some Tree.Home
        | "End" -> Some Tree.End
        | "Enter" | " " -> Some Tree.Activate
        | _ -> None
      match k with
      | Some k -> e.preventDefault (); this.commit (Tree.key k t)
      | None -> ()
    let row (r: Tree.Row) =
      let caret = if r.expandable then (if r.expanded then "▾" else "▸") else ""
      let dot =
        match r.node.status with
        | Some s -> html $"""<span class={"dot " + Tree.statusName s}></span>"""
        | None -> nothing
      let click (e: MouseEvent) =
        let onCaret: bool = emitJsExpr e "$0.target.classList.contains('caret')"
        let t = Tree.select r.node.id t
        this.commit (if onCaret || r.selected then Tree.toggle r.node.id t else t)
      html $"""<li role="treeitem" aria-level={string (r.depth + 1)} aria-selected={string r.selected}
  aria-expanded={if r.expandable then string r.expanded else "undefined"} style={$"padding-left: {6 + r.depth * 14}px"} @click={click}>
  <span class="caret">{caret}</span>{dot}<span>{r.node.label}</span><span class="detail">{r.node.detail}</span></li>"""
    let filter =
      if this.filterable then
        html $"""<input type="search" placeholder="filter" .value={t.filter} @input={fun (e: Event) -> this.commit (Tree.setFilter (e.target :?> HTMLInputElement).value t)}>"""
      else nothing
    html $"""{filter}<ul role="tree" tabindex="0" @keydown={key}>{Tree.rows t |> List.map row}</ul>"""
