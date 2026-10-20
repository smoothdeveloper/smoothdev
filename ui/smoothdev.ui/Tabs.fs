/// Tabs: an ordered set of named tabs, one selected; next and previous wrap, closing selects a neighbour.
module smoothdev.ui.Tabs

type Tab = { id: string; label: string; closable: bool }

type Tabs = { tabs: Tab list; selected: string option }

let tab id label = { id = id; label = label; closable = false }

let create (tabs: Tab list) = { tabs = tabs; selected = tabs |> List.tryHead |> Option.map _.id }

/// The selected tab's id: the chosen one if it is still there, the first otherwise.
let current (t: Tabs) =
  match t.selected with
  | Some id when t.tabs |> List.exists (fun x -> x.id = id) -> Some id
  | _ -> t.tabs |> List.tryHead |> Option.map _.id

let select (id: string) (t: Tabs) = if t.tabs |> List.exists (fun x -> x.id = id) then { t with selected = Some id } else t

let private shift (by: int) (t: Tabs) =
  match current t with
  | None -> t
  | Some id ->
    let i = t.tabs |> List.findIndex (fun x -> x.id = id)
    let n = t.tabs.Length
    { t with selected = Some t.tabs[((i + by) % n + n) % n].id }

let next (t: Tabs) = shift 1 t
let previous (t: Tabs) = shift -1 t

/// The tabs replaced (as when the processes behind them change), keeping the selection when it remains.
let setTabs (tabs: Tab list) (t: Tabs) = { t with tabs = tabs } |> fun t -> { t with selected = current t }

let close (id: string) (t: Tabs) =
  match t.tabs |> List.tryFindIndex (fun x -> x.id = id) with
  | None -> t
  | Some i ->
    let rest = t.tabs |> List.filter (fun x -> x.id <> id)
    let selected =
      if current t = Some id then rest |> List.tryItem (min i (rest.Length - 1)) |> Option.map _.id
      else t.selected
    { tabs = rest; selected = selected }
