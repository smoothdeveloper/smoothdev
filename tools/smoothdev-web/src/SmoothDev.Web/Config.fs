/// smoothdev.web.json: parsing, auto-detection when it is absent, and writing it back.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module SmoothDev.Web.Config

open System
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.RegularExpressions

let fileName = "smoothdev.web.json"

/// Path.GetFileName without the nullable result (F# does not read NotNullIfNotNull).
let nameOf (path: string) =
  match Path.GetFileName path with
  | null -> ""
  | name -> name

module Defaults =
  let vitePort    = 5173
  let serverPort  = 5000
  let prodPort    = 8080
  let previewPort = 4173
  let guiPort     = 5399
  let fableOutDir = "fable-out"

// Package managers

let pmName pm =
  match pm with
  | Pnpm -> "pnpm"
  | Npm -> "npm"
  | Yarn -> "yarn"
  | Bun -> "bun"

let parsePm (s: string) =
  match s.Trim().ToLowerInvariant() with
  | "pnpm" -> Some Pnpm
  | "npm" -> Some Npm
  | "yarn" -> Some Yarn
  | "bun" -> Some Bun
  | _ -> None

let lockFiles =
  [|
    "pnpm-lock.yaml"   , Pnpm
    "package-lock.json", Npm
    "yarn.lock"        , Yarn
    "bun.lock"         , Bun
    "bun.lockb"        , Bun
  |]

let lockFileOf dir =
  lockFiles
  |> Array.tryFind (fun (f, _) -> File.Exists(dir </> f))

/// argv that installs the app's Node packages (frozen to the lockfile when there is one).
let installArgv pm hasLock =
  match pm, hasLock with
  | Pnpm, true  -> [| "pnpm"; "install"; "--frozen-lockfile" |]
  | Pnpm, false -> [| "pnpm"; "install" |]
  | Npm , true  -> [| "npm";  "ci";      "--no-audit"; "--no-fund" |]
  | Npm , false -> [| "npm";  "install"; "--no-audit"; "--no-fund" |]
  | Yarn, true  -> [| "yarn"; "install"; "--frozen-lockfile" |]
  | Yarn, false -> [| "yarn"; "install" |]
  | Bun , true  -> [| "bun";  "install"; "--frozen-lockfile" |]
  | Bun , false -> [| "bun";  "install" |]

/// argv that runs an installed package binary (vite) through the package manager, never installing it.
let execArgv pm bin =
  match pm with
  | Pnpm -> [| "pnpm"; "exec"; bin |]
  | Npm  -> [| "npm";  "exec"; "--no"; "--"; bin |]
  | Yarn -> [| "yarn"; bin |]
  | Bun  -> [| "bun";  "x";    bin |]

// Detection

let readText path =
  if File.Exists path then File.ReadAllText path else ""

let viteConfigs dir =
  if Directory.Exists dir then
    Directory.GetFiles(dir, "vite.config.*")
  else
    [||]

/// pnpm reads package.yaml in place of package.json. packages.yaml is the same file under the other spelling.
let manifests = [| "package.json"; "package.yaml"; "packages.yaml" |]

let manifestTexts dir =
  manifests
  |> Array.choose (fun name ->
    let path = dir </> name
    if File.Exists path then Some(readText path) else None)

/// A Vite app: a vite config, or a manifest that depends on vite. The manifest may be package.json,
/// package.yaml, or absent when pnpm-lock.yaml sits next to the vite config.
let isViteApp dir =
  let texts = manifestTexts dir
  let hasLock = File.Exists(dir </> "pnpm-lock.yaml")
  let hasViteConfig = (viteConfigs dir).Length > 0

  let mentionsVite (text: string) =
    text.Contains "\"vite\"" || Regex.IsMatch(text, "(?m)^\\s*vite\\s*:")

  (texts.Length > 0 || hasLock) && (hasViteConfig || texts |> Array.exists mentionsVite)

let isWebSdk project =
  (readText project).Contains "Microsoft.NET.Sdk.Web"

let skipDirs =
  set
    [
      "node_modules"
      "bin"
      "obj"
      "fable_modules"
      ".git"
      "dist"
      ".smoothdev"
      "artifacts"
      "paket-files"
      ".paket"
    ]

/// .fsproj/.csproj files under dir, at most depth folders down, skipping build and package folders.
let rec findProjects depth dir =
  if depth < 0 || not (Directory.Exists dir) then
    [||]
  else
    let here =
      Array.append (Directory.GetFiles(dir, "*.fsproj")) (Directory.GetFiles(dir, "*.csproj"))

    let below =
      Directory.GetDirectories dir
      |> Array.filter (fun d -> not (skipDirs.Contains(nameOf d)))
      |> Array.collect (findProjects (depth - 1))

    Array.append here below

let scriptImport =
  Regex("""(?:src|from|import)\s*=?\s*["'](?:\./|/)?([A-Za-z0-9_\-.]+)/[^"']*\.m?js["']""")

/// The folder index.html loads Fable's JavaScript from (`./out-js/App.js` -> out-js), or the default.
let guessOutDir clientDir =
  let html = readText (clientDir </> "index.html")

  scriptImport.Matches html
  |> Seq.map _.Groups[1].Value
  |> Seq.tryFind (fun d -> not (List.contains d [ "node_modules"; "src"; "assets"; "public"; "." ]))
  |> Option.defaultValue Defaults.fableOutDir

/// vite-plugin-fable when package.json or the vite config mention it, else `dotnet fable` when the
/// client dir holds an F# project, else no Fable.
let detectFable clientDir =
  let mentionsPlugin =
    [|
      for name in manifests -> clientDir </> name
      yield! viteConfigs clientDir
    |]
    |> Array.exists (fun f -> (readText f).Contains "vite-plugin-fable")

  if mentionsPlugin then
    Plugin
  else
    Directory.GetFiles(clientDir, "*.fsproj")
    |> Array.filter (isWebSdk >> not)
    |> Array.sort
    |> Array.tryHead
    |> Option.map (fun p -> Cli(p, guessOutDir clientDir, None))
    |> Option.defaultValue NoFable

let packageManagerField dir =
  let text = readText (dir </> "package.json")
  let m = Regex.Match(text, "\"packageManager\"\\s*:\\s*\"([a-z]+)@")
  if m.Success then parsePm m.Groups[1].Value else None

let detectPm dirs =
  dirs
  |> Array.tryPick (fun d -> lockFileOf d |> Option.map snd)
  |> Option.orElse (dirs |> Array.tryPick packageManagerField)
  |> Option.defaultValue Pnpm

let packageName dir =
  manifestTexts dir
  |> Array.tryPick (fun text ->
    let json = Regex.Match(text, "\"name\"\\s*:\\s*\"([^\"]+)\"")
    let yaml = Regex.Match(text, "(?m)^name:\\s*([^\\s#]+)")

    if json.Success then Some json.Groups[1].Value
    elif yaml.Success then Some yaml.Groups[1].Value
    else None)

let clientCandidates =
  [| "."; "src/Client"; "src/client"; "client"; "src/Web"; "web"; "frontend" |]

/// Builds a config from what the folder contains: a Vite app (at the root or a usual client folder) and
/// an ASP.NET Core project (Microsoft.NET.Sdk.Web) anywhere up to four folders down.
let detect root =
  let root = Path.GetFullPath root

  let client =
    clientCandidates
    |> Array.map (fun c -> Path.GetFullPath(root </> c))
    |> Array.tryFind isViteApp
    |> Option.map (fun dir ->
      {
        dir = dir
        fable = detectFable dir
        port = Defaults.vitePort
        dist = dir </> "dist"
        basePath = None
      })

  let clientProject =
    match client with
    | Some { fable = Cli(p, _, _) } -> Some p
    | _ -> None

  let server =
    findProjects 4 root
    |> Array.filter isWebSdk
    |> Array.filter (fun p -> Some p <> clientProject)
    |> Array.sortBy _.Length
    |> Array.tryHead
    |> Option.map (fun p ->
      {
        project = p
        port = Defaults.serverPort
        prodPort = Defaults.prodPort
        serveDist = client.IsSome
      })

  if client.IsNone && server.IsNone then
    Error $"no {fileName} in {root} or above, and no Vite app or ASP.NET Core project detected in it"
  else
    let dirs =
      match client with
      | Some c -> [| c.dir; root |]
      | None   -> [| root |]

    let name =
      dirs
      |> Array.tryPick packageName
      |> Option.defaultValue (nameOf root)

    Ok
      {
        name           = name
        root           = root
        file           = None
        packageManager = detectPm dirs
        client         = client
        server         = server
        previewPort    = Defaults.previewPort
        guiPort        = Defaults.guiPort
      }

// Parsing

let prop (name: string) (e: JsonElement) =
  match e.TryGetProperty name with
  | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
  | _ -> None

let expectObject where (e: JsonElement) =
  if e.ValueKind <> JsonValueKind.Object then
    failwith $"{where} must be an object"

let checkKeys where (keys: string list) (e: JsonElement) =
  for p in e.EnumerateObject() do
    if not (List.contains p.Name keys) then
      failwith $"""unknown key "{p.Name}" in {where} (expected one of: {String.Join(", ", keys)})"""

let text (v: JsonElement) =
  match v.GetString() with
  | null -> ""
  | s -> s

let str name e =
  prop name e
  |> Option.map (fun v ->
    if v.ValueKind = JsonValueKind.String then
      text v
    else
      failwith $"\"{name}\" must be a string")

let port name e =
  prop name e
  |> Option.map (fun v ->
    match v.ValueKind, v.TryGetInt32() with
    | JsonValueKind.Number, (true, p) when p > 0 && p < 65536 -> p
    | _ -> failwith $"\"{name}\" must be a port number (1-65535)")

let flag name e =
  prop name e
  |> Option.map (fun v ->
    match v.ValueKind with
    | JsonValueKind.True  -> true
    | JsonValueKind.False -> false
    | _                   -> failwith $"\"{name}\" must be true or false")

let resolve dir path =
  Path.GetFullPath(dir </> path)

let fableNamed clientDir name =
  match name with
  | "plugin" -> Plugin
  | "none"   -> NoFable
  | "cli"    ->
    match detectFable clientDir with
    | Cli _ as cli -> cli
    | _            -> failwith "client.fable is \"cli\" but the client dir holds no .fsproj"
  | other ->
    failwith $"client.fable must be \"plugin\", \"cli\", \"none\" or an object, not \"{other}\""

let fableObject clientDir (v: JsonElement) =
  expectObject "client.fable" v
  checkKeys "client.fable" [ "project"; "outDir"; "extension" ] v

  let project =
    str "project" v
    |> Option.defaultWith (fun () -> failwith "client.fable.project is required")
    |> resolve clientDir

  if not (File.Exists project) then
    failwith $"client.fable.project: {project} does not exist"

  Cli(project, str "outDir" v |> Option.defaultValue (guessOutDir clientDir), str "extension" v)

let parseClient root (c: JsonElement) =
  expectObject "client" c
  checkKeys "client" [ "dir"; "fable"; "port"; "dist"; "base" ] c
  let dir = str "dir" c |> Option.defaultValue "." |> resolve root

  if not (Directory.Exists dir) then
    failwith $"client.dir: {dir} does not exist"

  let fable =
    match prop "fable" c with
    | None -> detectFable dir
    | Some v when v.ValueKind = JsonValueKind.String -> fableNamed dir (text v)
    | Some v -> fableObject dir v

  {
    dir      = dir
    fable    = fable
    port     = port "port" c |> Option.defaultValue Defaults.vitePort
    dist     = str "dist" c |> Option.defaultValue "dist" |> resolve dir
    basePath = str "base" c
  }

let parseServer root hasClient (s: JsonElement) =
  expectObject "server" s
  checkKeys "server" [ "project"; "port"; "prodPort"; "serveDist" ] s

  let project =
    str "project" s
    |> Option.defaultWith (fun () -> failwith "server.project is required")
    |> resolve root

  if not (File.Exists project) then
    failwith $"server.project: {project} does not exist"

  {
    project = project
    port = port "port" s |> Option.defaultValue Defaults.serverPort
    prodPort = port "prodPort" s |> Option.defaultValue Defaults.prodPort
    serveDist = flag "serveDist" s |> Option.defaultValue hasClient
  }

/// Parses smoothdev.web.json text; relative paths resolve against root (the file's folder).
let parse root file (json: string) =
  try
    let options =
      JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)

    use doc = JsonDocument.Parse(json, options)
    let top = doc.RootElement
    expectObject "the top level" top

    checkKeys
      "the top level"
      [
        "$schema"
        "name"
        "packageManager"
        "client"
        "server"
        "previewPort"
        "guiPort"
      ]
      top

    let client = prop "client" top |> Option.map (parseClient root)
    let server = prop "server" top |> Option.map (parseServer root client.IsSome)

    let dirs =
      match client with
      | Some c -> [| c.dir; root |]
      | None -> [| root |]

    let pm =
      match str "packageManager" top with
      | Some s ->
        parsePm s
        |> Option.defaultWith (fun () -> failwith $"packageManager must be pnpm, npm, yarn or bun, not \"{s}\"")
      | None -> detectPm dirs

    Ok
      {
        name =
          str "name" top
          |> Option.orElse (dirs |> Array.tryPick packageName)
          |> Option.defaultValue (nameOf root)
        root = root
        file = file
        packageManager = pm
        client = client
        server = server
        previewPort = port "previewPort" top |> Option.defaultValue Defaults.previewPort
        guiPort = port "guiPort" top |> Option.defaultValue Defaults.guiPort
      }
  with
  | :? JsonException as e -> Error $"{fileName}: invalid JSON ({e.Message})"
  | Failure msg -> Error $"{fileName}: {msg}"

let rec findRoot dir =
  if File.Exists(dir </> fileName) then
    Some dir
  else
    match Directory.GetParent dir with
    | null -> None
    | parent -> findRoot parent.FullName

/// The config of the app containing dir: the nearest smoothdev.web.json at or above it, else detection.
let load dir =
  let dir = Path.GetFullPath dir

  match findRoot dir with
  | Some root ->
    let file = root </> fileName
    parse root (Some file) (File.ReadAllText file)
  | None -> detect dir

/// The URL path the bundle is served under (Vite's `base`): "/" unless an absolute base is configured.
let urlBase (basePath: string option) =
  match basePath |> Option.map _.Trim('/', '.') with
  | None
  | Some "" -> "/"
  | Some b -> $"/{b}/"

// Writing

/// smoothdev.web.json text for a config (paths relative to its root): `config init` and `config show`.
let toJson (cfg: Config) =
  use stream = new MemoryStream()

  use w =
    new Utf8JsonWriter(
      stream,
      JsonWriterOptions(Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
    )

  let rel from path =
    Path.GetRelativePath(from, path).Replace('\\', '/')

  w.WriteStartObject()
  w.WriteString("name", cfg.name)
  w.WriteString("packageManager", pmName cfg.packageManager)

  match cfg.client with
  | Some c ->
    w.WriteStartObject "client"
    w.WriteString("dir", rel cfg.root c.dir)

    match c.fable with
    | Plugin -> w.WriteString("fable", "plugin")
    | NoFable -> w.WriteString("fable", "none")
    | Cli(project, outDir, extension) ->
      w.WriteStartObject "fable"
      w.WriteString("project", rel c.dir project)
      w.WriteString("outDir", outDir)
      extension |> Option.iter (fun e -> w.WriteString("extension", e))
      w.WriteEndObject()

    w.WriteNumber("port", c.port)
    w.WriteString("dist", rel c.dir c.dist)
    c.basePath |> Option.iter (fun b -> w.WriteString("base", b))
    w.WriteEndObject()
  | None -> ()

  match cfg.server with
  | Some s ->
    w.WriteStartObject "server"
    w.WriteString("project", rel cfg.root s.project)
    w.WriteNumber("port", s.port)
    w.WriteNumber("prodPort", s.prodPort)
    w.WriteBoolean("serveDist", s.serveDist)
    w.WriteEndObject()
  | None -> ()

  w.WriteNumber("previewPort", cfg.previewPort)
  w.WriteNumber("guiPort", cfg.guiPort)
  w.WriteEndObject()
  w.Flush()
  Encoding.UTF8.GetString(stream.ToArray()) + "\n"

// Scanning a tree of repositories. One walk: configs, Vite apps, Web SDK projects.

type ScanRow =
  { path : string
    kind : string
    note : string }

type ScanLine =
  { depth : int
    path  : string
    fold  : bool option
    kind  : string
    label : string
    note  : string
    apps  : int }

let scanSkip =
  skipDirs
  |> Set.add "target"
  |> Set.add "packages"

let relTo root path =
  Path.GetRelativePath(root, path).Replace('\\', '/')

let scanVisit root (ct: Threading.CancellationToken) onProgress onRow =
  let root = Path.GetFullPath root
  let visited = ref 0

  let rec walk inside dir =
    if not ct.IsCancellationRequested then
      visited.Value <- visited.Value + 1
      onProgress visited.Value (relTo root dir)

      let hereConfig = File.Exists(dir </> fileName)

      if hereConfig then
        let name =
          match load dir with
          | Ok cfg -> cfg.name
          | Error _ -> nameOf dir

        onRow { path = relTo root dir; kind = "config"; note = name }

      if not inside && not hereConfig then
        if isViteApp dir then
          let name = packageName dir |> Option.defaultValue (nameOf dir)
          onRow { path = relTo root dir; kind = "vite"; note = name }

        for project in Array.append (Directory.GetFiles(dir, "*.fsproj")) (Directory.GetFiles(dir, "*.csproj")) do
          if isWebSdk project then
            onRow
              { path = relTo root project
                kind = "server"
                note = nameOf project }

      if not ct.IsCancellationRequested then
        for sub in Directory.GetDirectories dir do
          if not (scanSkip.Contains(nameOf sub)) then
            walk (inside || hereConfig) sub

  if Directory.Exists root then
    walk false root

  visited.Value

let scan root =
  let rows = ResizeArray()

  scanVisit root Threading.CancellationToken.None (fun _ _ -> ()) (rows.Add)
  |> ignore

  rows.ToArray()

let cacheFile root =
  let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
  let key = Path.GetFullPath(root).Replace(Path.DirectorySeparatorChar, '_').Replace(':', '_')
  home </> ".smoothdev" </> "scan" </> $"{key}.json"

/// Folder of a scan hit the tool can run: a config or Vite directory, or the directory of a server project.
let scanAppDir root kind path =
  if kind = "folder" then
    None
  else
    let full =
      if path = "" || path = "." then
        Path.GetFullPath root
      else
        Path.GetFullPath(root </> path)

    let dir =
      if kind = "server" then
        match Path.GetDirectoryName full with
        | null -> full
        | parent -> parent
      else
        full

    Some dir

let private rootFromKey (key: string) =
  let guess =
    if key.StartsWith "_" then
      "/" + key.Substring(1).Replace('_', '/')
    else
      key.Replace('_', '/')

  if Directory.Exists guess then Some guess else None

let private readCacheFile file =
  try
    let text = File.ReadAllText file
    use doc = JsonDocument.Parse text

    if doc.RootElement.ValueKind = JsonValueKind.Array then
      let rows = JsonSerializer.Deserialize<ScanRow array> text |> Option.ofObj |> Option.defaultValue [||]

      let key =
        match Path.GetFileNameWithoutExtension file with
        | null -> ""
        | name -> name

      match rootFromKey key with
      | Some cachedRoot -> Some(cachedRoot, rows)
      | None -> None
    else
      let cachedRoot = doc.RootElement.GetProperty("root").GetString()
      let rows = JsonSerializer.Deserialize<ScanRow array>(doc.RootElement.GetProperty("rows").GetRawText())

      match cachedRoot with
      | null -> None
      | cachedRoot -> Some(cachedRoot, rows |> Option.ofObj |> Option.defaultValue [||])
  with _ ->
    None

let private rebase cachedRoot requested (rows: ScanRow array) =
  rows
  |> Array.choose (fun row ->
    let full = Path.GetFullPath(cachedRoot </> row.path)
    let rel = Path.GetRelativePath(requested, full).Replace('\\', '/')

    if rel = ".." || rel.StartsWith "../" then
      None
    else
      Some { row with path = if rel = "." then "" else rel })

/// Rows from the cache of root, or from the nearest ancestor cache that already covers root.
let scanFromCache root =
  let requested = Path.GetFullPath root

  match Path.GetDirectoryName(cacheFile requested) with
  | null -> None
  | dir when not (Directory.Exists dir) -> None
  | dir ->
    Directory.GetFiles(dir, "*.json")
    |> Array.choose readCacheFile
    |> Array.filter (fun (cachedRoot, _) ->
      requested = cachedRoot || requested.StartsWith(cachedRoot.TrimEnd('/') + "/"))
    |> Array.sortByDescending (fun (cachedRoot, _) -> cachedRoot.Length)
    |> Array.tryHead
    |> Option.map (fun (cachedRoot, rows) ->
      rebase cachedRoot requested rows, cachedRoot <> requested, cachedRoot)

let scanCache root =
  match scanFromCache root with
  | Some(rows, false, _) -> rows
  | _ -> [||]

let saveScanCache root (visited: int) (rows: ScanRow array) =
  ignore visited
  let path = cacheFile root

  match Path.GetDirectoryName path with
  | null -> ()
  | dir -> Directory.CreateDirectory dir |> ignore

  File.WriteAllText(path, JsonSerializer.Serialize {| root = Path.GetFullPath root; rows = rows |})

type private Branch =
  { hits : ScanRow list
    dirs : Map<string, Branch> }

let private emptyBranch = { hits = []; dirs = Map.empty }

let rec private insert parts row branch =
  match parts with
  | [] -> { branch with hits = row :: branch.hits }
  | name :: rest ->
    let child = branch.dirs |> Map.tryFind name |> Option.defaultValue emptyBranch
    { branch with dirs = branch.dirs.Add(name, insert rest row child) }

let rec private countApps branch =
  branch.hits.Length + (branch.dirs |> Map.fold (fun n _ child -> n + countApps child) 0)

let private join path name =
  if path = "" then
    name
  else
    $"{path}/{name}"

let private isOpen path (opened: Set<string>) (closed: Set<string>) =
  if Set.contains path closed then false
  elif Set.contains path opened then true
  else path.Split('/').Length <= 2

let scanLines (rows: ScanRow array) (opened: string array) (closed: string array) =
  let root =
    rows
    |> Array.fold
      (fun branch row ->
        let parts = if row.path = "" || row.path = "." then [] else row.path.Split '/' |> Array.toList
        insert parts row branch)
      emptyBranch

  let opened = Set.ofArray opened
  let closed = Set.ofArray closed
  let lines = ResizeArray()

  let rec flatten depth path branch =
    let apps = countApps branch
    let leaf = branch.dirs.IsEmpty && branch.hits.Length = 1
    let label = if path = "" then "." else path.Split '/' |> Array.last

    if path <> "" && leaf then
      let row = branch.hits.Head

      lines.Add
        { depth = depth
          path  = row.path
          fold  = None
          kind  = row.kind
          label = label
          note  = row.note
          apps  = 1 }
    elif path <> "" then
      let openNow = isOpen path opened closed

      lines.Add
        { depth = depth
          path  = path
          fold  = Some openNow
          kind  = "folder"
          label = label
          note  = if apps > 1 then $"{apps} apps" else ""
          apps  = apps }

      if openNow then
        for row in List.rev branch.hits do
          lines.Add
            { depth = depth + 1
              path  = row.path
              fold  = None
              kind  = row.kind
              label = row.path.Split '/' |> Array.last
              note  = row.note
              apps  = 1 }

        for name, child in Map.toArray branch.dirs do
          flatten (depth + 1) (join path name) child
    else
      for name, child in Map.toArray branch.dirs do
        flatten 0 name child

  flatten 0 "" root
  lines.ToArray()

/// Shown rows. Seed with the cache, upsert hits as the walk finds them, and Commit once the
/// walk finishes so rows that disappeared are dropped.
type ScanView() =
  let gate = obj ()
  let rows = ResizeArray<ScanRow>()
  let seen = Collections.Generic.HashSet<string>()
  let key (row: ScanRow) = $"{row.kind}\n{row.path}"

  member _.Load(cached: ScanRow array) =
    lock gate (fun () -> rows.AddRange cached)

  member _.Add row =
    lock gate (fun () ->
      seen.Add(key row) |> ignore

      match rows |> Seq.tryFindIndex (fun r -> key r = key row) with
      | Some i -> rows[i] <- row
      | None -> rows.Add row)

  member _.Commit() =
    lock gate (fun () ->
      let keep = rows |> Seq.filter (fun r -> seen.Contains(key r)) |> Seq.toArray
      rows.Clear()
      rows.AddRange keep)

  member _.Rows() =
    lock gate (fun () -> rows.ToArray())

