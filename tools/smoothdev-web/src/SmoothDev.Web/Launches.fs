/// The launch registry, ~/.smoothdev/launches: one JSON file per smoothdev-web invocation that starts
/// something (site dev, dev start, gui...). It records who started it (the parent process and its
/// ancestors, and a launcher name a caller passes in SMOOTHDEV_WEB_LAUNCHER), where, the ports it
/// claimed and the processes it started. Ports are claimed in the registry, under its lock, before
/// anything binds them, so two invocations started together (in one folder or in two) never pick the
/// same port. A launch whose command and processes have all exited moves to launches/ended.
module SmoothDev.Web.Launches

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading

/// A process above the launch: pid, executable name and command line.
type Ancestor =
  { pid     : int
    name    : string
    command : string }

/// A port the launch holds: claimed (pid 0) before its process binds it, then the process's pid.
type PortClaim =
  { port : int
    role : string
    url  : string
    pid  : int }

/// A process (group) the launch started, as recorded in its app folder's state file.
type Child =
  { name : string
    pid  : int
    root : string
    port : int
    url  : string
    log  : string }

type Launch =
  { id        : string
    pid       : int             // the smoothdev-web process
    ppid      : int
    ancestors : Ancestor array  // parent first
    launcher  : string          // SMOOTHDEV_WEB_LAUNCHER, "" when unset
    user      : string
    cwd       : string
    dir       : string          // the folder the command works on (--dir)
    command   : string array    // smoothdev-web's arguments
    startedAt : DateTimeOffset
    ports     : PortClaim array
    children  : Child array
    endedAt   : string }        // "" while it runs

/// ~/.smoothdev, or SMOOTHDEV_HOME.
let home () =
  match Environment.GetEnvironmentVariable "SMOOTHDEV_HOME" with
  | null | "" -> Environment.GetFolderPath Environment.SpecialFolder.UserProfile </> ".smoothdev"
  | h -> h

let dirOf home = home </> "launches"
let endedDirOf home = dirOf home </> "ended"
let fileOf home id = dirOf home </> $"{id}.json"

let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

let toJson (l: 'a) = JsonSerializer.Serialize(l, jsonOptions)

let private readFile (path: string) : Launch option =
  try
    match JsonSerializer.Deserialize<Launch>(File.ReadAllText path, jsonOptions) with
    | null -> None
    | l -> Some l
  with _ ->
    None

let private writeTo (path: string) (l: Launch) =
  Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
  let tmp = $"{path}.{Environment.ProcessId}.{Thread.CurrentThread.ManagedThreadId}.tmp"
  File.WriteAllText(tmp, toJson l)
  File.Move(tmp, path, true)

/// Runs f while holding the registry's lock (an exclusive lock file, flock on Unix): every claim
/// reads the others and writes its own inside it.
let withLock home (f: unit -> 'a) =
  Directory.CreateDirectory(dirOf home) |> ignore
  let lockPath = dirOf home </> ".lock"

  let rec acquire attempt =
    try
      new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
    with :? IOException when attempt < 400 ->
      Thread.Sleep 25
      acquire (attempt + 1)

  use _lock = acquire 0
  f ()

let private readDir (dir: string) =
  if Directory.Exists dir then
    Directory.GetFiles(dir, "*.json") |> Array.choose readFile
  else
    [||]

/// Something of the launch still runs: its own command, or a process it started.
let isAlive (l: Launch) =
  ProcessManagement.alive l.pid
  || l.children |> Array.exists (fun c -> ProcessManagement.alive c.pid || (c.pid > 0 && ProcessManagement.groupHasLiveMember c.pid))

/// "active" while its command runs (a foreground command such as gui stays there), "running" when
/// the command has exited and its processes run, "ended" after.
let status (l: Launch) =
  if l.endedAt <> "" then "ended"
  elif ProcessManagement.alive l.pid then "active"
  elif isAlive l then "running"
  else "ended"

let private keepEnded = 50

/// Moves launches with nothing left running to ended/ (the newest 50 are kept); returns the live ones.
/// Call under the lock.
let sweepUnlocked home =
  let all = readDir (dirOf home)
  let live, dead = all |> Array.partition isAlive

  for l in dead do
    let ended = { l with endedAt = DateTimeOffset.Now.ToString "o"; ports = [||] }
    writeTo (endedDirOf home </> $"{l.id}.json") ended
    File.Delete(fileOf home l.id)

  if dead.Length > 0 then
    let files = Directory.GetFiles(endedDirOf home, "*.json") |> Array.sortDescending

    for f in files |> Array.skip (min files.Length keepEnded) do
      File.Delete f

  live

let sweep home = withLock home (fun () -> sweepUnlocked home)

/// The live launches, oldest first; with all, the ended ones too, newest first, after them.
let list home all =
  let live = sweep home |> Array.sortBy _.startedAt

  if all then
    Array.append live (readDir (endedDirOf home) |> Array.sortByDescending _.startedAt)
  else
    live

/// Every port a live launch holds.
let claimedPorts (live: Launch array) =
  live |> Array.collect (fun l -> l.ports |> Array.map _.port)

/// The process table as (pid, ppid, executable, command line); empty where ps is missing.
let processTable () =
  let ps (columns: string) =
    try
      let info = ProcessStartInfo("ps", $"-A -o pid= -o ppid= -o {columns}=", RedirectStandardOutput = true, UseShellExecute = false)
      use p = Process.Start info |> nonNull
      let text = p.StandardOutput.ReadToEnd()
      p.WaitForExit()
      text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
      |> Array.choose (fun line ->
        let parts = line.Trim().Split([| ' ' |], 3, StringSplitOptions.RemoveEmptyEntries)
        if parts.Length < 2 then None
        else
          match Int32.TryParse parts[0], Int32.TryParse parts[1] with
          | (true, pid), (true, ppid) -> Some(pid, (ppid, if parts.Length > 2 then parts[2] else ""))
          | _ -> None)
      |> Map.ofArray
    with _ ->
      Map.empty

  if OperatingSystem.IsWindows() then
    [||]
  else
    let comm = ps "comm"
    let args = ps "args"
    [| for KeyValue(pid, (ppid, exe)) in comm ->
         pid, ppid, (match Path.GetFileName exe with null -> exe | n -> n), (match args.TryFind pid with Some(_, a) -> a | None -> exe) |]

/// A command line as recorded: its first 200 characters (a shell running a script carries the script).
let private clip (command: string) =
  if command.Length <= 200 then command else command.Substring(0, 200) + "…"

/// The processes above pid in a process table, its parent first, up to the first process (or 16 levels).
let ancestryIn (table: Map<int, int * string * string>) (pid: int) =
  let rec up (p: int) depth acc =
    match table.TryFind p with
    | Some(ppid, _, _) when ppid > 0 && depth < 16 ->
      match table.TryFind ppid with
      | Some(_, name, command) -> up ppid (depth + 1) ({ pid = ppid; name = name; command = clip command } :: acc)
      | None -> List.rev acc
    | _ -> List.rev acc

  up pid 0 [] |> Array.ofList

let tableOf (rows: (int * int * string * string) array) =
  rows |> Array.map (fun (p, pp, n, c) -> p, (pp, n, c)) |> Map.ofArray

/// The processes above pid, its parent first, up to the first process (or 16 levels).
let ancestry (pid: int) = ancestryIn (tableOf (processTable ())) pid

/// A chain that ends at the first process at once: the parent exited before it was read (a command
/// started with `(cmd &)`, or a supervisor that detached).
let orphaned (chain: Ancestor array) = chain.Length = 0 || (chain.Length = 1 && chain[0].pid = 1)

/// Who started pid, even when its parent has exited: its own ancestry, else (orphaned) the leader of
/// its process group when that is another live process (the shell a `(cmd &)` ran in), and above it.
let starterOf (table: Map<int, int * string * string>) (pid: int) (pgid: int) =
  let own = ancestryIn table pid
  if orphaned own && pgid > 1 && pgid <> pid then
    match table.TryFind pgid with
    | Some(_, name, command) -> Array.append [| { pid = pgid; name = name; command = clip command } |] (ancestryIn table pgid)
    | None -> own
  else
    own

/// SMOOTHDEV_WEB_ANCESTRY: the chain a smoothdev-web process recorded when it started, handed to the
/// processes it starts, so a detached one still knows who started the launch.
let ancestryVariable = "SMOOTHDEV_WEB_ANCESTRY"

let encodeAncestry (chain: Ancestor array) = JsonSerializer.Serialize chain

let decodeAncestry (text: string) : Ancestor array =
  try
    match JsonSerializer.Deserialize<Ancestor array> text with
    | null -> [||]
    | a -> a
  with _ ->
    [||]

/// The chain to record: its own, unless it is orphaned and an inherited one says more.
let chooseAncestry (own: Ancestor array) (inherited: Ancestor array) =
  if orphaned own && inherited.Length > 0 then inherited else own

/// A launch for this process, not yet in the registry.
let create (dir: string) (command: string array) (ancestors: Ancestor array) =
  let pid = Environment.ProcessId
  { id        = $"{DateTime.Now:``yyyyMMdd-HHmmss``}-{pid}"
    pid       = pid
    ppid      = (if ancestors.Length > 0 then ancestors[0].pid else 0)
    ancestors = ancestors
    launcher  = (match Environment.GetEnvironmentVariable "SMOOTHDEV_WEB_LAUNCHER" with | null -> "" | v -> v)
    user      = Environment.UserName
    cwd       = Environment.CurrentDirectory
    dir       = dir
    command   = command
    startedAt = DateTimeOffset.Now
    ports     = [||]
    children  = [||]
    endedAt   = "" }

/// Applies f to the launch's registry file (seeded with `me` the first time), under the lock.
let updateUnlocked home (me: Launch) (f: Launch -> Launch) =
  let path = fileOf home me.id
  let current = readFile path |> Option.defaultValue me
  let next = f current
  writeTo path next
  next

/// Claims a port for role: the first in [preferred, preferred + 100) that no live launch holds, that is
/// not in `reserved` and that isFree accepts (Ports.isFree: nothing listens, it can be bound). The claim
/// is written before the lock is released, so a concurrent claim skips it. The claims of the launch
/// `parent` (SMOOTHDEV_WEB_LAUNCH, set for what a launch starts: `gui --detach` hands its port to the
/// GUI process it starts) count as this one's.
let claimFor home (parent: string) (me: Launch) (role: string) (preferred: int) (reserved: int array) (isFree: int -> bool) =
  withLock home (fun () ->
    let live = sweepUnlocked home
    let taken = Array.append reserved (claimedPorts (live |> Array.filter (fun l -> l.id <> parent)))
    let port = Ports.pickWith isFree taken preferred 100
    updateUnlocked home me (fun l -> { l with ports = Array.append (l.ports |> Array.filter (fun p -> p.port <> port)) [| { port = port; role = role; url = ""; pid = 0 } |] })
    |> ignore
    port)

let claim home (me: Launch) role preferred reserved isFree = claimFor home "" me role preferred reserved isFree

/// Records a process the launch started, and binds its port's claim to it.
let addChild home (me: Launch) (c: Child) =
  withLock home (fun () ->
    updateUnlocked home me (fun l ->
      { l with
          children = Array.append (l.children |> Array.filter (fun x -> x.name <> c.name || x.root <> c.root)) [| c |]
          ports =
            if c.port > 0 && not (l.ports |> Array.exists (fun p -> p.port = c.port)) then
              Array.append l.ports [| { port = c.port; role = c.name; url = c.url; pid = c.pid } |]
            else
              l.ports |> Array.map (fun p -> if p.port = c.port then { p with url = c.url; pid = c.pid } else p) })
    |> ignore)

/// Drops a stopped process (and its port) from whichever live launch has it.
let removeChild home (root: string) (name: string) (pid: int) =
  withLock home (fun () ->
    for l in readDir (dirOf home) do
      if l.children |> Array.exists (fun c -> c.pid = pid && c.name = name && c.root = root) then
        updateUnlocked home l (fun l ->
          { l with
              children = l.children |> Array.filter (fun c -> not (c.pid = pid && c.name = name))
              ports = l.ports |> Array.filter (fun p -> p.pid <> pid) })
        |> ignore)

/// When the command exits: the claims no process took are released; a launch with nothing running
/// is moved to ended/.
let finish home (me: Launch) =
  withLock home (fun () ->
    if File.Exists(fileOf home me.id) then
      updateUnlocked home me (fun l -> { l with ports = l.ports |> Array.filter (fun p -> p.pid <> 0) }) |> ignore
      sweepUnlocked home |> ignore)

/// The live launch holding port.
let holderOf home (port: int) =
  list home false |> Array.tryFind (fun l -> l.ports |> Array.exists (fun p -> p.port = port))

/// Who started it: the launcher name, then the parent processes up to the first one that is not a shell
/// or a dotnet host (an editor, a terminal, an agent), at most four.
let byOf (launcher: string) (ancestors: Ancestor array) =
  let shells = set [ "zsh"; "-zsh"; "bash"; "-bash"; "sh"; "fish"; "dotnet"; "login"; "node"; "env"; "npm"; "pnpm"; "yarn"; "npx"; "smoothdev-web" ]
  let chain =
    ancestors
    |> Array.truncate 6
    |> Array.map (fun a -> $"{a.name} ({a.pid})")
  let named =
    ancestors |> Array.tryFind (fun a -> not (shells.Contains a.name)) |> Option.map _.name
  let lead =
    match launcher, named with
    | "", Some n -> n
    | "", None -> "?"
    | launcher, _ -> launcher
  lead, String.concat " <- " chain

let by (l: Launch) = byOf l.launcher l.ancestors

/// This process's launch: created on its first claim or child, so commands that start nothing
/// (status, logs, launches) leave no entry.
module Current =
  let mutable private dir = Environment.CurrentDirectory
  let mutable private command: string array = [||]
  let mutable private me: Launch option = None
  let mutable private startChain: Ancestor array option = None
  let private gate = obj ()

  /// Reads who started this process now, before its parent can exit or it detaches, and hands the
  /// chain (this process first) to what it starts in SMOOTHDEV_WEB_ANCESTRY. Program calls it first.
  let capture () =
    try
      let pid = Environment.ProcessId
      let table = tableOf (processTable ())
      let own = starterOf table pid (ProcessManagement.processGroup pid)
      let inherited =
        match Environment.GetEnvironmentVariable ancestryVariable with
        | null | "" -> [||]
        | text -> decodeAncestry text
      let chain = chooseAncestry own inherited
      startChain <- Some chain
      let self =
        match table.TryFind pid with
        | Some(_, name, command) -> [| { pid = pid; name = name; command = clip command } |]
        | None -> [||]
      Environment.SetEnvironmentVariable(ancestryVariable, encodeAncestry (Array.append self chain))
    with _ ->
      ()

  /// What the launch records as its folder and command (Program sets it from the arguments).
  let describe (folder: string) (argv: string array) =
    dir <- folder
    command <- argv

  let get () =
    lock gate (fun () ->
      match me with
      | Some l -> l
      | None ->
        let chain = match startChain with Some c -> c | None -> ancestry Environment.ProcessId
        let l = create dir command chain
        me <- Some l
        l)

  let started () = me.IsSome

  /// The launch that started this process, when one did.
  let parent () =
    match Environment.GetEnvironmentVariable "SMOOTHDEV_WEB_LAUNCH" with
    | null -> ""
    | id -> id

  let claim role preferred reserved = claimFor (home ()) (parent ()) (get ()) role preferred reserved Ports.isFree

  let addChild (root: string) (e: Entry) =
    addChild (home ()) (get ()) { name = e.name; pid = e.pid; root = root; port = e.port; url = e.url; log = e.log }

  let finish () =
    match me with
    | Some l -> try finish (home ()) l with _ -> ()
    | None -> ()
