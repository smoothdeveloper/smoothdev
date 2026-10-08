module SmoothDev.Web.Tests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open Expecto
open SmoothDev.Web

/// A fresh temporary folder, deleted when disposed.
type TempDir() =
  let root =
    Path.Combine(Path.GetTempPath(), $"smoothdev-web-tests-{Guid.NewGuid():N}")
    |> Directory.CreateDirectory
    |> _.FullName

  member _.path = root

  member _.write (relative: string) (content: string) =
    let file = Path.Combine(root, relative)
    Directory.CreateDirectory(Path.GetDirectoryName file |> nonNull) |> ignore
    File.WriteAllText(file, content)
    file

  interface IDisposable with
    member _.Dispose() =
      try
        Directory.Delete(root, true)
      with _ ->
        ()

let ok result =
  match result with
  | Ok v -> v
  | Error msg -> failtest msg

let error result =
  match result with
  | Ok _ -> failtest "expected an error"
  | Error(msg: string) -> msg

let ports =
  testList
    "ports"
    [
      test "the preferred port when free" {
        Expect.equal (Ports.pickWith (fun _ -> true) [||] 5173 100) 5173 "preferred"
      }

      test "skips busy and reserved ports" {
        let busy = set [ 5173; 5174 ]
        let picked = Ports.pickWith (fun p -> not (busy.Contains p)) [| 5175 |] 5173 100
        Expect.equal picked 5176 "first port neither busy nor reserved"
      }

      test "falls back to an ephemeral port when the window is full" {
        let picked = Ports.pickWith (fun _ -> false) [||] 5173 10
        Expect.isTrue (picked > 0 && (picked < 5173 || picked > 5182)) "outside the window"
      }

      test "a port with a listener is not free, and pick moves past it" {
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let taken = (listener.LocalEndpoint :?> IPEndPoint).Port

        try
          Expect.isFalse (Ports.isFree taken) "listening port"
          let picked = Ports.pick [||] taken
          Expect.notEqual picked taken "another port"
          Expect.isTrue (Ports.isFree picked) "the picked port is free"
        finally
          listener.Stop()

        Expect.isTrue (Ports.isFree taken) "free again once closed"
      }
    ]

let config =
  testList
    "config"
    [
      test "full file, relative paths resolved against its folder" {
        use dir = new TempDir()
        dir.write "src/Client/package.json" """{ "name": "client" }""" |> ignore

        dir.write "src/Client/App.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        |> ignore

        let server =
          dir.write "src/Server/Server.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />"

        let json =
          """{
  // comments and trailing commas are fine
  "name": "demo",
  "packageManager": "pnpm",
  "client": {
    "dir": "src/Client",
    "fable": { "project": "App.fsproj", "outDir": "out", "extension": ".jsx" },
    "port": 3000,
    "dist": "../../dist",
    "base": "/app/",
  },
  "server": { "project": "src/Server/Server.fsproj", "port": 5050, "prodPort": 9090 },
  "previewPort": 4000
}"""

        let cfg = Config.parse dir.path (Some "smoothdev.web.json") json |> ok
        let client = Option.get cfg.client
        Expect.equal cfg.name "demo" "name"
        Expect.equal cfg.packageManager Pnpm "packageManager"
        Expect.equal client.dir (Path.Combine(dir.path, "src", "Client")) "client.dir"
        Expect.equal client.fable (Cli(Path.Combine(client.dir, "App.fsproj"), "out", Some ".jsx")) "client.fable"
        Expect.equal client.port 3000 "client.port"
        Expect.equal client.dist (Path.Combine(dir.path, "dist")) "client.dist"
        Expect.equal client.basePath (Some "/app/") "client.base"
        Expect.equal (Config.urlBase client.basePath) "/app/" "url base"
        let s = Option.get cfg.server
        Expect.equal s.project server "server.project"
        Expect.equal (s.port, s.prodPort, s.serveDist) (5050, 9090, true) "server ports, serveDist defaults on"
        Expect.equal (cfg.previewPort, cfg.guiPort) (4000, Config.Defaults.guiPort) "ports"
      }

      test "a static app needs no server" {
        use dir = new TempDir()
        dir.write "package-lock.json" "{}" |> ignore

        let cfg = Config.parse dir.path None """{ "client": { "fable": "none" } }""" |> ok

        Expect.isNone cfg.server "no server"
        Expect.equal (Option.get cfg.client).fable NoFable "fable"
        Expect.equal cfg.packageManager Npm "from the lockfile"
        Expect.equal (Config.urlBase None) "/" "default base"
      }

      test "rejects unknown keys, bad values and missing projects" {
        use dir = new TempDir()

        let parse json =
          Config.parse dir.path None json |> error

        Expect.stringContains (parse """{ "clinet": {} }""")                           "unknown key \"clinet\"" "typo"
        Expect.stringContains (parse """{ "client": { "fable": "maybe" } }""")         "client.fable" "fable"
        Expect.stringContains (parse """{ "client": { "port": 70000 } }""")            "port number" "port"
        Expect.stringContains (parse """{ "server": { "project": "nope.fsproj" } }""") "does not exist" "server"
        Expect.stringContains (parse """{ "packageManager": "pip" }""")                "packageManager" "pm"
        Expect.stringContains (parse "{ nope")                                         "invalid JSON" "syntax"
      }

      test "detects a Fable CLI client and its output folder from index.html" {
        use dir = new TempDir()

        dir.write "package.json" """{ "name": "rpp", "devDependencies": { "vite": "8.3.2" } }"""
        |> ignore

        dir.write "package-lock.json" "{}" |> ignore
        dir.write "Browser.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk\" />" |> ignore

        dir.write "index.html" """<script type="module">import { start } from "./out-js/src/App.js";</script>"""
        |> ignore

        let cfg = Config.detect dir.path |> ok
        let client = Option.get cfg.client
        Expect.equal cfg.name "rpp" "name from package.json"
        Expect.equal cfg.packageManager Npm "npm from package-lock.json"
        Expect.equal client.fable (Cli(Path.Combine(dir.path, "Browser.fsproj"), "out-js", None)) "fable"
        Expect.isNone cfg.server "no server"
      }

      test "detects vite-plugin-fable and a Web SDK server" {
        use dir = new TempDir()

        dir.write "src/Client/package.json" """{ "devDependencies": { "vite": "8", "vite-plugin-fable": "0.2" } }"""
        |> ignore

        dir.write "src/Client/vite.config.js" "import fable from 'vite-plugin-fable'"
        |> ignore

        dir.write "src/Client/pnpm-lock.yaml" "" |> ignore

        let server =
          dir.write "src/Server/Server.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />"

        dir.write "src/Server/bin/Copy.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />"
        |> ignore

        let cfg = Config.detect dir.path |> ok
        Expect.equal (Option.get cfg.client).fable Plugin "plugin"
        Expect.equal (Option.get cfg.server).project server "server, bin/ skipped"
        Expect.equal cfg.packageManager Pnpm "pnpm"
      }

      test "package.yaml and pnpm-lock.yaml are a Vite app" {
        use dir = new TempDir()
        dir.write "web/package.yaml" "name: shop\ndevDependencies:\n  vite: ^8.3.2\n" |> ignore
        dir.write "web/pnpm-lock.yaml" "" |> ignore
        dir.write "web/vite.config.mjs" "export default {}" |> ignore

        let cfg = Config.detect dir.path |> ok
        let client = Option.get cfg.client
        Expect.equal cfg.name "shop" "name from package.yaml"
        Expect.equal cfg.packageManager Pnpm "pnpm-lock.yaml"
        Expect.equal client.dir (Path.Combine(dir.path, "web")) "client.dir"
        Expect.equal client.fable NoFable "no fsproj in the client dir"
      }

      test "a vite folder and the server beside it are one app" {
        use dir = new TempDir()
        dir.write "web/package.json" """{ "name": "playlist", "devDependencies": { "vite": "8" } }""" |> ignore
        dir.write "web/vite.config.mjs" "export default {}" |> ignore
        let server = dir.write "server/App.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />"
        let web = Path.Combine(dir.path, "web")
        let fromWeb = Config.load web |> ok
        let fromServer = Config.load (Path.GetDirectoryName server |> nonNull) |> ok
        Expect.equal fromWeb.root dir.path "opening the client uses the parent app"
        Expect.equal (Option.get fromWeb.server).project server "the sibling server"
        Expect.equal fromServer.root dir.path "opening the server uses the same app"
        Expect.equal (Option.get fromServer.client).dir web "the sibling client"
      }

      test "pnpm-lock.yaml beside a vite config needs no manifest" {
        use dir = new TempDir()
        dir.write "pnpm-lock.yaml" "" |> ignore
        dir.write "vite.config.mjs" "export default {}" |> ignore
        let cfg = Config.detect dir.path |> ok
        Expect.isSome cfg.client "client"
        Expect.equal cfg.packageManager Pnpm "pnpm"
      }

      test "scan lists a vite app and skips one covered by smoothdev.web.json" {
        use dir = new TempDir()
        dir.write "shop/web/package.yaml"    "name: shop\ndevDependencies:\n  vite: ^8\n" |> ignore
        dir.write "shop/web/vite.config.mjs" "export default {}" |> ignore
        dir.write "shop/smoothdev.web.json"  """{ "name": "shop", "client": { "dir": "web", "fable": "none" } }""" |> ignore
        dir.write "other/package.json"       """{ "name": "other", "devDependencies": { "vite": "8" } }""" |> ignore
        dir.write "other/vite.config.mjs"    "export default {}" |> ignore
        dir.write "api/Api.fsproj"           "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />" |> ignore

        let rows = Config.scan dir.path
        let kinds = rows |> Array.map (fun r -> r.kind, r.path) |> Array.sort
        Expect.equal kinds [| "config", "shop"; "server", "api/Api.fsproj"; "vite", "other" |] "rows"
      }

      test "scan lines fold a group and keep the first two levels open" {
        let row kind path note : Config.ScanRow = { path = path; kind = kind; note = note }

        let rows =
          [| row "vite"   "src/github.com/acme/shop" "shop"
             row "server" "src/github.com/acme/api/Api.fsproj" "Api"
             row "config" "src/gitlab.com/lab" "lab" |]

        let openLines = Config.scanLines rows [||] [||]
        let labels = openLines |> Array.map (fun l -> l.kind, l.label)
        Expect.equal
          labels
          [| "folder", "src"
             "folder", "github.com"
             "folder", "acme"
             "folder", "gitlab.com"
             "config", "lab" |]
          "third level stays folded"

        let opened = Config.scanLines rows [| "src/github.com/acme" |] [||]
        Expect.isTrue (opened |> Array.exists (fun l -> l.kind = "vite" && l.label = "shop")) "opening a group shows its app"

        let folded = Config.scanLines rows [||] [| "src" |]
        Expect.equal (folded |> Array.map (fun l -> l.label)) [| "src" |] "closing src hides the rest"
        Expect.equal (openLines |> Array.find (fun l -> l.label = "acme")).note "2 apps" "group counts the apps under it"
      }

      test "a parent scan cache covers a subfolder" {
        use dir = new TempDir()
        let row kind path note : Config.ScanRow = { path = path; kind = kind; note = note }
        Config.saveScanCache dir.path 1 [| row "vite" "apps/web" "web"; row "vite" "other" "other" |]

        let rows, covered, _ = Config.scanFromCache (Path.Combine(dir.path, "apps")) |> Option.get
        Expect.isTrue covered "ancestor cache"
        Expect.equal (rows |> Array.map (fun r -> r.path)) [| "web" |] "rebased under the subfolder"
      }

      test "nothing to run is an error" {
        use dir = new TempDir()
        Expect.stringContains (Config.detect dir.path |> error) "no Vite app" "message"
      }

      test "toJson round-trips through parse" {
        use dir = new TempDir()

        dir.write "package.json" """{ "name": "x", "devDependencies": { "vite": "8" } }"""
        |> ignore

        dir.write "App.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk\" />" |> ignore
        let detected = Config.detect dir.path |> ok
        let parsed = Config.parse dir.path None (Config.toJson detected) |> ok
        Expect.equal parsed detected "same config"
      }
    ]

let waitUntil (timeout: TimeSpan) condition =
  let deadline = DateTime.UtcNow + timeout

  while not (condition ()) && DateTime.UtcNow < deadline do
    Thread.Sleep 50

  condition ()

let tracking =
  testList
    "process tracking"
    [
      test "members: the group plus descendants that left it" {
        // pid, ppid, pgid
        let table =
          [|
            100, 1, 100 // leader
            101, 100, 100 // child in the group
            102, 101, 102 // grandchild that called setsid
            103, 102, 102 // its child
            200, 1, 200
          |] // unrelated

        let pids = Runner.members table (Some 100) 100 |> Array.sort
        Expect.equal pids [| 100; 101; 102; 103 |] "whole tree, nothing else"
        let orphaned = Runner.members table None 100 |> Array.sort
        Expect.equal orphaned [| 100; 101; 102; 103 |] "group members found without the leader pid"
      }

      test "ps snapshot: zombies do not count as running" {
        let snapshot =
          Runner.parseTable
            """  100     1   100 Ss
  101   100   100 Z
  102   100   100 S+
  200     1   200 Z+
garbage line
"""

        Expect.equal snapshot.table.Length 4 "four rows"
        Expect.equal snapshot.zombies (set [ 101; 200 ]) "zombies"
        Expect.isTrue (Runner.running snapshot 102) "sleeping is running"
        Expect.isFalse (Runner.running snapshot 101) "zombie"
        Expect.isFalse (Runner.running snapshot 999) "absent"
        Expect.isTrue (Runner.groupRunning snapshot 100) "group with live members"
        Expect.isFalse (Runner.groupRunning snapshot 200) "group of only a zombie"
      }

      test "ps listing: a zombie-only group is not a live member" {
        let listing =
          """  100 Z
  101 S
  200 Z+
"""

        Expect.isFalse (Posix.liveInGroup listing 100) "zombie leader"
        Expect.isTrue  (Posix.liveInGroup listing 101) "sleeping member"
        Expect.isFalse (Posix.liveInGroup listing 200) "zombie only"
        Expect.isFalse (Posix.liveInGroup listing 999) "absent"
      }

      test "start records a process group; stop ends every member and clears the state" {
        use dir = new TempDir()
        // a shell with a child and a grandchild, all in the new group
        let argv = [| "/bin/sh"; "-c"; "sleep 60 & (sleep 60 &) ; echo started; wait" |]
        let e = Runner.start dir.path "demo" argv [| "DEMO", "1" |] dir.path 0 "" |> ok
        Expect.equal e.pgid e.pid "leader of its own group"
        Expect.notEqual (Posix.processGroup e.pid) (Posix.processGroup Environment.ProcessId) "not our group"

        Expect.isTrue
          (waitUntil (TimeSpan.FromSeconds 5.) (fun () -> (File.ReadAllText e.log).Contains "started"))
          "log"

        Expect.equal (State.live dir.path |> Array.map _.name) [| "demo" |] "recorded"
        Expect.equal (State.runState e) Running "running"

        let snapshot = (Runner.processTable ()).Result
        let pids = Runner.members snapshot.table (Some e.pid) e.pgid
        Expect.isGreaterThanOrEqual pids.Length 3 "sh and its sleeps"

        let forced = (Runner.stop dir.path e (TimeSpan.FromSeconds 5.)).Result
        Expect.isFalse forced "SIGTERM was enough"
        Expect.isTrue (waitUntil (TimeSpan.FromSeconds 3.) (fun () -> not (Posix.groupAlive e.pgid))) "group gone"

        Expect.isTrue
          (waitUntil (TimeSpan.FromSeconds 3.) (fun () -> pids |> Array.forall (Posix.alive >> not)))
          "no orphans"

        Expect.isEmpty (State.read dir.path) "state cleared"
      }

      test "a process ignoring SIGTERM is killed after the timeout" {
        use dir = new TempDir()

        let argv =
          [| "/bin/sh"; "-c"; "trap '' TERM; echo ready; while true; do sleep 1; done" |]

        let e = Runner.start dir.path "stubborn" argv [||] dir.path 0 "" |> ok
        Expect.isTrue (waitUntil (TimeSpan.FromSeconds 5.) (fun () -> (File.ReadAllText e.log).Contains "ready")) "log"
        let forced = (Runner.stop dir.path e (TimeSpan.FromSeconds 1.)).Result
        Expect.isTrue forced "needed SIGKILL"
        Expect.isTrue (waitUntil (TimeSpan.FromSeconds 3.) (fun () -> not (Posix.groupAlive e.pgid))) "gone"
      }

      test "dead entries are dropped from the state file" {
        use dir = new TempDir()

        let e =
          Runner.start dir.path "short" [| "/bin/sh"; "-c"; "exit 0" |] [||] dir.path 0 ""
          |> ok

        Expect.isTrue (waitUntil (TimeSpan.FromSeconds 5.) (fun () -> not (State.isAlive e))) "exited"
        Expect.isEmpty (State.live dir.path) "not live"
        Expect.isEmpty (State.read dir.path) "pruned from the file"
      }

      test "state survives a round trip and concurrent updates" {
        use dir = new TempDir()

        let entry name =
          {
            name = name
            pid = 1
            pgid = 0
            port = 0
            url = ""
            log = ""
            command = [| "x" |]
            startedAt = DateTimeOffset.Now
            owner = 1
          }

        Array.Parallel.iter (fun i -> State.add dir.path (entry $"c{i}")) [| 1..20 |]
        let names = State.read dir.path |> Array.map _.name |> Array.sort
        Expect.equal names.Length 20 "no lost update"
        State.remove dir.path "c1"
        Expect.equal (State.read dir.path).Length 19 "removed"
      }

      test "a missing program is an error, not an exception" {
        use dir = new TempDir()

        let msg =
          Runner.start dir.path "nope" [| "smoothdev-no-such-program" |] [||] dir.path 0 ""
          |> error

        Expect.stringContains msg "cannot start" "message"
      }

      test "spawned commands are asked to colour redirected output" {
        Environment.SetEnvironmentVariable("NO_COLOR", "1")
        let env = Runner.environment [| "PORT", "1" |]
        Environment.SetEnvironmentVariable("NO_COLOR", null)
        let has key = env |> Array.exists (fun e -> e.StartsWith(key + "="))
        Expect.isTrue (has "FORCE_COLOR") "force"
        Expect.isTrue (has "DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION") "dotnet"
        Expect.isFalse (env |> Array.exists (fun e -> e.StartsWith "NO_COLOR=")) "no color off"
        Expect.isTrue (has "PORT") "extra kept"
      }

      test "a server that bound another port is stopped, so start is offered" {
        use dir = new TempDir()
        dir.write "server/App.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />" |> ignore
        let cfg = Config.load (Path.Combine(dir.path, "server")) |> ok
        let log = dir.write ".smoothdev/web/logs/server.log" "listening http://127.0.0.1:8766\n"

        State.add
          cfg.root
          { name = "server"
            pid = Environment.ProcessId
            pgid = 0
            port = 5001
            url = "http://127.0.0.1:5001/"
            log = log
            command = [||]
            startedAt = DateTimeOffset.Now
            owner = 0 }

        let row = Actions.rows cfg |> Array.find (fun r -> r.name = "server")
        Expect.equal row.state Stopped "the row is stopped while the log shows another port"
        let parked = dir.write ".smoothdev/web/logs/server.log" $"{Actions.watchParked} ...\n"
        Expect.isTrue (Actions.serverGaveUp { (State.read cfg.root)[0] with log = parked }) "watch parked after the app exited"
        let french = dir.write ".smoothdev/web/logs/server.log" "En attente de modification d'un fichier avant le redémarrage ...\n"
        Expect.isFalse (Actions.serverGaveUp { (State.read cfg.root)[0] with log = french }) "a translated park line is not the sentence the tool knows"
        let keys = Actions.dotnetEnglish |> Array.map fst
        Expect.contains keys "DOTNET_CLI_UI_LANGUAGE" "watch is started in English"
        Expect.contains keys "VSLANG" "MSBuild messages follow en-US"
      }

      // url, page file, fragment id
      let documentationPages = [
        DocLink.url    , DocLink.page    , "server-url"
        DocLink.viteUrl, DocLink.vitePage, "vite-proxy"
      ]

      for url, page, fragment in documentationPages do
        test $"the documentation link is the {fragment} page next to this tool" {
          Expect.equal
            url
            $"https://smoothdeveloper.github.io/smoothdev/tools/smoothdev-web/{fragment}/#{fragment}"
            "route follows the repo path under the published site root"
          Expect.isTrue page.Exists "the page is in the repository"
          let text = File.ReadAllText page.FullName
          Expect.stringContains text $"id=\"{fragment}\"" "the fragment on the link is in the page"
        }

      test "vite proxy warning names the prefixes that miss the dev server" {
        let server = "http://127.0.0.1:5002"
        Expect.isNone (Actions.viteProxyWarning server [| { prefix = "/api"; target = "http://localhost:5002" } |]) "localhost is the same server"
        let missing = Actions.viteProxyWarning server [||]
        Expect.isSome missing "no proxy"
        Expect.stringContains (Option.get missing) "no server.proxy" "says the config has none"
        Expect.stringContains (Option.get missing) DocLink.viteUrl "links the vite-proxy page"
        let others =
          Actions.viteProxyWarning server [|
            { prefix = "/api1"; target = "http://0.0.0.0:8080" }
            { prefix = "/api2"; target = "http://0.0.0.0:5000" }
          |]
        Expect.stringContains (Option.get others) "/api1 -> http://0.0.0.0:8080" "lists the first prefix"
        Expect.stringContains (Option.get others) "/api2 -> http://0.0.0.0:5000" "lists the other prefix"
        Expect.stringContains (Option.get others) server "names the dev server"
        Expect.stringContains (Option.get others) DocLink.viteUrl "links the vite-proxy page"
        Expect.isFalse ((Option.get others).Contains DocLink.url) "the ASP.NET page is the wrong-port case"
      }

      test "a listen line on another port is reported, a build log url is not" {
        let log = "see https://127.0.0.1:9999/docs\nNow listening on: http://127.0.0.1:8766\nlistening http://127.0.0.1:5000\n"
        let found = Runner.otherListenUrls log 5000
        Expect.equal found [| "http://127.0.0.1:8766" |] "only the other listen url"
      }

      test "ansi colour in a log line becomes html" {
        let line = "\u001b[38;5;196mERR\u001b[0m plain"
        let html = Runner.ansiHtml line
        Expect.stringContains html "color:#ff0000" "red"
        Expect.stringContains html "ERR" "text"
        Expect.stringContains html "plain" "reset"
        Expect.isFalse (html.Contains "\u001b") "no raw codes"
      }
    ]

let staticServer =
  testList
    "static server"
    [
      test "maps paths under the base, with index.html and SPA fallbacks" {
        use dir = new TempDir()
        let index = dir.write "index.html" "<html/>"
        let js = dir.write "assets/app.js" ""
        let resolve = StaticServer.resolve dir.path "/app/"
        Expect.equal (resolve "/app/") (Some index) "root"
        Expect.equal (resolve "/app") (Some index) "base without slash"
        Expect.equal (resolve "/app/assets/app.js") (Some js) "file"
        Expect.equal (resolve "/app/some/route") (Some index) "client-side route"
        Expect.isNone (resolve "/app/missing.js") "missing asset"
        Expect.isNone (resolve "/other/") "outside the base"
        Expect.isNone (resolve "/app/../../etc/passwd") "escape"
      }
    ]

let guiPage =
  testList
    "gui page"
    [
      test "the main page hides the gui row and Stop all ends the gui last" {
        let entry name url : Entry =
          { name = name
            pid = 1
            pgid = 0
            port = 1
            url = url
            log = ""
            command = [||]
            startedAt = DateTimeOffset.Now
            owner = 0 }

        let row name url : Row =
          { name = name
            role = name
            state = Running
            entry = Some(entry name url) }

        let cfg: Config =
          { name = "demo"
            root = "."
            file = None
            packageManager = Npm
            client = None
            server = None
            previewPort = 4000
            guiPort = 5050 }

        let html =
          Gui.page
            cfg
            [| row "server" "http://127.0.0.1:5000/"
               row "gui" "http://127.0.0.1:5050/"
               row "vite" "http://127.0.0.1:5173/" |]
            None
            [||]
            ""
            "/"

        Expect.stringContains html ">server<" "server row stays"
        Expect.stringContains html "http://127.0.0.1:5173/" "vite url stays"
        Expect.isFalse (html.Contains ">gui<") "no gui row"
        Expect.isFalse (html.Contains "http://127.0.0.1:5050/") "no link to this page"
        Expect.stringContains html ">Stop all<" "stop all button"

        Expect.equal
          Actions.stopAllOrder
          [| "preview"; "prod"; "vite"; "fable"; "server"; "gui" |]
          "apps first, in the same order stop uses, then the gui"
        Expect.equal (Array.last Actions.stopAllOrder) "gui" "the gui is last"
      }
    ]

let tests = testList "smoothdev.web" [ ports; config; tracking; staticServer; guiPage ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv tests
