/// Theme tokens as data: named values (colours, fonts) for a dark and a light scheme. A target turns them
/// into what it styles with (CSS custom properties, a resource dictionary, terminal colours).
module smoothdev.ui.Theme

type Scheme =
  | Dark
  | Light

type Theme =
  { name   : string
    scheme : Scheme
    /// Token name (without any prefix) and value, in order.
    tokens : (string * string) list }

/// The tokens every control may use; a theme gives each a value.
let names =
  [ "bg"; "panel"; "panel-2"; "line"; "ink"; "bright"; "muted"; "accent"; "accent-2"
    "ok"; "warn"; "bad"; "glow"; "mono"; "sans" ]

let private fonts =
  [ "mono", "ui-monospace, SFMono-Regular, \"SF Mono\", Menlo, Consolas, monospace"
    "sans", "-apple-system, BlinkMacSystemFont, \"Segoe UI\", Helvetica, Arial, sans-serif" ]

let dark =
  { name = "smoothdev dark"
    scheme = Dark
    tokens =
      [ "bg", "#0d1117"; "panel", "#161b22"; "panel-2", "#1c2128"; "line", "#30363d"; "ink", "#c9d1d9"
        "bright", "#f0f6fc"; "muted", "#8b949e"; "accent", "#58a6ff"; "accent-2", "#bc8cff"
        "ok", "#3fb950"; "warn", "#d29922"; "bad", "#f85149"; "glow", "rgba(88, 166, 255, .18)" ]
      @ fonts }

let light =
  { name = "smoothdev light"
    scheme = Light
    tokens =
      [ "bg", "#f6f8fa"; "panel", "#ffffff"; "panel-2", "#f1f3f5"; "line", "#d0d7de"; "ink", "#1f2328"
        "bright", "#0d1117"; "muted", "#656d76"; "accent", "#0969da"; "accent-2", "#8250df"
        "ok", "#1a7f37"; "warn", "#9a6700"; "bad", "#cf222e"; "glow", "rgba(9, 105, 218, .12)" ]
      @ fonts }

let tryGet (name: string) (t: Theme) = t.tokens |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

/// The theme with a token set (replaced in place, or added at the end).
let set (name: string) (value: string) (t: Theme) =
  if t.tokens |> List.exists (fun (k, _) -> k = name) then
    { t with tokens = t.tokens |> List.map (fun (k, v) -> if k = name then k, value else k, v) }
  else { t with tokens = t.tokens @ [ name, value ] }

/// Tokens of `extra` added to (or replacing those of) `t`: an application's own tokens on a base theme.
let extend (extra: (string * string) list) (t: Theme) = extra |> List.fold (fun t (k, v) -> set k v t) t

/// The token names a theme does not give.
let missing (t: Theme) = names |> List.filter (fun n -> (tryGet n t).IsNone)

let toggle =
  function
  | Dark -> Light
  | Light -> Dark

let schemeName =
  function
  | Dark -> "dark"
  | Light -> "light"
