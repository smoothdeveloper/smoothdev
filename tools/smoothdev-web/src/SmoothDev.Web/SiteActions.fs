/// Runs a site plan (`Sites.plan`): the site's own dev server, one Vite dev server per mounted app
/// under its base, and a front proxy that puts them on one port; or the assembled production site.
module SmoothDev.Web.SiteActions

open System
open System.IO
open System.Threading.Tasks
open SmoothDev.Web.Actions

let frontName  = "site-front"
let siteName   = "site-nacara"
let previewName = "site-preview"
let viteName (m: Sites.Mount) = $"site-vite-{Sites.slug m.app}"

module Defaults =
  let frontPort   = 5400
  let sitePort    = 8080
  let vitePort    = 5173
  let previewPort = 4180

/// A Config for the folder the plan was made in: the state file and the logs live there.
let configFor (plan: Sites.Plan) : Config =
  { name           = plan.sites |> Array.tryHead |> Option.map _.name |> Option.defaultValue (Config.nameOf plan.root)
    root           = plan.root
    file           = None
    packageManager = Config.detectPm [| plan.root |]
    client         = None
    server         = None
    previewPort    = Defaults.previewPort
    guiPort        = Config.Defaults.guiPort }

let siteNames (root: string) =
  State.live root |> Array.map _.name |> Array.filter (fun n -> n.StartsWith "site-")

/// The folder holding the app's lockfile (the app, or the workspace root above it, up to the repository).
let lockDir (repo: string) (dir: string) =
  let rec up (d: string) =
    match Config.lockFileOf d with
    | Some(_, pm) -> Some(d, pm)
    | None when Path.GetFullPath d = Path.GetFullPath repo -> None
    | None ->
      match Directory.GetParent d with
      | null -> None
      | p -> up p.FullName
  up dir

let clientOf (app: Sites.ViteApp) : Client =
  { dir      = app.dir
    fable    = app.fable
    port     = app.port |> Option.defaultValue Defaults.vitePort
    dist     = app.outDir
    basePath = None }

let ensureAppDeps ctx (m: Sites.Mount) =
  task {
    if not (Directory.Exists(m.app.dir </> "node_modules")) then
      match lockDir m.site.repo m.app.dir with
      | Some(dir, pm) ->
        ctx.say Info $"installing Node packages with {Config.pmName pm} in {Config.nameOf dir}"
        let! r = once ctx "install" (Config.installArgv pm true) [||] dir
        orFail r
      | None ->
        let pm = ctx.config.packageManager
        ctx.say Info $"installing Node packages with {Config.pmName pm}"
        let! r = once ctx "install" (Config.installArgv pm false) [||] m.app.dir
        orFail r
  }

let mountsOf (plan: Sites.Plan) (site: Sites.Site) =
  plan.mounts |> Array.filter (fun m -> m.site.project = site.project)

/// What `site dev` would start, one line each (for `site plan` and the GUI).
let devLines (plan: Sites.Plan) (site: Sites.Site) =
  [|
    $"{siteName,-24} dotnet run --project {Path.GetRelativePath(plan.root, site.project)} -- watch --port <free, from {Defaults.sitePort}>"
    for m in mountsOf plan site do
      $"{viteName m,-24} vite --base {m.url} --port <free, from {m.app.port |> Option.defaultValue Defaults.vitePort}> --strictPort  (in {Path.GetRelativePath(plan.root, m.app.dir)})"
    let apps = String.Join(", ", [| for m in mountsOf plan site -> m.url |])
    $"{frontName,-24} one port <free, from {Defaults.frontPort}>: {apps} -> Vite, the rest -> the site"
  |]

let devStart ctx (plan: Sites.Plan) (site: Sites.Site) (frontPreferred: int option) =
  let root = ctx.config.root
  let started = Collections.Generic.List<string>()
  task {
    match State.find root frontName with
    | Some e ->
      ctx.say Info $"the site already runs on {e.url}"
      return Ok e.url
    | None ->
    try
      let sitePort = pickPort ctx "the site" Defaults.sitePort
      let siteUrl = $"http://127.0.0.1:{sitePort}"
      let nacara =
        Runner.start
          root
          siteName
          [| "dotnet"; "run"; "--project"; site.project; "--"; "watch"; "--port"; string sitePort |]
          [| "DOTNET_NOLOGO", "1" |]
          site.dir
          sitePort
          (siteUrl + site.baseUrl)
        |> orFail
      started.Add siteName
      ctx.say Info (startedText nacara)

      let vites = Collections.Generic.List<Sites.Mount * Entry>()
      for m in mountsOf plan site do
        do! ensureAppDeps ctx m
        let port = pickPort ctx $"Vite ({m.app.name})" (m.app.port |> Option.defaultValue Defaults.vitePort)
        let argv =
          viteArgv
            ctx.config.packageManager
            (clientOf m.app)
            [| "--port"; string port; "--strictPort"; "--host"; "127.0.0.1"; "--base"; m.url |]
        let env =
          [| "SMOOTHDEV_WEB_VITE_PORT", string port
             "SMOOTHDEV_WEB_SITE_URL", siteUrl
             "BROWSER", "none" |]
        let e = Runner.start root (viteName m) argv env m.app.dir port $"http://127.0.0.1:{port}{m.url}" |> orFail
        started.Add e.name
        ctx.say Info (startedText e)
        vites.Add((m, e))

      let frontPort = pickPort ctx "the front" (frontPreferred |> Option.defaultValue Defaults.frontPort)
      let routes =
        [|
          for (m, e) in vites do
            yield $"{m.url}=http://127.0.0.1:{e.port}"
          yield $"/=http://127.0.0.1:{sitePort}"
        |]
      let front =
        Runner.start
          root
          frontName
          [| yield! Runner.selfArgv (); "__front"; string frontPort; site.baseUrl; yield! routes |]
          [||]
          root
          frontPort
          $"http://127.0.0.1:{frontPort}{site.baseUrl}"
        |> orFail
      started.Add frontName
      ctx.say Info (startedText front)

      let! frontReady = Runner.waitReady { front with url = $"http://127.0.0.1:{frontPort}/__front" } (TimeSpan.FromSeconds 15.)
      orFail frontReady
      for (m, e) in vites do
        let! ready = Runner.waitReady e (TimeSpan.FromSeconds 180.)
        match ready with
        | Ok () -> ctx.say Success $"{m.app.name} ready: http://127.0.0.1:{frontPort}{m.url}"
        | Error msg ->
          showTail ctx e
          raise (Step msg)
      let! siteReady = Runner.waitReady nacara (TimeSpan.FromSeconds 600.)
      match siteReady with
      | Ok () -> ()
      | Error msg ->
        showTail ctx nacara
        raise (Step msg)
      ctx.say Success $"site ready: {front.url}"
      return Ok front.url
    with Step msg ->
      ctx.say Fail msg
      let! _ = stopNames ctx (started.ToArray())
      return Error msg
  }

let stop ctx =
  task {
    let names = siteNames ctx.config.root
    // the front first, then the servers behind it
    let order = Array.append (names |> Array.filter ((=) frontName)) (names |> Array.filter ((<>) frontName))
    return! stopNames ctx (Array.rev order)
  }

let distDir (plan: Sites.Plan) = plan.root </> ".smoothdev" </> "web" </> "site-dist"

let rec private copyTree (src: string) (dst: string) =
  Directory.CreateDirectory dst |> ignore
  for f in Directory.GetFiles src do
    File.Copy(f, dst </> Config.nameOf f, true)
  for d in Directory.GetDirectories src do
    copyTree d (dst </> Config.nameOf d)

/// The production site: the site's own build, copied, then each mounted app built with its base into
/// its route. Apps whose route is also a page of the site leave that page as the site published it.
let dist ctx (plan: Sites.Plan) (site: Sites.Site) =
  guarded ctx (fun () ->
    task {
      let out = distDir plan
      let! built =
        once ctx "site-build" [| "dotnet"; "run"; "--project"; site.project; "--"; "build" |] [| "DOTNET_NOLOGO", "1" |] site.dir
      orFail built
      if not (Directory.Exists site.output) then
        raise (Step $"the site build did not produce {site.output}")
      if Directory.Exists out then
        Directory.Delete(out, true)
      copyTree site.output out
      ctx.say Info $"site copied from {Path.GetRelativePath(plan.root, site.output)} into {Path.GetRelativePath(plan.root, out)}"
      for m in mountsOf plan site do
        if m.pageOwned then
          ctx.say Info $"{m.app.name}: {m.route} is a page of the site, kept as the site built it"
        else
          do! ensureAppDeps ctx m
          let target = out </> m.route
          let argv =
            viteArgv
              ctx.config.packageManager
              (clientOf m.app)
              [| "build"; "--base"; m.url; "--outDir"; target; "--emptyOutDir" |]
          let! r = once ctx $"site-dist-{Sites.slug m.app}" argv [| "BROWSER", "none" |] m.app.dir
          orFail r
          ctx.say Info $"{m.app.name} built into {m.route}"
      let files = Directory.GetFiles(out, "*", SearchOption.AllDirectories)
      let kib = (files |> Array.sumBy (fun f -> FileInfo(f).Length)) / 1024L
      ctx.say Success $"site dist ready: {out} ({files.Length} files, {kib} KiB), served at {site.baseUrl}"
    })

let preview ctx (plan: Sites.Plan) (site: Sites.Site) =
  task {
    let root = ctx.config.root
    let out = distDir plan
    match State.find root previewName with
    | Some e ->
      ctx.say Info $"the site preview already runs on {e.url}"
      return Ok e.url
    | None when not (Directory.Exists out) ->
      return fail ctx $"no site dist at {out}: run `smoothdev-web site dist` first" |> Result.map (fun () -> "")
    | None ->
      let port = pickPort ctx "the site preview" Defaults.previewPort
      let argv = [| yield! Runner.selfArgv (); "__serve-static"; out; site.baseUrl; string port |]
      match Runner.start root previewName argv [||] root port $"http://127.0.0.1:{port}{site.baseUrl}" with
      | Error msg -> return fail ctx msg |> Result.map (fun () -> "")
      | Ok e ->
        let! ready = Runner.waitReady e (TimeSpan.FromSeconds 20.)
        match ready with
        | Ok () ->
          ctx.say Success $"site preview ready: {e.url}"
          return Ok e.url
        | Error msg -> return fail ctx msg |> Result.map (fun () -> "")
  }
