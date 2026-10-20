/// Other running instances: dev and web servers listening on this machine that no launch in the
/// registry holds. Read from the listening TCP sockets (lsof, else ss on Linux), with each process's
/// command line, folder, parents and age, and classified by what its command looks like.
module smoothdev.web.Others

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks

/// A listening socket: the process and the port.
type Listener =
  { pid     : int
    name    : string
    port    : int
    address : string }

type Instance =
  { pid       : int
    name      : string               // executable name
    command   : string               // command line, clipped
    cwd       : string
    repo      : string               // git repository root, "" when none
    project   : string               // repository folder (and the folder inside it), else the folder's name
    kind      : string
    ports     : int array
    urls      : string array         // one per port
    title     : string               // <title> of the first URL, "" when not probed or none
    startedAt : string               // ISO 8601, "" when unknown
    uptime    : string
    by        : string
    chain     : string
    ancestors : Launches.Ancestor array }

// ---------------------------------------------------------------------------------------------
// Classification: pure, from the executable name and the command line.

let private has (needle: string) (text: string) = text.Contains(needle, StringComparison.OrdinalIgnoreCase)
let private matches (pattern: string) (text: string) = Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)

/// What a listening process is; nacara when its project is known to be a Nacara site.
let classify (name: string) (command: string) (nacara: bool) =
  let c = command
  if nacara || has "nacara" c then "nacara"
  elif name = "smoothdev-web" || has "smoothdev-web" c then "smoothdev-web"
  elif has "/vite/bin/" c || has "node_modules/.bin/vite" c || matches @"(^|[/\s])vite(\.js)?(\s|$)" c then "vite"
  elif has "webpack" c then "webpack"
  elif has "next/dist" c || matches @"(^|[/\s])next(\s+(dev|start)\b|-server)" c then "next"
  elif has "dotnet-watch" c || matches @"dotnet(\.dll)?\s+watch\b" c then "dotnet watch"
  elif has "fable" c && has "watch" c then "fable watch"
  elif matches @"(^|[/\s])(pnpm|npm|yarn|bun)(\.c?js)?\s+(run\s+)?(dev|start|serve|preview)\b" c then "npm script"
  elif has "http.server" c || has "SimpleHTTPServer" c then "python http.server"
  elif has "live-server" c then "live-server"
  elif has "jupyter" c then "jupyter"
  elif name = "agy" || has "antigravity" c then "antigravity"
  elif has "Microsoft.AspNetCore" c || has "--urls" c || has "ASPNETCORE_" c then "asp.net"
  elif name = "dotnet" || matches @"/bin/(Debug|Release)/net\d[^/]*/" c then "dotnet"
  elif name = "node" || matches @"(^|/)node\s" c then "node"
  elif name.StartsWith "python" || matches @"(^|/)python[\d.]*\s" c then "python"
  else "other"

/// The generic kinds: a runtime, not a dev server we recognize.
let private generic = set [ "node"; "python"; "dotnet"; "other" ]

/// An application bundle or the system: an editor's helper, a menu bar app.
let private appOrSystem (command: string) =
  [ "/Applications/"; "/System/"; "/Library/"; "/usr/libexec/"; "/usr/sbin/"; "/sbin/" ] |> List.exists command.StartsWith
  || has ".app/Contents/" command

/// Shown by default: a recognized dev server, or a generic runtime working in a folder of the user's
/// that is not an application's or the system's.
let isDev (kind: string) (command: string) (cwd: string) (home: string) =
  if not (generic.Contains kind) then true
  elif appOrSystem command then false
  else
    let home = home.TrimEnd('/')
    cwd <> "" && cwd.StartsWith(home + "/") && not (cwd.StartsWith(home + "/Library/"))

/// The pids the registered launches own: each launch's command and the processes (groups) it started.
let ownedRoots (live: Launches.Launch array) =
  set [ for l in live do
          yield l.pid
          for c in l.children do
            if c.pid > 0 then yield c.pid ]

/// A listener a registered launch holds: its port is claimed, or the process, one of its parents or
/// its process group is a launch's.
let excluded (roots: Set<int>) (claimed: Set<int>) (table: Map<int, int * string * string>) (pgidOf: int -> int) (pid: int) (ports: int array) =
  let rec up p depth =
    p > 1 && depth < 32 && (roots.Contains p || (match table.TryFind p with Some(pp, _, _) -> up pp (depth + 1) | None -> false))
  ports |> Array.exists claimed.Contains || up pid 0 || roots.Contains(pgidOf pid)

/// A base path in the command line: Vite's `--base /x/`.
let baseOf (command: string) =
  match Regex.Match(command, @"--base(?:=|\s+)(\S+)") with
  | m when m.Success -> Sites.normalBase (m.Groups[1].Value.Trim('\'', '"'))
  | _ -> "/"

let urlOf (address: string) (port: int) (basePath: string) =
  let host = if address.Contains "::1" then "localhost" else "127.0.0.1"
  $"http://{host}:{port}{basePath}"

// ---------------------------------------------------------------------------------------------
// Reading the machine.

let private run (file: string) (args: string) =
  try
    let psi = ProcessStartInfo(file, args, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    use p = Process.Start psi |> nonNull
    let _ = p.StandardError.ReadToEndAsync()
    let text = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    text
  with _ ->
    ""

/// `lsof -F pcn` output: p<pid>, c<command>, n<address:port> lines.
let parseLsof (text: string) =
  let found = ResizeArray<Listener>()
  let mutable pid = 0
  let mutable name = ""
  for line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
    match line[0] with
    | 'p' -> pid <- (match Int32.TryParse(line.Substring 1) with | true, p -> p | _ -> 0)
    | 'c' -> name <- line.Substring 1
    | 'n' ->
      let a = line.Substring 1
      match a.LastIndexOf ':' with
      | i when i > 0 ->
        match Int32.TryParse(a.Substring(i + 1)) with
        | true, port when pid > 0 -> found.Add { pid = pid; name = name; port = port; address = a.Substring(0, i) }
        | _ -> ()
      | _ -> ()
    | _ -> ()
  found.ToArray() |> Array.distinctBy (fun l -> l.pid, l.port)

/// `ss -ltnpH` output: LISTEN 0 511 127.0.0.1:5173 0.0.0.0:* users:(("node",pid=1234,fd=20))
let parseSs (text: string) =
  [| for line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
       let m = Regex.Match(line, @"(\S+):(\d+)\s+\S+\s+users:\(\(""([^""]+)"",pid=(\d+)")
       if m.Success then
         yield { pid = int m.Groups[4].Value; name = m.Groups[3].Value; port = int m.Groups[2].Value; address = m.Groups[1].Value } |]
  |> Array.distinctBy (fun l -> l.pid, l.port)

let listeners () =
  if OperatingSystem.IsWindows() then
    [||]
  else
    match parseLsof (run "lsof" "-nP -iTCP -sTCP:LISTEN -F pcn") with
    | [||] when OperatingSystem.IsLinux() -> parseSs (run "ss" "-ltnpH")
    | found -> found

/// The working folder of each pid: /proc on Linux, else lsof.
let private cwdsOf (pids: int array) =
  if pids.Length = 0 then
    Map.empty
  elif Directory.Exists "/proc/self" then
    pids
    |> Array.choose (fun pid ->
      try
        match FileInfo($"/proc/{pid}/cwd").LinkTarget with
        | null -> None
        | t -> Some(pid, t)
      with _ ->
        None)
    |> Map.ofArray
  else
    let csv = pids |> Array.map string |> String.concat ","
    let found = ResizeArray()
    let mutable pid = 0
    for line in (run "lsof" $"-a -d cwd -p {csv} -Fpn").Split('\n', StringSplitOptions.RemoveEmptyEntries) do
      match line[0] with
      | 'p' -> pid <- (match Int32.TryParse(line.Substring 1) with | true, p -> p | _ -> 0)
      | 'n' when pid > 0 -> found.Add(pid, line.Substring 1)
      | _ -> ()
    Map.ofSeq found

/// `ps` elapsed time: [[dd-]hh:]mm:ss.
let parseElapsed (text: string) =
  try
    let days, rest =
      match text.Trim().Split('-') with
      | [| d; r |] -> int d, r
      | _ -> 0, text.Trim()
    match rest.Split(':') |> Array.map int with
    | [| h; m; s |] -> Some(TimeSpan(days, h, m, s))
    | [| m; s |] -> Some(TimeSpan(days, 0, m, s))
    | _ -> None
  with _ ->
    None

/// pid -> (process group, elapsed time).
let private groupsAndAges () =
  (run "ps" "-A -o pid= -o pgid= -o etime=").Split('\n', StringSplitOptions.RemoveEmptyEntries)
  |> Array.choose (fun line ->
    match line.Trim().Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) with
    | [| pid; pgid; etime |] ->
      match Int32.TryParse pid, Int32.TryParse pgid with
      | (true, p), (true, g) -> Some(p, (g, parseElapsed etime))
      | _ -> None
    | _ -> None)
  |> Map.ofArray

/// A Nacara site the process serves: `dotnet run --project X.fsproj`, or the apphost a build left in
/// <project>/bin/<configuration>/<tfm>/.
let private nacaraProjectOf (command: string) (cwd: string) =
  let fromRun = Regex.Match(command, @"--project\s+(\S+\.fsproj)")
  let fromHost = Regex.Match(command, @"^(\S+)/bin/(Debug|Release)/net[^/\s]+/[^/\s]+")
  let candidates =
    [ if fromRun.Success then
        let v = fromRun.Groups[1].Value
        yield (if Path.IsPathRooted v || cwd = "" then v else cwd </> v)
      if fromHost.Success && Directory.Exists fromHost.Groups[1].Value then
        yield! Directory.GetFiles(fromHost.Groups[1].Value, "*.fsproj") ]
  candidates |> List.tryFind (fun p -> File.Exists p && Sites.isNacaraProject p)

let private nameOf (dir: string) =
  match Path.GetFileName(dir.TrimEnd('/')) with
  | null | "" -> dir
  | n -> n

let private ago (d: TimeSpan) =
  if d.TotalDays >= 1.0 then $"{int d.TotalDays}d{d.Hours:D2}h"
  elif d.TotalHours >= 1.0 then $"{int d.TotalHours}h{d.Minutes:D2}m"
  elif d.TotalMinutes >= 1.0 then $"{d.Minutes}m{d.Seconds:D2}s"
  else $"{d.Seconds}s"

let private client = lazy (new HttpClient(Timeout = TimeSpan.FromMilliseconds 900.))

/// The <title> of an HTML page, "" for anything else or no answer.
let titleOf (url: string) : Task<string> =
  task {
    try
      use! r = client.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
      let media =
        match r.Content.Headers.ContentType with
        | null -> ""
        | c -> (match c.MediaType with null -> "" | m -> m)
      if media.Contains "html" then
        let! body = r.Content.ReadAsStringAsync()
        let m = Regex.Match(body, @"<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase)
        return if m.Success then (match Net.WebUtility.HtmlDecode(m.Groups[1].Value) with null -> "" | t -> t.Trim()) else ""
      else
        return ""
    with _ ->
      return ""
  }

/// The other running instances; with all, every listener (the system's too); probe reads page titles.
let discover (all: bool) (probe: bool) : Instance array =
  let found = listeners ()
  if found.Length = 0 then
    [||]
  else
    let table = Launches.tableOf (Launches.processTable ())
    let info = groupsAndAges ()
    let live = try Launches.list (Launches.home ()) false with _ -> [||]
    let roots = ownedRoots live
    let claimed = set (Launches.claimedPorts live)
    let pgidOf p = match info.TryFind p with Some(g, _) -> g | None -> 0
    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
    let groups = found |> Array.groupBy _.pid
    let cwds = cwdsOf (groups |> Array.map fst)
    let instances =
      [| for pid, ls in groups do
           let ports = ls |> Array.map _.port |> Array.distinct |> Array.sort
           if pid <> Environment.ProcessId && not (excluded roots claimed table pgidOf pid ports) then
             let name, command =
               match table.TryFind pid with
               | Some(_, n, c) -> n, c
               | None -> ls[0].name, ls[0].name
             let cwd = cwds.TryFind pid |> Option.defaultValue ""
             let nacara = nacaraProjectOf command cwd
             let kind = classify name command nacara.IsSome
             if all || isDev kind command cwd home then
               let basePath =
                 match nacara |> Option.bind Sites.readSite with
                 | Some site -> site.baseUrl
                 | None ->
                   match baseOf command with
                   | "/" when kind = "vite" && cwd <> "" ->
                     (Sites.readApp cwd).basePath |> Option.map Sites.normalBase |> Option.defaultValue "/"
                   | b -> b
               let repo = if cwd = "" then None else Sites.repoRoot cwd
               let project =
                 match repo with
                 | Some r when r = cwd -> nameOf r
                 | Some r -> nameOf r + "/" + Path.GetRelativePath(r, cwd).Replace('\\', '/')
                 | None when cwd = home -> "~"
                 | None -> nameOf cwd
               let ancestors = Launches.starterOf table pid (pgidOf pid)
               let lead, chain = Launches.byOf "" ancestors
               let elapsed = info.TryFind pid |> Option.bind snd
               yield
                 { pid       = pid
                   name      = name
                   command   = (if command.Length <= 300 then command else command.Substring(0, 300) + "…")
                   cwd       = cwd
                   repo      = repo |> Option.defaultValue ""
                   project   = project
                   kind      = kind
                   ports     = ports
                   urls      = ports |> Array.map (fun p -> urlOf (ls |> Array.find (fun l -> l.port = p)).address p basePath)
                   title     = ""
                   startedAt = elapsed |> Option.map (fun e -> (DateTimeOffset.Now - e).ToString "o") |> Option.defaultValue ""
                   uptime    = elapsed |> Option.map ago |> Option.defaultValue ""
                   by        = lead
                   chain     = chain
                   ancestors = ancestors } |]
      |> Array.sortBy (fun i -> i.project, i.ports)
    if probe then
      let titles = instances |> Array.map (fun i -> titleOf i.urls[0]) |> Task.WhenAll
      Array.map2 (fun i t -> { i with title = t }) instances titles.Result
    else
      instances

/// SIGTERM to another instance, only one discover lists (not the system's, not a launch's).
let stop (pid: int) =
  match discover false false |> Array.tryFind (fun i -> i.pid = pid) with
  | Some i when ProcessManagement.signal pid ProcessManagement.SIGTERM -> Ok i
  | Some _ -> Error $"cannot signal {pid}"
  | None -> Error $"{pid} is not another running instance"

/// The console's view, refreshed in the background at most every few seconds.
module Cache =
  let mutable private last: Instance array = [||]
  let mutable private at = DateTime.MinValue
  let mutable private refreshing = false
  let private gate = obj ()

  let get () =
    lock gate (fun () ->
      if not refreshing && DateTime.Now - at > TimeSpan.FromSeconds 4. then
        refreshing <- true
        Task.Run(fun () ->
          try
            let fresh = try discover false true with _ -> last
            lock gate (fun () -> last <- fresh; at <- DateTime.Now)
          finally
            refreshing <- false)
        |> ignore
      last)

  let invalidate () = lock gate (fun () -> at <- DateTime.MinValue)
