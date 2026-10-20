/// What the console shows, as the tool's `/api/console` sends it, and the view logic that needs no
/// browser (also compiled into the .NET tests). Field names are the JSON's.
module SmoothDev.Web.Gui.Model

type ScanRow =
  { path : string
    kind : string     // site | vite | server | config
    note : string }

type App =
  { dir        : string
    name       : string
    port       : int option
    fable      : string
    library    : bool
    middleware : bool
    outDir     : string }

type Mount =
  { url        : string
    route      : string
    kind       : string
    confidence : string  // high | medium | low
    pageOwned  : bool
    reasons    : string array
    app        : App }

type Process =
  { name   : string
    pid    : int
    port   : int
    url    : string
    state  : string
    uptime : string }

type Link =
  { label : string
    route : string }

type Site =
  { project   : string
    name      : string
    baseUrl   : string
    repo      : string
    output    : string
    pages     : int
    links     : Link array
    reasons   : string array
    dev       : string array
    mounts    : Mount array
    running   : Process array
    front     : string option
    distReady : bool }

type Unmounted =
  { app    : App
    reason : string }

type Message =
  { level : string
    text  : string
    at    : string
    age   : int }     // seconds since

/// Something running under the root, any app or site: `dir` is its folder relative to the root ("" for the root).
type Running =
  { dir    : string
    name   : string
    pid    : int
    port   : int
    url    : string
    state  : string
    uptime : string }

/// A port a launch claimed, with what it is for.
type LaunchPort =
  { port : int
    role : string
    url  : string }

/// A smoothdev-web invocation that runs, from the launch registry (~/.smoothdev/launches), any folder.
type Launch =
  { id      : string
    status  : string   // active | running
    by      : string   // the launcher, or the first parent process that is not a shell
    chain   : string   // the parent processes
    command : string
    dir     : string
    started : string   // how long ago
    ports   : LaunchPort array }

type Console =
  { root      : string
    demo      : bool
    scanning  : bool
    visited   : int
    scan      : ScanRow array
    sites     : Site array
    unmounted : Unmounted array
    busy      : string
    running   : Running array
    launches  : Launch array
    messages  : Message array }

/// Scan hits grouped by their first folder (a repository under a workspace), groups in path order,
/// a group whose hits are all at the top level named "." .
let groups (rows: ScanRow array) =
  rows
  |> Array.groupBy (fun r ->
    match r.path.IndexOf '/' with
    | -1 -> "."
    | i -> r.path.Substring(0, i))
  |> Array.sortBy fst
  |> Array.map (fun (g, rs) -> g, rs |> Array.sortBy (fun r -> (match r.kind with "site" -> 0 | "config" -> 1 | "vite" -> 2 | _ -> 3), r.path))

/// Rows whose path or note contains every word of the query (case-insensitive).
let filter (query: string) (rows: ScanRow array) =
  let words = query.ToLowerInvariant().Split([| ' ' |], System.StringSplitOptions.RemoveEmptyEntries)
  if words.Length = 0 then
    rows
  else
    rows |> Array.filter (fun r ->
      let text = (r.path + " " + r.note + " " + r.kind).ToLowerInvariant()
      words |> Array.forall text.Contains)

/// The site's row in the scan: its project path ends with the scan path.
let siteOfRow (sites: Site array) (row: ScanRow) =
  sites |> Array.tryFind (fun s -> row.kind = "site" && s.project.EndsWith row.path)

/// One line of the front proxy's routing: prefix, what answers it, its port (0 while not running).
type RouteLine =
  { prefix : string
    target : string
    port   : int
    app    : bool }

/// The front's routes for a site: each mounted app under its URL, longest first, then the site itself.
let routes (site: Site) =
  let portOf name =
    site.running |> Array.tryFind (fun p -> p.name = name) |> Option.map _.port |> Option.defaultValue 0
  let slug (m: Mount) =
    let n = m.app.dir.TrimEnd('/').Split('/') |> Array.last
    let cleaned = System.Text.RegularExpressions.Regex.Replace(n.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-')
    if cleaned = "" then "app" else cleaned
  [|
    yield!
      site.mounts
      |> Array.sortByDescending _.url.Length
      |> Array.map (fun m -> { prefix = m.url; target = m.app.name; port = portOf $"site-vite-{slug m}"; app = true })
    { prefix = site.baseUrl; target = $"{site.name} (Nacara)"; port = portOf "site-nacara"; app = false }
  |]

/// The messages shown as toasts: the newest few, while fresh.
let toasts (messages: Message array) =
  messages |> Array.filter (fun m -> m.age < 12) |> Array.truncate 3

let isRunning (site: Site) = site.front.IsSome

/// Confidence as a 0..3 meter.
let confidenceLevel (c: string) =
  match c with
  | "high" -> 3
  | "medium" -> 2
  | "low" -> 1
  | _ -> 0

/// What runs and can be opened: entries with a URL, a site's front first, then by folder and name.
let openable (running: Running array) =
  running
  |> Array.filter (fun r -> r.url <> "")
  |> Array.sortBy (fun r -> (if r.name = "site-front" then 0 elif r.name = "site-preview" then 1 else 2), r.dir, r.name)

/// The entries of a scan hit's app: those of the nearest folder, at or above the hit, that has some
/// (an app's state sits next to its config, which can be above the client folder).
let runningFor (running: Running array) (row: ScanRow) =
  let covers (dir: string) = dir = "" || row.path = dir || row.path.StartsWith(dir + "/")
  match running |> Array.map _.dir |> Array.distinct |> Array.filter covers |> Array.sortByDescending _.Length |> Array.tryHead with
  | Some dir -> running |> Array.filter (fun r -> r.dir = dir)
  | None -> [||]
