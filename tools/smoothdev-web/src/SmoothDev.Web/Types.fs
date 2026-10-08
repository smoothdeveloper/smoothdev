namespace SmoothDev.Web

open System

/// The Node package manager an app uses (read from its lockfile).
type PackageManager =
  | Pnpm
  | Npm
  | Yarn
  | Bun

/// How the client's F# reaches the browser.
type Fable =
  /// vite-plugin-fable compiles the project from inside Vite
  | Plugin
  /// `dotnet fable watch` runs beside Vite and writes JavaScript to outDir (relative to the client dir)
  | Cli of project: string * outDir: string * extension: string option
  /// a plain Vite client, no F#
  | NoFable

/// The browser side: a Vite app, optionally fed by Fable.
type Client =
  { dir      : string          // absolute
    fable    : Fable
    port     : int             // preferred Vite dev port
    dist     : string          // absolute output folder of the production bundle
    basePath : string option } // Vite `base` for the bundle; None keeps the vite config's own

/// The .NET web server (ASP.NET Core, Giraffe, Saturn...): dotnet watch in dev, published for prod.
type Server =
  { project   : string  // absolute .fsproj/.csproj
    port      : int     // preferred dev port
    prodPort  : int     // preferred port of the published server
    serveDist : bool }  // point the published server's web root at the client's dist

type Config =
  { name           : string
    root           : string         // absolute app folder (where smoothdev.web.json lives)
    file           : string option  // the config file, None when detected
    packageManager : PackageManager
    client         : Client option
    server         : Server option
    previewPort    : int
    guiPort        : int }

/// A process (group) started by smoothdev-web, as recorded in the state file.
type Entry =
  { name      : string
    pid       : int
    pgid      : int             // 0: a single process, signalled by pid
    port      : int             // 0: none
    url       : string          // "": none
    log       : string          // "": none
    command   : string array
    startedAt : DateTimeOffset
    owner     : int }           // pid of the smoothdev-web process that started it

type RunState =
  | Running
  | Orphaned               // the leader exited, other members of its process group still run
  | Stopped
  | Unavailable of reason: string

type Row =
  { name  : string
    role  : string
    state : RunState
    entry : Entry option }

type Level =
  | Info
  | Success
  | Warn
  | Fail
  | Detail

/// A tracked process. `name` is what `Entry.name` stores; the state file keeps those words.
[<RequireQualifiedAccess>]
type Component =
  | Server
  | Fable
  | Vite
  | Prod
  | Preview
  | Gui

  member part.name =
    match part with
    | Server  -> "server"
    | Fable   -> "fable"
    | Vite    -> "vite"
    | Prod    -> "prod"
    | Preview -> "preview"
    | Gui     -> "gui"

  member part.role =
    match part with
    | Server  -> "dev server (dotnet watch)"
    | Fable   -> "Fable watch"
    | Vite    -> "Vite dev server"
    | Prod    -> "production server"
    | Preview -> "dist preview (static)"
    | Gui     -> "web GUI"

  static member parse name =
    match name with
    | "server"  -> Some Server
    | "fable"   -> Some Fable
    | "vite"    -> Some Vite
    | "prod"    -> Some Prod
    | "preview" -> Some Preview
    | "gui"     -> Some Gui
    | _         -> None

  static member dev = [| Server; Fable; Vite |]
  static member all = [| Server; Fable; Vite; Prod; Preview; Gui |]

/// What the GUI, the TUI and the CLI ask the tool to do. The route is the form path and the busy label.
[<RequireQualifiedAccess>]
type ActionTarget =
  | DevStart
  | DevStop
  | ProdStart
  | ProdStop
  | PreviewStart
  | PreviewStop
  | Dist
  | Build
  | Open
  | OpenDev
  | OpenDist
  | OpenProd
  | Stop

  member action.route =
    match action with
    | DevStart     -> "dev-start"
    | DevStop      -> "dev-stop"
    | ProdStart    -> "prod-start"
    | ProdStop     -> "prod-stop"
    | PreviewStart -> "preview-start"
    | PreviewStop  -> "preview-stop"
    | Dist         -> "dist"
    | Build        -> "build"
    | Open         -> "open"
    | OpenDev      -> "open-dev"
    | OpenDist     -> "open-dist"
    | OpenProd     -> "open-prod"
    | Stop         -> "stop"

  static member parse route =
    match route with
    | "dev-start"     -> Some DevStart
    | "dev-stop"      -> Some DevStop
    | "prod-start"    -> Some ProdStart
    | "prod-stop"     -> Some ProdStop
    | "preview-start" -> Some PreviewStart
    | "preview-stop"  -> Some PreviewStop
    | "dist"          -> Some Dist
    | "build"         -> Some Build
    | "open"          -> Some Open
    | "open-dev"      -> Some OpenDev
    | "open-dist"     -> Some OpenDist
    | "open-prod"     -> Some OpenProd
    | "stop"          -> Some Stop
    | _               -> None

  /// `smoothdev-web open <word>`. `preview` is the dist preview.
  static member parseOpen word =
    match word with
    | "dev"      -> Some OpenDev
    | "dist"
    | "preview" -> Some OpenDist
    | "prod"    -> Some OpenProd
    | _         -> None

/// Path.Combine, for scripts and tools that join paths a lot. Chain it: `root </> "dist" </> "index.html"`.
[<AutoOpen>]
module Paths =
  open System.IO

  let (</>) (a: string) (b: string) = Path.Combine(a, b)
