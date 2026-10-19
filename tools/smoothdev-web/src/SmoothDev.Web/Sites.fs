/// Static documentation sites (Nacara: the site is an F# program) and the Vite apps they host.
/// Everything here is read from the files: no process runs. `plan` says which app is mounted where
/// under which site, why, and how sure the guess is; `SiteActions` runs that plan.
module SmoothDev.Web.Sites

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

type Confidence =
  | High
  | Medium
  | Low

  member c.word =
    match c with
    | High   -> "high"
    | Medium -> "medium"
    | Low    -> "low"

/// A Nacara site: an F# program calling `Nacara.run`.
type Site =
  { project  : string          // absolute .fsproj
    dir      : string          // absolute project folder
    repo     : string          // absolute repository root (nearest .git), or dir
    name     : string
    baseUrl  : string          // "/mattern/": always starts and ends with '/'
    output   : string          // absolute output folder (pruned by every build)
    statics  : string option   // absolute folder copied verbatim to the site root
    content  : string array    // absolute content folders
    links    : (string * string) array // navbar (label, site-relative route "playground/")
    pages    : string array    // site-relative routes of content pages ("guide/introduction/")
    hooks    : string array    // <Exec Command> of the project: what its build runs first
    reasons  : string array }

/// A Vite app, as far as its config says.
type ViteApp =
  { dir        : string          // absolute
    name       : string          // package name, else folder name
    config     : string option   // absolute vite.config.*
    basePath   : string option   // `base` of the config
    outDir     : string          // absolute build.outDir
    port       : int option      // server.port
    fable      : Fable
    library    : bool            // build.lib: a library build, no page to mount
    pages      : int             // HTML entries (rollupOptions.input count, else index.html)
    middleware : bool            // configureServer: hand-written dev routes
    proxy      : bool }          // server.proxy

type MountKind =
  /// build.outDir is inside the site's static folder: the site copies the bundle at that route
  | StaticFolder
  /// build.outDir is inside the site's output: a site build prunes it
  | OutputFolder
  /// a navbar link or a content page has the app's name
  | Linked
  /// the app's path in the repository, under the closest content section
  | Convention
  /// smoothdev.site.json says so
  | Configured

  member k.word =
    match k with
    | StaticFolder -> "static-folder"
    | OutputFolder -> "output-folder"
    | Linked       -> "linked"
    | Convention   -> "convention"
    | Configured   -> "configured"

type Mount =
  { site       : Site
    app        : ViteApp
    route      : string          // site-relative, "playground/"
    url        : string          // absolute path on the dev front and in dist: base + route
    kind       : MountKind
    confidence : Confidence
    pageOwned  : bool            // a content page is published at the same route
    reasons    : string array }

type Unmounted =
  { app    : ViteApp
    reason : string }

type Plan =
  { root      : string
    sites     : Site array
    apps      : ViteApp array
    mounts    : Mount array
    unmounted : Unmounted array }

// Reading

let private readText path =
  if File.Exists path then File.ReadAllText path else ""

let private rel (root: string) (path: string) =
  Path.GetRelativePath(root, path).Replace('\\', '/')

let private inside (parent: string) (path: string) =
  let p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
  let c = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
  c.StartsWith(p, StringComparison.Ordinal)

let private first (pattern: string) (text: string) =
  let m = Regex.Match(text, pattern)
  if m.Success then Some m.Groups[1].Value else None

/// "/mattern" -> "/mattern/", "" -> "/".
let normalBase (b: string) =
  let t = b.Trim()
  let t = if t.StartsWith "/" then t else "/" + t
  if t.EndsWith "/" then t else t + "/"

/// A site-relative route: no leading slash, a trailing one ("" is the site root).
let normalRoute (r: string) =
  let t = r.Trim().Trim('/')
  if t = "" then "" else t + "/"

let rec repoRoot (dir: string) =
  if Directory.Exists(dir </> ".git") || File.Exists(dir </> ".git") then
    Some dir
  else
    match Directory.GetParent dir with
    | null -> None
    | parent -> repoRoot parent.FullName

/// A Nacara project: it references Nacara.Core (paket.references or PackageReference) and calls Nacara.run.
let isNacaraProject (project: string) =
  let dir = Path.GetDirectoryName project |> nonNull
  let refs = readText (dir </> "paket.references") + readText project
  refs.Contains "Nacara.Core"

let private sources (project: string) =
  let dir = Path.GetDirectoryName project |> nonNull
  Regex.Matches(readText project, """<Compile\s+Include="([^"]+)["]""")
  |> Seq.map (fun m -> Path.GetFullPath(dir </> m.Groups[1].Value.Replace('\\', '/')))
  |> Seq.filter File.Exists
  |> Seq.toArray

let private navbar =
  Regex("""Navbar(?:Link|Section)\s*\(\s*"([^"]*)"\s*,\s*(?:"[^"]*"\s*,\s*)?"(/[^"]*)"\s*\)""")

let private contentPages (contentDir: string) =
  if not (Directory.Exists contentDir) then
    [||]
  else
    Directory.GetFiles(contentDir, "*.md", SearchOption.AllDirectories)
    |> Array.map (fun f ->
      let r = rel contentDir f
      let r = r.Substring(0, r.Length - 3)
      if r = "index" then ""
      elif r.EndsWith "/index" then normalRoute (r.Substring(0, r.Length - 6))
      else normalRoute r)
    |> Array.distinct
    |> Array.sort

/// Reads a Nacara site from its project. None when the project is not one.
let readSite (project: string) : Site option =
  let project = Path.GetFullPath project
  if not (isNacaraProject project) then
    None
  else
    let dir = Path.GetDirectoryName project |> nonNull
    let code = sources project |> Array.map readText |> String.concat "\n"
    if not (code.Contains "Nacara.run") then
      None
    else
      let reasons = ResizeArray [ $"{Config.nameOf project} references Nacara.Core and calls Nacara.run" ]
      let siteRoot = dir </> "site-root.txt"

      let baseUrl =
        match first """Site\.baseUrl\s+"([^"]*)["]""" code with
        | Some b ->
          reasons.Add $"Site.baseUrl \"{b}\""
          normalBase b
        | None when File.Exists siteRoot ->
          let lines =
            File.ReadAllLines siteRoot
            |> Array.map _.Trim()
            |> Array.filter (fun l -> l <> "")
          match lines |> Array.tryFind (fun l -> l.StartsWith "/") with
          | Some b ->
            reasons.Add $"base {b} from site-root.txt"
            normalBase b
          | None -> "/"
        | None -> "/"

      let output = first """Site\.output\s+"([^"]+)["]""" code |> Option.defaultValue "output"

      let statics =
        if code.Contains "Site.noStaticFiles" then
          None
        else
          match first """Site\.staticFiles\s+"([^"]+)["]""" code with
          | Some s -> Some(Path.GetFullPath(dir </> s))
          | None ->
            let guess = dir </> "public"
            if Directory.Exists guess then Some guess else None

      let content =
        let named =
          Regex.Matches(code, """Theme\.docs\s+\w+\s+"([^"]+)["]""")
          |> Seq.map (fun m -> Path.GetFullPath(dir </> m.Groups[1].Value))
          |> Seq.toArray
        if named.Length > 0 then named
        elif Directory.Exists(dir </> "content") then [| dir </> "content" |]
        else [||]

      let links =
        navbar.Matches code
        |> Seq.map (fun m -> m.Groups[1].Value, normalRoute m.Groups[2].Value)
        |> Seq.toArray

      let hooks =
        Regex.Matches(readText project, """<Exec\s+Command="([^"]+)["]""")
        |> Seq.map (fun m -> System.Net.WebUtility.HtmlDecode m.Groups[1].Value |> nonNull)
        |> Seq.toArray

      Some
        { project = project
          dir     = dir
          repo    = repoRoot dir |> Option.defaultValue dir
          name    = first """Site\.create\s+"([^"]+)["]""" code |> Option.defaultValue (Config.nameOf dir)
          baseUrl = baseUrl
          output  = Path.GetFullPath(dir </> output)
          statics = statics
          content = content
          links   = links
          pages   = content |> Array.collect contentPages |> Array.distinct
          hooks   = hooks
          reasons = reasons.ToArray() }

let private viteConfigOf dir =
  if Directory.Exists dir then
    Directory.GetFiles(dir, "vite.config.*") |> Array.sort |> Array.tryHead
  else
    None

/// Reads what a Vite app's config says about where it lives and builds.
let readApp (dir: string) : ViteApp =
  let dir = Path.GetFullPath dir
  let config = viteConfigOf dir
  let text = config |> Option.map readText |> Option.defaultValue ""
  let library = Regex.IsMatch(text, """\blib\s*:\s*\{""")
  let inputs =
    match Regex.Match(text, """input\s*:\s*\{([^}]*)\}""", RegexOptions.Singleline) with
    | m when m.Success -> Regex.Matches(m.Groups[1].Value, """\.html['"`]""").Count
    | _ -> 0

  { dir        = dir
    name       = Config.packageName dir |> Option.defaultValue (Config.nameOf dir)
    config     = config
    basePath   = first """(?m)^\s*base\s*:\s*["'`]([^"'`]*)["'`]""" text
    outDir     = Config.detectDist dir
    port       = first """server\s*:\s*\{[^}]*?\bport\s*:\s*(\d+)""" text |> Option.map int
    fable      = Config.detectFable dir
    library    = library
    pages      = if inputs > 0 then inputs elif File.Exists(dir </> "index.html") then 1 else 0
    middleware = text.Contains "configureServer"
    proxy      = Regex.IsMatch(text, """\bproxy\s*:""") }

// Overrides: smoothdev.site.json at the repository (or scan) root, optional.
//   { "frontPort": 5400, "apps": { "playground": { "route": "lab/" }, "wasm": { "mount": false } } }

type AppOverride =
  { route : string option
    mount : bool }

type Overrides =
  { frontPort : int option
    apps      : Map<string, AppOverride> }   // key: app folder relative to the file's folder

let overridesFile = "smoothdev.site.json"

let readOverrides (dir: string) : Overrides =
  let file = dir </> overridesFile
  let none = { frontPort = None; apps = Map.empty }
  if not (File.Exists file) then
    none
  else
    let options = JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)
    use doc = JsonDocument.Parse(File.ReadAllText file, options)
    let root = doc.RootElement
    let mutable v = Unchecked.defaultof<JsonElement>
    let frontPort =
      if root.TryGetProperty("frontPort", &v) then Some(v.GetInt32()) else None
    let apps =
      if root.TryGetProperty("apps", &v) then
        [ for p in v.EnumerateObject() ->
            let mutable r = Unchecked.defaultof<JsonElement>
            let route = if p.Value.TryGetProperty("route", &r) then Some(normalRoute (r.GetString() |> nonNull)) else None
            let mount = not (p.Value.TryGetProperty("mount", &r) && r.ValueKind = JsonValueKind.False)
            p.Name.Trim('/'), { route = route; mount = mount } ]
        |> Map.ofList
      else
        Map.empty
    { frontPort = frontPort; apps = apps }

// Relations

let private lastSegment (route: string) =
  route.TrimEnd('/').Split('/') |> Array.last

/// Names an app answers to: its folder, its package name, and the package name without the site's prefix
/// ("mattern-playground" under the site "mattern" is "playground").
let private namesOf (site: Site) (app: ViteApp) =
  [|
    Config.nameOf app.dir
    app.name
    let prefix = site.name + "-"
    if app.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
      app.name.Substring prefix.Length
  |]
  |> Array.map _.ToLowerInvariant()
  |> Array.distinct

/// The app's folder in the repository, placed under the deepest content section with the same path
/// ("tools/smoothdev-web/gui" with content "tools/smoothdev-web/" -> "tools/smoothdev-web/gui/"),
/// else its folder name.
let conventionRoute (site: Site) (app: ViteApp) =
  let parts = (rel site.repo app.dir).Split('/')
  let sections =
    [ for k in parts.Length - 1 .. -1 .. 1 do
        let prefix = String.Join("/", parts[.. k - 1])
        if site.content |> Array.exists (fun c -> Directory.Exists(c </> prefix)) then
          yield k ]
  match sections with
  | k :: _ -> normalRoute (String.Join("/", parts)), Some(String.Join("/", parts[.. k - 1]))
  | [] -> normalRoute (Array.last parts), None

/// A build step of the site project that names the app: its package (`--filter <name>`) or its folder.
let private hookFor (site: Site) (app: ViteApp) =
  let names path = $"""(^|[\s"'=]){Regex.Escape path}([\s"'/]|$)"""
  let paths = [| rel site.dir app.dir; rel site.repo app.dir |]
  site.hooks
  |> Array.tryFind (fun h ->
    h.Contains($"--filter {app.name}") || paths |> Array.exists (fun p -> Regex.IsMatch(h, names p)))

/// Where the app goes under the site, if anywhere.
let mountOf (overrides: Overrides) (site: Site) (app: ViteApp) : Choice<Mount, string> =
  let key = rel site.repo app.dir
  let ov = overrides.apps |> Map.tryFind key
  let make route kind confidence (reasons: string list) =
    let hook = hookFor site app
    let reasons =
      [ yield! reasons
        match hook with
        | Some h -> yield $"{Config.nameOf site.project} builds it first: `{h}`"
        | None -> ()
        if app.middleware then
          yield "its vite config has hand-written dev routes (configureServer): the front proxy makes them unnecessary" ]
    let confidence =
      match confidence, hook with
      | Low, Some _ -> Medium
      | c, _ -> c
    let pageOwned = Array.contains route site.pages
    Choice1Of2
      { site       = site
        app        = app
        route      = route
        url        = site.baseUrl + route
        kind       = kind
        confidence = confidence
        pageOwned  = pageOwned
        reasons    = List.toArray reasons }

  if app.library then
    Choice2Of2 "library build (build.lib): no page to mount"
  elif app.pages = 0 then
    Choice2Of2 "no index.html or HTML input: no page to mount"
  elif not (inside site.repo app.dir) then
    Choice2Of2 "not in the site's repository"
  else
    match ov with
    | Some { mount = false } -> Choice2Of2 $"{overridesFile}: mount false"
    | Some { route = Some r } -> make r Configured High [ $"{overridesFile} sets route {r}" ]
    | _ ->
    if app.library then
      Choice2Of2 "library build (build.lib): no page to mount"
    elif app.pages = 0 then
      Choice2Of2 "no index.html or HTML input: no page to mount"
    else
      let outRel parent = normalRoute (rel parent app.outDir)
      match site.statics with
      | Some s when inside s app.outDir && Path.GetFullPath s <> Path.GetFullPath app.outDir ->
        let r = outRel s
        make r StaticFolder High [ $"build.outDir {rel app.dir app.outDir} is in the site's static folder {rel site.dir s}/: published at {site.baseUrl}{r}" ]
      | _ ->
      if inside site.output app.outDir && Path.GetFullPath site.output <> Path.GetFullPath app.outDir then
        let r = outRel site.output
        make r OutputFolder Medium [ $"build.outDir is in the site's output {rel site.dir site.output}/, which a site build prunes: assembled after the site instead" ]
      else
        let names = namesOf site app
        let link =
          site.links |> Array.tryFind (fun (_, r) -> r <> "" && Array.contains ((lastSegment r).ToLowerInvariant()) names)
        let page =
          site.pages |> Array.tryFind (fun r -> r <> "" && not (r.TrimEnd('/').Contains '/') && Array.contains ((lastSegment r).ToLowerInvariant()) names)
        match link, page with
        | Some(label, r), _ ->
          make r Linked Medium [ $"the navbar links \"{label}\" to {site.baseUrl}{r}, the app's name" ]
        | None, Some r ->
          make r Linked Medium [ $"the content page {r} has the app's name" ]
        | None, None ->
          let r, section = conventionRoute site app
          let why =
            match section with
            | Some s -> $"its folder {key} sits under the content section {s}/"
            | None -> $"its folder name, in the site's repository"
          make r Convention Low [ why ]

/// Sites and Vite apps under root (and the Vite apps in each site's repository), and how they connect.
let plan (root: string) : Plan =
  let root = Path.GetFullPath root
  let overrides =
    readOverrides (repoRoot root |> Option.defaultValue root)
  let projects = Config.findProjects 5 root |> Array.filter (fun p -> p.EndsWith ".fsproj")
  let sites = projects |> Array.choose readSite |> Array.sortBy _.project
  let appDirs =
    [|
      yield! Config.findViteApps 6 root
      for s in sites do
        yield! Config.findViteApps 6 s.repo
    |]
    |> Array.map Path.GetFullPath
    |> Array.distinct
  let apps = appDirs |> Array.map readApp |> Array.sortBy _.dir
  let mounts = ResizeArray()
  let unmounted = ResizeArray()
  for app in apps do
    let candidates = sites |> Array.map (fun s -> s, mountOf overrides s app)
    let best =
      candidates
      |> Array.choose (fun (_, c) -> match c with Choice1Of2 m -> Some m | _ -> None)
      |> Array.sortBy (fun m -> (match m.confidence with High -> 0 | Medium -> 1 | Low -> 2), -m.site.repo.Length)
      |> Array.tryHead
    match best with
    | Some m -> mounts.Add m
    | None ->
      let reason =
        candidates
        |> Array.tryPick (fun (_, c) -> match c with Choice2Of2 r when r <> "not in the site's repository" -> Some r | _ -> None)
        |> Option.orElse (if app.library then Some "library build (build.lib): no page to mount" elif app.pages = 0 then Some "no index.html or HTML input: no page to mount" else None)
        |> Option.defaultValue (if sites.Length = 0 then "no Nacara site found" else "not in a site's repository")
      unmounted.Add { app = app; reason = reason }
  { root      = root
    sites     = sites
    apps      = apps
    mounts    = mounts.ToArray()
    unmounted = unmounted.ToArray() }

/// A short id for an app, used in component and log names: "site-vite-<slug>".
let slug (app: ViteApp) =
  let s = Regex.Replace((Config.nameOf app.dir).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-')
  if s = "" then "app" else s

// Text and JSON

let describe (p: Plan) =
  let sb = Text.StringBuilder()
  let line (s: string) = sb.AppendLine s |> ignore
  let r path = let x = rel p.root path in if x = "." then "." else x
  if p.sites.Length = 0 then line "no Nacara site"
  for s in p.sites do
    line $"site     {r s.project}  \"{s.name}\" at {s.baseUrl}"
    let statics = s.statics |> Option.map (fun x -> r x + "/") |> Option.defaultValue "-"
    line $"           output {r s.output}/  static {statics}  pages {s.pages.Length}"
    for m in p.mounts |> Array.filter (fun m -> m.site.project = s.project) do
      let dist = if m.pageOwned then "dist: the site's own page stays" else $"dist: vite build into {m.route}"
      line $"  mount  {m.url,-28} <- {r m.app.dir}  [{m.kind.word}, {m.confidence.word}]  {dist}"
      for why in m.reasons do
        line $"           {why}"
      if m.pageOwned then
        line $"           the site also publishes a page at {m.route}: in dev the app answers there"
  for u in p.unmounted do
    line $"app      {r u.app.dir}  not mounted: {u.reason}"
  sb.ToString()

let toJson (p: Plan) =
  use stream = new MemoryStream()
  use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
  let r path = rel p.root path
  let app (w: Utf8JsonWriter) (a: ViteApp) =
    w.WriteString("dir", r a.dir)
    w.WriteString("name", a.name)
    a.basePath |> Option.iter (fun b -> w.WriteString("base", b))
    w.WriteString("outDir", r a.outDir)
    a.port |> Option.iter (fun x -> w.WriteNumber("port", x))
    w.WriteString("fable", (match a.fable with Plugin -> "plugin" | Cli _ -> "cli" | NoFable -> "none"))
    w.WriteBoolean("library", a.library)
    w.WriteNumber("pages", a.pages)
    w.WriteBoolean("middleware", a.middleware)
  w.WriteStartObject()
  w.WriteString("root", p.root)
  w.WriteStartArray "sites"
  for s in p.sites do
    w.WriteStartObject()
    w.WriteString("project", r s.project)
    w.WriteString("name", s.name)
    w.WriteString("base", s.baseUrl)
    w.WriteString("output", r s.output)
    s.statics |> Option.iter (fun x -> w.WriteString("static", r x))
    w.WriteNumber("pages", s.pages.Length)
    w.WriteStartArray "links"
    for (label, route) in s.links do
      w.WriteStartObject()
      w.WriteString("label", label)
      w.WriteString("route", route)
      w.WriteEndObject()
    w.WriteEndArray()
    w.WriteStartArray "reasons"
    for x in s.reasons do w.WriteStringValue x
    w.WriteEndArray()
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteStartArray "mounts"
  for m in p.mounts do
    w.WriteStartObject()
    w.WriteString("site", r m.site.project)
    w.WriteString("url", m.url)
    w.WriteString("route", m.route)
    w.WriteString("kind", m.kind.word)
    w.WriteString("confidence", m.confidence.word)
    w.WriteBoolean("pageOwned", m.pageOwned)
    w.WriteStartObject "app"
    app w m.app
    w.WriteEndObject()
    w.WriteStartArray "reasons"
    for x in m.reasons do w.WriteStringValue x
    w.WriteEndArray()
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteStartArray "unmounted"
  for u in p.unmounted do
    w.WriteStartObject()
    w.WriteStartObject "app"
    app w u.app
    w.WriteEndObject()
    w.WriteString("reason", u.reason)
    w.WriteEndObject()
  w.WriteEndArray()
  w.WriteEndObject()
  w.Flush()
  Text.Encoding.UTF8.GetString(stream.ToArray())
