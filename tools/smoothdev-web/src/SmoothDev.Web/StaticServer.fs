/// A small static file server for dist/ (the preview): what a static host would serve, on 127.0.0.1.
module SmoothDev.Web.StaticServer

open System
open System.IO
open System.Net
open System.Threading.Tasks

let contentTypes =
  dict
    [
      ".html" , "text/html; charset=utf-8"
      ".js"   , "text/javascript; charset=utf-8"
      ".mjs"  , "text/javascript; charset=utf-8"
      ".css"  , "text/css; charset=utf-8"
      ".json" , "application/json"
      ".map"  , "application/json"
      ".svg"  , "image/svg+xml"
      ".png"  , "image/png"
      ".jpg"  , "image/jpeg"
      ".jpeg" , "image/jpeg"
      ".gif"  , "image/gif"
      ".webp" , "image/webp"
      ".ico"  , "image/x-icon"
      ".wasm" , "application/wasm"
      ".woff" , "font/woff"
      ".woff2", "font/woff2"
      ".txt"  , "text/plain; charset=utf-8"
      ".xml"  , "application/xml"
    ]

let contentType (path: string) =
  let extension =
    match Path.GetExtension path with
    | null -> ""
    | e -> e.ToLowerInvariant()

  match contentTypes.TryGetValue extension with
  | true, t -> t
  | _ -> "application/octet-stream"

/// The file a request path maps to under root, below the URL base. A folder serves its index.html; an
/// unknown path without an extension falls back to the root index.html (client-side routing). None when
/// nothing matches, the path is outside the base, or it escapes root.
let resolve (root: string) (urlBase: string) (requestPath: string) =
  let root = Path.GetFullPath root
  let path = Uri.UnescapeDataString requestPath

  let relative =
    if path.StartsWith urlBase then
      Some(path.Substring urlBase.Length)
    elif path = urlBase.TrimEnd '/' then
      Some ""
    else
      None

  relative
  |> Option.bind (fun rel ->
    let full = Path.GetFullPath(root </> rel.TrimStart '/')
    let index = root </> "index.html"

    if not (full = root || full.StartsWith(root + string Path.DirectorySeparatorChar)) then
      None
    elif File.Exists full then
      Some full
    elif Directory.Exists full && File.Exists(full </> "index.html") then
      Some(full </> "index.html")
    elif Path.GetExtension(full) = "" && File.Exists index then
      Some index
    else
      None)

let respond (ctx: HttpListenerContext) (root: string) (urlBase: string) =
  task {
    let request = ctx.Request
    let response = ctx.Response

    let path =
      request.Url
      |> Option.ofObj
      |> Option.map _.AbsolutePath
      |> Option.defaultValue "/"

    try
      match resolve root urlBase path with
      | Some file when request.HttpMethod = "GET" || request.HttpMethod = "HEAD" ->
        let bytes = File.ReadAllBytes file
        response.ContentType <- contentType file
        response.Headers["Cache-Control"] <- "no-cache"
        response.ContentLength64 <- int64 bytes.Length

        if request.HttpMethod = "GET" then
          do! response.OutputStream.WriteAsync(bytes, 0, bytes.Length)

        printfn "%s %s 200" request.HttpMethod path
      | _ ->
        response.StatusCode <- 404
        printfn "%s %s 404" request.HttpMethod path
    finally
      response.Close()
  }

/// Serves root on http://127.0.0.1:port under urlBase until the process is stopped.
let run (root: string) (urlBase: string) (port: int) =
  let root = Path.GetFullPath root
  use listener = new HttpListener()
  listener.Prefixes.Add $"http://127.0.0.1:{port}/"
  listener.Start()
  printfn "serving %s at http://127.0.0.1:%d%s" root port urlBase

  while listener.IsListening do
    let ctx = listener.GetContext()
    Task.Run(fun () -> respond ctx root urlBase :> Task) |> ignore

  0
