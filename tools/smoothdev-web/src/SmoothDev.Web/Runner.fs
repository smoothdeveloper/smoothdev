/// Starting, watching and stopping components; one-shot commands through CliWrap.
module SmoothDev.Web.Runner

open System
open System.Collections
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open CliWrap
open CliWrap.Buffered
open UnMango.CliWrap.FSharp

let commandLine (argv: string array) =
  argv
  |> Array.map (fun a -> if a.Contains ' ' then $"\"{a}\"" else a)
  |> String.concat " "

/// This process's environment plus overrides, as KEY=VALUE strings.
/// Children write to a log file, not a terminal, so colour is forced on: the GUI renders those codes.
let environment (extra: (string * string) array) =
  let vars = Dictionary<string, string>()

  for e in Environment.GetEnvironmentVariables() |> Seq.cast<DictionaryEntry> do
    vars[string e.Key] <- string e.Value

  vars.Remove "NO_COLOR" |> ignore

  if not (vars.ContainsKey "TERM") || String.IsNullOrWhiteSpace vars["TERM"] then
    vars["TERM"] <- "xterm-256color"

  for k, v in
    [|
      "FORCE_COLOR", "1"
      "CLICOLOR_FORCE", "1"
      "DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION", "1"
    |] do
    vars[k] <- v

  for k, v in extra do
    vars[k] <- v

  [| for KeyValue(k, v) in vars -> $"{k}={v}" |]

/// argv prefix that runs this tool again: the apphost, or `dotnet <dll>` under `dotnet run`.
let selfArgv () =
  let processPath =
    Environment.ProcessPath |> Option.ofObj |> Option.defaultValue "dotnet"

  if Path.GetFileNameWithoutExtension processPath = "dotnet" then
    [| processPath; typeof<Entry>.Assembly.Location |]
  else
    [| processPath |]

let resetLog (path: string) =
  Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
  File.WriteAllText(path, "")

/// Starts a component as its own process group, output to its log file, and records it in the state file.
let start root name (argv: string array) env cwd port url : Result<Entry, string> =
  let log = State.logFile root name
  Directory.CreateDirectory(State.logDir root) |> ignore

  File.WriteAllText(
    log,
    $"# {DateTimeOffset.Now:``yyyy-MM-dd HH:mm:ss``} smoothdev-web started {name}: {commandLine argv}\n# in {cwd}\n"
  )

  Posix.spawn argv (environment env) cwd log
  |> Result.map (fun pid ->
    let entry =
      {
        name = name
        pid = pid
        pgid = pid
        port = port
        url = url
        log = log
        command = argv
        startedAt = DateTimeOffset.Now
        owner = Environment.ProcessId
      }

    State.add root entry
    entry)

/// A process snapshot: (pid, ppid, pgid) rows, and the pids that are zombies (exited, not yet reaped by
/// their parent: `kill -0` still succeeds on them, so they must not count as running).
type Snapshot =
  {
    table: (int * int * int) array
    zombies: Set<int>
  }

/// Parses `ps -A -o pid=,ppid=,pgid=,stat=` output.
let parseTable (psOutput: string) =
  let rows =
    psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun line ->
      match line.Split(' ', StringSplitOptions.RemoveEmptyEntries) with
      | [| pid; ppid; pgid; stat |] ->
        match Int32.TryParse pid, Int32.TryParse ppid, Int32.TryParse pgid with
        | (true, a), (true, b), (true, c) -> Some((a, b, c), stat.StartsWith 'Z')
        | _ -> None
      | _ -> None)

  {
    table = rows |> Array.map fst
    zombies =
      rows
      |> Array.filter snd
      |> Array.map (fun ((pid, _, _), _) -> pid)
      |> Set.ofArray
  }

let processTable () =
  task {
    try
      let! result =
        (Cli.wrap "ps"
         |> Cli.args [ "-A"; "-o"; "pid=,ppid=,pgid=,stat=" ]
         |> Cli.validation CommandResultValidation.None)
          .ExecuteBufferedAsync()

      return parseTable result.StandardOutput
    with _ ->
      return { table = [||]; zombies = Set.empty }
  }

/// Running = in the snapshot and not a zombie.
let running (snapshot: Snapshot) pid =
  snapshot.table |> Array.exists (fun (p, _, _) -> p = pid)
  && not (snapshot.zombies.Contains pid)

let groupRunning (snapshot: Snapshot) pgid =
  snapshot.table
  |> Array.exists (fun (p, _, g) -> g = pgid && not (snapshot.zombies.Contains p))

/// Pids that belong to a component: the members of its process group, plus every descendant of them
/// (a tool that moved its own children to another group or session is still caught).
let members (table: (int * int * int) array) (leader: int option) pgid =
  let children = table |> Array.groupBy (fun (_, ppid, _) -> ppid) |> Map.ofArray

  let rec walk (seen: Set<int>) pid =
    match children.TryFind pid with
    | None -> seen
    | Some kids ->
      kids
      |> Array.fold (fun acc (kid, _, _) -> if acc.Contains kid then acc else walk (acc.Add kid) kid) seen

  let roots =
    Array.append
      (leader |> Option.toArray)
      (table
       |> Array.choose (fun (pid, _, g) -> if pgid > 0 && g = pgid then Some pid else None))
    |> Set.ofArray

  roots |> Set.fold walk roots |> Set.toArray

/// Stops a component: SIGTERM to its group and every member, SIGKILL after timeout. Returns true when it
/// had to kill. The entry leaves the state file.
let stop root (e: Entry) (timeout: TimeSpan) =
  task {
    let! snapshot = processTable ()
    let leader = if State.leaderAlive e then Some e.pid else None
    let pids = members snapshot.table leader e.pgid

    // from a fresh `ps` snapshot (zombies are gone); kill -0 when ps is unavailable
    let gone () =
      task {
        pids |> Array.iter (Posix.reap >> ignore)
        let! now = processTable ()

        if now.table.Length = 0 then
          return
            pids |> Array.forall (Posix.alive >> not)
            && (e.pgid = 0 || not (Posix.groupAlive e.pgid))
        else
          return
            pids |> Array.forall (running now >> not)
            && (e.pgid = 0 || not (groupRunning now e.pgid))
      }

    let signalAll signo =
      if e.pgid > 0 then
        Posix.signalGroup e.pgid signo |> ignore

      pids |> Array.iter (fun p -> Posix.signal p signo |> ignore)

    signalAll Posix.SIGTERM
    let deadline = DateTime.UtcNow + timeout

    let! first = gone ()
    let mutable finished = first

    while not finished && DateTime.UtcNow < deadline do
      do! Task.Delay 150
      let! check = gone ()
      finished <- check

    let forced = not finished

    if forced then
      signalAll Posix.SIGKILL
      do! Task.Delay 200

    State.remove root e.name
    return forced
  }

let http = new HttpClient(Timeout = TimeSpan.FromSeconds 3.)

/// The HTTP status a URL answers with, None when nothing answers.
let probe (url: string) =
  task {
    try
      use! response = http.GetAsync url
      return Some(int response.StatusCode)
    with _ ->
      return None
  }

/// Reads a file another process is writing.
let readShared (path: string) (fromEnd: int64) =
  if not (File.Exists path) then
    ""
  else
    use fs =
      new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

    if fromEnd > 0L && fs.Length > fromEnd then
      fs.Seek(-fromEnd, SeekOrigin.End) |> ignore

    use reader = new StreamReader(fs, Encoding.UTF8)
    reader.ReadToEnd()

let ansi =
  Text.RegularExpressions.Regex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07]*\x07")

/// A log line without terminal colour codes (tools colour their output even when it goes to a file).
let plain (line: string) = ansi.Replace(line, "").TrimEnd('\r')

let serverUrlDoc = DocLink.url

let private urlWithPort =
  Text.RegularExpressions.Regex(@"https?://(?:\[[^\]]+\]|[^/\s:]+):(\d+)", Text.RegularExpressions.RegexOptions.IgnoreCase)

/// Listen URLs a server printed for a port other than the one this tool passed.
/// Only lines that say the process is listening, so a doc link in the build log is not one.
let otherListenUrls (logText: string) (port: int) =
  logText.Split '\n'
  |> Array.collect (fun line ->
    let text = plain line
    if text.IndexOf("listening", StringComparison.OrdinalIgnoreCase) < 0 then
      [||]
    else
      urlWithPort.Matches text
      |> Seq.cast<Text.RegularExpressions.Match>
      |> Seq.choose (fun m ->
        match Int32.TryParse m.Groups[1].Value with
        | true, found when found <> port -> Some m.Value
        | _ -> None)
      |> Seq.toArray)
  |> Array.distinct

/// Waits until the component answers HTTP on its URL; fails when it exits, the timeout passes,
/// or the log shows it finished starting on a different port.
let waitReady (e: Entry) (timeout: TimeSpan) =
  task {
    let deadline = DateTime.UtcNow + timeout
    let mutable result = None

    while result.IsNone do
      let! status = probe e.url
      let elsewhere = otherListenUrls (readShared e.log 65536L) e.port

      match status with
      | Some _ -> result <- Some(Ok())
      | None when elsewhere.Length > 0 ->
        let reported = String.concat ", " elsewhere
        result <-
          Some(Error $"{e.name} finished starting, but {e.url} is not serving it. The log reports {reported}. {serverUrlDoc}")
      | None when not (State.isAlive e) -> result <- Some(Error $"{e.name} exited (log: {e.log})")
      | None when DateTime.UtcNow > deadline ->
        result <- Some(Error $"{e.name} did not answer on {e.url} within {int timeout.TotalSeconds}s (log: {e.log})")
      | None -> do! Task.Delay 300

    return Option.get result
  }

/// The last count lines of a log file, colour codes removed.
let tail (path: string) count =
  let lines =
    (readShared path 65536L).Split('\n')
    |> Array.map plain
    |> Array.filter (String.IsNullOrWhiteSpace >> not)

  lines[max 0 (lines.Length - count) ..]

let private color256 n =
  let basic =
    [|
      "#1a1a1a"; "#e45649"; "#50a14f"; "#c18401"; "#4078f2"; "#a626a4"; "#0184bc"; "#a0a1a7"
      "#5c6370"; "#e06c75"; "#98c379"; "#e5c07b"; "#61afef"; "#c678dd"; "#56b6c2"; "#ffffff"
    |]

  if n < 16 then
    basic[n]
  elif n < 232 then
    let i = n - 16
    let step = [| 0; 95; 135; 175; 215; 255 |]
    $"#{step[i / 36]:x2}{step[(i / 6) % 6]:x2}{step[i % 6]:x2}"
  else
    let v = min 255 (8 + (n - 232) * 10)
    $"#{v:x2}{v:x2}{v:x2}"

let private htmlEncode (s: string) =
  s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

/// A log line as HTML, with ANSI colours kept (Serilog's console theme, and other tools).
let ansiHtml (line: string) =
  let sb = Text.StringBuilder()
  let mutable i = 0
  let mutable fg = ""
  let mutable bg = ""
  let mutable bold = false

  let emit (text: string) =
    if text <> "" then
      let style =
        [|
          if bold then "font-weight:700"
          if fg <> "" then $"color:{fg}"
          if bg <> "" then $"background:{bg}"
        |]

      if style.Length = 0 then
        sb.Append(htmlEncode text) |> ignore
      else
        let body =
          "<span style=\"" + String.concat ";" style + "\">" + htmlEncode text + "</span>"

        sb.Append body |> ignore

  let apply (codes: int array) =
    let mutable k = 0

    while k < codes.Length do
      match codes[k] with
      | 0 ->
        bold <- false
        fg <- ""
        bg <- ""
      | 1 -> bold <- true
      | 22 -> bold <- false
      | 39 -> fg <- ""
      | 49 -> bg <- ""
      | n when n >= 30 && n <= 37 -> fg <- color256 (n - 30)
      | n when n >= 90 && n <= 97 -> fg <- color256 (n - 90 + 8)
      | n when n >= 40 && n <= 47 -> bg <- color256 (n - 40)
      | n when n >= 100 && n <= 107 -> bg <- color256 (n - 100 + 8)
      | 38
      | 48 when k + 2 < codes.Length && codes[k + 1] = 5 ->
        let hex = color256 (max 0 (min 255 codes[k + 2]))
        if codes[k] = 38 then fg <- hex else bg <- hex
        k <- k + 2
      | _ -> ()

      k <- k + 1

  while i < line.Length do
    if line[i] = '\u001b' && i + 1 < line.Length && line[i + 1] = '[' then
      let mutable j = i + 2

      while j < line.Length && line[j] <> 'm' && (line[j] < '@' || line[j] > '~') do
        j <- j + 1

      if j < line.Length && line[j] = 'm' then
        let body = line.Substring(i + 2, j - (i + 2))

        let codes =
          if body = "" then
            [| 0 |]
          else
            body.Split(';') |> Array.map (fun s -> match Int32.TryParse s with true, n -> n | _ -> 0)

        apply codes

      i <- min line.Length (j + 1)
    else
      let start = i

      while i < line.Length && line[i] <> '\u001b' do
        i <- i + 1

      emit (line.Substring(start, i - start))

  sb.ToString()

/// The last count lines, still carrying ANSI colour codes.
let tailRaw (path: string) count =
  let lines =
    (readShared path 65536L).Split('\n')
    |> Array.map (fun line -> line.TrimEnd '\r')
    |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace(plain line)))

  lines[max 0 (lines.Length - count) ..]

/// Waits until the component's log contains one of the patterns (e.g. Fable's first compilation).
let waitLog (e: Entry) (patterns: string array) (timeout: TimeSpan) =
  task {
    let deadline = DateTime.UtcNow + timeout
    let mutable result = None

    while result.IsNone do
      let text = readShared e.log 0L

      if patterns |> Array.exists text.Contains then
        result <- Some(Ok())
      elif not (State.isAlive e) then
        result <- Some(Error $"{e.name} exited (log: {e.log})")
      elif DateTime.UtcNow > deadline then
        result <- Some(Error $"{e.name}: no sign of readiness within {int timeout.TotalSeconds}s (log: {e.log})")
      else
        do! Task.Delay 300

    return Option.get result
  }

/// Runs a command to completion (CliWrap): every output line goes to echo and is appended to log.
/// Returns the exit code (127 when the program cannot be started).
let runOnce (echo: string -> unit) (log: string) (argv: string array) (env: (string * string) array) (cwd: string) =
  task {
    Directory.CreateDirectory(Path.GetDirectoryName log |> nonNull) |> ignore
    use writer = new StreamWriter(log, true, AutoFlush = true)
    writer.WriteLine $"# {DateTimeOffset.Now:``yyyy-MM-dd HH:mm:ss``} $ {commandLine argv}  (in {cwd})"
    let gate = obj ()

    let line (s: string) =
      lock gate (fun () ->
        writer.WriteLine s
        echo s)

    let target = PipeTarget.ToDelegate(Action<string> line)

    try
      let! result =
        Cli.wrap argv[0]
        |> Cli.args argv[1..]
        |> Cli.workDir cwd
        |> Cli.env env
        |> Cli.stdout target
        |> Cli.stderr target
        |> Cli.validation CommandResultValidation.None
        |> Cli.Task.exec CancellationToken.None

      return result.ExitCode
    with e ->
      line $"cannot run {argv[0]}: {e.Message}"
      return 127
  }
