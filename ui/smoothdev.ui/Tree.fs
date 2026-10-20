/// A tree view's model: nodes with a status, which are expanded, which is selected, a filter, and the
/// rows a target draws (depth first, only under expanded nodes, or every match and its ancestors while
/// filtering). Keyboard moves follow the usual tree conventions.
module smoothdev.ui.Tree

open System

[<RequireQualifiedAccess>]
type Status =
  | Idle
  | Ok
  | Busy
  | Warn
  | Bad

type Node =
  { id       : string
    label    : string
    status   : Status option
    /// Small text after the label (a count, a port).
    detail   : string
    children : Node list }

type Tree =
  { roots    : Node list
    expanded : Set<string>
    selected : string option
    filter   : string }

type Row = { node: Node; depth: int; expandable: bool; expanded: bool; selected: bool }

let node id label children = { id = id; label = label; status = None; detail = ""; children = children }

let leaf id label = node id label []

let create (roots: Node list) = { roots = roots; expanded = Set.empty; selected = None; filter = "" }

let rec private walk (f: Node -> Node list -> unit) (path: Node list) (ns: Node list) =
  for n in ns do
    f n path
    walk f (n :: path) n.children

/// Every node, with the ids of its ancestors (nearest first).
let all (t: Tree) =
  let acc = ResizeArray()
  walk (fun n path -> acc.Add(n, path |> List.map _.id)) [] t.roots
  List.ofSeq acc

let tryFind (id: string) (t: Tree) = all t |> List.tryFind (fun (n, _) -> n.id = id) |> Option.map fst

let ancestors (id: string) (t: Tree) = all t |> List.tryFind (fun (n, _) -> n.id = id) |> Option.map snd |> Option.defaultValue []

let private matches (q: string) (n: Node) =
  q = "" || n.label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || n.detail.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0

/// The rows to draw.
let rows (t: Tree) : Row list =
  let q = t.filter.Trim()
  let rec keep (n: Node) = matches q n || n.children |> List.exists keep
  let rec go depth (ns: Node list) =
    [ for n in ns do
        if q = "" || keep n then
          let open' = if q = "" then t.expanded.Contains n.id else n.children |> List.exists keep
          yield { node = n; depth = depth; expandable = not n.children.IsEmpty; expanded = open'; selected = t.selected = Some n.id }
          if open' then yield! go (depth + 1) n.children ]
  go 0 t.roots

let expand (id: string) (t: Tree) = { t with expanded = t.expanded.Add id }
let collapse (id: string) (t: Tree) = { t with expanded = t.expanded.Remove id }
let toggle (id: string) (t: Tree) = if t.expanded.Contains id then collapse id t else expand id t

/// Select a node and open its ancestors so it shows (follow).
let reveal (id: string) (t: Tree) =
  match tryFind id t with
  | Some _ -> { t with selected = Some id; expanded = ancestors id t |> List.fold (fun s a -> Set.add a s) t.expanded }
  | None -> t

let select (id: string) (t: Tree) = { t with selected = Some id }

let setFilter (q: string) (t: Tree) = { t with filter = q }

type Key =
  | Up
  | Down
  | Left
  | Right
  | Home
  | End
  /// Enter or space: open or close.
  | Activate

/// The tree after a key: up and down move along the rows; right opens, then goes to the first child;
/// left closes, then goes to the parent; home and end go to the first and the last row.
let key (k: Key) (t: Tree) =
  let rs = rows t
  let index = rs |> List.tryFindIndex (fun r -> r.selected)
  let at i = { t with selected = Some rs[max 0 (min (rs.Length - 1) i)].node.id }
  if rs.IsEmpty then t
  else
    match k, index with
    | (Up | Down | Left | Right | Activate), None -> at 0
    | Up, Some i -> at (i - 1)
    | Down, Some i -> at (i + 1)
    | Home, _ -> at 0
    | End, _ -> at (rs.Length - 1)
    | Right, Some i ->
      let r = rs[i]
      if r.expandable && not r.expanded then expand r.node.id t
      elif r.expandable then at (i + 1)
      else t
    | Left, Some i ->
      let r = rs[i]
      if r.expandable && r.expanded then collapse r.node.id t
      else
        match ancestors r.node.id t with
        | parent :: _ -> { t with selected = Some parent }
        | [] -> t
    | Activate, Some i -> if rs[i].expandable then toggle rs[i].node.id t else t

/// The expanded ids as text, to remember between sessions; `restore` reads it back.
let save (t: Tree) = t.expanded |> Set.toList |> String.concat "\n"

let restore (text: string) (t: Tree) =
  { t with expanded = text.Split '\n' |> Array.filter (fun s -> s <> "") |> Set.ofArray }

let statusName =
  function
  | Status.Idle -> "idle"
  | Status.Ok -> "ok"
  | Status.Busy -> "busy"
  | Status.Warn -> "warn"
  | Status.Bad -> "bad"
