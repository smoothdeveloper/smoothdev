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

/// Path.Combine, for scripts and tools that join paths a lot. Chain it: `root </> "dist" </> "index.html"`.
[<AutoOpen>]
module Paths =
  open System.IO

  let (</>) (a: string) (b: string) = Path.Combine(a, b)
