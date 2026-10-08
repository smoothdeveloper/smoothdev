/// smoothdev.web.json: parsing, auto-detection when it is absent, and writing it back.
/// The signature lists what the rest of this tool (and its tests) call. Helpers stay in Config.fs.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module SmoothDev.Web.Config

val fileName: string

/// Path.GetFileName without the nullable result (F# does not read NotNullIfNotNull).
val nameOf: path: string -> string

module Defaults =
  val guiPort: int

val pmName: pm: PackageManager -> string
val lockFileOf: dir: string -> (string * PackageManager) option
val installArgv: pm: PackageManager -> hasLock: bool -> string array
val execArgv: pm: PackageManager -> bin: string -> string array
val detect: root: string -> Result<Config, string>
val parse: root: string -> file: string option -> json: string -> Result<Config, string>
val load: dir: string -> Result<Config, string>
val urlBase: basePath: string option -> string
val toJson: cfg: Config -> string

type ScanRow =
  { path : string
    kind : string
    note : string }

type ScanLine =
  { depth  : int
    path   : string
    fold   : bool option
    kind   : string
    label  : string
    note   : string
    apps   : int }

/// Vite apps, Web SDK projects and smoothdev.web.json files under root. A config file covers
/// the vite app and server projects inside its folder, so those are not listed again.
val scan: root: string -> ScanRow array

/// Walks root on the calling thread. onProgress runs for every folder (count, relative path);
/// onRow runs as soon as a hit is known. Returns how many folders were visited.
val scanVisit:
  root: string ->
  ct: System.Threading.CancellationToken ->
  onProgress: (int -> string -> unit) ->
  onRow: (ScanRow -> unit) ->
    int

/// Folder of a scan hit the tool can run: a config or Vite directory, or the directory of a server project.
val scanAppDir: root: string -> kind: string -> path: string -> string option
val scanCache: root: string -> ScanRow array
val saveScanCache: root: string -> visited: int -> rows: ScanRow array -> unit

/// Rows from the cache of root, or from the nearest ancestor cache that already covers root.
/// The bool is true when the rows come from an ancestor, so the walk can be skipped. Paths are
/// relative to root.
val scanFromCache: root: string -> (ScanRow array * bool * string) option

/// Folded folder tree. A folder is open when its path is in opened, closed when it is in closed,
/// and otherwise open for the first two levels. A folder with two or more apps is a group.
val scanLines: rows: ScanRow array -> opened: string array -> closed: string array -> ScanLine array

/// Shown rows. Seed with the cache, upsert hits as the walk finds them, and Commit once the
/// walk finishes so rows that disappeared are dropped.
type ScanView =
  new: unit -> ScanView
  member Load: cached: ScanRow array -> unit
  member Add: row: ScanRow -> unit
  member Commit: unit -> unit
  member Rows: unit -> ScanRow array
