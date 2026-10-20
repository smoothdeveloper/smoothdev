/// An editor of a list of rows (rings, circuits, layers…): add, duplicate, move, remove, edit a cell,
/// within a minimum and a maximum number of rows. Rows are records of named cells described by columns.
module smoothdev.ui.Rows

type Cell =
  | Number of float
  | Text of string
  | Flag of bool
  | Colour of string

type Kind =
  | NumberKind of low: float * high: float * step: float
  | TextKind
  | Choice of string list
  | FlagKind
  | ColourKind

type Column = { key: string; label: string; kind: Kind; initial: Cell }

type Row = Map<string, Cell>

type Rows =
  { columns : Column list
    rows    : Row list
    minRows : int
    maxRows : int }

let column key label kind initial = { key = key; label = label; kind = kind; initial = initial }

/// A row with every column at its initial value.
let blank (columns: Column list) : Row = columns |> List.map (fun c -> c.key, c.initial) |> Map.ofList

let create (columns: Column list) (minRows: int) (maxRows: int) (rows: Row list) =
  { columns = columns; rows = rows; minRows = max 0 minRows; maxRows = max minRows maxRows }

let canAdd (r: Rows) = r.rows.Length < r.maxRows
let canRemove (r: Rows) = r.rows.Length > r.minRows

/// A value made to fit its column: numbers clamped and snapped to the step, choices kept to the list.
let fit (c: Column) (v: Cell) =
  match c.kind, v with
  | NumberKind(lo, hi, step), Number n ->
    let n = max lo (min hi n)
    Number(if step > 0.0 then lo + System.Math.Round((n - lo) / step) * step |> min hi else n)
  | Choice options, Text t -> if List.contains t options then Text t else c.initial
  | TextKind, Text _ | FlagKind, Flag _ | ColourKind, Colour _ -> v
  | _ -> c.initial

let private at (i: int) (r: Rows) = i >= 0 && i < r.rows.Length

/// A row added at the end (a blank one, or a copy of the last).
let add (copyLast: bool) (r: Rows) =
  if not (canAdd r) then r
  else
    let row = if copyLast && not r.rows.IsEmpty then List.last r.rows else blank r.columns
    { r with rows = r.rows @ [ row ] }

/// Row `i` duplicated just below itself.
let duplicate (i: int) (r: Rows) =
  if not (canAdd r) || not (at i r) then r
  else { r with rows = r.rows |> List.mapi (fun j row -> if j = i then [ row; row ] else [ row ]) |> List.concat }

let remove (i: int) (r: Rows) =
  if not (canRemove r) || not (at i r) then r
  else { r with rows = r.rows |> List.indexed |> List.filter (fun (j, _) -> j <> i) |> List.map snd }

/// Row `i` moved to index `j` (the others keep their order).
let move (i: int) (j: int) (r: Rows) =
  if not (at i r) then r
  else
    let row = r.rows[i]
    let rest = r.rows |> List.indexed |> List.filter (fun (k, _) -> k <> i) |> List.map snd
    let j = max 0 (min rest.Length j)
    { r with rows = List.take j rest @ [ row ] @ List.skip j rest }

/// Cell `key` of row `i` set (fitted to its column).
let set (i: int) (key: string) (v: Cell) (r: Rows) =
  match r.columns |> List.tryFind (fun c -> c.key = key) with
  | Some c when at i r -> { r with rows = r.rows |> List.mapi (fun j row -> if j = i then Map.add key (fit c v) row else row) }
  | _ -> r

let get (i: int) (key: string) (r: Rows) = if at i r then Map.tryFind key r.rows[i] else None
