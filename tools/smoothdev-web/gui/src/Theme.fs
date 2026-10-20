/// The palette: smoothdev.ui's theme tokens and the console's own (one colour per kind of process), as the
/// page's custom properties (they reach inside every component's shadow root), dark and light after
/// `data-theme` on <html>, which the Nacara theme sets too.
module SmoothDev.Web.Gui.Theme

open smoothdev.ui
open smoothdev.ui.fable.webcomponents

let dark = Theme.dark |> Theme.extend [ "site", "#bc8cff"; "vite", "#f0b72f"; "server", "#3fb950"; "config", "#58a6ff" ]

let light = Theme.light |> Theme.extend [ "site", "#8250df"; "vite", "#b08800"; "server", "#1a7f37"; "config", "#0969da" ]

let palette = Css.stylesheet Css.prefix dark light

let install () = Events.installTheme palette

/// Flip dark / light, remembered under the Nacara theme's key.
let toggle () = Events.toggleScheme ()
