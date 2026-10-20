/// <sd-console>: the workspace as smoothdev-web sees it. Documentation sites, the Vite apps each one hosts
/// (where, why, how sure), the one-port wiring of `site dev`, and the controls and logs of what runs.
module SmoothDev.Web.Gui.Console

open Fable.Core
open Fable.Core.JsInterop
open Browser
open Browser.Types
open Firelight
open type Firelight.Lit
open SmoothDev.Web.Gui.Model

let private kindLabel kind =
  match kind with
  | "site"   -> "site"
  | "vite"   -> "vite"
  | "server" -> "server"
  | "config" -> "app"
  | k        -> k

let private shortPath (root: string) (path: string) =
  if path.StartsWith(root.TrimEnd('/') + "/") then path.Substring(root.TrimEnd('/').Length + 1) else path

[<AttachMembers>]
type SdConsole() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "data"    , PropertyDeclaration<Console option>(state = true)
      "error"   , PropertyDeclaration<string>(state = true)
      "selected", PropertyDeclaration<string>(state = true)
      "query"   , PropertyDeclaration<string>(state = true)
      "pending" , PropertyDeclaration<string>(state = true)
      "open"    , PropertyDeclaration<string array>(state = true)
    ]

  static member styles =
    css $"""
:host {{ display: grid; grid-template-rows: auto 1fr; height: 100vh; color: var(--sd-ink); background: var(--sd-bg);
  font: 14px/1.5 var(--sd-sans); }}
header {{ display: flex; align-items: center; gap: 14px; padding: 12px 20px; border-bottom: 1px solid var(--sd-line);
  background: linear-gradient(90deg, var(--sd-panel), var(--sd-bg)); }}
.brand {{ display: flex; align-items: center; gap: 10px; font-weight: 650; color: var(--sd-bright); letter-spacing: .2px; }}
.mark {{ width: 22px; height: 22px; transform: rotate(45deg); border-radius: 5px;
  background: linear-gradient(135deg, var(--sd-accent), var(--sd-accent-2)); box-shadow: 0 0 18px var(--sd-glow); }}
.root {{ font: 12px var(--sd-mono); color: var(--sd-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }}
.grow {{ flex: 1; }}
.pill {{ font: 12px var(--sd-mono); padding: 3px 10px; border-radius: 999px; border: 1px solid var(--sd-line); color: var(--sd-muted); }}
.pill.live {{ color: var(--sd-ok); border-color: var(--sd-ok); }}
.pill.demo {{ color: var(--sd-warn); border-color: var(--sd-warn); }}
button {{ font: 13px var(--sd-sans); color: var(--sd-ink); background: var(--sd-panel-2); border: 1px solid var(--sd-line);
  border-radius: 8px; padding: 6px 12px; cursor: pointer; transition: border-color .15s, background .15s; }}
button:hover:not([disabled]) {{ border-color: var(--sd-accent); background: var(--sd-glow); }}
button[disabled] {{ opacity: .45; cursor: not-allowed; }}
button.primary {{ color: #fff; border-color: transparent; background: linear-gradient(135deg, var(--sd-accent), var(--sd-accent-2)); }}
button.icon {{ padding: 4px 9px; }}
main {{ display: grid; grid-template-columns: 300px 1fr; min-height: 0; }}
aside {{ border-right: 1px solid var(--sd-line); overflow: auto; padding: 14px 10px; background: var(--sd-panel); }}
input[type=search] {{ width: 100%%; box-sizing: border-box; padding: 7px 10px; border-radius: 8px; border: 1px solid var(--sd-line);
  background: var(--sd-bg); color: var(--sd-ink); font: 13px var(--sd-sans); margin-bottom: 10px; }}
.group {{ margin: 6px 0 10px; }}
.group > .name {{ font: 600 11px var(--sd-sans); text-transform: uppercase; letter-spacing: .8px; color: var(--sd-muted);
  padding: 4px 8px; cursor: pointer; user-select: none; }}
.row {{ display: grid; grid-template-columns: 52px 1fr; align-items: baseline; gap: 6px; padding: 4px 8px; border-radius: 6px;
  cursor: pointer; font-size: 13px; }}
.row:hover {{ background: var(--sd-panel-2); }}
.row.sel {{ background: var(--sd-glow); outline: 1px solid var(--sd-accent); }}
.row .path {{ font: 12px var(--sd-mono); color: var(--sd-ink); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }}
.kind {{ font: 600 10px var(--sd-mono); text-transform: uppercase; letter-spacing: .5px; padding: 1px 6px; border-radius: 4px;
  text-align: center; border: 1px solid currentColor; }}
.k-site {{ color: var(--sd-site); }} .k-vite {{ color: var(--sd-vite); }} .k-server {{ color: var(--sd-server); }} .k-config {{ color: var(--sd-config); }}
section.content {{ overflow: auto; padding: 22px 28px 60px; }}
.card {{ background: var(--sd-panel); border: 1px solid var(--sd-line); border-radius: 14px; padding: 18px 20px; margin-bottom: 18px; }}
.card h2 {{ margin: 0; font-size: 20px; color: var(--sd-bright); display: flex; align-items: center; gap: 10px; }}
.card h3 {{ margin: 0 0 12px; font-size: 13px; text-transform: uppercase; letter-spacing: .8px; color: var(--sd-muted); }}
.meta {{ display: flex; flex-wrap: wrap; gap: 6px 18px; margin: 8px 0 14px; font: 12px var(--sd-mono); color: var(--sd-muted); }}
.meta b {{ color: var(--sd-ink); font-weight: 500; }}
.actions {{ display: flex; flex-wrap: wrap; gap: 8px; }}
.mounts {{ display: grid; grid-template-columns: repeat(auto-fill, minmax(330px, 1fr)); gap: 12px; }}
.mount {{ border: 1px solid var(--sd-line); border-radius: 12px; padding: 12px 14px; background: var(--sd-panel-2); }}
.mount .url {{ font: 600 14px var(--sd-mono); color: var(--sd-bright); }}
.mount .url a {{ color: inherit; text-decoration: none; }}
.mount .url a:hover {{ color: var(--sd-accent); }}
.mount .app {{ font: 12px var(--sd-mono); color: var(--sd-muted); margin: 2px 0 8px; }}
.chips {{ display: flex; gap: 6px; flex-wrap: wrap; margin-bottom: 8px; }}
.chip {{ font: 11px var(--sd-mono); padding: 1px 8px; border-radius: 999px; border: 1px solid var(--sd-line); color: var(--sd-muted); }}
.c-high {{ color: var(--sd-ok); border-color: var(--sd-ok); }} .c-medium {{ color: var(--sd-warn); border-color: var(--sd-warn); }}
.c-low {{ color: var(--sd-muted); }}
.meter {{ display: inline-flex; gap: 2px; vertical-align: middle; }}
.meter i {{ width: 10px; height: 4px; border-radius: 2px; background: var(--sd-line); }}
.meter i.on {{ background: currentColor; }}
ul.why {{ margin: 0; padding-left: 16px; color: var(--sd-ink); font-size: 12.5px; }}
ul.why li {{ margin: 2px 0; }}
.note {{ font-size: 12.5px; color: var(--sd-warn); margin-top: 6px; }}
.dev {{ font: 12px/1.7 var(--sd-mono); color: var(--sd-muted); background: var(--sd-bg); border: 1px solid var(--sd-line);
  border-radius: 10px; padding: 10px 12px; margin-top: 12px; white-space: pre; overflow: auto; }}
table {{ width: 100%%; border-collapse: collapse; font: 12px var(--sd-mono); }}
td, th {{ text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--sd-line); }}
th {{ color: var(--sd-muted); font-weight: 500; }}
.ok {{ color: var(--sd-ok); }} .bad {{ color: var(--sd-bad); }}
.unmounted li {{ font-size: 13px; margin: 3px 0; }}
.unmounted code {{ font: 12px var(--sd-mono); color: var(--sd-bright); }}
.messages {{ position: fixed; right: 18px; bottom: 18px; display: grid; gap: 6px; max-width: 520px; }}
.toast {{ background: var(--sd-panel); border: 1px solid var(--sd-line); border-left: 3px solid var(--sd-accent); border-radius: 8px;
  padding: 7px 12px; font-size: 12.5px; box-shadow: 0 6px 24px rgba(0,0,0,.25); }}
.toast.fail {{ border-left-color: var(--sd-bad); }} .toast.success {{ border-left-color: var(--sd-ok); }} .toast.warn {{ border-left-color: var(--sd-warn); }}
.empty {{ color: var(--sd-muted); padding: 40px; text-align: center; }}
.banner {{ border: 1px dashed var(--sd-warn); color: var(--sd-warn); border-radius: 10px; padding: 8px 12px; margin-bottom: 16px; font-size: 13px; }}
"""

  member val data: Console option = None with get, set
  member val error = "" with get, set
  member val selected = "" with get, set
  member val query = "" with get, set
  member val pending = "" with get, set
  member val ``open``: string array = [||] with get, set
  member val private timer = 0.0 with get, set

  member this.refresh() =
    Api.console
      (fun c ->
        this.data <- Some c
        this.error <- ""
        if this.selected = "" then
          match c.sites |> Array.tryHead with
          | Some s -> this.selected <- s.project
          | None -> ())
      (fun e -> this.error <- e)

  override this.connectedCallback() =
    base.connectedCallback ()
    Theme.install ()
    this.refresh ()
    this.timer <- window.setInterval ((fun () -> this.refresh ()), 2000)

  override this.disconnectedCallback() =
    base.disconnectedCallback ()
    window.clearInterval this.timer

  member this.run (a: Api.Act) (site: string) =
    this.pending <- a.word
    Api.act a site (fun () ->
      this.pending <- ""
      this.refresh ())

  member this.sidebar (c: Console) =
    let rows = filter this.query c.scan
    let groupView (name: string, rs: ScanRow array) =
      let items =
        [ for r in rs ->
            let site = siteOfRow c.sites r
            let isSel = site |> Option.exists (fun s -> s.project = this.selected)
            let pick (_: Event) = site |> Option.iter (fun s -> this.selected <- s.project)
            let shown = if name = "." then r.path else r.path.Substring(name.Length + 1)
            html $"""<div class={if isSel then "row sel" else "row"} title={r.path + "  " + r.note} @click={pick}>
  <span class={"kind k-" + r.kind}>{kindLabel r.kind}</span><span class="path">{if shown = "" then "." else shown}</span></div>""" ]
      html $"""<div class="group"><div class="name">{name}</div>{items}</div>"""
    let progress =
      if c.scanning then html $"""<span class="pill">scanning · {string c.visited} folders</span>"""
      else html $"""<span class="pill">{string c.scan.Length} hits</span>"""
    html $"""
<aside>
  <input type="search" placeholder="filter: playground vite, site, …" .value={this.query}
         @input={fun (e: Event) -> this.query <- (e.target :?> HTMLInputElement).value}>
  {progress}
  {groups rows |> Array.map groupView}
</aside>"""

  member this.mountCard (s: Site) (m: Mount) =
    let level = confidenceLevel m.confidence
    let meter = [ for i in 1 .. 3 -> html $"""<i class={if i <= level then "on" else ""}></i>""" ]
    let link =
      match s.front with
      | Some front ->
        let origin = front.Substring(0, front.Length - s.baseUrl.Length)
        html $"""<a href={origin + m.url} target="_blank" rel="noopener">{m.url} ↗</a>"""
      | None -> html $"""{m.url}"""
    let owned =
      if m.pageOwned then html $"""<div class="note">The site publishes its own page here: in dev the app answers, the site's page stays in dist.</div>"""
      else nothing
    let port = match m.app.port with Some p -> $"port {p} in its config" | None -> "no port in its config"
    let handWritten =
      if m.app.middleware then html $"""<span class="chip c-medium">hand-written dev routes</span>""" else nothing
    let reasons = m.reasons |> Array.map (fun r -> html $"""<li>{r}</li>""")
    html $"""
<div class="mount">
  <div class="url">{link}</div>
  <div class="app">{shortPath s.repo m.app.dir} · {m.app.name} · fable {m.app.fable} · {port}</div>
  <div class="chips">
    <span class={"chip c-" + m.confidence}><span class="meter">{meter}</span> {m.confidence}</span>
    <span class="chip">{m.kind}</span>
    {handWritten}
  </div>
  <ul class="why">{reasons}</ul>
  {owned}
</div>"""

  member this.siteView (c: Console) (s: Site) =
    let busy = this.pending <> "" || c.busy <> "" || c.demo
    let running = isRunning s
    let frontPort =
      s.running |> Array.tryFind (fun p -> p.name = "site-front") |> Option.map _.port |> Option.defaultValue 0
    let openFront (_: Event) = s.front |> Option.iter (fun u -> window.``open`` (u, "_blank") |> ignore)
    let devButton =
      if running then
        html $"""<button ?disabled={busy} @click={fun (_: Event) -> this.run Api.DevStop s.project}>■ Stop</button>"""
      else
        html $"""<button class="primary" ?disabled={busy} @click={fun (_: Event) -> this.run Api.DevStart s.project}>▶ Dev, one port</button>"""
    let links = s.links |> Array.map (fun l -> $"{l.label} → {l.route}") |> String.concat " · "
    let processes =
      [ for p in s.running ->
          let url = if p.url <> "" then html $"""<a href={p.url} target="_blank" rel="noopener">{p.url}</a>""" else nothing
          html $"""<tr><td>{p.name}</td><td>{string p.pid}</td><td>{if p.port > 0 then string p.port else ""}</td>
<td class={if p.state = "running" then "ok" else "bad"}>{p.state}</td><td>{p.uptime}</td><td>{url}</td></tr>""" ]
    let navbar = if links <> "" then html $"""<span>navbar <b>{links}</b></span>""" else nothing
    let working =
      if this.pending <> "" || c.busy <> "" then
        html $"""<span class="pill live">{if c.busy <> "" then c.busy else this.pending}…</span>"""
      else nothing
    let mounts =
      if s.mounts.Length = 0 then
        html $"""<p class="empty">No Vite app found in this site's repository yet. Add one (any folder with a vite config or a package.json depending on vite): it is mounted under the closest content section, or at its folder name.</p>"""
      else
        html $"""<div class="mounts">{s.mounts |> Array.map (this.mountCard s)}</div>"""
    let table =
      if s.running.Length = 0 then
        html $"""<p class="empty">Nothing runs. ▶ Dev starts the site's watch, each app's Vite under its base, and the front.</p>"""
      else
        html $"""<table><tr><th>name</th><th>pid</th><th>port</th><th>state</th><th>up</th><th>url</th></tr>{processes}</table>"""
    html $"""
<div class="card">
  <h2><span class="kind k-site">site</span>{s.name}</h2>
  <div class="meta">
    <span>base <b>{s.baseUrl}</b></span><span>project <b>{shortPath c.root s.project}</b></span>
    <span>pages <b>{string s.pages}</b></span><span>output <b>{shortPath s.repo s.output}</b></span>
    {navbar}
  </div>
  <div class="actions">
    {devButton}
    <button ?disabled={not running} @click={openFront}>↗ Open</button>
    <button ?disabled={busy} @click={fun (_: Event) -> this.run Api.Dist s.project}>⇪ Dist</button>
    <button ?disabled={busy || not s.distReady} @click={fun (_: Event) -> this.run Api.Preview s.project}>◎ Preview dist</button>
    {working}
  </div>
</div>
<div class="card">
  <h3>Dev wiring</h3>
  <sd-route-map .routes={routes s} front-port={string frontPort} ?running={running}></sd-route-map>
  <div class="dev">{String.concat "\n" s.dev}</div>
</div>
<div class="card">
  <h3>Mounted apps · {string s.mounts.Length}</h3>
  {mounts}
</div>
<div class="card">
  <h3>Processes</h3>
  {table}
  <div style="margin-top: 12px"><sd-log site={s.project} .names={s.running |> Array.map _.name}></sd-log></div>
</div>"""

  override this.render() =
    match this.data with
    | None ->
      let text = if this.error <> "" then $"cannot reach the tool: {this.error}" else "loading…"
      html $"""<header><span class="brand"><span class="mark"></span>smoothdev·web</span></header><p class="empty">{text}</p>"""
    | Some c ->
      let site = c.sites |> Array.tryFind (fun s -> s.project = this.selected)
      let body =
        match site with
        | Some s -> this.siteView c s
        | None ->
          html $"""<div class="card"><h2>No documentation site here</h2><p>smoothdev-web looks for an F# project calling <code>Nacara.run</code>. The Vite apps it found are listed on the left.</p></div>"""
      let unmounted =
        if c.unmounted.Length > 0 then
          let items = c.unmounted |> Array.map (fun u -> html $"""<li><code>{shortPath c.root u.app.dir}</code> {u.reason}</li>""")
          html $"""<div class="card unmounted"><h3>Vite apps not mounted · {string c.unmounted.Length}</h3><ul>{items}</ul></div>"""
        else nothing
      let banner =
        if c.demo then html $"""<div class="banner">Demo data. Run <code>smoothdev-web scan gui</code> in a folder to see your own workspace.</div>"""
        else nothing
      let toasts =
        SmoothDev.Web.Gui.Model.toasts c.messages
        |> Array.map (fun m -> html $"""<div class={"toast " + m.level}>{m.at} {m.text}</div>""")
      html $"""
<header>
  <span class="brand"><span class="mark"></span>smoothdev·web</span>
  <span class="root grow">{c.root}</span>
  <span class={if c.demo then "pill demo" else "pill live"}>{if c.demo then "demo" else "live"}</span>
  <button class="icon" title="rescan" ?disabled={c.demo || c.scanning} @click={fun (_: Event) -> this.run Api.Rescan ""}>⟳</button>
  <button class="icon" title="light / dark" @click={fun (_: Event) -> Theme.toggle ()}>◐</button>
</header>
<main>
  {this.sidebar c}
  <section class="content">{banner}{body}{unmounted}</section>
</main>
<div class="messages">{toasts}</div>"""

Components.register ()
defineElement<SdConsole> "sd-console"
