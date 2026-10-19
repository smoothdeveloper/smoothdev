/// The palette: the page's custom properties (they reach inside every component's shadow root), dark
/// and light after `data-theme` on <html>, which the Nacara theme sets too.
module SmoothDev.Web.Gui.Theme

open Browser

let palette = """
:root {
  --sd-bg: #0d1117; --sd-panel: #161b22; --sd-panel-2: #1c2128; --sd-line: #30363d; --sd-ink: #c9d1d9;
  --sd-bright: #f0f6fc; --sd-muted: #8b949e; --sd-accent: #58a6ff; --sd-accent-2: #bc8cff;
  --sd-ok: #3fb950; --sd-warn: #d29922; --sd-bad: #f85149; --sd-glow: rgba(88, 166, 255, .18);
  --sd-site: #bc8cff; --sd-vite: #f0b72f; --sd-server: #3fb950; --sd-config: #58a6ff;
  --sd-mono: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace;
  --sd-sans: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
  color-scheme: dark;
}
:root[data-theme="light"] {
  --sd-bg: #f6f8fa; --sd-panel: #ffffff; --sd-panel-2: #f1f3f5; --sd-line: #d0d7de; --sd-ink: #1f2328;
  --sd-bright: #0d1117; --sd-muted: #656d76; --sd-accent: #0969da; --sd-accent-2: #8250df;
  --sd-ok: #1a7f37; --sd-warn: #9a6700; --sd-bad: #cf222e; --sd-glow: rgba(9, 105, 218, .12);
  --sd-site: #8250df; --sd-vite: #b08800; --sd-server: #1a7f37; --sd-config: #0969da;
  color-scheme: light;
}
"""

let install () =
  let style = document.createElement "style"
  style.textContent <- palette
  document.head.appendChild style |> ignore

/// Flip dark / light, remembered under the Nacara theme's key.
let toggle () =
  let root = document.documentElement
  let next = if root.getAttribute "data-theme" = "light" then "dark" else "light"
  root.setAttribute ("data-theme", next)
  try window.localStorage.setItem ("nacara-theme", next) with _ -> ()
