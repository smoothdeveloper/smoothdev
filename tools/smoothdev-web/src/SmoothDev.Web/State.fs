/// The state file (.smoothdev/web/state.json in the app folder): what this app has running, so a second
/// invocation can show and stop it. Writes are atomic (temp file + rename) under a lock file.
module SmoothDev.Web.State

open System
open System.IO
open System.Text.Json
open System.Threading

let dir root = root </> ".smoothdev" </> "web"
let file root = dir root </> "state.json"
let logDir root = dir root </> "logs"

let logFile root name =
  logDir root </> $"{name}.log"

let jsonOptions = JsonSerializerOptions(WriteIndented = true)

let read root : Entry array =
  let path = file root

  if not (File.Exists path) then
    [||]
  else
    try
      match JsonSerializer.Deserialize<Entry array>(File.ReadAllText path, jsonOptions) with
      | null -> [||]
      | entries -> entries
    with _ ->
      [||]

let write root (entries: Entry array) =
  let path = file root
  let tmp = $"{path}.{Environment.ProcessId}.tmp"
  File.WriteAllText(tmp, JsonSerializer.Serialize(entries, jsonOptions))
  File.Move(tmp, path, true)

/// Runs f while holding the app's state lock (an exclusive lock file; .NET maps FileShare.None to flock).
let withLock root (f: unit -> 'a) =
  Directory.CreateDirectory(dir root) |> ignore
  let lockPath = dir root </> "state.lock"

  let rec acquire attempt =
    try
      new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
    with :? IOException when attempt < 200 ->
      Thread.Sleep 25
      acquire (attempt + 1)

  use _lock = acquire 0
  f ()

let update root (f: Entry array -> Entry array) =
  withLock root (fun () ->
    let entries = f (read root)
    write root entries
    entries)

let add root (e: Entry) =
  update
    root
    (fun entries -> Array.append (entries |> Array.filter (fun x -> x.name <> e.name)) [| e |])
  |> ignore

let remove root (name: string) =
  update
    root
    (Array.filter (fun x -> x.name <> name))
  |> ignore

/// The leader still runs and still leads the recorded group (guards against pid reuse).
let leaderAlive (e: Entry) =
  Posix.alive e.pid && (e.pgid = 0 || Posix.processGroup e.pid = e.pgid)

/// Something of the entry still runs: its leader, or a non-zombie member of its process group.
/// `kill -0` on the group stays true for zombies and for a group this process cannot signal, which
/// left a dead dev server shown as orphaned. `ps` is the check that drops those.
let isAlive (e: Entry) =
  leaderAlive e || (e.pgid > 0 && Posix.groupHasLiveMember e.pgid)

let runState (e: Entry) =
  if leaderAlive e then
    Running
  elif isAlive e then
    Orphaned
  else
    Stopped

/// Entries that still run; dead ones are dropped from the file.
let live root =
  let entries = read root
  let alive = entries |> Array.filter isAlive
  if alive.Length <> entries.Length then
    update root (Array.filter isAlive) |> ignore
  alive

let find root (name: string) =
  live root
  |> Array.tryFind (fun e -> e.name = name)
