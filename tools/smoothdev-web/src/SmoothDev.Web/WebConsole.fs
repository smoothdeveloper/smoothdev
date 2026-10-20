/// `scan gui`: the console page (a Lit app built from ../gui, shipped next to the tool in `gui/`) and its
/// JSON API. The page polls `/api/console`; actions run here, one at a time, in the background.
module smoothdev.web.WebConsole

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// The built page: SMOOTHDEV_WEB_GUI_DIR, else `gui/` next to the tool. None when it was not built.
let assets () =
  let fromEnv = Environment.GetEnvironmentVariable "SMOOTHDEV_WEB_GUI_DIR" |> Option.ofObj
  [ yield! Option.toList fromEnv
    AppContext.BaseDirectory </> "gui" ]
  |> List.tryFind (fun d -> File.Exists(d </> "index.html"))

type Message = { level: string; text: string; at: DateTime }

type State(root: string) =
  let view = Config.ScanView()
  let messages = ConcurrentQueue<Message>()
  let mutable plan: Sites.Plan option = None
  let mutable scanning = false
  let mutable visited = 0
  let mutable busy = ""
  let gate = obj ()

  member _.Root = root
  member _.View = view
  member _.Plan = plan
  member _.Scanning = scanning
  member _.Visited = visited
  member _.Busy with get () = busy and set v = busy <- v

  member _.Say (level: Level) (text: string) =
    let word =
      match level with
      | Fail -> "fail"
      | Warn -> "warn"
      | Success -> "success"
      | Info | Detail -> "info"
    messages.Enqueue { level = word; text = text; at = DateTime.Now }
    while messages.Count > 30 do
      messages.TryDequeue() |> ignore

  member _.Messages = messages.ToArray() |> Array.rev

  /// Scan (cache first) and plan, in the background.
  member this.Rescan() =
    lock gate (fun () ->
      if not scanning then
        scanning <- true
        Task.Run(fun () ->
          try
            try
              match Config.scanFromCache root with
              | Some(rows, _, _) -> view.Load rows
              | None -> ()
              plan <- Some(Sites.plan root)
              let n = Config.scanVisit root CancellationToken.None (fun n _ -> visited <- n) view.Add
              view.Commit()
              Config.saveScanCache root n (view.Rows())
              plan <- Some(Sites.plan root)
            with e ->
              this.Say Fail $"scan failed: {e.Message}"
          finally
            scanning <- false)
        |> ignore)

let private writeApp (w: Utf8JsonWriter) (root: string) (a: Sites.ViteApp) =
  w.WriteString("dir", a.dir)
  w.WriteString("name", a.name)
  match a.port with
  | Some p -> w.WriteNumber("port", p)
  | None -> w.WriteNull "port"
  w.WriteString("fable", (match a.fable with Plugin -> "plugin" | Cli _ -> "cli" | NoFable -> "none"))
  w.WriteBoolean("library", a.library)
  w.WriteBoolean("middleware", a.middleware)
  w.WriteString("outDir", Path.GetRelativePath(root, a.outDir))

/// Folders that may hold smoothdev-web state under the root: each scan hit's folder and the folders
/// above it (an app's config sits at or above its client), the root, and each site's repository.
let stateDirs (s: State) =
  let root = s.Root
  let within (d: string) = d = root || d.StartsWith(root + string Path.DirectorySeparatorChar)
  let rec upTo (d: string) =
    [ if within d then
        yield d
        if d <> root then
          match Path.GetDirectoryName d |> Option.ofObj with
          | None -> ()
          | Some parent -> yield! upTo parent ]
  [| yield root
     for r in s.View.Rows() do
       let full = Path.GetFullPath(root </> r.path)
       let dir = if File.Exists full then Path.GetDirectoryName full |> Option.ofObj |> Option.defaultValue root else full
       yield! upTo dir
     for site in s.Plan |> Option.map _.sites |> Option.defaultValue [||] do
       yield site.repo |]
  |> Array.distinct
  |> Array.filter (fun d -> File.Exists(State.file d))

/// Everything that runs under the root, wherever it was started: (folder, entry), the console itself left out.
let running (s: State) =
  stateDirs s
  |> Array.collect (fun d -> State.live d |> Array.map (fun e -> d, e))
  |> Array.filter (fun (_, e) -> Component.parse e.name <> Some Component.Gui)

/// The page's whole view, as `Model.Console` reads it.
let consoleJson (s: State) =
  use stream = new MemoryStream()
  use w = new Utf8JsonWriter(stream)
  let plan = s.Plan
  w.WriteStartObject()
  w.WriteString("root", s.Root)
  w.WriteBoolean("demo", false)
  w.WriteBoolean("scanning", s.Scanning)
  w.WriteNumber("visited", s.Visited)
  w.WriteString("busy", s.Busy)
  w.WriteStartArray "scan"
  for r in s.View.Rows() do
    w.WriteStartObject()
    w.WriteString("path", r.path)
    w.WriteString("kind", r.kind)
    w.WriteString("note", r.note)
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteStartArray "sites"
  for site in plan |> Option.map _.sites |> Option.defaultValue [||] do
    let p = Option.get plan
    let live = State.live site.repo |> Array.filter (fun e -> e.name.StartsWith "site-")
    w.WriteStartObject()
    w.WriteString("project", site.project)
    w.WriteString("name", site.name)
    w.WriteString("baseUrl", site.baseUrl)
    w.WriteString("repo", site.repo)
    w.WriteString("output", site.output)
    w.WriteNumber("pages", site.pages.Length)
    w.WriteStartArray "links"
    for (label, route) in site.links do
      w.WriteStartObject()
      w.WriteString("label", label)
      w.WriteString("route", route)
      w.WriteEndObject()
    w.WriteEndArray()
    w.WriteStartArray "reasons"
    for r in site.reasons do w.WriteStringValue r
    w.WriteEndArray()
    w.WriteStartArray "dev"
    for l in SiteActions.devLines p site do w.WriteStringValue l
    w.WriteEndArray()
    w.WriteStartArray "mounts"
    for m in SiteActions.mountsOf p site do
      w.WriteStartObject()
      w.WriteString("url", m.url)
      w.WriteString("route", m.route)
      w.WriteString("kind", m.kind.word)
      w.WriteString("confidence", m.confidence.word)
      w.WriteBoolean("pageOwned", m.pageOwned)
      w.WriteStartArray "reasons"
      for r in m.reasons do w.WriteStringValue r
      w.WriteEndArray()
      w.WriteStartObject "app"
      writeApp w site.repo m.app
      w.WriteEndObject()
      w.WriteEndObject()
    w.WriteEndArray()
    w.WriteStartArray "running"
    for e in live do
      w.WriteStartObject()
      w.WriteString("name", e.name)
      w.WriteNumber("pid", e.pid)
      w.WriteNumber("port", e.port)
      w.WriteString("url", e.url)
      w.WriteString("state", Actions.stateText (State.runState e))
      w.WriteString("uptime", Actions.uptime e)
      w.WriteEndObject()
    w.WriteEndArray()
    match live |> Array.tryFind (fun e -> e.name = SiteActions.frontName) with
    | Some f -> w.WriteString("front", f.url)
    | None -> w.WriteNull "front"
    w.WriteBoolean("distReady", Directory.Exists(SiteActions.distDir site))
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteStartArray "unmounted"
  for u in plan |> Option.map _.unmounted |> Option.defaultValue [||] do
    w.WriteStartObject()
    w.WriteStartObject "app"
    writeApp w s.Root u.app
    w.WriteEndObject()
    w.WriteString("reason", u.reason)
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteStartArray "running"
  for dir, e in running s do
    w.WriteStartObject()
    w.WriteString("dir", (let r = Path.GetRelativePath(s.Root, dir).Replace('\\', '/') in if r = "." then "" else r))
    w.WriteString("name", e.name)
    w.WriteNumber("pid", e.pid)
    w.WriteNumber("port", e.port)
    w.WriteString("url", e.url)
    w.WriteString("state", Actions.stateText (State.runState e))
    w.WriteString("uptime", Actions.uptime e)
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteStartArray "launches"
  let userHome = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
  for l in (try Launches.list (Launches.home ()) false with _ -> [||]) do
    let lead, chain = Launches.by l
    let age = DateTimeOffset.Now - l.startedAt
    w.WriteStartObject()
    w.WriteString("id", l.id)
    w.WriteString("status", Launches.status l)
    w.WriteString("by", lead)
    w.WriteString("chain", chain)
    w.WriteString("command", String.concat " " l.command)
    w.WriteString("dir", (if l.dir.StartsWith userHome then "~" + l.dir.Substring userHome.Length else l.dir))
    w.WriteString("started", (if age.TotalHours >= 1.0 then $"{int age.TotalHours}h{age.Minutes:D2}m" elif age.TotalMinutes >= 1.0 then $"{age.Minutes}m" else $"{age.Seconds}s"))
    w.WriteStartArray "ports"
    for p in l.ports |> Array.sortBy _.port do
      w.WriteStartObject()
      w.WriteNumber("port", p.port)
      w.WriteString("role", p.role)
      w.WriteString("url", p.url)
      w.WriteEndObject()
    w.WriteEndArray()
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteStartArray "others"
  for i in (try Others.Cache.get () with _ -> [||]) do
    w.WriteStartObject()
    w.WriteNumber("pid", i.pid)
    w.WriteString("project", i.project)
    w.WriteString("dir", (if i.cwd.StartsWith userHome then "~" + i.cwd.Substring userHome.Length else i.cwd))
    w.WriteString("kind", i.kind)
    w.WriteString("command", i.command)
    w.WriteString("title", i.title)
    w.WriteString("uptime", i.uptime)
    w.WriteString("by", i.by)
    w.WriteString("chain", i.chain)
    w.WriteStartArray "ports"
    for p, u in Array.zip i.ports i.urls do
      w.WriteStartObject()
      w.WriteNumber("port", p)
      w.WriteString("url", u)
      w.WriteEndObject()
    w.WriteEndArray()
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteStartArray "messages"
  for m in s.Messages do
    w.WriteStartObject()
    w.WriteString("level", m.level)
    w.WriteString("text", m.text)
    w.WriteString("at", m.at.ToString "HH:mm:ss")
    w.WriteNumber("age", int (DateTime.Now - m.at).TotalSeconds)
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteEndObject()
  w.Flush()
  Encoding.UTF8.GetString(stream.ToArray())

let private siteOf (s: State) (project: string) =
  s.Plan |> Option.bind (fun p -> p.sites |> Array.tryFind (fun x -> x.project = project))

/// Runs an action in the background; false when another one runs.
let perform (s: State) (action: string) (project: string) =
  if s.Busy <> "" then
    false
  elif action = "rescan" then
    s.Rescan ()
    true
  else
    match s.Plan, siteOf s project with
    | Some plan, Some site ->
      let ctx: Actions.Context =
        { config = SiteActions.configFor site; say = s.Say; echo = (fun _ _ -> ()); browser = false }
      let work: unit -> Task =
        match action with
        | "dev-start" -> fun () -> SiteActions.devStart ctx plan site None :> Task
        | "dev-stop"  -> fun () -> SiteActions.stop ctx :> Task
        | "dist"      -> fun () -> SiteActions.dist ctx plan site :> Task
        | "preview"   -> fun () -> SiteActions.preview ctx plan site :> Task
        | other       -> fun () -> (s.Say Fail $"unknown action {other}"; Task.CompletedTask)
      s.Busy <- $"{action} {site.name}"
      Task.Run(fun () ->
        task {
          try
            try
              do! work ()
            with e -> s.Say Fail e.Message
          finally
            s.Busy <- ""
        } :> Task)
      |> ignore
      true
    | _ ->
      s.Say Fail $"no site {project}"
      false

let private write (response: HttpListenerResponse) (status: int) (contentType: string) (body: byte array) =
  response.StatusCode <- status
  response.ContentType <- contentType
  response.Headers.Add("Cache-Control", "no-store")
  response.ContentLength64 <- int64 body.Length
  response.OutputStream.Write(body, 0, body.Length)
  response.Close()

let private json (response: HttpListenerResponse) (text: string) =
  write response 200 "application/json; charset=utf-8" (Encoding.UTF8.GetBytes text)

let handle (s: State) (dir: string) (ctx: HttpListenerContext) =
  let request = ctx.Request
  let path = request.Url |> Option.ofObj |> Option.map _.AbsolutePath |> Option.defaultValue "/"
  try
    match request.HttpMethod, path with
    | "GET", "/api/console" -> json ctx.Response (consoleJson s)
    | "GET", "/api/log" ->
      let q (k: string) = request.QueryString[k] |> Option.ofObj |> Option.defaultValue ""
      let name = q "name"
      let lines =
        match siteOf s (q "site") with
        | Some site when name.StartsWith "site-" ->
          match State.find site.repo name with
          | Some e -> Runner.tail e.log 200
          | None -> [||]
        | _ -> [||]
      json ctx.Response (JsonSerializer.Serialize {| name = name; lines = lines |})
    | "POST", "/api/action" ->
      use reader = new StreamReader(request.InputStream)
      use doc = JsonDocument.Parse(reader.ReadToEnd())
      let get (k: string) =
        let mutable v = Unchecked.defaultof<JsonElement>
        if doc.RootElement.TryGetProperty(k, &v) then v.GetString() |> Option.ofObj |> Option.defaultValue "" else ""
      let accepted = perform s (get "action") (get "site")
      json ctx.Response (JsonSerializer.Serialize {| accepted = accepted |})
    | "POST", "/api/others/stop" ->
      use reader = new StreamReader(request.InputStream)
      use doc = JsonDocument.Parse(reader.ReadToEnd())
      let mutable v = Unchecked.defaultof<JsonElement>
      let pid = if doc.RootElement.TryGetProperty("pid", &v) && v.ValueKind = JsonValueKind.Number then v.GetInt32() else 0
      let stopped =
        match Others.stop pid with
        | Ok i ->
          s.Say Success $"sent SIGTERM to {i.kind} {i.project} (pid {pid})"
          true
        | Error e ->
          s.Say Fail e
          false
      Others.Cache.invalidate ()
      json ctx.Response (JsonSerializer.Serialize {| stopped = stopped |})
    | "GET", _ ->
      match StaticServer.resolve dir "/" path with
      | Some file -> write ctx.Response 200 (StaticServer.contentType file) (File.ReadAllBytes file)
      | None -> write ctx.Response 404 "text/plain" (Encoding.UTF8.GetBytes "not found")
    | _ -> write ctx.Response 405 "text/plain" [||]
  with e ->
    try write ctx.Response 500 "text/plain" (Encoding.UTF8.GetBytes e.Message) with _ -> ()

let run (root: string) (dir: string) (preferredPort: int option) (browser: bool) =
  let s = State(Path.GetFullPath root)
  s.Rescan ()
  let port = Launches.Current.claim "console" (preferredPort |> Option.defaultValue Config.Defaults.guiPort) [||]
  let url = $"http://127.0.0.1:{port}/"
  use listener = new HttpListener()
  listener.Prefixes.Add url
  listener.Start()
  printfn "smoothdev-web console: %s  (Ctrl+C stops; what it started keeps running, `smoothdev-web site stop` ends it)" url
  if browser then
    try
      let opener = if OperatingSystem.IsMacOS() then "open" elif OperatingSystem.IsWindows() then "explorer" else "xdg-open"
      Diagnostics.Process.Start(opener, url) |> ignore
    with _ -> ()
  use stopping = new CancellationTokenSource()
  let stop (c: PosixSignalContext) =
    c.Cancel <- true
    stopping.Cancel()
    listener.Stop()
  use _int = PosixSignalRegistration.Create(PosixSignal.SIGINT, stop)
  use _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, stop)
  use _hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, stop)
  while not stopping.IsCancellationRequested do
    try
      let ctx = listener.GetContext()
      Task.Run(fun () -> handle s dir ctx) |> ignore
    with _ -> ()
  0
