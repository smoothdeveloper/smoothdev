module smoothdev.web.Program

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text
open System.Threading
open Spectre.Console

let usage =
  """smoothdev-web: a bird's-eye view of a Vite + Fable + .NET web app

Usage: smoothdev-web [--dir <app folder>] [--no-browser] <command> [options]

Commands:
  status [--json]            components, state, PID, port, URL
  dev start|stop|restart     dev server (dotnet watch --non-interactive), Fable watch, Vite; free ports picked
  prod start|stop            dotnet publish -c Release, then run it (Production) serving the client's dist
  preview start|stop         serve the client's dist as static files (what a static host serves)
  build                      compile once (dotnet build, Fable) to check the app builds
  dist                       production bundle into the client's dist (client.distCommand, or Fable then vite build)
  open [dev|dist|prod]       open the running version in a browser (dist starts the preview);
                             without a target: dev, else prod, else preview, else dist
  logs [name...] [-f] [-n N] print the last N lines (default 40) of component logs; -f follows them
  stop                       stop everything this app runs
  scan [tui|gui [--classic]] list Vite apps, Web SDK projects and smoothdev.web.json files under this folder;
                            tui and gui fold the folder tree and open an app with the same controls as `tui` or `gui` in that folder
                             tui or gui show hits as the walk finds them
  tui                        terminal UI
  gui [--port N] [--detach]  web GUI served by the tool on 127.0.0.1; --detach runs it in the background
                             (tracked like the other components: `stop` or the GUI's own stop ends it)
  config [show|diff|init]    print the resolved config, how the file differs from detection, or write it
  site [plan] [--json]       documentation sites (Nacara) under this folder, the Vite apps they host, and why
  site dev [--port N]        the site's dev server, each mounted Vite app under its base, one front port
  site stop|status [--json]  stop / list what `site dev` and `site preview` started
  site dist                  the site's build, with each mounted app built into its route (.smoothdev/web/site-dist)
  site preview               serve the site dist as a static host would
  launches [--json] [--all]  every smoothdev-web launch on this machine: who started it (launcher, parent
                             processes), where, its ports and URLs; --all adds the ended ones
  launches stop <id>         stop what a launch started
  others [--json] [--all]    other dev and web servers listening here that no launch holds (Vite, webpack,
                             Next, Nacara, dotnet watch, Fable, npm scripts, http.server, Jupyter...): folder,
                             kind, ports, URL, pid, age, who started it; --all adds the system's listeners
  others stop <pid>          SIGTERM to one of them
  who <port>                 the launch holding a port, else the process listening on it

Options:
  --dir <path>    the app folder (default: current folder; the nearest smoothdev.web.json above it wins)
  --no-browser    print URLs instead of opening a browser (also SMOOTHDEV_WEB_NO_BROWSER=1)
  --version, --help
"""

let version () =
  let assembly = Assembly.GetExecutingAssembly()

  match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
  | null -> "0.0.0"
  | a -> a.InformationalVersion.Split('+')[0]

let say level (msg: string) =
  let m = Markup.Escape msg

  match level with
  | Info    -> AnsiConsole.MarkupLine m
  | Success -> AnsiConsole.MarkupLine $"[green]{m}[/]"
  | Warn    -> AnsiConsole.MarkupLine $"[yellow]{m}[/]"
  | Fail    -> AnsiConsole.MarkupLine $"[red]{m}[/]"
  | Detail  -> AnsiConsole.MarkupLine $"[grey]{m}[/]"

let echo (name: string) (line: string) =
  AnsiConsole.MarkupLine $"[grey]{Markup.Escape name} |[/] {Markup.Escape line}"

/// A shell word: as is when it is plain, else single-quoted.
let shellQuote (word: string) =
  if word <> "" && word |> Seq.forall (fun c -> Char.IsLetterOrDigit c || "-_./=:@%+,".Contains c) then
    word
  else
    "'" + word.Replace("'", "'\\''") + "'"

/// The command line that started this process (this tool's apphost, or `dotnet <dll>`, then the arguments).
let invokedCommand (argv: string array) =
  Array.append (Runner.selfArgv ()) argv |> Array.map shellQuote |> String.concat " "

let exitCode (result: Result<unit, string>) =
  match result with
  | Ok ()   -> 0
  | Error _ -> 1

let status (ctx: Actions.Context) json =
  let cfg = ctx.config
  let rows = Actions.rows cfg

  if json then
    printfn "%s" (Gui.statusJson rows)
  else
    let source = cfg.file |> Option.defaultValue "detected (no smoothdev.web.json)"
    AnsiConsole.MarkupLine $"[bold]{Markup.Escape cfg.name}[/]  [grey]{Markup.Escape source}[/]"
    AnsiConsole.Write(Tui.statusTable rows)

  0

/// Prints the last lines of each log; with follow, keeps printing what they append until Ctrl+C.
let logs (cfg: Config) (names: string list) follow count =
  let live = State.live cfg.root

  let pick () =
    let known =
      if names.IsEmpty then
        let running = live |> Array.filter (fun e -> e.log <> "") |> Array.map _.name

        if running.Length > 0 then
          running
        else
          Actions.allNames
          |> Array.append [| "dist"; "build"; "publish"; "install" |]
          |> Array.filter (fun n -> File.Exists(State.logFile cfg.root n))
      else
        List.toArray names

    known |> Array.map (fun n -> n, State.logFile cfg.root n)

  let files = pick ()

  if files.Length = 0 then
    say Info "no logs yet"

  for name, path in files do
    for line in Runner.tail path count do
      echo name line

  if follow then
    use stopping = new CancellationTokenSource()

    Console.CancelKeyPress.Add(fun e ->
      e.Cancel <- true
      stopping.Cancel())

    let positions = Dictionary<string, int64>()

    for _, path in files do
      positions[path] <- if File.Exists path then FileInfo(path).Length else 0L

    while not stopping.IsCancellationRequested do
      for name, path in files do
        if File.Exists path then
          let length = FileInfo(path).Length
          let from = if length < positions[path] then 0L else positions[path]

          if length > from then
            use fs =
              new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

            fs.Seek(from, SeekOrigin.Begin) |> ignore
            use reader = new StreamReader(fs, Encoding.UTF8)
            let chunk = reader.ReadToEnd()
            let complete = chunk.LastIndexOf '\n'

            if complete >= 0 then
              chunk.Substring(0, complete).Split('\n')
              |> Array.iter (Runner.plain >> echo name)

              positions[path] <- from + int64 (Encoding.UTF8.GetByteCount(chunk.Substring(0, complete + 1)))

      Thread.Sleep 250

  0

let config (fromDir: string) (cfg: Config) sub =
  match sub with
  | []
  | [ "show" ] ->
    printf "%s" (Config.toJson cfg)
    0
  | [ "diff" ] ->
    let view = Config.explain fromDir cfg
    for line in view.lines do
      AnsiConsole.MarkupLine line
    if view.rows.Length > 0 then
      let table = Table().Border(TableBorder.Rounded).Expand()
      for column in [| "key"; "detection"; "file" |] do
        table.AddColumn(column) |> ignore
      for cells in view.rows do
        table.AddRow cells |> ignore
      AnsiConsole.Write table
    for line in view.footer do
      AnsiConsole.MarkupLine line
    0
  | [ "init" ] ->
    let path = cfg.root </> Config.fileName

    if File.Exists path then
      say Fail $"{path} already exists"
      1
    else
      File.WriteAllText(path, Config.toJson cfg)
      say Success $"wrote {path}"
      0
  | _ ->
    eprintf "%s" usage
    2

let private ago (t: DateTimeOffset) =
  let d = DateTimeOffset.Now - t
  if d.TotalHours >= 1.0 then $"{int d.TotalHours}h{d.Minutes:D2}m"
  elif d.TotalMinutes >= 1.0 then $"{d.Minutes}m{d.Seconds:D2}s"
  else $"{d.Seconds}s"

let private home (path: string) =
  let h = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
  if path.StartsWith h then "~" + path.Substring h.Length else path

let printLaunch (l: Launches.Launch) =
  let lead, chain = Launches.by l
  let state = Launches.status l
  let commandText = l.command |> String.concat " "
  AnsiConsole.MarkupLine $"[bold]{Markup.Escape l.id}[/]  {state}  [cyan]{Markup.Escape commandText}[/]  in {Markup.Escape (home l.dir)}  {ago l.startedAt} ago"
  AnsiConsole.MarkupLine $"  by [bold]{Markup.Escape lead}[/]  [grey]{Markup.Escape chain}[/]  user {Markup.Escape l.user}"
  for p in l.ports |> Array.sortBy _.port do
    let url = if p.url = "" then "(claimed)" else p.url
    AnsiConsole.MarkupLine $"  {p.port,-6} {Markup.Escape p.role,-28} {Markup.Escape url}"
  let procs = l.children |> Array.map (fun c -> $"{c.name} {c.pid}") |> String.concat ", "
  if procs <> "" then AnsiConsole.MarkupLine $"  [grey]processes {Markup.Escape procs}[/]"

let launches (sub: string list) =
  let home = Launches.home ()
  match sub with
  | [] | [ "--all" ] | [ "--json" ] | [ "--json"; "--all" ] | [ "--all"; "--json" ] ->
    let all = List.contains "--all" sub
    let ls = Launches.list home all
    if List.contains "--json" sub then
      let rows =
        ls |> Array.map (fun l ->
          let lead, _ = Launches.by l
          {| l with status = Launches.status l; by = lead |})
      printfn "%s" (Launches.toJson rows)
    elif ls.Length = 0 then
      printfn "no smoothdev-web launch runs (registry: %s)" (Launches.dirOf home)
    else
      for l in ls do printLaunch l
    0
  | [ "stop"; id ] ->
    match Launches.list home false |> Array.filter (fun l -> l.id = id || l.id.StartsWith id) with
    | [| l |] ->
      for c in l.children do
        match State.read c.root |> Array.tryFind (fun e -> e.name = c.name && e.pid = c.pid) with
        | Some e ->
          let forced = (Runner.stop c.root e (TimeSpan.FromSeconds 5.)).GetAwaiter().GetResult()
          let how = if forced then ", killed" else ""
          say Success $"stopped {c.name} (pid {c.pid}){how}"
        | None ->
          if ProcessManagement.alive c.pid then
            ProcessManagement.signalGroup c.pid ProcessManagement.SIGTERM |> ignore
            say Success $"sent SIGTERM to {c.name} (pid {c.pid})"
      Launches.sweep home |> ignore
      0
    | [||] ->
      say Fail $"no running launch {id}"
      1
    | many ->
      let ids = many |> Array.map _.id |> String.concat ", "
      say Fail $"{id} names {many.Length} launches: {ids}"
      1
  | _ ->
    eprintf "%s" usage
    2

let printOther (i: Others.Instance) =
  let title = if i.title <> "" then $"  \"{i.title}\"" else ""
  AnsiConsole.MarkupLine $"[bold]{Markup.Escape i.project}[/]  [cyan]{Markup.Escape i.kind}[/]  pid {i.pid}  up {Markup.Escape i.uptime}{Markup.Escape title}"
  for u in i.urls do
    AnsiConsole.MarkupLine $"  {Markup.Escape u}"
  AnsiConsole.MarkupLine $"  [grey]in {Markup.Escape (home i.cwd)}[/]"
  AnsiConsole.MarkupLine $"  [grey]{Markup.Escape i.command}[/]"
  AnsiConsole.MarkupLine $"  by [bold]{Markup.Escape i.by}[/]  [grey]{Markup.Escape i.chain}[/]"

let others (sub: string list) =
  match sub with
  | [ "stop"; pid ] ->
    match Others.stop (int pid) with
    | Ok i ->
      say Success $"sent SIGTERM to {i.kind} {i.project} (pid {i.pid})"
      0
    | Error e ->
      say Fail e
      1
  | flags when flags |> List.forall (fun f -> f = "--json" || f = "--all") ->
    let found = Others.discover (List.contains "--all" flags) true
    if List.contains "--json" flags then
      printfn "%s" (Launches.toJson found)
    elif found.Length = 0 then
      printfn "no other dev or web server listens here (--all lists every listener)"
    else
      for i in found do printOther i
    0
  | _ ->
    eprintf "%s" usage
    2

let who (port: int) =
  match Launches.holderOf (Launches.home ()) port with
  | Some l ->
    let p = l.ports |> Array.find (fun p -> p.port = port)
    let lead, chain = Launches.by l
    let where =
      if p.url <> "" then p.url
      else
        match SiteActions.listenerOf port with
        | Some(pid, _) -> $"http://127.0.0.1:{port}/ (pid {pid})"
        | None -> "(claimed, not bound yet)"
    printfn "%d  %s  %s" port p.role where
    printfn "  launch %s (%s), %s in %s" l.id (Launches.status l) (String.concat " " l.command) (home l.dir)
    printfn "  by %s  %s" lead chain
    0
  | None ->
    match SiteActions.listenerOf port with
    | Some(pid, cwd) ->
      printfn "%d  pid %d in %s: not started by smoothdev-web" port pid (home cwd)
      0
    | None ->
      printfn "%d  nothing listens there" port
      1

/// Splits the global options from the command.
let rec globals (args: string list) dir browser rest =
  match args with
  | "--dir" :: d :: tail
  | "-C" :: d :: tail      -> globals tail d browser rest
  | "--no-browser" :: tail -> globals tail dir false rest
  | a :: tail              -> globals tail dir browser (a :: rest)
  | []                     -> dir, browser, List.rev rest

/// `site dev|stop|status|dist|preview`: the first site the plan finds under dir (or `--site <name>`).
let site (dir: string) (browser: bool) (sub: string list) =
  let plan = Sites.plan dir
  let named, sub =
    match sub with
    | "--site" :: name :: rest -> Some name, rest
    | _ -> None, sub
  let wait (t: Threading.Tasks.Task<Result<'a, string>>) =
    match t.GetAwaiter().GetResult() with
    | Ok _ -> 0
    | Error _ -> 1
  let chosen =
    match named with
    | Some n -> plan.sites |> Array.tryFind (fun s -> s.name = n)
    | None -> plan.sites |> Array.tryHead
  match chosen with
  | None ->
    say Fail $"no Nacara site under {dir}"
    1
  | Some s ->
    if plan.sites.Length > 1 && named.IsNone then
      say Warn $"{plan.sites.Length} sites found: using {s.name} ({s.project}); choose with --site <name>"
    let ctx: Actions.Context =
      { config = SiteActions.configFor s; say = say; echo = echo; browser = browser }
    match sub with
    | [ "dev" ]
    | [ "dev"; "start" ] -> wait (SiteActions.devStart ctx plan s None)
    | [ "dev"; "--port"; p ]
    | [ "dev"; "start"; "--port"; p ] -> wait (SiteActions.devStart ctx plan s (Some(int p)))
    | [ "stop" ]
    | [ "dev"; "stop" ] -> wait (SiteActions.stop ctx)
    | [ "dist" ] -> wait (SiteActions.dist ctx plan s)
    | [ "preview" ] -> wait (SiteActions.preview ctx plan s)
    | [ "status"; "--json" ] ->
      printfn "%s" (SiteActions.statusJson (State.live s.repo))
      0
    | [ "status" ] ->
      let live = State.live s.repo |> Array.filter (fun e -> e.name.StartsWith "site-")
      if live.Length = 0 then printfn "nothing running"
      for e in live do
        printfn "%-24s pid %-6d %s" e.name e.pid e.url
      0
    | _ ->
      eprintf $"%s{usage}"
      2

let rec logOptions (args: string list) names follow count =
  match args with
  | ("-f" | "--follow") :: tail     -> logOptions tail names true count
  | ("-n" | "--lines") :: n :: tail -> logOptions tail names follow (int n)
  | name :: tail                    -> logOptions tail (name :: names) follow count
  | []                              -> List.rev names, follow, count

let run (fromDir: string) (ctx: Actions.Context) (argv: string array) (command: string list) =
  let wait (t: Threading.Tasks.Task<Result<unit, string>>) = t.GetAwaiter().GetResult() |> exitCode

  match command with
  | []
  | [ "status" ]           -> status ctx false
  | [ "status"; "--json" ] -> status ctx true
  | [ "dev"; "start" ]     -> wait (Actions.devStart ctx)
  | [ "dev"; "stop" ]      -> wait (Actions.perform ctx ActionTarget.DevStop)
  | [ "dev"; "restart" ]   -> wait (Actions.stopNames ctx Actions.devNames) |> ignore
                              wait (Actions.devStart ctx)
  | [ "prod"; "start" ]    -> wait (Actions.prodStart ctx)
  | [ "prod"; "stop" ]     -> wait (Actions.perform ctx ActionTarget.ProdStop)
  | [ "preview"; "start" ] -> wait (Actions.perform ctx ActionTarget.PreviewStart)
  | [ "preview"; "stop" ]  -> wait (Actions.perform ctx ActionTarget.PreviewStop)
  | [ "build" ]            -> wait (Actions.build ctx)
  | [ "dist" ]             -> wait (Actions.dist ctx)
  | [ "open" ]             -> wait (Actions.openTarget ctx None)
  | [ "open"; target ]     -> match ActionTarget.parseOpen target with
                              | Some action -> wait (Actions.openTarget ctx (Some action))
                              | None        -> say Fail $"open what? dev, dist or prod, not \"{target}\""
                                               1
  | "logs" :: rest ->
    let names, follow, count = logOptions rest [] false 40
    logs ctx.config names follow count
  | [ "stop" ] -> wait (Actions.stopNames ctx Actions.allNames)
  | [ "tui" ]  -> let code = Tui.run ctx.config
                  printfn ""
                  printfn "smoothdev-web tui was started with:"
                  printfn "  %s" (invokedCommand argv)
                  code
  | [ "gui" ]              -> Gui.run ctx.config None ctx.browser
  | [ "gui"; "--port"; p ] -> Gui.run ctx.config (Some(int p)) ctx.browser
  | [ "gui"; "--detach" ]  -> wait (Gui.detach ctx None)
  | [ "gui"; "--detach"; "--port"; p ]
  | [ "gui"; "--port"; p; "--detach" ] -> wait (Gui.detach ctx (Some(int p)))
  | "config" :: sub -> config fromDir ctx.config sub
  | _ ->
    eprintf $"%s{usage}"
    2

[<EntryPoint>]
let main argv =
  if not (OperatingSystem.IsWindows()) then
    ProcessManagement.Posix.followerArgv <- Runner.selfArgv ()
  match List.ofArray argv with
  | [ "__follow"; log ] when not (OperatingSystem.IsWindows()) ->
    ProcessManagement.Posix.copyAll 0 log
    0
  | "__front" :: port :: siteBase :: routes ->
    let parsed = routes |> List.map FrontProxy.parseRoute
    match parsed |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
    | Some e ->
      eprintfn "%s" e
      2
    | None ->
      FrontProxy.run (int port) siteBase [| for r in parsed do match r with Ok r -> yield r | Error _ -> () |]
  | [ "__serve-static"; dir; urlBase; port ] ->
    StaticServer.run dir urlBase (int port)
  | [ "--version" ] ->
    printfn "%s" (version ())
    0
  | [ "--help" ]
  | [ "-h" ]
  | [ "help" ] ->
    printf "%s" usage
    0
  | args ->
    // who started this process, read before its parent can exit or it detaches
    Launches.Current.capture ()
    let noBrowserEnv =
      Environment.GetEnvironmentVariable "SMOOTHDEV_WEB_NO_BROWSER" = "1"

    let dir, browser, command =
      globals args Environment.CurrentDirectory (not noBrowserEnv) []

    Launches.Current.describe (Path.GetFullPath dir) (List.toArray command)
    AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> Launches.Current.finish ())

    match
      Environment.GetEnvironmentVariable "COLUMNS"
      |> Option.ofObj
      |> Option.map Int32.TryParse
    with
    | Some(true, width) when width > 20 -> AnsiConsole.Profile.Width <- width
    | _ -> ()

    match command with
    | "launches" :: "--others" :: sub -> others sub
    | "launches" :: sub -> launches sub
    | "others" :: sub -> others sub
    | [ "who"; port ] -> who (int port)
    | [ "scan" ] ->
      match Config.scanFromCache dir with
      | Some(rows, true, cachedRoot) ->
        for row in rows do
          printfn "%-8s %s  %s" row.kind row.path row.note

        eprintfn "cached  %d apps from %s" rows.Length cachedRoot
        0
      | _ ->
        let fresh = ResizeArray()

        let visited =
          Config.scanVisit
            dir
            Threading.CancellationToken.None
            (fun n path ->
              if n % 250 = 0 then
                eprintfn "scanning %d  %s" n path)
            (fun row ->
              fresh.Add row
              printfn "%-8s %s  %s" row.kind row.path row.note)

        Config.saveScanCache dir visited (fresh.ToArray())
        eprintfn "done  %d folders" visited
        0
    | [ "site" ]
    | [ "site"; "plan" ] ->
      let plan = Sites.plan dir
      printf "%s" (Sites.describe plan)
      for site in plan.sites do
        printfn "dev wiring for %s (`smoothdev-web site dev`):" site.name
        for line in SiteActions.devLines plan site do
          printfn "  %s" line
      0
    | [ "site"; "--json" ]
    | [ "site"; "plan"; "--json" ] ->
      printfn "%s" (Sites.toJson (Sites.plan dir))
      0
    | "site" :: sub -> site dir browser sub
    | [ "scan"; "tui" ] -> Tui.scan dir
    | "scan" :: "gui" :: rest ->
      let classic = List.contains "--classic" rest
      let port =
        match rest |> List.filter ((<>) "--classic") with
        | [ "--port"; p ] -> Some(int p)
        | _ -> None
      match WebConsole.assets () with
      | Some assets when not classic -> WebConsole.run dir assets port browser
      | _ -> Gui.scan dir port
    | _ when not (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) ->
      say Fail "smoothdev-web runs on macOS, Linux and Windows"
      1
    | _ ->
      match Config.load dir with
      | Error msg ->
        say Fail msg
        say Info $"write one with `smoothdev-web config init` in the app folder, or see the README"
        1
      | Ok cfg ->
        let ctx: Actions.Context =
          {
            config = cfg
            say = say
            echo = echo
            browser = browser
          }

        run dir ctx argv command
