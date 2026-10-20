/// Custom events the elements send: they bubble out of shadow roots, with the detail as F# values.
module smoothdev.ui.fable.webcomponents.Events

open Fable.Core.JsInterop
open Browser

let emit (element: obj) (name: string) (detail: obj) =
  emitJsStatement (element, name, detail) "$0.dispatchEvent(new CustomEvent($1, { detail: $2, bubbles: true, composed: true }))"

/// The detail of a custom event.
let detail<'T> (e: Types.Event) : 'T = e?detail

/// The theme stylesheet installed once on the page.
let installTheme (css: string) =
  match document.getElementById "sd-theme" with
  | null ->
    let style = document.createElement "style"
    style.id <- "sd-theme"
    style.textContent <- css
    document.head.appendChild style |> ignore
  | existing -> existing.textContent <- css

/// Flip dark and light on <html>, remembered under the Nacara theme's key.
let toggleScheme () =
  let root = document.documentElement
  let next = if root.getAttribute "data-theme" = "light" then "dark" else "light"
  root.setAttribute ("data-theme", next)
  try window.localStorage.setItem ("nacara-theme", next) with _ -> ()
