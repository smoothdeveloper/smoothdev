/// Web GUI: a page served by the tool itself on 127.0.0.1. Server-rendered HTML with plain forms and a
/// meta refresh, so there is no client-side code to build or ship.
module SmoothDev.Web.Gui

open System
open System.Collections.Concurrent
open System.Net
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

let html (s: string) =
  match WebUtility.HtmlEncode s with
  | null -> ""
  | encoded -> encoded

let private urlInText =
  Text.RegularExpressions.Regex(@"https?://[^\s<]+")

/// Makes URLs in an already-encoded message into links.
let linkify (encoded: string) =
  urlInText.Replace(encoded, fun (m: Text.RegularExpressions.Match) -> $"<a href=\"{m.Value}\">{m.Value}</a>")

let messageItem (m: string) =
  let kind, text =
    if m.StartsWith "fail\t" then "error", m.Substring 5
    else "", m
  let cls = if kind = "" then "" else $" class=\"{kind}\""
  $"<li{cls}>{linkify (html text)}</li>"

/// Keeps a log view at the bottom while the reader is already there, and leaves it
/// where it is when they have scrolled up.
let logStick =
  """<script>
(function () {
  var log = document.querySelector("pre.log");
  if (!log) return;
  var stickKey = "smoothdev-log-stick";
  var topKey = "smoothdev-log-top";
  var stick = sessionStorage.getItem(stickKey);
  if (stick === "0") {
    var saved = sessionStorage.getItem(topKey);
    if (saved) log.scrollTop = Number(saved);
  } else {
    log.scrollTop = log.scrollHeight;
  }
  var keep = function () {
    var near = log.scrollHeight - log.scrollTop - log.clientHeight < 24;
    sessionStorage.setItem(stickKey, near ? "1" : "0");
    sessionStorage.setItem(topKey, String(log.scrollTop));
  };
  log.addEventListener("scroll", keep);
  setInterval(keep, 300);
})();
</script>"""

/// Reloads the page, but not while the pointer is down. The window stays where it was. A selection
/// anywhere on the page is found again by its text and the characters beside it, so a ticking clock
/// or a new log line does not move it.
let keepSelection =
  """<script>
(function () {
  var url = document.body.getAttribute("data-refresh");
  var log = document.querySelector("pre.log");
  var key = "smoothdev-sel";
  var down = false;
  if (history.scrollRestoration) history.scrollRestoration = "manual";
  var at = function (root, node, offset) {
    if (!node) return 0;
    if (node.nodeType !== 3) {
      if (!node.childNodes.length) return 0;
      if (offset >= node.childNodes.length) {
        var last = node.childNodes[node.childNodes.length - 1];
        return at(root, last, last.nodeType === 3 ? last.textContent.length : last.childNodes.length);
      }
      return at(root, node.childNodes[offset], 0);
    }
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    var total = 0;
    var current = walker.nextNode();
    while (current) {
      if (current === node) return total + offset;
      total += current.textContent.length;
      current = walker.nextNode();
    }
    return total;
  };
  var textOf = function (root) {
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    var parts = [];
    var node = walker.nextNode();
    while (node) {
      parts.push(node.textContent);
      node = walker.nextNode();
    }
    return parts.join("");
  };
  var point = function (root, index) {
    if (index < 0) return null;
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    var left = index;
    var node = walker.nextNode();
    while (node) {
      var n = node.textContent.length;
      if (left <= n) return { node: node, offset: left };
      left -= n;
      node = walker.nextNode();
    }
    return null;
  };
  var save = function () {
    var sel = window.getSelection();
    var anchor = sel && sel.anchorNode;
    if (!sel || sel.rangeCount === 0 || !anchor || (anchor !== document.body && !document.body.contains(anchor)))
      sessionStorage.removeItem(key);
    else {
      var range = sel.getRangeAt(0);
      var text = textOf(document.body);
      var start = at(document.body, range.startContainer, range.startOffset);
      var end = at(document.body, range.endContainer, range.endOffset);
      if (end < start) { var swap = start; start = end; end = swap; }
      sessionStorage.setItem(key, JSON.stringify({
        before: text.slice(Math.max(0, start - 32), start),
        selected: text.slice(start, end),
        after: text.slice(end, end + 32)
      }));
    }
    sessionStorage.setItem("smoothdev-win-x", String(window.scrollX));
    sessionStorage.setItem("smoothdev-win-y", String(window.scrollY));
  };
  var restore = function () {
    var raw = sessionStorage.getItem(key);
    if (!raw) return;
    var saved;
    try { saved = JSON.parse(raw); } catch (e) { return; }
    var text = textOf(document.body);
    var needle = (saved.before || "") + (saved.selected || "") + (saved.after || "");
    var found = needle === "" ? -1 : text.indexOf(needle);
    if (found < 0 && saved.selected) {
      found = text.indexOf(saved.selected);
      if (found >= 0 && text.indexOf(saved.selected, found + 1) >= 0) return;
      saved.before = "";
    }
    if (found < 0) return;
    var start = found + (saved.before || "").length;
    var end = start + (saved.selected || "").length;
    var a = point(document.body, start);
    var b = point(document.body, end);
    if (!a || !b) return;
    var range = document.createRange();
    range.setStart(a.node, a.offset);
    range.setEnd(b.node, b.offset);
    var logTop = log ? log.scrollTop : 0;
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    if (log) log.scrollTop = logTop;
  };
  var winX = sessionStorage.getItem("smoothdev-win-x");
  var winY = sessionStorage.getItem("smoothdev-win-y");
  restore();
  if (winY !== null) window.scrollTo(Number(winX) || 0, Number(winY) || 0);
  window.addEventListener("pointerdown", function () { down = true; });
  window.addEventListener("pointerup", function () { down = false; save(); });
  window.addEventListener("pointercancel", function () { down = false; });
  if (!url) return;
  setInterval(function () {
    if (down) return;
    save();
    location.replace(url);
  }, 2000);
})();
</script>"""

/// A path the page shows, with a button that copies the exact text. The page refreshes
/// often enough that a mouse selection does not survive.
let copyButton (text: string) =
  $"""<button type="button" class="copy" data-copy="{html text}" title="copy" aria-label="copy"><svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><path fill="currentColor" d="M5 2h2V1h2v1h2a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1zm0 1v9h6V3H5zm1-1h4v1H6V2z"/></svg></button><span class="copied" hidden>copied to clipboard</span>"""

let pathLine (text: string) =
  $"""<span class="path">{html text} {copyButton text}</span>"""

let copyScript =
  """<script>
(function () {
  var key = "smoothdev-copied";
  var show = function (b) {
    var note = b.nextElementSibling;
    if (note) note.hidden = false;
  };
  var saved = sessionStorage.getItem(key);
  if (saved) {
    var cut = saved.lastIndexOf("\n");
    var text = saved.slice(0, cut);
    var at = Number(saved.slice(cut + 1));
    if (Date.now() - at < 4000) {
      document.querySelectorAll("button.copy").forEach(function (b) {
        if (b.getAttribute("data-copy") === text) show(b);
      });
    }
  }
  var write = function (text) {
    var area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.left = "-9999px";
    document.body.appendChild(area);
    area.select();
    var ok = false;
    try { ok = document.execCommand("copy"); } catch (e) { ok = false; }
    area.remove();
    if (ok) return Promise.resolve();
    if (navigator.clipboard && navigator.clipboard.writeText)
      return navigator.clipboard.writeText(text);
    return Promise.reject();
  };
  document.addEventListener("click", function (ev) {
    var b = ev.target.closest && ev.target.closest("button.copy");
    if (!b) return;
    ev.preventDefault();
    var text = b.getAttribute("data-copy");
    write(text).then(function () {
      sessionStorage.setItem(key, text + "\n" + Date.now());
      show(b);
    });
  });
})();
</script>"""

/// Opens the server in its own tab. The same address focuses that tab again.
let externalLink (url: string) =
  let name =
    "sd-"
    + (url
       |> Seq.map (fun c -> if Char.IsLetterOrDigit c then c else '-')
       |> Seq.truncate 60
       |> Seq.toArray
       |> System.String)

  $"""<a href="{html url}" target="{html name}" onclick="var w=window.open(this.href,this.target);if(w)w.focus();return false;">{html url}</a>"""

let stateClass state =
  match state with
  | Running       -> "running"
  | Orphaned      -> "orphaned"
  | Stopped       -> "stopped"
  | Unavailable _ -> "na"

let button (here: string) (action: ActionTarget) label =
  let query =
    match here.IndexOf '?' with
    | -1 -> ""
    | i -> here.Substring i

  $"""<form method="post" action="/action/{action.route}{query}"><button>{html label}</button></form>"""

let controls (here: string) (r: Row) =
  match Component.parse r.name, r.state with
  | Some Component.Server, Unavailable _
  | Some Component.Fable, Unavailable _ -> ""
  | Some (Component.Server | Component.Fable | Component.Vite), (Running | Orphaned) -> button here ActionTarget.DevStop "stop dev"
  | Some (Component.Server | Component.Fable | Component.Vite), Stopped -> button here ActionTarget.DevStart "start dev"
  | Some Component.Prod, (Running | Orphaned) -> button here ActionTarget.ProdStop "stop" + button here ActionTarget.OpenProd "open"
  | Some Component.Prod, Stopped -> button here ActionTarget.ProdStart "start"
  | Some Component.Preview, (Running | Orphaned) -> button here ActionTarget.PreviewStop "stop" + button here ActionTarget.OpenDist "open"
  | Some Component.Preview, _ -> button here ActionTarget.PreviewStart "start"
  | _ -> ""

let page (cfg: Config) (rows: Row array) (selected: string option) (messages: string array) (busy: string) (here: string) =
  let withLogs =
    rows |> Array.filter (fun r -> r.entry |> Option.exists (fun e -> e.log <> ""))

  let shown =
    selected
    |> Option.bind (fun n -> withLogs |> Array.tryFind (fun r -> r.name = n))
    |> Option.orElse (Array.tryHead withLogs)

  let rowHtml (r: Row) =
    let cells =
      match r.entry with
      | Some e ->
        let link =
          if e.url = "" then
            ""
          else
            $"""{externalLink e.url}"""

        let port = if e.port > 0 then string e.port else ""
        $"<td>{e.pid}</td><td>{port}</td><td>{link}</td><td>{Actions.uptime e}</td>"
      | None -> "<td></td><td></td><td></td><td></td>"

    $"""<tr><th>{html r.name}</th><td class="{stateClass r.state}">{html (Actions.stateText r.state)}</td>{cells}<td class="role">{html r.role}</td><td>{controls here r}</td></tr>"""

  let tabs =
    withLogs
    |> Array.map (fun r ->
      let cls =
        if Some r.name = (shown |> Option.map _.name) then
          " class=\"on\""
        else
          ""

      let href =
        if here.Contains '?' then
          $"{here}&log={Uri.EscapeDataString r.name}"
        else
          $"{here}?log={r.name}"

      $"""<a{cls} href="{href}">{r.name}</a>""")
    |> String.concat " "

  let log, logPath =
    match shown |> Option.bind _.entry with
    | Some e when e.log <> "" ->
      Runner.tailRaw e.log 60 |> Array.map Runner.ansiHtml |> String.concat "\n",
      $"""<div class="src">{pathLine e.log}</div>"""
    | _ -> "no component running", ""

  let busyHtml =
    if busy = "" then
      ""
    else
      $"""<span class="busy">busy: {html busy}</span>"""

  let refresh =
    match selected with
    | Some n when here.Contains '?' -> $"{here}&log={Uri.EscapeDataString n}"
    | Some n -> $"{here}?log={Uri.EscapeDataString n}"
    | None -> here

  let back =
    if here = "/" then
      ""
    else
      """<p><a href="/">back to scan</a></p>"""

  $"""<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<title>smoothdev-web: {html cfg.name}</title>
<style>
  body {{ font: 14px/1.4 ui-sans-serif, system-ui, sans-serif; margin: 1.5rem; color: #1d2330; background: #f6f7f9; }}
  h1 {{ font-size: 1.2rem; margin: 0 0 .2rem; }} .src {{ color: #6b7280; margin-bottom: 1rem; }}
  table {{ border-collapse: collapse; background: #fff; box-shadow: 0 1px 2px #0002; }}
  th, td {{ padding: .35rem .7rem; border-bottom: 1px solid #e5e7eb; text-align: left; vertical-align: middle; }}
  td.running {{ color: #15803d; font-weight: 600; }} td.orphaned {{ color: #b45309; font-weight: 600; }}
  td.stopped, td.na, td.role {{ color: #6b7280; }}
  form {{ display: inline; }} button {{ margin-right: .3rem; padding: .15rem .6rem; cursor: pointer; }}
  .bar {{ margin: 1rem 0; }} .busy {{ color: #b45309; margin-left: 1rem; }}
  pre.log {{ background: #11151c; color: #d1d5db; padding: .8rem; max-height: 26rem; overflow: auto; font-size: 12px; white-space: pre-wrap; }}
  .tabs a {{ margin-right: .6rem; }} .tabs a.on {{ font-weight: 700; }} ul {{ color: #374151; padding-left: 0; list-style: none; }}
  button.copy {{ border: 0; background: transparent; padding: 0 .15rem; cursor: pointer; vertical-align: middle; color: #6b7280; }}
  button.copy:hover {{ color: #1d2330; }}
  .copied {{ color: #15803d; margin-left: .25rem; font-size: 12px; }}
  .path {{ font-family: ui-monospace, monospace; font-size: 12px; }}
  li.error {{ background: #fef2f2; border-left: 3px solid #dc2626; padding: .35rem .6rem; margin: .4rem 0; }}
  li.error a {{ color: #b91c1c; }}
</style></head><body data-refresh="{html refresh}">
<h1>smoothdev-web: {html cfg.name}</h1>
{back}
<div class="src">{pathLine cfg.root}, {html (cfg.file |> Option.map Config.nameOf |> Option.defaultValue "detected config")}</div>
<table><tr><th>component</th><th>state</th><th>pid</th><th>port</th><th>url</th><th>up</th><th>role</th><th></th></tr>
{rows |> Array.map rowHtml |> String.concat "\n"}
</table>
<div class="bar">{button here ActionTarget.Dist "build dist"}{button here ActionTarget.Build "compile"}{button here ActionTarget.Open "open best"}{button here ActionTarget.Stop "stop all"}{busyHtml}</div>
<div class="tabs">log: {tabs}</div>
{logPath}
<pre class="log">{log}</pre>
{logStick}
{keepSelection}
{copyScript}
<ul>{messages |> Array.map messageItem |> String.concat ""}</ul>
</body></html>
"""

let statusJson (rows: Row array) =
  JsonSerializer.Serialize(
    rows
    |> Array.map (fun r ->
      {|
        name = r.name
        state =
          match r.state with
          | Running -> "running"
          | Orphaned -> "orphaned"
          | Stopped -> "stopped"
          | Unavailable _ -> "unavailable"
        reason =
          match r.state with
          | Unavailable reason -> reason
          | _ -> ""
        pid = r.entry |> Option.map _.pid |> Option.defaultValue 0
        port = r.entry |> Option.map _.port |> Option.defaultValue 0
        url = r.entry |> Option.map _.url |> Option.defaultValue ""
      |})
  )

/// Runs a posted action. The path segment is the route; anything else is reported and ignored.
let dispatch (busy: string ref) push ctx raw =
  match ActionTarget.parse raw with
  | None -> push Fail $"unknown action {raw}"
  | Some action ->
    if busy.Value = "" then
      busy.Value <- action.route

      Task.Run(fun () ->
        task {
          try
            let! _ = Actions.perform ctx action
            ()
          with e ->
            push Fail e.Message

          busy.Value <- ""
        }
        :> Task)
      |> ignore
    else
      push Info $"busy ({busy.Value}): {action.route} ignored"

let write (response: HttpListenerResponse) status contentType (body: string) =
  let bytes = Encoding.UTF8.GetBytes body
  response.StatusCode <- status
  response.ContentType <- contentType
  response.ContentLength64 <- int64 bytes.Length
  response.OutputStream.Write(bytes, 0, bytes.Length)
  response.Close()

/// Serves the GUI until Ctrl+C or SIGTERM, then stops what it started.
let run (cfg: Config) (preferredPort: int option) (browser: bool) =
  let messages = ConcurrentQueue<string>()

  let push level (line: string) =
    let kind = if level = Fail then "fail\t" else ""
    messages.Enqueue $"{kind}{DateTime.Now:``HH:mm:ss``} {line}"

    while messages.Count > 12 do
      messages.TryDequeue() |> ignore

  let ctx: Actions.Context =
    {
      config = cfg
      say =
        fun level msg ->
          printfn "%s" msg

          if level <> Detail then
            push level msg
      echo = fun _ _ -> ()
      browser = true
    }

  // ports held by the app, except a GUI entry: `gui --detach` records this process before it starts
  let reserved =
    State.live cfg.root
    |> Array.filter (fun e -> Component.parse e.name <> Some Component.Gui && e.port > 0)
    |> Array.map _.port

  let port = Ports.pick reserved (preferredPort |> Option.defaultValue cfg.guiPort)

  let url = $"http://127.0.0.1:{port}/"
  use listener = new HttpListener()
  listener.Prefixes.Add url
  listener.Start()

  // `gui --detach` recorded this process (its group and log) before it started: keep that
  let recorded =
    State.read cfg.root
    |> Array.tryFind (fun e -> Component.parse e.name = Some Component.Gui && e.pid = Environment.ProcessId)

  State.add
    cfg.root
    {
      name      = Component.Gui.name
      pid       = Environment.ProcessId
      pgid      = recorded |> Option.map _.pgid |> Option.defaultValue 0
      port      = port
      url       = url
      log       = recorded |> Option.map _.log |> Option.defaultValue ""
      command   = Environment.GetCommandLineArgs()
      startedAt = DateTimeOffset.Now
      owner     = Environment.ProcessId
    }

  printfn "smoothdev-web GUI for %s: %s  (Ctrl+C stops it and what it started)" cfg.name url

  if browser then
    (Actions.openUrl ctx url)
      .GetAwaiter()
      .GetResult()
    |> ignore

  use stopping = new CancellationTokenSource()
  use _reaper = new Timer((fun _ -> Actions.reapOwned cfg), null, 500, 500)

  let stop (c: PosixSignalContext) =
    c.Cancel <- true
    stopping.Cancel()

  use _int = PosixSignalRegistration.Create(PosixSignal.SIGINT, stop)
  use _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, stop)
  let busy = ref ""

  let handle (c: HttpListenerContext) =
    let request = c.Request

    let path =
      request.Url
      |> Option.ofObj
      |> Option.map _.AbsolutePath
      |> Option.defaultValue "/"

    try
      match request.HttpMethod, path with
      | "GET", "/" ->
        let selected = request.QueryString["log"] |> Option.ofObj

        page
          cfg
          (Actions.rows cfg)
          selected
          (messages.ToArray())
          busy.Value
          "/"
        |> write c.Response 200 "text/html; charset=utf-8"

      | "GET", "/api/status" -> write c.Response 200 "application/json" (statusJson (Actions.rows cfg))
      | "POST", p when p.StartsWith "/action/" ->
        dispatch busy push ctx (p.Substring "/action/".Length)
        c.Response.Redirect "/"
        c.Response.StatusCode <- 303
        c.Response.Close()
      | _ -> write c.Response 404 "text/plain" "not found"
    with e ->
      eprintfn "gui: %s" e.Message

  try
    try
      while not stopping.IsCancellationRequested do
        let next = listener.GetContextAsync()
        next.Wait stopping.Token
        handle next.Result
    with :? OperationCanceledException ->
      ()
  finally
    printfn "stopping what the GUI started..."

    let quiet: Actions.Context =
      { ctx with
          say = fun _ msg -> printfn "%s" msg
      }

    (Actions.stopOwned quiet).GetAwaiter().GetResult() |> ignore
    State.remove cfg.root Component.Gui.name

  0

/// `scan gui`: cached hits show at once. The walk refreshes them. A folder link folds that group.
let scan root preferredPort =
  let view = Config.ScanView()
  let seeded = ref false
  let finished = ref false

  match Config.scanFromCache root with
  | Some(rows, _, _) ->
    view.Load rows
    seeded.Value <- true
  | None -> ()

  let opened = Collections.Generic.HashSet<string>()
  let closed = Collections.Generic.HashSet<string>()
  let visited = ref 0
  let current = ref root
  use stopping = new CancellationTokenSource()

  Task.Run(fun () ->
      let n =
        Config.scanVisit
          root
          stopping.Token
          (fun n path ->
            visited.Value <- n
            current.Value <- path)
          view.Add

      if not stopping.IsCancellationRequested then
        view.Commit()
        Config.saveScanCache root n (view.Rows())

      visited.Value <- n
      finished.Value <- true)
    |> ignore

  let port = Ports.pick [||] (preferredPort |> Option.defaultValue 5399)
  let url = $"http://127.0.0.1:{port}/"
  use listener = new HttpListener()
  listener.Prefixes.Add url
  listener.Start()
  printfn "smoothdev-web scan: %s  (Ctrl+C stops)" url

  let stop (c: PosixSignalContext) =
    c.Cancel <- true
    stopping.Cancel()

  use _int = PosixSignalRegistration.Create(PosixSignal.SIGINT, stop)
  use _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, stop)

  let treePage (selected: string) =
    let rows = view.Rows()
    let lines = Config.scanLines rows (Seq.toArray opened) (Seq.toArray closed)
    let live = Actions.scanLive root rows
    let logs = live |> Array.filter (fun e -> e.log <> "")

    let progress =
      if finished.Value then
        $"done, {visited.Value} folders, {view.Rows().Length} apps, {live.Length} running"
      elif seeded.Value && visited.Value = 0 then
        "cached, refreshing"
      else
        $"scanning, {visited.Value} folders, {view.Rows().Length} apps, {current.Value}"

    let body =
      lines
      |> Array.map (fun line ->
        let running = Actions.scanRunning line.path live
        let bucket = if line.apps >= 8 then 8 elif line.apps >= 4 then 4 elif line.apps >= 2 then 2 else 1
        let state = if running > 0 then "run" else "idle"

        let mark =
          match line.fold with
          | Some true -> "v"
          | Some false -> ">"
          | None -> ""

        let pad = line.depth * 16
        let keep = if selected = "" then "" else $"&log={Uri.EscapeDataString selected}"

        let label =
          match line.fold with
          | Some _ -> $"<a href=\"/fold?path={Uri.EscapeDataString line.path}{keep}\">{html mark} {html line.label}</a>"
          | None ->
            let q = $"path={Uri.EscapeDataString line.path}&kind={Uri.EscapeDataString line.kind}"
            $"<a href=\"/app?{q}\">{html line.label}</a>"

        let note =
          if running > 0 then
            if line.note = "" then $"{running} running" else $"{line.note}, {running} running"
          else
            line.note

        $"<tr class=\"n{bucket} {state}\"><td>{html line.kind}</td><td style=\"padding-left:{pad}px\">{label}</td><td>{html note}</td></tr>")
      |> String.concat "\n"

    let shown =
      logs
      |> Array.tryFind (fun e -> $"{e.app}/{e.name}" = selected)
      |> Option.orElse (Array.tryHead logs)

    let tabs =
      logs
      |> Array.map (fun e ->
        let key = $"{e.app}/{e.name}"
        let cls = if shown |> Option.exists (fun s -> s.app = e.app && s.name = e.name) then " class=\"on\"" else ""
        $"""<a{cls} href="/?log={Uri.EscapeDataString key}">{html e.app} / {html e.name}</a>""")
      |> String.concat " "

    let log, logPath =
      match shown with
      | Some e when e.log <> "" ->
        Runner.tailRaw e.log 80 |> Array.map Runner.ansiHtml |> String.concat "\n",
        $"""<p class="src">{pathLine e.log}</p>"""
      | _ -> "nothing running", ""

    let refresh = if selected = "" then "/" else $"/?log={Uri.EscapeDataString selected}"

    let doc =
      """<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<title>smoothdev-web scan</title>
<style>
  body { font: 14px/1.4 ui-sans-serif, system-ui, sans-serif; margin: 1.5rem; background: #f6f7f9; color: #1d2330; }
  table { border-collapse: collapse; background: #fff; width: 100%; }
  th, td { padding: .3rem .7rem; border-bottom: 1px solid #e5e7eb; text-align: left; }
  a { color: inherit; text-decoration: none; }
  .progress { color: #b45309; }
  tr.n1 td:first-child { box-shadow: inset 4px 0 #e2e8f0; }
  tr.n2 td:first-child { box-shadow: inset 4px 0 #93c5fd; }
  tr.n4 td:first-child { box-shadow: inset 4px 0 #2563eb; }
  tr.n8 td:first-child { box-shadow: inset 4px 0 #1e3a8a; }
  tr.idle td { color: #64748b; }
  tr.run td { color: #15803d; font-weight: 600; }
  .tabs a { margin-right: .8rem; color: #374151; } .tabs a.on { font-weight: 700; }
  .src { color: #6b7280; }
  button.copy { border: 0; background: transparent; padding: 0 .15rem; cursor: pointer; vertical-align: middle; color: #6b7280; }
  .copied { color: #15803d; margin-left: .25rem; font-size: 12px; }
  .path { font-family: ui-monospace, monospace; font-size: 12px; }
  pre { background: #11151c; color: #d1d5db; padding: .8rem; max-height: 18rem; overflow: auto; font-size: 12px; white-space: pre-wrap; }
</style></head><body data-refresh="__REFRESH__">
<h1>smoothdev-web scan</h1>
<p class="src">__ROOT__</p>
<p class="progress">__PROGRESS__</p>
<table><tr><th>kind</th><th>path</th><th>note</th></tr>
__BODY__
</table>
<div class="tabs">logs: __TABS__</div>
__LOGPATH__
<pre class="log">__LOG__</pre>
</body></html>"""

    doc
      .Replace("__REFRESH__" , refresh)
      .Replace("__ROOT__"    , pathLine root)
      .Replace("__PROGRESS__", html progress)
      .Replace("__BODY__"    , body)
      .Replace("__TABS__"    , tabs)
      .Replace("__LOGPATH__", logPath)
      .Replace("__LOG__"     , log)
    + logStick
    + keepSelection
    + copyScript

  let messages = ConcurrentQueue<string>()
  let busy = ref ""
  let managed = Collections.Generic.Dictionary<string, Config>()

  let push level line =
    let kind = if level = Fail then "fail\t" else ""
    messages.Enqueue $"{kind}{DateTime.Now:``HH:mm:ss``} {line}"

    while messages.Count > 12 do
      messages.TryDequeue() |> ignore

  let appFrom (request: HttpListenerRequest) =
    let kind = request.QueryString.Get "kind" |> Option.ofObj |> Option.defaultValue ""
    let rel = request.QueryString.Get "path" |> Option.ofObj |> Option.defaultValue ""

    match Config.scanAppDir root kind rel with
    | None -> Error "not an app"
    | Some dir ->
      match Config.load dir with
      | Error msg -> Error msg
      | Ok cfg ->
        managed[cfg.root] <- cfg
        Ok(cfg, rel, kind)

  let hereOf (rel: string) (kind: string) =
    $"/app?path={Uri.EscapeDataString rel}&kind={Uri.EscapeDataString kind}"

  use _reaper =
    new Timer(
      (fun _ ->
        for cfg in managed.Values |> Seq.toArray do
          Actions.reapOwned cfg),
      null,
      500,
      500
    )

  try
    try
      while not stopping.IsCancellationRequested do
        let next = listener.GetContextAsync()
        next.Wait stopping.Token
        let ctx = next.Result

        try
          let path =
            match ctx.Request.Url with
            | null -> "/"
            | url -> url.AbsolutePath

          if path = "/fold" then
            match ctx.Request.QueryString.Get "path" with
            | null -> ()
            | folder ->
              let openNow =
                Config.scanLines (view.Rows()) (Seq.toArray opened) (Seq.toArray closed)
                |> Array.exists (fun line -> line.path = folder && line.fold = Some true)

              if openNow then
                opened.Remove folder |> ignore
                closed.Add folder |> ignore
              else
                closed.Remove folder |> ignore
                opened.Add folder |> ignore

            let back =
              match ctx.Request.QueryString.Get "log" with
              | null -> "/"
              | log -> $"/?log={Uri.EscapeDataString log}"

            ctx.Response.Redirect back
            ctx.Response.Close()
          elif path = "/app" then
            match appFrom ctx.Request with
            | Error msg -> write ctx.Response 200 "text/html; charset=utf-8" $"<p>{html msg}</p><p><a href=\"/\">back</a></p>"
            | Ok(cfg, rel, kind) ->
              let selected = ctx.Request.QueryString["log"] |> Option.ofObj
              let here = hereOf rel kind

              write
                ctx.Response
                200
                "text/html; charset=utf-8"
                (page cfg (Actions.rows cfg) selected (messages.ToArray()) busy.Value here)
          elif path.StartsWith "/action/" then
            match appFrom ctx.Request with
            | Error msg -> write ctx.Response 200 "text/html; charset=utf-8" $"<p>{html msg}</p>"
            | Ok(cfg, rel, kind) ->
              let actx: Actions.Context =
                { config  = cfg
                  say     = fun level msg -> push level msg
                  echo    = fun _ _ -> ()
                  browser = true }

              dispatch busy push actx (path.Substring "/action/".Length)
              ctx.Response.Redirect(hereOf rel kind)
              ctx.Response.StatusCode <- 303
              ctx.Response.Close()
          else
            write ctx.Response 200 "text/html; charset=utf-8" (treePage (ctx.Request.QueryString.Get "log" |> Option.ofObj |> Option.defaultValue ""))
        with e ->
          eprintfn "scan gui: %s" e.Message
    with :? OperationCanceledException ->
      ()
  finally
    stopping.Cancel()

    for cfg in managed.Values |> Seq.toArray do
      let quiet: Actions.Context =
        { config  = cfg
          say     = fun _ msg -> printfn "%s" msg
          echo    = fun _ _ -> ()
          browser = false }

      (Actions.stopOwned quiet).GetAwaiter().GetResult() |> ignore

  0

/// Starts the GUI in the background as a tracked process group, then opens it.
let detach (ctx: Actions.Context) (preferredPort: int option) =
  task {
    let cfg = ctx.config

    match State.find cfg.root Component.Gui.name with
    | Some e ->
      ctx.say Info $"the GUI is already running on {e.url}"
      return! Actions.openUrl ctx e.url
    | None ->
      let port =
        Ports.pick 
          (Actions.reserved cfg)
          (preferredPort |> Option.defaultValue cfg.guiPort)

      let url = $"http://127.0.0.1:{port}/"

      let argv =
        [|
          yield! Runner.selfArgv ()
          "--dir"
          cfg.root
          "--no-browser"
          "gui"
          "--port"
          string port
        |]

      match Runner.startComponent cfg.root Component.Gui argv [||] cfg.root port url with
      | Error msg -> return Actions.fail ctx msg
      | Ok e ->
        let! ready = Runner.waitReady e (TimeSpan.FromSeconds 20.)

        match ready with
        | Ok () ->
          ctx.say Success $"GUI running in the background: {url} (pid {e.pid}; `smoothdev-web stop` ends it)"
          return! Actions.openUrl ctx url
        | Error msg -> return Actions.fail ctx msg
  }
