/// What the commands, the TUI and the GUI do: start and stop components, build, bundle, open.
module SmoothDev.Web.Actions

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open CliWrap
open UnMango.CliWrap.FSharp

type Context =
  {
    config: Config
    /// a message for the user
    say: Level -> string -> unit
    /// an output line of a foreground command (component name, line)
    echo: string -> string -> unit
    /// false: print URLs instead of starting a browser
    browser: bool
  }

exception Step of string

let orFail result =
  match result with
  | Ok v -> v
  | Error msg -> raise (Step msg)

let fail ctx (msg: string) =
  ctx.say Fail msg
  Error msg

let done' () = Task.FromResult(Ok())

let devNames = [| "server"; "fable"; "vite" |]
let allNames = [| "server"; "fable"; "vite"; "prod"; "preview"; "gui" |]

let roles =
  dict
    [
      "server" , "dev server (dotnet watch)"
      "fable"  , "Fable watch"
      "vite"   , "Vite dev server"
      "prod"   , "production server"
      "preview", "dist preview (static)"
      "gui"    , "web GUI"
    ]

/// Why a component cannot run for this app, None when it can.
let unavailable cfg name =
  match name, cfg.client, cfg.server with
  | "server" , _     , None                                 -> Some "no server"
  | "fable"  , None  , _                                    -> Some "no client"
  | "fable"  , Some c, _ when c.fable.IsPlugin              -> Some "vite-plugin-fable, inside Vite"
  | "fable"  , Some c, _ when c.fable.IsNoFable             -> Some "no F# client"
  | "vite"   , None  , _                                    -> Some "no client"
  | "prod"   , _     , None                                 -> Some "static app: see preview"
  | "preview", None  , _                                    -> Some "no client"
  | "preview", Some c, _ when not (Directory.Exists c.dist) -> Some "no dist yet"
  | _                                                       -> None

let rows (cfg: Config) =
  let live = State.live cfg.root

  allNames
  |> Array.choose (fun name ->
    let entry = live |> Array.tryFind (fun e -> e.name = name)

    let state =
      match entry, unavailable cfg name with
      | Some e, _                   -> Some(State.runState e)
      | None  , _ when name = "gui" -> None
      | None  , Some reason         -> Some(Unavailable reason)
      | None  , None                -> Some Stopped

    state
    |> Option.map (fun s ->
      {
        name  = name
        role  = roles[name]
        state = s
        entry = entry
      }))

let stateText state =
  match state with
  | Running            -> "running"
  | Orphaned           -> "orphaned"
  | Stopped            -> "stopped"
  | Unavailable reason -> $"- ({reason})"

let uptime (e: Entry) =
  let t = DateTimeOffset.Now - e.startedAt
  if t.TotalHours >= 1.0 then
    $"{int t.TotalHours}h{t.Minutes:D2}m"
  elif t.TotalMinutes >= 1.0 then
    $"{t.Minutes}m{t.Seconds:D2}s"
  else
    $"{t.Seconds}s"

/// Ports held by this app's running components (never handed out twice).
let reserved (cfg: Config) =
  State.live cfg.root |> Array.map _.port |> Array.filter (fun p -> p > 0)

let pickPort ctx what preferred =
  let port = Ports.pick (reserved ctx.config) preferred

  if port <> preferred then
    ctx.say Warn $"port {preferred} is busy: {what} uses {port}"

  port

let devUrl (cfg: Config) =
  let live = State.live cfg.root

  let find n =
    live |> Array.tryFind (fun e -> e.name = n)

  match find "vite", find "server" with
  | Some v, _      -> Some v.url
  | None  , Some s -> Some s.url
  | None  , None   -> None

/// Runs a foreground command; its output goes to ctx.echo and to the named log.
let once ctx name (argv: string array) env cwd =
  task {
    ctx.say Detail $"$ {Runner.commandLine argv}"
    let log = State.logFile ctx.config.root name
    let! code = Runner.runOnce (ctx.echo name) log argv env cwd

    return
      if code = 0 then
        Ok ()
      else
        Error $"`{Runner.commandLine argv}` exited with code {code} (log: {log})"
  }

let ensureDeps ctx (c: Client) =
  if Directory.Exists(c.dir </> "node_modules") then
    done' ()
  else
    let pm = ctx.config.packageManager
    let hasLock = (Config.lockFileOf c.dir).IsSome
    ctx.say Info $"installing Node packages with {Config.pmName pm}"
    once ctx "install" (Config.installArgv pm hasLock) [||] c.dir

let rec hasToolManifest (dir: string) =
  File.Exists(dir </> ".config" </> "dotnet-tools.json")
  || File.Exists(dir </> "dotnet-tools.json")
  || (match Directory.GetParent dir with
      | null -> false
      | parent -> hasToolManifest parent.FullName)

let restoreTools ctx dir =
  if hasToolManifest dir then
    once ctx "tools" [| "dotnet"; "tool"; "restore" |] [||] dir
  else
    done' ()

let fableArgv (watch: bool) (project: string) (outDir: string) (extension: string option) =
  [|
    "dotnet"
    "fable"
    if watch then
      "watch"
    project
    "-o"
    outDir
    match extension with
    | Some e ->
      "--extension"
      e
    | None   -> ()
  |]

/// Vite through the client's node_modules/.bin when installed, else through the package manager.
let viteArgv pm (c: Client) rest =
  let local = c.dir </> "node_modules" </> ".bin" </> "vite"
  [|
    if File.Exists local then
      local
    else
      yield! Config.execArgv pm "vite"
    yield! rest
  |]

let fableReady =
  [| "Watching"; "watching"; "compilation finished"; "Compilation finished" |]

let startServerDev ctx (s: Server) =
  let port = pickPort ctx "the dev server" s.port
  let url = $"http://127.0.0.1:{port}"

  let env =
    [|
      "ASPNETCORE_URLS"                     , url
      "ASPNETCORE_ENVIRONMENT"              , "Development"
      "PORT"                                , string port
      "SMOOTHDEV_WEB_SERVER_PORT"           , string port
      "SMOOTHDEV_WEB_SERVER_URL"            , url
      "DOTNET_WATCH_RESTART_ON_RUDE_EDIT"   , "1"
      "DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER", "1"
      "DOTNET_WATCH_SUPPRESS_EMOJIS"        , "1"
      "DOTNET_NOLOGO"                       , "1"
    |]

  let argv =
    [|
      "dotnet"
      "watch"
      "--non-interactive"
      "--no-launch-profile"
      "--project"
      s.project
    |]

  Runner.start ctx.config.root "server" argv env (Path.GetDirectoryName s.project |> nonNull) port $"{url}/"

let startFable ctx (c: Client) project outDir extension =
  Runner.start ctx.config.root "fable" (fableArgv true project outDir extension) [| "DOTNET_NOLOGO", "1" |] c.dir 0 ""

let startVite ctx (c: Client) (server: Entry option) =
  let port = pickPort ctx "Vite" c.port

  let serverEnv =
    match server with
    | Some s ->
      [|
        "SMOOTHDEV_WEB_SERVER_URL", s.url.TrimEnd '/'
        "SMOOTHDEV_WEB_SERVER_PORT", string s.port
      |]
    | None -> [||]

  let env =
    [|
      "SMOOTHDEV_WEB_VITE_PORT", string port
      "BROWSER", "none"
      yield! serverEnv
    |]

  let argv =
    viteArgv
      ctx.config.packageManager
      c
      [| "--port"; string port; "--strictPort"; "--host"; "127.0.0.1" |]

  Runner.start
    ctx.config.root
    "vite"
    argv
    env
    c.dir
    port
    $"http://127.0.0.1:{port}/"

let startedText (e: Entry) =
  if e.port > 0 then
    $"started {e.name} (pid {e.pid}, port {e.port})"
  else
    $"started {e.name} (pid {e.pid})"

/// Stops the named components that run, last started first.
let stopNames ctx (names: string array) =
  task {
    let live =
      State.live ctx.config.root
      |> Array.filter (fun e -> Array.contains e.name names)
      |> Array.sortByDescending (fun e -> Array.IndexOf(names, e.name))

    if live.Length = 0 then
      ctx.say Info $"""nothing running ({String.Join(", ", names)})"""

    for e in live do
      let! forced = Runner.stop ctx.config.root e (TimeSpan.FromSeconds 8.)

      if forced then
        ctx.say Warn $"stopped {e.name} (pid {e.pid}): killed after it ignored SIGTERM"
      else
        ctx.say Info $"stopped {e.name} (pid {e.pid})"

    return Ok ()
  }

/// Collects exited children of this process (the TUI and GUI run this on a timer), so a component that
/// stopped is not left as a zombie that still looks alive to other invocations.
type ScanLive =
  { path : string
    app  : string
    name : string
    url  : string
    log  : string }

/// Running components for the scan rows. A folder contains a hit when the hit path is the folder or under it.
let scanLive (root: string) (rows: Config.ScanRow array) =
  rows
  |> Array.collect (fun row ->
    match Config.scanAppDir root row.kind row.path with
    | None     -> [||]
    | Some dir ->
      match Config.load dir with
      | Error _ -> [||]
      | Ok cfg  ->
        State.live cfg.root
        |> Array.filter (fun e -> e.name <> "gui")
        |> Array.map (fun e ->
          { path = row.path
            app = cfg.name
            name = e.name
            url = e.url
            log = e.log }))

let scanRunning path (live: ScanLive array) =
  live
  |> Array.filter (fun e -> e.path = path || e.path.StartsWith(path + "/"))
  |> Array.map _.path
  |> Array.distinct
  |> Array.length

let reapOwned (cfg: Config) =
  State.read cfg.root
  |> Array.filter (fun e -> e.owner = Environment.ProcessId && e.pid <> Environment.ProcessId)
  |> Array.iter (fun e -> Posix.reap e.pid |> ignore)

/// Stops what this smoothdev-web process started (the TUI and GUI do this when they exit).
let stopOwned ctx =
  let owned =
    State.live ctx.config.root
    |> Array.filter (fun e -> e.owner = Environment.ProcessId && e.pid <> Environment.ProcessId)
    |> Array.map _.name

  if owned.Length = 0 then
    done' ()
  else
    stopNames ctx owned

let showTail ctx (e: Entry) =
  Runner.tail e.log 15 |> Array.iter (ctx.echo e.name)

/// Dev: the server under `dotnet watch --non-interactive`, Fable watch when the client uses the Fable CLI,
/// then Vite with its proxy target in SMOOTHDEV_WEB_SERVER_URL. Ports are picked free. When a step
/// fails, what this call started is stopped again.
let devStart ctx =
  task {
    let cfg = ctx.config

    let running =
      State.live cfg.root
      |> Array.filter (fun e -> Array.contains e.name devNames)

    if running.Length > 0 then
      let names = running |> Array.map _.name |> String.concat ", "
      return fail ctx $"dev is already running ({names}): `dev stop` or `dev restart` first"
    else
      let started = ResizeArray<Entry>()

      let keep (e: Entry) =
        started.Add e
        ctx.say Info (startedText e)
        e

      let! outcome =
        task {
          try
            match cfg.client with
            | Some c ->
              let! deps = ensureDeps ctx c
              orFail deps

              if c.fable.IsCli then
                let! tools = restoreTools ctx c.dir
                orFail tools
            | None -> ()

            let server =
              cfg.server |> Option.map (fun s -> startServerDev ctx s |> orFail |> keep)

            match cfg.client with
            | Some c ->
              match c.fable with
              | Cli(project, outDir, extension) ->
                let fable = startFable ctx c project outDir extension |> orFail |> keep
                ctx.say Info "waiting for Fable's first compilation"
                let! compiled = Runner.waitLog fable fableReady (TimeSpan.FromMinutes 5.)
                orFail compiled
              | Plugin
              | NoFable -> ()

              let vite = startVite ctx c server |> orFail |> keep
              let! viteReady = Runner.waitReady vite (TimeSpan.FromMinutes 2.)
              orFail viteReady
            | None -> ()

            match server with
            | Some s ->
              ctx.say Info "waiting for the dev server (the first build can take a while)"
              let! serverReady = Runner.waitReady s (TimeSpan.FromMinutes 5.)
              orFail serverReady
            | None -> ()

            return Ok()
          with Step msg ->
            return Error msg
        }

      match outcome with
      | Ok() ->
        for e in started do
          if e.url <> "" then
            ctx.say Success $"{e.name} ready: {e.url}"

        return Ok()
      | Error msg ->
        ctx.say Fail msg

        if started.Count > 0 then
          showTail ctx started[started.Count - 1]

        for e in Seq.rev started do
          let! _ = Runner.stop cfg.root e (TimeSpan.FromSeconds 5.)
          ctx.say Info $"stopped {e.name}"

        return Error msg
  }

/// Runs work, turning a failed Step into an Error.
let guarded ctx (work: unit -> Task<unit>) =
  task {
    try
      do! work ()
      return Ok ()
    with Step msg ->
      ctx.say Fail msg
      return Error msg
  }

/// Production bundle: Fable (when compiled outside Vite), then `vite build` into the client's dist.
let dist ctx =
  guarded ctx (fun () ->
    task {
      let cfg = ctx.config

      match cfg.client with
      | None -> raise (Step "no client: nothing to bundle (a server-only app is published by `prod start`)")
      | Some c ->
        Runner.resetLog (State.logFile cfg.root "dist")
        let! deps = ensureDeps ctx c
        orFail deps

        match c.fable with
        | Cli(project, outDir, extension) ->
          let! tools = restoreTools ctx c.dir
          orFail tools
          let! compiled = once ctx "dist" (fableArgv false project outDir extension) [||] c.dir
          orFail compiled
        | Plugin
        | NoFable -> ()

        let argv =
          viteArgv
            cfg.packageManager
            c
            [|
              "build"
              "--outDir"
              c.dist
              "--emptyOutDir"
              match c.basePath with
              | Some b ->
                "--base"
                b
              | None -> ()
            |]

        let! bundled = once ctx "dist" argv [||] c.dir
        orFail bundled
        let files = Directory.GetFiles(c.dist, "*", SearchOption.AllDirectories)
        let kib = (files |> Array.sumBy (fun f -> FileInfo(f).Length)) / 1024L
        ctx.say Success $"dist ready: {c.dist} ({files.Length} files, {kib} KiB)"
    })

/// Compiles once to check the app builds: `dotnet build` of the server, Fable for a CLI-compiled client.
let build ctx =
  guarded ctx (fun () ->
    task {
      let cfg = ctx.config
      Runner.resetLog (State.logFile cfg.root "build")

      match cfg.server with
      | Some s ->
        let! built =
          once ctx "build" [| "dotnet"; "build"; s.project; "--nologo" |] [||] cfg.root

        orFail built
      | None -> ()

      match cfg.client with
      | Some({
               fable = Cli(project, outDir, extension)
             } as c) ->
        let! tools = restoreTools ctx c.dir
        orFail tools

        let! compiled =
          once ctx "build" (fableArgv false project outDir extension) [||] c.dir

        orFail compiled
      | Some { fable = Plugin } -> ctx.say Info "vite-plugin-fable compiles inside Vite: `dist` builds the client"
      | Some _
      | None -> ()

      ctx.say Success "build ok"
    })

let publishDir (cfg: Config) =
  State.dir cfg.root </> "publish"

/// The published entry assembly: the .dll beside the publish folder's runtimeconfig.json.
let entryDll (dir: string) =
  if Directory.Exists dir then
    Directory.GetFiles(dir, "*.runtimeconfig.json")
    |> Array.map (fun f -> f.Replace(".runtimeconfig.json", ".dll"))
    |> Array.tryFind File.Exists
  else
    None

/// Prod: `dotnet publish -c Release`, then the published server with ASPNETCORE_ENVIRONMENT=Production,
/// serving the client's dist (built first when missing). Output goes to the prod log file.
let prodStart ctx =
  guarded ctx (fun () ->
    task {
      let cfg = ctx.config

      match cfg.server, State.find cfg.root "prod" with
      | None, _ ->
        ctx.say
          Warn
          "this app has no server: its production form is the static dist (`preview start`, `open dist`)"
      | Some _, Some e -> ctx.say Info $"prod is already running on {e.url}"
      | Some s, None ->
        match cfg.client with
        | Some c when s.serveDist && not (Directory.Exists c.dist) ->
          let! bundled = dist ctx
          orFail bundled
        | _ -> ()

        let out = publishDir cfg
        Runner.resetLog (State.logFile cfg.root "publish")

        let! published =
          once
            ctx
            "publish"
            [| "dotnet"; "publish"; s.project; "-c"; "Release"; "-o"; out; "--nologo" |]
            [||]
            cfg.root

        orFail published

        let dll =
          entryDll out
          |> Option.defaultWith (fun () -> raise (Step $"no entry assembly (*.runtimeconfig.json) in {out}"))

        let port = pickPort ctx "the production server" s.prodPort
        let url = $"http://127.0.0.1:{port}"

        let distEnv =
          match cfg.client with
          | Some c when s.serveDist -> [| "SMOOTHDEV_WEB_DIST", c.dist; "ASPNETCORE_WEBROOT", c.dist |]
          | _ -> [||]

        let env =
          [|
            "ASPNETCORE_URLS"          , url
            "ASPNETCORE_ENVIRONMENT"   , "Production"
            "PORT"                     , string port
            "SMOOTHDEV_WEB_SERVER_PORT", string port
            yield! distEnv
          |]

        let e =
          Runner.start
            cfg.root
            "prod"
            [| "dotnet"; dll |]
            env
            out
            port
            $"{url}/"
          |> orFail

        ctx.say Info (startedText e)
        let! ready = Runner.waitReady e (TimeSpan.FromMinutes 1.)

        match ready with
        | Ok() ->
          ctx.say Success $"prod ready: {e.url} (log: {e.log})"
        | Error msg ->
          showTail ctx e
          let! _ = Runner.stop cfg.root e (TimeSpan.FromSeconds 5.)
          raise (Step msg)
    })

/// Preview: the static server of this tool on dist, under the bundle's URL base.
let previewStart ctx =
  task {
    let cfg = ctx.config

    match cfg.client, State.find cfg.root "preview" with
    | None, _        -> return fail ctx "no client: nothing to preview"
    | Some _, Some e ->
      ctx.say Info $"preview is already running on {e.url}"
      return Ok e
    | Some c, None when not (Directory.Exists c.dist) ->
      return fail ctx $"no dist at {c.dist}: run `smoothdev-web dist` first"
    | Some c, None ->
      let port = pickPort ctx "the preview" cfg.previewPort
      let urlBase = Config.urlBase c.basePath

      let argv =
        [|
          yield! Runner.selfArgv ()
          "__serve-static"
          c.dist
          urlBase
          string port
        |]

      match Runner.start cfg.root "preview" argv [||] c.dir port $"http://127.0.0.1:{port}{urlBase}" with
      | Error msg -> return fail ctx msg
      | Ok e ->
        ctx.say Info (startedText e)
        let! ready = Runner.waitReady e (TimeSpan.FromSeconds 20.)

        match ready with
        | Ok() ->
          ctx.say Success $"preview ready: {e.url}"
          return Ok e
        | Error msg ->
          showTail ctx e
          let! _ = Runner.stop cfg.root e (TimeSpan.FromSeconds 5.)
          return fail ctx msg
  }

let openUrl ctx (url: string) =
  task {
    if ctx.browser then
      ctx.say Info $"opening {url}"

      let opener =
        if OperatingSystem.IsMacOS() then "open"
        elif OperatingSystem.IsWindows() then "explorer"
        else "xdg-open"

      try
        let! _ =
          Cli.wrap opener
          |> Cli.arg url
          |> Cli.validation CommandResultValidation.None
          |> Cli.Task.exec CancellationToken.None

        ()
      with e ->
        ctx.say Warn $"could not start a browser ({e.Message}): {url}"
    else
      ctx.say Info $"url: {url}"

    return Ok()
  }

let openDist ctx =
  task {
    match! previewStart ctx with
    | Ok e      -> return! openUrl ctx e.url
    | Error msg -> return Error msg
  }

/// Opens dev, dist (starting the preview) or prod; without a target the best running one.
let openTarget ctx (target: string option) =
  let cfg = ctx.config
  let find n = State.find cfg.root n

  match target with
  | Some "dev" ->
    match devUrl cfg with
    | Some url -> openUrl ctx url
    | None     -> Task.FromResult(fail ctx "dev is not running: `smoothdev-web dev start`")
  | Some "dist"
  | Some "preview" -> openDist ctx
  | Some "prod"    ->
    match cfg.server, find "prod" with
    | None, _ ->
      ctx.say Info "no server: the production form of this app is its dist"
      openDist ctx
    | Some _, Some e -> openUrl ctx e.url
    | Some _, None   -> Task.FromResult(fail ctx "prod is not running: `smoothdev-web prod start`")
  | Some other -> Task.FromResult(fail ctx $"open what? dev, dist or prod, not \"{other}\"")
  | None ->
    match devUrl cfg, find "prod", find "preview", cfg.client with
    | Some url, _     , _     , _ -> openUrl ctx url
    | None    , Some p, _     , _ -> openUrl ctx p.url
    | None    , None  , Some v, _ -> openUrl ctx v.url
    | None    , None  , None  , Some c
      when Directory.Exists c.dist -> openDist ctx
    | _ -> Task.FromResult(fail ctx "nothing to open: `dev start`, `prod start` or `dist` first")

let ignoreValue (t: Task<Result<'a, string>>) =
  task {
    let! r = t
    return r |> Result.map ignore
  }

/// The actions the TUI and the GUI offer, by name.
let perform ctx (action: string) : Task<Result<unit, string>> =
  match action with
  | "dev-start"     -> devStart     ctx
  | "dev-stop"      -> stopNames    ctx devNames
  | "prod-start"    -> prodStart    ctx
  | "prod-stop"     -> stopNames    ctx [| "prod" |]
  | "preview-start" -> previewStart ctx |> ignoreValue
  | "preview-stop"  -> stopNames    ctx [| "preview" |]
  | "dist"          -> dist         ctx
  | "build"         -> build        ctx
  | "open"          -> openTarget   ctx None
  | "open-dev"      -> openTarget   ctx (Some "dev")
  | "open-dist"     -> openTarget   ctx (Some "dist")
  | "open-prod"     -> openTarget   ctx (Some "prod")
  | "stop"          -> stopNames    ctx (allNames |> Array.filter ((<>) "gui"))
  | other           -> Task.FromResult(fail ctx $"unknown action {other}")
