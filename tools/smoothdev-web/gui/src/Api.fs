/// The tool's HTTP API, relative to the page (it is served at `/`, or under a site's base in development).
module SmoothDev.Web.Gui.Api

open Fable.Core
open Fable.Core.JsInterop
open SmoothDev.Web.Gui.Model

[<Emit("fetch($0, { cache: 'no-store' }).then(r => { if (!r.ok) throw new Error(r.status + ' ' + r.statusText); return r.json() })")>]
let private getJson (url: string) : JS.Promise<obj> = jsNative

[<Emit("fetch($0, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify($1) }).then(r => r.json())")>]
let private postJson (url: string) (body: obj) : JS.Promise<obj> = jsNative

[<Emit("$0.then($1, $2)")>]
let finish (pending: JS.Promise<'a>) (success: 'a -> unit) (failure: obj -> unit) : unit = jsNative

/// The console's state; the demo file next to the page when no tool answers (the page published in a site).
let console (onData: Console -> unit) (onError: string -> unit) =
  finish (getJson "./api/console") (fun o -> onData (unbox o)) (fun _ ->
    finish (getJson "./demo.json") (fun o -> onData { (unbox<Console> o) with demo = true }) (fun e -> onError (string e)))

type Act =
  | DevStart
  | DevStop
  | Dist
  | Preview
  | Rescan

  member a.word =
    match a with
    | DevStart -> "dev-start"
    | DevStop  -> "dev-stop"
    | Dist     -> "dist"
    | Preview  -> "preview"
    | Rescan   -> "rescan"

let act (a: Act) (site: string) (onDone: unit -> unit) =
  finish (postJson "./api/action" (createObj [ "action" ==> a.word; "site" ==> site ])) (fun _ -> onDone ()) (fun _ -> onDone ())

type LogLines = { name: string; lines: string array }

[<Emit("encodeURIComponent($0)")>]
let private encode (s: string) : string = jsNative

let log (site: string) (name: string) (onData: LogLines -> unit) =
  let url = "./api/log?site=" + encode site + "&name=" + encode name
  finish (getJson url) (fun o -> onData (unbox o)) ignore
