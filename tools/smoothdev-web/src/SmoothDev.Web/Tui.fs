/// Terminal UI: a Spectre.Console live display of the components, one log, messages and keys.
module SmoothDev.Web.Tui

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Spectre.Console
open Spectre.Console.Rendering

let stateMarkup state =
  match state with
  | Running            -> "[green]running[/]"
  | Orphaned           -> "[yellow]orphaned[/]"
  | Stopped            -> "[grey]stopped[/]"
  | Unavailable reason -> $"[grey]- {Markup.Escape reason}[/]"

/// The status table shared by `status` and the TUI.
let statusTable (rows: Row array) =
  let table = Table().Border(TableBorder.Rounded)

  [| "component"; "state"; "pid"; "port"; "url"; "up"; "role" |]
  |> Array.iter (fun c -> table.AddColumn c |> ignore)

  for r in rows do
    let cells =
      match r.entry with
      | Some e ->
        [|
          $"[bold]{r.name}[/]"
          stateMarkup r.state
          string e.pid
          (if e.port > 0 then string e.port else "")
          Markup.Escape e.url
          Actions.uptime e
          Markup.Escape r.role
        |]
      | None -> [| r.name; stateMarkup r.state; ""; ""; ""; ""; Markup.Escape r.role |]

    table.AddRow cells |> ignore

  table

let keys =
  "[bold]d[/] dev  [bold]p[/] prod  [bold]v[/] preview  [bold]b[/] dist  [bold]o[/] open  "
  + "[bold]l[/] next log  [bold]q[/] quit (stops what it started)  [bold]x[/] quit, leave running"

let run (cfg: Config) =
  if Console.IsInputRedirected || Console.IsOutputRedirected then
    AnsiConsole.MarkupLine "[red]tui needs an interactive terminal: use `status`, `logs -f` or `gui`[/]"
    1
  else
    let messages = ConcurrentQueue<string>()

    let push (line: string) =
      messages.Enqueue line

      while messages.Count > 200 do
        messages.TryDequeue() |> ignore

    let levelMarkup level (msg: string) =
      let m = Markup.Escape msg

      match level with
      | Info    -> m
      | Success -> $"[green]{m}[/]"
      | Warn    -> $"[yellow]{m}[/]"
      | Fail    -> $"[red]{m}[/]"
      | Detail  -> $"[grey]{m}[/]"

    let ctx: Actions.Context =
      {
        config  = cfg
        say     = fun level msg -> push (levelMarkup level msg)
        echo    = fun name line -> push $"[grey]{Markup.Escape name} | {Markup.Escape line}[/]"
        browser = true
      }

    let busy = ref ""
    let logIndex = ref 0
    let quit = ref None

    let launch label (work: unit -> Task<Result<unit, string>>) =
      if busy.Value <> "" then
        push $"[yellow]busy ({Markup.Escape busy.Value}), try again when it is done[/]"
      else
        busy.Value <- label

        Task.Run(fun () ->
          task {
            try
              let! _ = work ()
              ()
            with e ->
              push $"[red]{Markup.Escape e.Message}[/]"

            busy.Value <- ""
          }
          :> Task)
        |> ignore

    let toggle (names: string array) (startAction: ActionTarget) (stopAction: ActionTarget) =
      let running =
        State.live cfg.root |> Array.exists (fun e -> Array.contains e.name names)

      let action = if running then stopAction else startAction
      launch action.route (fun () -> Actions.perform ctx action)

    let render () : IRenderable =
      let rows = Actions.rows cfg

      let withLogs =
        rows |> Array.filter (fun r -> r.entry |> Option.exists (fun e -> e.log <> ""))

      let logPanel =
        if withLogs.Length = 0 then
          Panel(Markup "[grey]no component running[/]").Header("log")
        else
          let r = withLogs[logIndex.Value % withLogs.Length]
          let e = Option.get r.entry
          let height = max 5 (Console.WindowHeight - 24)

          let lines =
            Runner.tail e.log height
            |> Array.map (fun l -> Markup.Escape(if l.Length > 200 then l.Substring(0, 200) else l))

          Panel(Markup(String.Join("\n", lines))).Header($"log: {r.name} ({Markup.Escape e.log})").Expand()

      let recent = messages.ToArray()
      let recent = recent[max 0 (recent.Length - 6) ..]

      let header =
        let source = cfg.file |> Option.defaultValue "detected (no smoothdev.web.json)"

        let state =
          if busy.Value = "" then
            ""
          else
            $"  [yellow]busy: {Markup.Escape busy.Value}[/]"

        Markup $"[bold]smoothdev-web[/] {Markup.Escape cfg.name}  [grey]{Markup.Escape source}[/]{state}"

      Rows(
        header,
        statusTable rows,
        logPanel,
        Panel(
          Markup(
            if recent.Length = 0 then
              "[grey]-[/]"
            else
              String.Join("\n", recent)
          )
        )
          .Header("messages")
          .Expand(),
        Markup keys
      )

    Console.CancelKeyPress.Add(fun e ->
      e.Cancel <- true
      quit.Value <- Some true)

    AnsiConsole
      .Live(render ())
      .AutoClear(false)
      .Overflow(VerticalOverflow.Ellipsis)
      .Start(fun live ->
        while quit.Value.IsNone do
          while Console.KeyAvailable do
            match Char.ToLowerInvariant(Console.ReadKey(true).KeyChar) with
            | 'd' -> toggle Actions.devNames ActionTarget.DevStart ActionTarget.DevStop
            | 'p' -> toggle [| Component.Prod.name |] ActionTarget.ProdStart ActionTarget.ProdStop
            | 'v' -> toggle [| Component.Preview.name |] ActionTarget.PreviewStart ActionTarget.PreviewStop
            | 'b' -> launch ActionTarget.Dist.route (fun () -> Actions.perform ctx ActionTarget.Dist)
            | 'o' -> launch ActionTarget.Open.route (fun () -> Actions.perform ctx ActionTarget.Open)
            | 'l' -> logIndex.Value <- logIndex.Value + 1
            | 'q' -> quit.Value <- Some true
            | 'x' -> quit.Value <- Some false
            | _ -> ()

          Actions.reapOwned cfg
          live.UpdateTarget(render ())
          Thread.Sleep 250)

    if quit.Value = Some true then
      AnsiConsole.MarkupLine "stopping what this session started..."

      let say: Actions.Context =
        { ctx with
            say =
              fun level msg ->
                let text = Markup.Escape msg
                let line = if level = Fail then $"[red]{text}[/]" else text
                AnsiConsole.MarkupLine line
        }

      (Actions.stopOwned say).GetAwaiter().GetResult() |> ignore

    0

/// `scan tui`: cached hits show at once. The walk refreshes them. j/k move, space folds a folder.
let scan (root: string) =
  if Console.IsInputRedirected || Console.IsOutputRedirected then
    AnsiConsole.MarkupLine "[red]scan tui needs an interactive terminal: use `scan` or `scan gui`[/]"
    1
  else
    let view = Config.ScanView()
    let covered = ref ""
    let finished = ref false

    match Config.scanFromCache root with
    | Some(rows, true, cachedRoot) ->
      view.Load rows
      covered.Value <- cachedRoot
      finished.Value <- true
    | Some(rows, false, _) -> view.Load rows
    | None -> ()

    let opened = Collections.Generic.HashSet<string>()
    let closed = Collections.Generic.HashSet<string>()
    let visited = ref 0
    let current = ref root
    let selected = ref 0
    let window = ref 0
    let logIndex = ref 0
    use stopping = new CancellationTokenSource()

    if covered.Value = "" then
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

    let quit = ref false

    let lines () =
      Config.scanLines (view.Rows()) (Seq.toArray opened) (Seq.toArray closed)

    let render () =
      let all = lines ()
      if selected.Value >= all.Length then selected.Value <- max 0 (all.Length - 1)
      if selected.Value < window.Value then window.Value <- selected.Value
      if selected.Value >= window.Value + 30 then window.Value <- selected.Value - 29

      let live = Actions.scanLive root (view.Rows())
      let logs = live |> Array.filter (fun e -> e.log <> "")

      let table = Table().Border(TableBorder.Rounded)
      [| ""; "kind"; "path"; "note" |] |> Array.iter (fun c -> table.AddColumn(c) |> ignore)

      let last = min all.Length (window.Value + 24)

      for i in window.Value .. last - 1 do
        let line = all[i]
        let running = Actions.scanRunning line.path live

        let mark =
          match line.fold with
          | Some true -> "v"
          | Some false -> ">"
          | None -> " "

        let cursor = if i = selected.Value then ">" else " "
        let pad = String(' ', line.depth * 2)
        let tint = if running > 0 then "green" elif line.apps >= 4 then "blue" elif line.apps >= 2 then "cyan" else "grey"
        let note =
          if running > 0 then
            if line.note = "" then $"{running} running" else $"{line.note}, {running} running"
          else
            line.note

        let pathText = Markup.Escape $"{pad}{mark} {line.label}"

        table.AddRow(
          cursor,
          $"[{tint}]{line.kind}[/]",
          $"[{tint}]{pathText}[/]",
          Markup.Escape note)
        |> ignore

      let progress =
        if covered.Value <> "" then
          $"[green]cached[/]  {view.Rows().Length} apps from {Markup.Escape covered.Value}, {live.Length} running"
        elif finished.Value then
          $"[green]done[/]  {visited.Value} folders, {view.Rows().Length} apps, {live.Length} running"
        elif view.Rows().Length > 0 && visited.Value = 0 then
          "[yellow]cached[/]  refreshing"
        else
          $"[yellow]scanning[/]  {visited.Value} folders, {view.Rows().Length} apps  [grey]{Markup.Escape current.Value}[/]"

      let logPanel =
        if logs.Length = 0 then
          Panel(Markup "[grey]nothing running[/]").Header("logs")
        else
          let e = logs[logIndex.Value % logs.Length]
          let height = max 4 (Console.WindowHeight - 28)

          let lines =
            Runner.tail e.log height
            |> Array.map (fun l -> Markup.Escape(if l.Length > 200 then l.Substring(0, 200) else l))

          Panel(Markup(String.Join("\n", lines))).Header($"log: {Markup.Escape e.app} / {e.name}").Expand()

      Rows(
        Markup $"[bold]smoothdev-web scan[/]  {Markup.Escape root}",
        Markup progress,
        table,
        logPanel,
        Markup "[bold]j[/]/[bold]k[/] move   [bold]space[/] fold   [bold]enter[/] open   [bold]l[/] next log   [bold]q[/] quit")

    let openApp = ref None

    let toggle () =
      let all = lines ()
      if all.Length > 0 && selected.Value < all.Length then
        let line = all[selected.Value]

        match line.fold with
        | Some openNow ->
          if openNow then
            opened.Remove line.path |> ignore
            closed.Add line.path |> ignore
          else
            closed.Remove line.path |> ignore
            opened.Add line.path |> ignore
        | None ->
          match Config.scanAppDir root line.kind line.path with
          | Some dir -> openApp.Value <- Some dir
          | None -> ()

    Console.CancelKeyPress.Add(fun e ->
      e.Cancel <- true
      quit.Value <- true
      stopping.Cancel())

    while not quit.Value do
      AnsiConsole
        .Live(render ())
        .AutoClear(false)
        .Overflow(VerticalOverflow.Ellipsis)
        .Start(fun live ->
          while not quit.Value && openApp.Value.IsNone do
            while Console.KeyAvailable && openApp.Value.IsNone do
              let key = Console.ReadKey(true)

              match key.Key with
              | ConsoleKey.Q ->
                quit.Value <- true
                stopping.Cancel()
              | ConsoleKey.J
              | ConsoleKey.DownArrow -> selected.Value <- selected.Value + 1
              | ConsoleKey.K
              | ConsoleKey.UpArrow -> selected.Value <- max 0 (selected.Value - 1)
              | ConsoleKey.Spacebar ->
                match lines () with
                | all when selected.Value < all.Length && all[selected.Value].fold.IsSome -> toggle ()
                | _ -> ()
              | ConsoleKey.L -> logIndex.Value <- logIndex.Value + 1
              | ConsoleKey.Enter -> toggle ()
              | _ -> ()

            if openApp.Value.IsNone then
              live.UpdateTarget(render ())
              Thread.Sleep 200)

      match openApp.Value with
      | None -> ()
      | Some dir ->
        openApp.Value <- None
        match Config.load dir with
        | Error msg -> AnsiConsole.MarkupLine $"[red]{Markup.Escape msg}[/]"
        | Ok cfg -> run cfg |> ignore

    0
