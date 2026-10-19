/// One port in front of a site's dev servers: each request goes to the upstream whose path prefix is the
/// longest match (a Vite app under its base, the site's own dev server for everything else). It works on
/// raw TCP after the request head, so WebSocket upgrades (Vite HMR, the site's reload) pass through as
/// they are. Requests that are not upgrades get `Connection: close`, so one browser connection never
/// carries requests meant for two upstreams.
module SmoothDev.Web.FrontProxy

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks

type Route =
  { prefix : string   // "/acme/playground/"; "/" is the fallback
    host   : string
    port   : int }

/// "/acme/playground/=http://127.0.0.1:5174"
let parseRoute (text: string) =
  match text.IndexOf '=' with
  | -1 -> Error $"route without '=': {text}"
  | i ->
    let prefix = text.Substring(0, i)
    match (try Some(Uri(text.Substring(i + 1), UriKind.Absolute)) with _ -> None) with
    | Some uri -> Ok { prefix = prefix; host = uri.Host; port = uri.Port }
    | None -> Error $"not a URL: {text.Substring(i + 1)}"

/// The upstream for a request path: the longest prefix that matches, a prefix without its trailing
/// slash counting too ("/acme/playground" goes to the app).
let pick (routes: Route array) (path: string) =
  let path = match path.IndexOf '?' with -1 -> path | i -> path.Substring(0, i)
  routes
  |> Array.filter (fun r -> path.StartsWith(r.prefix, StringComparison.Ordinal) || path = r.prefix.TrimEnd('/'))
  |> Array.sortByDescending _.prefix.Length
  |> Array.tryHead

type Decision =
  | Health
  | Forward of Route
  | Redirect of location: string
  | Missing

/// "/" redirects to the site's base; "/base/app" (no slash) to "/base/app/" so relative URLs resolve.
let decide (routes: Route array) (siteBase: string) (path: string) =
  let bare = match path.IndexOf '?' with -1 -> path | i -> path.Substring(0, i)
  if bare = "/__front" then
    Health
  elif bare = "/" && siteBase <> "/" then
    Redirect siteBase
  else
    match pick routes path with
    | Some r when bare = r.prefix.TrimEnd('/') && r.prefix <> "/" -> Redirect(r.prefix + path.Substring bare.Length)
    | Some r -> Forward r
    | None -> Missing

/// The request head with `Connection: close` unless it asks for an upgrade.
let rewriteHead (head: string) =
  let lines = head.Split("\r\n")
  let upgrade =
    lines |> Array.exists (fun l -> l.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase))
  if upgrade then
    head
  else
    let kept =
      lines
      |> Array.filter (fun l ->
        not (l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
        && not (l.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase))
        && not (l.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase)))
      |> Array.filter (fun l -> l <> "")
    String.Join("\r\n", Array.append kept [| "Connection: close"; ""; "" |])

let private headEnd (buffer: byte array) count =
  let mutable found = -1
  let mutable i = 3
  while found < 0 && i < count do
    if buffer[i - 3] = 13uy && buffer[i - 2] = 10uy && buffer[i - 1] = 13uy && buffer[i] = 10uy then
      found <- i + 1
    i <- i + 1
  found

let private respond (stream: Stream) (status: string) (headers: string list) (body: string) =
  task {
    let bytes = Encoding.UTF8.GetBytes body
    let head =
      [ $"HTTP/1.1 {status}"
        yield! headers
        "Content-Type: text/html; charset=utf-8"
        $"Content-Length: {bytes.Length}"
        "Connection: close"
        ""; "" ]
      |> String.concat "\r\n"
    do! stream.WriteAsync(Encoding.ASCII.GetBytes head)
    do! stream.WriteAsync bytes
  }

let private handle (routes: Route array) (siteBase: string) (log: string -> unit) (client: TcpClient) =
  task {
    use client = client
    let stream = client.GetStream()
    let buffer = Array.zeroCreate<byte> 65536
    let mutable count = 0
    let mutable stop = -1
    let mutable closed = false
    while stop < 0 && not closed && count < buffer.Length do
      let! n = stream.ReadAsync(buffer, count, buffer.Length - count)
      if n = 0 then closed <- true
      count <- count + n
      stop <- headEnd buffer count
    if stop > 0 then
      let head = Encoding.Latin1.GetString(buffer, 0, stop)
      let requestLine = head.Substring(0, max 0 (head.IndexOf "\r\n"))
      let parts = requestLine.Split ' '
      let path = if parts.Length > 1 then parts[1] else "/"
      match decide routes siteBase path with
      | Redirect location ->
        log $"{requestLine} -> 302 {location}"
        do! respond stream "302 Found" [ $"Location: {location}" ] ""
      | Health ->
        do! respond stream "200 OK" [] "front"
      | Missing ->
        log $"{requestLine} -> 404"
        do! respond stream "404 Not Found" [] "<p>no upstream for this path</p>"
      | Forward route ->
        use upstream = new TcpClient()
        let! connected =
          task {
            try
              do! upstream.ConnectAsync(route.host, route.port)
              return true
            with _ ->
              return false
          }
        if not connected then
          log $"{requestLine} -> 502 {route.host}:{route.port}"
          let page = $"<h1>Starting…</h1><p>{WebUtility.HtmlEncode route.prefix} goes to {route.host}:{route.port}, which does not answer yet. This page reloads.</p><script>setTimeout(() => location.reload(), 1500)</script>"
          do! respond stream "502 Bad Gateway" [] page
        else
          log $"{requestLine} -> {route.host}:{route.port}"
          let up = upstream.GetStream()
          do! up.WriteAsync(Encoding.Latin1.GetBytes(rewriteHead head))
          if count > stop then
            do! up.WriteAsync(buffer, stop, count - stop)
          let toUpstream =
            task {
              try
                do! stream.CopyToAsync up
              with _ -> ()
              try upstream.Client.Shutdown SocketShutdown.Send with _ -> ()
            }
          let toClient =
            task {
              try
                do! up.CopyToAsync stream
              with _ -> ()
            }
          let! _ = Task.WhenAny(toClient, Task.WhenAll(toUpstream, toClient))
          ()
  }

/// Serves until the process is stopped. `routes` must hold a "/" fallback to reach the site itself.
let run (port: int) (siteBase: string) (routes: Route array) =
  let listener = new TcpListener(IPAddress.Loopback, port)
  listener.Start()
  let log (line: string) = Console.Out.WriteLine $"{DateTime.Now:``HH:mm:ss``} {line}"
  log $"front on http://127.0.0.1:{port}{siteBase}"
  for r in routes |> Array.sortByDescending _.prefix.Length do
    log $"  {r.prefix} -> http://{r.host}:{r.port}"
  Console.Out.Flush()
  while true do
    let client = listener.AcceptTcpClient()
    Task.Run(fun () -> (handle routes siteBase log client :> Task)) |> ignore
  0
