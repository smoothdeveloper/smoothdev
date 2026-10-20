/// Theme tokens as CSS custom properties (`--sd-bg: #0d1117;`): they reach inside every shadow root, so one
/// stylesheet on the page themes every element. Dark by default, light under `data-theme="light"` on
/// <html>, which the Nacara theme sets too.
module smoothdev.ui.fable.webcomponents.Css

open smoothdev.ui

let prefix = "sd"

/// `--<prefix>-<name>: <value>;` for each token, one line of `perLine` declarations.
let declarations (prefix: string) (t: Theme.Theme) =
  t.tokens |> List.map (fun (k, v) -> $"--{prefix}-{k}: {v};") |> String.concat " "

/// The stylesheet: the dark theme on :root, the light one under [data-theme="light"].
let stylesheet (prefix: string) (dark: Theme.Theme) (light: Theme.Theme) =
  $":root {{ {declarations prefix dark} color-scheme: dark; }}\n"
  + $":root[data-theme=\"light\"] {{ {declarations prefix light} color-scheme: light; }}\n"

/// `var(--sd-<name>)`, for an element's styles.
let var (name: string) = $"var(--{prefix}-{name})"
