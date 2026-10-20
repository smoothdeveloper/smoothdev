module SmoothDev.Web.Tests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open Expecto
open SmoothDev.Web
open SmoothDev.Web.Gui

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
        Expect.equal client.distCommand None "no dist command"
        Expect.equal client.basePath (Some "/app/") "client.base"
        Expect.equal (Config.urlBase client.basePath) "/app/" "url base"
        let s = Option.get cfg.server
        Expect.equal s.project server "server.project"
        Expect.equal (s.port, s.prodPort, s.serveDist) (5050, 9090, true) "server ports, serveDist defaults on"
        Expect.equal (cfg.previewPort, cfg.guiPort) (4000, Config.Defaults.guiPort) "ports"
      }

      test "distCommand is an argv, from a string or an array" {
        use dir = new TempDir()
        let fromString = Config.parse dir.path None """{ "client": { "fable": "none", "distCommand": "dotnet fsi build.fsx" } }""" |> ok
        Expect.equal (Option.get fromString.client).distCommand (Some [| "dotnet"; "fsi"; "build.fsx" |]) "string splits on spaces"
        let fromArray = Config.parse dir.path None """{ "client": { "fable": "none", "distCommand": ["dotnet", "fsi", "build.fsx"] } }""" |> ok
        Expect.equal (Option.get fromArray.client).distCommand (Some [| "dotnet"; "fsi"; "build.fsx" |]) "array is the argv"
        Expect.stringContains (Config.parse dir.path None """{ "client": { "distCommand": 1 } }""" |> error) "distCommand" "rejects a number"
      }

      test "config diff says what the file changes" {
        use dir = new TempDir()
        dir.write "web/vite.config.mjs" "export default {}" |> ignore
        dir.write "web/package.json" """{ "devDependencies": { "vite": "1.0.0" } }""" |> ignore
        dir.write "server/App.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />" |> ignore
        let json = """{ "client": { "dir": "web", "fable": { "project": "../ui/App.fsproj", "outDir": "js" }, "distCommand": "dotnet fsi build.fsx" }, "server": { "project": "server/App.fsproj", "serveDist": false } }"""
        Directory.CreateDirectory(Path.Combine(dir.path, "ui")) |> ignore
        dir.write "ui/App.fsproj" "<Project />" |> ignore
        let cfg = Config.parse dir.path (Some (Path.Combine(dir.path, "smoothdev.web.json"))) json |> ok
        let view = Config.explain dir.path cfg
        Expect.equal view.lines[0] "[blue]smoothdev.web.json[/]" "first line is the config relative to --dir"
        let text = view.rows |> Array.concat |> String.concat "\n"
        Expect.stringContains text "Fable" "detection has no fable project in web/"
        Expect.stringContains text "App.fsproj" "the file sets the cli watch"
        Expect.stringContains text "build.fsx" "the file sets the dist command"
        Expect.stringContains text "[bold]serveDist[/]" "serveDist key"
        Expect.stringContains text "true" "detection serveDist"
        Expect.stringContains text "false" "file serveDist"
        let moved = Config.parse dir.path (Some (Path.Combine(dir.path, "smoothdev.web.json"))) """{ "client": { "dir": "web", "dist": "out", "base": "/shop/" } }""" |> ok
        let movedText = (Config.explain dir.path moved).rows |> Array.concat |> String.concat "\n"
        Expect.stringContains movedText "[bold]client.dist[/]" "dist folder row"
        Expect.stringContains movedText "/shop/" "base row"
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

        Expect.isFalse (ProcessManagement.Posix.liveInGroup listing 100) "zombie leader"
        Expect.isTrue  (ProcessManagement.Posix.liveInGroup listing 101) "sleeping member"
        Expect.isFalse (ProcessManagement.Posix.liveInGroup listing 200) "zombie only"
        Expect.isFalse (ProcessManagement.Posix.liveInGroup listing 999) "absent"
      }

      test "start records a process group; stop ends every member and clears the state" {
        use dir = new TempDir()
        // a shell with a child and a grandchild, all in the new group
        let argv = [| "/bin/sh"; "-c"; "sleep 60 & (sleep 60 &) ; echo started; wait" |]
        let e = Runner.start dir.path "demo" argv [| "DEMO", "1" |] dir.path 0 "" |> ok
        Expect.equal e.pgid e.pid "leader of its own group"
        Expect.notEqual (ProcessManagement.processGroup e.pid) (ProcessManagement.processGroup Environment.ProcessId) "not our group"

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
        Expect.isTrue (waitUntil (TimeSpan.FromSeconds 3.) (fun () -> not (ProcessManagement.groupAlive e.pgid))) "group gone"

        Expect.isTrue
          (waitUntil (TimeSpan.FromSeconds 3.) (fun () -> pids |> Array.forall (ProcessManagement.alive >> not)))
          "no orphans"

        Expect.isEmpty (State.read dir.path) "state cleared"
      }

      test "a component does not inherit the fds of the command that started it" {
        use dir = new TempDir()
        ProcessManagement.Posix.resolver.Force()
        // dup() leaves the copy inheritable, like a stdout the runtime duplicated
        let fd = ProcessManagement.Posix.Native.dup 1
        Expect.isGreaterThan fd 2 "a spare fd"

        try
          // the log starts with the command line: the answers are computed, so only the shell prints them
          let argv =
            [| "/bin/sh"; "-c"; $"if (: >&{fd}) 2>/dev/null; then echo open$((6*7)); else echo shut$((6*7)); fi; sleep 60" |]

          let e = Runner.start dir.path "fds" argv [||] dir.path 0 "" |> ok

          try
            let said () = let t = File.ReadAllText e.log in t.Contains "open42" || t.Contains "shut42"
            Expect.isTrue (waitUntil (TimeSpan.FromSeconds 5.) said) "log"
            Expect.stringContains (File.ReadAllText e.log) "shut42" "fd closed in the child"
          finally
            (Runner.stop dir.path e (TimeSpan.FromSeconds 5.)).Wait()
        finally
          ProcessManagement.Posix.Native.close fd |> ignore
      }

      test "a process ignoring SIGTERM is killed after the timeout" {
        use dir = new TempDir()

        let argv =
          [| "/bin/sh"; "-c"; "trap '' TERM; echo ready; while true; do sleep 1; done" |]

        let e = Runner.start dir.path "stubborn" argv [||] dir.path 0 "" |> ok
        Expect.isTrue (waitUntil (TimeSpan.FromSeconds 5.) (fun () -> (File.ReadAllText e.log).Contains "ready")) "log"
        let forced = (Runner.stop dir.path e (TimeSpan.FromSeconds 1.)).Result
        Expect.isTrue forced "needed SIGKILL"
        Expect.isTrue (waitUntil (TimeSpan.FromSeconds 3.) (fun () -> not (ProcessManagement.groupAlive e.pgid))) "gone"
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

      test "the proxy probe runs in node and reads server.proxy with the server URL" {
        use dir = new TempDir()
        // a stand-in for vite: resolveConfig answers what a config reading SMOOTHDEV_WEB_SERVER_URL would
        let vite = Path.Combine(dir.path, "node_modules", "vite")
        Directory.CreateDirectory vite |> ignore
        File.WriteAllText(Path.Combine(vite, "package.json"), """{ "name": "vite", "type": "module", "exports": "./index.js" }""")

        File.WriteAllText(
          Path.Combine(vite, "index.js"),
          """export const resolveConfig = async () => { console.log("vite banner"); return { server: { proxy: { "/api": { target: process.env.SMOOTHDEV_WEB_SERVER_URL }, "/ws": "ws://elsewhere" } } } }"""
        )

        let proxies = (Actions.readViteProxy dir.path "http://127.0.0.1:5002/").Result |> ok
        Expect.equal proxies [| { prefix = "/api"; target = "http://127.0.0.1:5002" }; { prefix = "/ws"; target = "ws://elsewhere" } |] "rows"
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


/// A Nacara site in docs/ (base from site-root.txt, static folder public/) and Vite apps around it.
let siteFixture (dir: TempDir) =
  Directory.CreateDirectory(Path.Combine(dir.path, ".git")) |> ignore
  dir.write "docs/Docs.fsproj" """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><Compile Include="Site.fs" /></ItemGroup>
  <Target Name="BuildExplorer" BeforeTargets="BeforeBuild">
    <Exec Command="pnpm --filter demo-explorer run build" WorkingDirectory=".." />
  </Target>
</Project>""" |> ignore
  dir.write "docs/paket.references" "FSharp.Core\nNacara.Core\n" |> ignore
  dir.write "docs/site-root.txt" "https://example.org\n/demo/\n" |> ignore
  dir.write "docs/Site.fs" """module Docs.Site
let theme =
  Theme.defaults
  |> Theme.navbar
    [ NavbarSection("Guide", "guide", "/guide/introduction/")
      NavbarLink("Playground", "/playground/") ]
let site =
  Site.create "demo"
  |> Site.baseUrl baseUrl
  |> Site.output "output"
  |> Site.staticFiles "public"
  |> Site.collection (Theme.docs theme "content")
[<EntryPoint>]
let main argv = Nacara.run site argv""" |> ignore
  dir.write "docs/content/index.md" "# home" |> ignore
  dir.write "docs/content/guide/introduction.md" "# intro" |> ignore
  dir.write "docs/content/tools/web/index.md" "# web tool" |> ignore
  // linked by the navbar, with hand-written dev routes
  dir.write "playground/package.json" """{ "name": "demo-playground", "devDependencies": { "vite": "8.0.0", "vite-plugin-fable": "0.5.2" } }""" |> ignore
  dir.write "playground/index.html" "<script type=module src=./src/App.fs></script>" |> ignore
  dir.write "playground/vite.config.js" """export default { plugins: [fable(), { configureServer(server) {} }], server: { port: 5174, strictPort: true } }""" |> ignore
  // built into the site's static folder
  dir.write "tools/explorer/package.json" """{ "name": "demo-explorer", "devDependencies": { "vite": "8.0.0" } }""" |> ignore
  dir.write "tools/explorer/index.html" "<p>explorer</p>" |> ignore
  dir.write "tools/explorer/vite.config.js" """export default { base: "./", server: { port: 5175 }, build: { outDir: "../../docs/public/explorer", emptyOutDir: true } }""" |> ignore
  // a page app under a content section, nothing else says where it goes
  dir.write "tools/web/gui/package.json" """{ "name": "web-gui", "devDependencies": { "vite": "8.0.0" } }""" |> ignore
  dir.write "tools/web/gui/index.html" "<p>gui</p>" |> ignore
  // a library build
  dir.write "wasm/package.json" """{ "name": "demo-wasm", "devDependencies": { "vite": "8.0.0" } }""" |> ignore
  dir.write "wasm/vite.config.mts" """export default { build: { outDir: "../out/wasm", lib: { entry: { processor: "./P.fs" }, formats: ["es"] } } }""" |> ignore

let sites =
  testList
    "sites"
    [
      test "a Nacara site: base, output, static folder, navbar, pages, build hooks" {
        use dir = new TempDir()
        siteFixture dir
        let plan = Sites.plan dir.path
        Expect.equal plan.sites.Length 1 "one site"
        let s = plan.sites[0]
        Expect.equal s.name "demo" "Site.create"
        Expect.equal s.baseUrl "/demo/" "base from site-root.txt"
        Expect.equal s.output (Path.Combine(dir.path, "docs", "output")) "output"
        Expect.equal s.statics (Some(Path.Combine(dir.path, "docs", "public"))) "static folder"
        Expect.contains s.links ("Playground", "playground/") "navbar link"
        Expect.contains s.pages "guide/introduction/" "content page route"
        Expect.contains s.pages "" "home page"
        Expect.equal s.hooks [| "pnpm --filter demo-explorer run build" |] "Exec commands"
      }

      test "mounts: static folder, navbar link, content section convention; a library is not mounted" {
        use dir = new TempDir()
        siteFixture dir
        let plan = Sites.plan dir.path
        let byDir d = plan.mounts |> Array.find (fun m -> m.app.dir = Path.Combine(dir.path, d))
        let explorer = byDir "tools/explorer"
        Expect.equal (explorer.url, explorer.kind, explorer.confidence) ("/demo/explorer/", Sites.StaticFolder, Sites.High) "explorer"
        Expect.isTrue (explorer.reasons |> Array.exists (fun r -> r.Contains "--filter demo-explorer")) "the build hook is a reason"
        let playground = byDir "playground"
        Expect.equal (playground.url, playground.kind, playground.confidence) ("/demo/playground/", Sites.Linked, Sites.Medium) "playground"
        Expect.equal playground.app.port (Some 5174) "server.port"
        Expect.isTrue playground.app.middleware "configureServer"
        Expect.isFalse playground.pageOwned "no content page at playground/"
        let gui = byDir "tools/web/gui"
        Expect.equal (gui.url, gui.kind, gui.confidence) ("/demo/tools/web/gui/", Sites.Convention, Sites.Low) "gui under the tools/web section"
        let wasm = plan.unmounted |> Array.exactlyOne
        Expect.stringContains wasm.reason "library" "wasm is a library build"
      }

      test "a content page at the app's route keeps the page in dist" {
        use dir = new TempDir()
        siteFixture dir
        dir.write "docs/content/playground/index.md" "<div id=root></div>" |> ignore
        let plan = Sites.plan dir.path
        let playground = plan.mounts |> Array.find (fun m -> m.app.name = "demo-playground")
        Expect.isTrue playground.pageOwned "page owned"
      }

      test "smoothdev.site.json sets a route or drops an app" {
        use dir = new TempDir()
        siteFixture dir
        dir.write "smoothdev.site.json" """{ "apps": { "tools/web/gui": { "route": "/console" }, "playground": { "mount": false } } }""" |> ignore
        let plan = Sites.plan dir.path
        let gui = plan.mounts |> Array.find (fun m -> m.app.name = "web-gui")
        Expect.equal (gui.url, gui.kind) ("/demo/console/", Sites.Configured) "route from the file"
        Expect.isTrue (plan.unmounted |> Array.exists (fun u -> u.app.name = "demo-playground")) "playground dropped"
      }

      test "no site: apps are listed, not mounted" {
        use dir = new TempDir()
        dir.write "web/package.json" """{ "name": "w", "devDependencies": { "vite": "8.0.0" } }""" |> ignore
        dir.write "web/index.html" "<p/>" |> ignore
        let plan = Sites.plan dir.path
        Expect.isEmpty plan.sites "no site"
        Expect.equal (plan.unmounted |> Array.map _.reason) [| "no Nacara site found" |] "reason"
      }

      test "a project that does not call Nacara.run is not a site" {
        use dir = new TempDir()
        dir.write "x/X.fsproj" """<Project><ItemGroup><PackageReference Include="Nacara.Core" /><Compile Include="A.fs" /></ItemGroup></Project>""" |> ignore
        dir.write "x/A.fs" "module A" |> ignore
        Expect.isEmpty (Sites.plan dir.path).sites "no Nacara.run"
      }

      test "plan JSON names sites, mounts and reasons" {
        use dir = new TempDir()
        siteFixture dir
        let json = Sites.toJson (Sites.plan dir.path)
        use doc = Text.Json.JsonDocument.Parse json
        let mounts = doc.RootElement.GetProperty("mounts")
        Expect.equal (mounts.GetArrayLength()) 3 "three mounts"
        Expect.stringContains json "\"kind\": \"static-folder\"" "kind"
      }
    ]

/// The layout of a tool repository documenting itself: a Nacara site in docs/ without a static folder,
/// content pages per tool under tools/<tool>/, and (later) a Vite app next to a tool.
let selfDocFixture (dir: TempDir) =
  Directory.CreateDirectory(Path.Combine(dir.path, ".git")) |> ignore
  dir.write "docs/Docs.fsproj" """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="Site.fs" /></ItemGroup></Project>""" |> ignore
  dir.write "docs/paket.references" "FSharp.Core\nNacara.Core\n" |> ignore
  dir.write "docs/site-root.txt" "https://example.org\n/kit/\n" |> ignore
  dir.write "docs/Site.fs" """module Docs.Site
let site =
  Site.create "kit"
  |> Site.baseUrl baseUrl
  |> Site.collection (Theme.docs theme "content")
[<EntryPoint>]
let main argv = Nacara.run site argv""" |> ignore
  dir.write "docs/content/index.md" "# kit" |> ignore
  dir.write "docs/content/tools/kit-web/index.md" "# the web tool" |> ignore
  dir.write "tools/kit-web/src/Kit.Web.fsproj" "<Project />" |> ignore

let addGui (dir: TempDir) =
  dir.write "tools/kit-web/gui/package.json" """{ "name": "kit-web-gui", "dependencies": { "lit": "3.3.0" }, "devDependencies": { "vite": "8.0.0", "vite-plugin-fable": "0.5.2" } }""" |> ignore
  dir.write "tools/kit-web/gui/index.html" "<kit-console></kit-console>" |> ignore
  dir.write "tools/kit-web/gui/vite.config.mts" """export default { base: "./", plugins: [fable()], server: { port: 5401 }, build: { outDir: "dist" } }""" |> ignore

let selfDoc =
  testList
    "a site documenting its own tools"
    [
      test "the site alone: found, nothing mounted, dev runs the site behind the front" {
        use dir = new TempDir()
        selfDocFixture dir
        let plan = Sites.plan dir.path
        let s = Expect.wantSome (Array.tryExactlyOne plan.sites) "one site"
        Expect.equal s.baseUrl "/kit/" "base"
        Expect.isNone s.statics "no static folder"
        Expect.isEmpty plan.mounts "no app yet"
        let lines = SiteActions.devLines plan s
        Expect.isTrue (lines |> Array.exists (fun l -> l.StartsWith(SiteActions.siteName: string))) "the site's watch"
        Expect.isTrue (lines |> Array.exists (fun l -> l.StartsWith(SiteActions.frontName: string))) "the front"
      }

      test "a Vite app added next to a tool is mounted under that tool's section" {
        use dir = new TempDir()
        selfDocFixture dir
        addGui dir
        let plan = Sites.plan dir.path
        let m = Expect.wantSome (Array.tryExactlyOne plan.mounts) "one mount"
        Expect.equal (m.url, m.kind) ("/kit/tools/kit-web/gui/", Sites.Convention) "under tools/kit-web/"
        Expect.isFalse m.pageOwned "no page there: dist takes the app's build"
        let lines = SiteActions.devLines plan plan.sites[0]
        Expect.isTrue (lines |> Array.exists (fun l -> l.Contains "--base /kit/tools/kit-web/gui/")) "vite gets the mount as base"
      }
    ]

let console =
  testList
    "console"
    [
      let row path kind : Model.ScanRow = { path = path; kind = kind; note = "" }

      test "scan rows group by first folder, sites first" {
        let groups = Model.groups [| row "b/web" "vite"; row "a/docs/Docs.fsproj" "site"; row "a/play" "vite"; row "x" "config" |]
        Expect.equal (groups |> Array.map fst) [| "."; "a"; "b" |] "groups in path order"
        Expect.equal (snd groups[1] |> Array.map _.kind) [| "site"; "vite" |] "site first"
      }

      test "filter keeps rows with every word" {
        let rows = [| row "acme/playground" "vite"; row "acme/docs/Docs.fsproj" "site"; row "other/web" "vite" |]
        Expect.equal (Model.filter "acme vite" rows |> Array.map _.path) [| "acme/playground" |] "both words"
        Expect.equal (Model.filter "  " rows).Length 3 "empty query keeps all"
      }

      test "the console JSON reads back as the page's model, routes longest first" {
        use dir = new TempDir()
        siteFixture dir
        let state = WebConsole.State dir.path
        state.Rescan()
        let started = DateTime.Now
        while (state.Scanning || state.Plan.IsNone) && DateTime.Now - started < TimeSpan.FromSeconds 20.0 do
          Threading.Thread.Sleep 50
        state.Say Success "hello"
        let c = Text.Json.JsonSerializer.Deserialize<Model.Console>(WebConsole.consoleJson state) |> nonNull
        Expect.isFalse c.demo "live"
        let s = c.sites |> Array.exactlyOne
        Expect.equal s.baseUrl "/demo/" "base"
        Expect.equal s.mounts.Length 3 "mounts"
        Expect.isNone s.front "nothing runs"
        Expect.isTrue (c.scan |> Array.exists (fun r -> r.kind = "site" && Model.siteOfRow c.sites r = Some s)) "scan row finds its site"
        let routes = Model.routes s
        Expect.equal (Array.last routes).prefix "/demo/" "the site last"
        Expect.isTrue (routes |> Array.pairwise |> Array.forall (fun (a, b) -> a.prefix.Length >= b.prefix.Length)) "longest first"
        Expect.equal (c.unmounted |> Array.map _.app.name) [| "demo-wasm" |] "library unmounted"
        Expect.equal (Model.toasts c.messages |> Array.map _.text) [| "hello" |] "a fresh message is a toast"
        Expect.equal (Model.confidenceLevel "high", Model.confidenceLevel "?") (3, 0) "meter"
      }

      test "the console lists what runs under the root, sites and plain apps, with their URLs" {
        use dir = new TempDir()
        siteFixture dir
        // a dev server started in an app folder (`smoothdev-web dev start` there): this process stands in for it
        let app = Path.Combine(dir.path, "wasm")
        let entry name port : Entry =
          { name = name; pid = Environment.ProcessId; pgid = 0; port = port; url = $"http://127.0.0.1:{port}/"
            log = ""; command = [||]; startedAt = DateTimeOffset.Now; owner = Environment.ProcessId }
        State.add app (entry "vite" 5180)
        State.add dir.path { entry "site-front" 5400 with url = "http://127.0.0.1:5400/demo/" }
        State.add dir.path { entry "gui" 5399 with url = "http://127.0.0.1:5399/" }
        let state = WebConsole.State dir.path
        state.Rescan()
        let started = DateTime.Now
        while (state.Scanning || state.Plan.IsNone) && DateTime.Now - started < TimeSpan.FromSeconds 20.0 do
          Threading.Thread.Sleep 50
        let c = Text.Json.JsonSerializer.Deserialize<Model.Console>(WebConsole.consoleJson state) |> nonNull
        let opened = Model.openable c.running |> Array.map (fun r -> r.dir, r.name, r.url)
        Expect.equal opened [| "", "site-front", "http://127.0.0.1:5400/demo/"; "wasm", "vite", "http://127.0.0.1:5180/" |] "the front first, the console itself left out"
        let wasmRow = c.scan |> Array.find (fun r -> r.path = "wasm")
        Expect.equal (Model.runningFor c.running wasmRow |> Array.map _.name) [| "vite" |] "an app's row finds its dev server"
      }
    ]

let front =
  testList
    "front proxy"
    [
      let routes =
        [| "/demo/app/=http://127.0.0.1:5174"; "/=http://127.0.0.1:8080" |]
        |> Array.map (FrontProxy.parseRoute >> ok)

      test "longest prefix wins; the site is the fallback" {
        Expect.equal (FrontProxy.pick routes "/demo/app/src/App.fs.js?t=1").Value.port 5174 "app"
        Expect.equal (FrontProxy.pick routes "/demo/guide/").Value.port 8080 "site"
      }

      test "redirects: / to the base, an app without its slash" {
        Expect.equal (FrontProxy.decide routes "/demo/" "/") (FrontProxy.Redirect "/demo/") "root"
        Expect.equal (FrontProxy.decide routes "/demo/" "/demo/app?x=1") (FrontProxy.Redirect "/demo/app/?x=1") "slash"
        Expect.equal (FrontProxy.decide routes "/demo/" "/__front") FrontProxy.Health "health"
      }

      test "Connection: close unless upgrading" {
        let head = "GET / HTTP/1.1\r\nHost: x\r\nConnection: keep-alive\r\n\r\n"
        Expect.equal (FrontProxy.rewriteHead head) "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n" "close"
        let ws = "GET /ws HTTP/1.1\r\nHost: x\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n"
        Expect.equal (FrontProxy.rewriteHead ws) ws "upgrade untouched"
      }

      testTask "forwards to two upstreams on one port" {
        let serve (body: string) =
          let port = Ports.pick [||] 47100
          let listener = new HttpListener()
          listener.Prefixes.Add $"http://127.0.0.1:{port}/"
          listener.Start()
          Tasks.Task.Run(fun () ->
            while listener.IsListening do
              let c = listener.GetContext()
              let bytes = Text.Encoding.UTF8.GetBytes $"{body} {c.Request.RawUrl}"
              c.Response.OutputStream.Write(bytes, 0, bytes.Length)
              c.Response.Close()) |> ignore
          port
        let site = serve "site"
        let app = serve "app"
        let frontPort = Ports.pick [| site; app |] 47200
        let routes =
          [| $"/demo/app/=http://127.0.0.1:{app}"; $"/=http://127.0.0.1:{site}" |]
          |> Array.map (FrontProxy.parseRoute >> ok)
        Threading.Thread((fun () -> FrontProxy.run frontPort "/demo/" routes |> ignore), IsBackground = true).Start()
        let client = new Net.Http.HttpClient()
        let mutable up = false
        while not up do
          try
            let! _ = client.GetStringAsync $"http://127.0.0.1:{frontPort}/__front"
            up <- true
          with _ -> do! Tasks.Task.Delay 50
        let! a = client.GetStringAsync $"http://127.0.0.1:{frontPort}/demo/app/x.js"
        let! b = client.GetStringAsync $"http://127.0.0.1:{frontPort}/demo/guide/"
        Expect.equal a "app /demo/app/x.js" "app route"
        Expect.equal b "site /demo/guide/" "site route"
      }
    ]

let siteStatus =
  testList
    "site status --json"
    [
      test "the site's processes, the front first, with their links" {
        let entry name port url =
          { name = name; pid = 4242; pgid = 4242; port = port; url = url; log = (if url = "" then "" else "/tmp/" + name + ".log")
            command = [||]; startedAt = DateTimeOffset.UnixEpoch; owner = 1 }
        let json =
          SiteActions.statusJson
            [| entry "site-vite-playground" 5174 "http://127.0.0.1:5174/demo/playground/"
               entry "server" 5000 "http://127.0.0.1:5000/"
               entry "site-front" 5400 "http://127.0.0.1:5400/demo/"
               entry "site-nacara" 0 "" |]
        use doc = System.Text.Json.JsonDocument.Parse json
        let rows = doc.RootElement.EnumerateArray() |> Seq.toArray
        let names = rows |> Array.map (fun r -> r.GetProperty("name").GetString())
        Expect.equal names [| "site-front"; "site-nacara"; "site-vite-playground" |] "site entries only, the front first"
        Expect.equal (rows[0].GetProperty("url").GetString()) "http://127.0.0.1:5400/demo/" "the link to open"
        Expect.equal (rows[0].GetProperty("port").GetInt32()) 5400 "port"
        Expect.equal (rows[0].GetProperty("log").GetString()) "/tmp/site-front.log" "its log"
      }
    ]

let launches =
  let launchIn (home: string) (pid: int) suffix =
    { Launches.create home [| "site"; "dev" |] with id = $"test-{suffix}"; pid = pid }
  testList
    "launches"
    [
      test "two launches claiming at once never get the same port" {
        use home = new TempDir()
        let a = launchIn home.path Environment.ProcessId "a"
        let b = launchIn home.path Environment.ProcessId "b"
        // every port looks free: only the registry keeps them apart
        let claims (l: Launches.Launch) =
          async { return [ for i in 1 .. 12 -> Launches.claim home.path l $"role {i}" 8080 [||] (fun _ -> true) ] }
        let got = [ claims a; claims b ] |> Async.Parallel |> Async.RunSynchronously |> List.concat
        Expect.equal (List.distinct got |> List.length) 24 "24 claims, 24 ports"
        Expect.equal (List.sort got) [ 8080 .. 8103 ] "the first free ones from the preferred port"
        let live = Launches.list home.path false
        Expect.equal (live |> Array.map _.id |> Array.sort) [| "test-a"; "test-b" |] "both launches are listed"
        Expect.equal (Launches.holderOf home.path 8080 |> Option.map _.ports.Length) (Some 12) "the holder of a port"
      }

      test "a launch whose processes all exited is moved to ended, and frees its ports" {
        use home = new TempDir()
        let p = Diagnostics.Process.Start("true")
        p.WaitForExit()
        let dead = launchIn home.path p.Id "dead"
        Launches.claim home.path dead "site" 9000 [||] (fun _ -> true) |> ignore
        let live = launchIn home.path Environment.ProcessId "live"
        Expect.equal (Launches.claim home.path live "site" 9000 [||] (fun _ -> true)) 9000 "the dead launch's port is free again"
        Expect.equal (Launches.list home.path false |> Array.map _.id) [| "test-live" |] "only the live one runs"
        let ended = Launches.list home.path true |> Array.filter (fun l -> l.id = "test-dead")
        Expect.equal ended.Length 1 "the dead one is listed with --all"
        Expect.equal (Launches.status ended[0]) "ended" "as ended"
        Expect.isTrue (File.Exists(Launches.endedDirOf home.path </> "test-dead.json")) "in launches/ended"
      }

      test "a launch whose command exited stays while a process it started runs" {
        use home = new TempDir()
        let p = Diagnostics.Process.Start("true")
        p.WaitForExit()
        let l = launchIn home.path p.Id "site"
        Launches.claim home.path l "front" 5400 [||] (fun _ -> true) |> ignore
        Launches.addChild home.path l { name = "site-front"; pid = Environment.ProcessId; root = home.path; port = 5400; url = "http://127.0.0.1:5400/"; log = "" }
        Launches.finish home.path l
        match Launches.list home.path false with
        | [| r |] ->
          Expect.equal (Launches.status r) "running" "its command exited, its process runs"
          Expect.equal (r.ports |> Array.map (fun c -> c.port, c.pid, c.url)) [| 5400, Environment.ProcessId, "http://127.0.0.1:5400/" |] "the claim is bound to the process and its URL"
        | other -> failtest $"expected one live launch, got {other.Length}"
      }

      test "the parent processes of a launch" {
        if not (OperatingSystem.IsWindows()) then
          let l = Launches.create "/tmp" [| "status" |]
          let parent = l.ancestors
          Expect.isNonEmpty parent "ancestors"
          let ppid =
            let info = Diagnostics.ProcessStartInfo("ps", $"-o ppid= -p {Environment.ProcessId}", RedirectStandardOutput = true)
            use ps = Diagnostics.Process.Start info |> nonNull
            int (ps.StandardOutput.ReadToEnd().Trim())
          Expect.equal l.ppid ppid "the parent first"
          Expect.equal parent[0].pid ppid "the parent first"
          Expect.isNotEmpty parent[0].name "with its name"
          Expect.isNotEmpty parent[0].command "and command line"
          Expect.equal l.user Environment.UserName "user"
      }
    ]

let tests = testList "smoothdev.web" [ launches; ports; config; tracking; staticServer; guiPage; sites; selfDoc; console; front; siteStatus ]

[<EntryPoint>]
let main argv =
  // launches the tests start are recorded in a registry of their own, not in ~/.smoothdev
  let home = Path.Combine(Path.GetTempPath(), $"smoothdev-web-tests-home-{Guid.NewGuid():N}")
  Environment.SetEnvironmentVariable("SMOOTHDEV_HOME", home)
  try
    runTestsWithCLIArgs [] argv tests
  finally
    try Directory.Delete(home, true) with _ -> ()
