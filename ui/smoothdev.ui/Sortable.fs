/// A list reordered by dragging, and items dragged in from a toolbar: where a drop lands, and the list
/// after it.
module smoothdev.ui.Sortable

/// The index a drop at `y` inserts at, given the vertical middle of each item in order (pixels, or any
/// unit along the list's axis).
let dropIndex (middles: float list) (y: float) =
  middles |> List.tryFindIndex (fun m -> y < m) |> Option.defaultValue middles.Length

/// The item at `from` moved so that it lands before the item that was at `before` (0..length).
let moveBefore (from: int) (before: int) (items: 'T list) =
  if from < 0 || from >= items.Length then items
  else
    let item = items[from]
    let rest = items |> List.indexed |> List.filter (fun (i, _) -> i <> from) |> List.map snd
    let at = if before > from then before - 1 else before
    let at = max 0 (min rest.Length at)
    List.take at rest @ [ item ] @ List.skip at rest

/// A new item inserted at `index` (clamped), as from a toolbar.
let insertAt (index: int) (item: 'T) (items: 'T list) =
  let at = max 0 (min items.Length index)
  List.take at items @ [ item ] @ List.skip at items

/// What a drop does: a move within the list, or an insert of something from outside it.
type Drop<'T> =
  | Move of from: int
  | Insert of 'T

let drop (d: Drop<'T>) (before: int) (items: 'T list) =
  match d with
  | Move from -> moveBefore from before items
  | Insert item -> insertAt before item items
