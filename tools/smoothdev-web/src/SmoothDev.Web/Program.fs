module SmoothDev.Web.Program

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
  dist                       production bundle into the client's dist (Fable, then vite build)
  open [dev|dist|prod]       open the running version in a browser (dist starts the preview);
                             without a target: dev, else prod, else preview, else dist
  logs [name...] [-f] [-n N] print the last N lines (default 40) of component logs; -f follows them
  stop                       stop everything this app runs
  scan [tui|gui]            list Vite apps, Web SDK projects and smoothdev.web.json files under this folder;
                            tui and gui fold the folder tree and open an app with the same controls as `tui` or `gui` in that folder
                             tui or gui show hits as the walk finds them
  tui                        terminal UI
  gui [--port N] [--detach]  web GUI served by the tool on 127.0.0.1; --detach runs it in the background
                             (tracked like the other components: `stop` or the GUI's own stop ends it)
  config [show|init]         print the resolved config, or write smoothdev.web.json from detection

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

let config (cfg: Config) sub =
  match sub with
  | []
  | [ "show" ] ->
    printf "%s" (Config.toJson cfg)
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

/// Splits the global options from the command.
let rec globals (args: string list) dir browser rest =
  match args with
  | "--dir" :: d :: tail
  | "-C" :: d :: tail      -> globals tail d browser rest
  | "--no-browser" :: tail -> globals tail dir false rest
  | a :: tail              -> globals tail dir browser (a :: rest)
  | []                     -> dir, browser, List.rev rest

let rec logOptions (args: string list) names follow count =
  match args with
  | ("-f" | "--follow") :: tail     -> logOptions tail names true count
  | ("-n" | "--lines") :: n :: tail -> logOptions tail names follow (int n)
  | name :: tail                    -> logOptions tail (name :: names) follow count
  | []                              -> List.rev names, follow, count

let run (ctx: Actions.Context) (argv: string array) (command: string list) =
  let wait (t: Threading.Tasks.Task<Result<unit, string>>) = t.GetAwaiter().GetResult() |> exitCode

  match command with
  | []
  | [ "status" ]           -> status ctx false
  | [ "status"; "--json" ] -> status ctx true
  | [ "dev"; "start" ]     -> wait (Actions.devStart ctx)
  | [ "dev"; "stop" ]      -> wait (Actions.stopNames ctx Actions.devNames)
  | [ "dev"; "restart" ]   ->
    wait (Actions.stopNames ctx Actions.devNames) |> ignore
    wait (Actions.devStart ctx)
  | [ "prod"; "start" ]    -> wait (Actions.prodStart ctx)
  | [ "prod"; "stop" ]     -> wait (Actions.stopNames ctx [| "prod" |])
  | [ "preview"; "start" ] -> wait (Actions.perform ctx "preview-start")
  | [ "preview"; "stop" ]  -> wait (Actions.stopNames ctx [| "preview" |])
  | [ "build" ]        -> wait (Actions.build ctx)
  | [ "dist" ]         -> wait (Actions.dist ctx)
  | [ "open" ]         -> wait (Actions.openTarget ctx None)
  | [ "open"; target ] -> wait (Actions.openTarget ctx (Some target))
  | "logs" :: rest ->
    let names, follow, count = logOptions rest [] false 40
    logs ctx.config names follow count
  | [ "stop" ] -> wait (Actions.stopNames ctx Actions.allNames)
  | [ "tui" ] ->
    let code = Tui.run ctx.config
    printfn ""
    printfn "smoothdev-web tui was started with:"
    printfn "  %s" (invokedCommand argv)
    code
  | [ "gui" ]              -> Gui.run ctx.config None ctx.browser
  | [ "gui"; "--port"; p ] -> Gui.run ctx.config (Some(int p)) ctx.browser
  | [ "gui"; "--detach" ]  -> wait (Gui.detach ctx None)
  | [ "gui"; "--detach"; "--port"; p ]
  | [ "gui"; "--port"; p; "--detach" ] -> wait (Gui.detach ctx (Some(int p)))
  | "config" :: sub -> config ctx.config sub
  | _ ->
    eprintf $"%s{usage}"
    2

[<EntryPoint>]
let main argv =
  match List.ofArray argv with
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
    let noBrowserEnv =
      Environment.GetEnvironmentVariable "SMOOTHDEV_WEB_NO_BROWSER" = "1"

    let dir, browser, command =
      globals args Environment.CurrentDirectory (not noBrowserEnv) []

    match
      Environment.GetEnvironmentVariable "COLUMNS"
      |> Option.ofObj
      |> Option.map Int32.TryParse
    with
    | Some(true, width) when width > 20 -> AnsiConsole.Profile.Width <- width
    | _ -> ()

    match command with
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
    | [ "scan"; "tui" ] -> Tui.scan dir
    | [ "scan"; "gui" ] -> Gui.scan dir None
    | [ "scan"; "gui"; "--port"; port ] -> Gui.scan dir (Some(int port))
    | _ when not (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) ->
      say Fail "smoothdev-web runs on macOS and Linux for now (process groups are POSIX)"
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

        run ctx argv command
