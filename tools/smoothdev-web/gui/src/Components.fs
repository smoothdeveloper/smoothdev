/// Small components of the console: the front proxy's routing drawn as a map, and a log viewer.
module smoothdev.web.gui.Components

open Fable.Core
open Fable.Core.JsInterop
open Browser
open Browser.Types
open Firelight
open type Firelight.Lit
open smoothdev.web.gui.Model
open smoothdev.ui.fable.webcomponents

/// <sd-route-map .routes=… front-port=… running>: one port in front, a line to each upstream.
[<AttachMembers>]
type RouteMap() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "routes"   , PropertyDeclaration<RouteLine array>(attribute = !^false)
      "frontPort", PropertyDeclaration<int>(attribute = !^"front-port")
      "running"  , PropertyDeclaration<bool>(reflect = true)
    ]

  static member styles =
    css $"""
:host {{ display: block; }}
svg {{ width: 100%%; height: auto; font-family: var(--sd-mono); }}
.box {{ fill: var(--sd-panel-2); stroke: var(--sd-line); stroke-width: 1; }}
.front {{ fill: var(--sd-glow); stroke: var(--sd-accent); }}
.app .box {{ stroke: var(--sd-vite); }}
.site .box {{ stroke: var(--sd-site); }}
.label {{ fill: var(--sd-bright); font-size: 12px; }}
.sub {{ fill: var(--sd-muted); font-size: 11px; }}
.wire {{ fill: none; stroke: var(--sd-line); stroke-width: 1.5; }}
:host([running]) .wire {{ stroke: var(--sd-accent); stroke-dasharray: 6 5; animation: flow 1.2s linear infinite; }}
@keyframes flow {{ to {{ stroke-dashoffset: -22; }} }}
.dot {{ fill: var(--sd-muted); }}
.dot.on {{ fill: var(--sd-ok); }}
"""

  member val routes: RouteLine array = [||] with get, set
  member val frontPort = 0 with get, set
  member val running = false with get, set

  override this.render() =
    let rowH = 46
    let height = max 80 (this.routes.Length * rowH + 16)
    let frontY = height / 2 - 20
    let port p = if p > 0 then $":{p}" else "not running"
    let rows =
      [ for i, r in Array.indexed this.routes ->
          let y = 8 + i * rowH
          let midY = y + 19
          let cls = if r.app then "app" else "site"
          svg $"""
<path class="wire" d={$"M 190 {frontY + 20} C 260 {frontY + 20}, 260 {midY}, 330 {midY}"}></path>
<g class={cls}>
  <rect class="box" x="330" y={string y} width="330" height="38" rx="8"></rect>
  <circle class={if r.port > 0 then "dot on" else "dot"} cx="346" cy={string midY} r="4"></circle>
  <text class="label" x="358" y={string (y + 16)}>{r.prefix}</text>
  <text class="sub" x="358" y={string (y + 31)}>{r.target}  {port r.port}</text>
</g>""" ]
    let front = if this.frontPort > 0 then $"127.0.0.1:{this.frontPort}" else "one port"
    html $"""
<svg viewBox={$"0 0 670 {height}"} role="img" aria-label="front proxy routes">
  <rect class="box front" x="8" y={string frontY} width="182" height="40" rx="10"></rect>
  <text class="label" x="22" y={string (frontY + 17)}>front</text>
  <text class="sub" x="22" y={string (frontY + 32)}>{front}</text>
  {rows}
</svg>"""

/// <sd-log site=… .names=…>: the last lines of one process's log, a tab per process, refreshed.
[<AttachMembers>]
type LogView() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "site"    , PropertyDeclaration<string>()
      "names"   , PropertyDeclaration<string array>(attribute = !^false)
      "selected", PropertyDeclaration<string>(state = true)
      "lines"   , PropertyDeclaration<string array>(state = true)
    ]

  static member styles =
    css $"""
:host {{ display: block; }}
sd-tabs {{ margin-bottom: 8px; }}
pre {{ margin: 0; max-height: 260px; overflow: auto; padding: 12px; background: var(--sd-bg); border: 1px solid var(--sd-line);
  border-radius: 10px; font: 12px/1.5 var(--sd-mono); color: var(--sd-ink); white-space: pre-wrap; }}
.empty {{ color: var(--sd-muted); font: 13px var(--sd-sans); }}
"""

  member val site = "" with get, set
  member val names: string array = [||] with get, set
  member val selected = "" with get, set
  member val lines: string array = [||] with get, set
  member val private timer = 0.0 with get, set

  member this.current =
    if Array.contains this.selected this.names then this.selected
    else this.names |> Array.tryHead |> Option.defaultValue ""

  member this.refresh() =
    let name = this.current
    if name <> "" then
      Api.log this.site name (fun l -> if l.name = this.current then this.lines <- l.lines)

  override this.connectedCallback() =
    base.connectedCallback ()
    this.timer <- window.setInterval ((fun () -> this.refresh ()), 1500)
    this.refresh ()

  override this.disconnectedCallback() =
    base.disconnectedCallback ()
    window.clearInterval this.timer

  override this.render() =
    if this.names.Length = 0 then
      html $"""<p class="empty">Nothing runs for this site yet.</p>"""
    else
      let pick (e: Event) =
        this.selected <- Events.detail<string> e
        this.lines <- [||]
        this.refresh ()
      html $"""<sd-tabs .tabs={this.names} selected={this.current} @sd-select={pick}></sd-tabs><pre>{String.concat "\n" this.lines}</pre>"""

let register () =
  Elements.register ()
  defineElement<RouteMap> "sd-route-map"
  defineElement<LogView> "sd-log"
