/// The few POSIX calls process tracking needs: posix_spawn into a new process group, signals, reaping.
/// CliWrap runs the one-shot commands; it cannot start a child in its own process group, which is what
/// lets a later invocation stop a whole dev server tree, so long-running components start here.
module SmoothDev.Web.Posix

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Threading

let isMac = OperatingSystem.IsMacOS()

let resolver =
  lazy
    NativeLibrary.SetDllImportResolver(
      Assembly.GetExecutingAssembly(),
      DllImportResolver(fun name _ _ ->
        if name = "libc" then
          NativeLibrary.Load(if isMac then "/usr/lib/libSystem.B.dylib" else "libc.so.6")
        else
          0n)
    )

module Native =
  [<DllImport("libc", SetLastError = true)>]
  extern int kill(int pid, int signal)

  [<DllImport("libc", SetLastError = true)>]
  extern int getpgid(int pid)

  [<DllImport("libc", SetLastError = true)>]
  extern int waitpid(int pid, int& status, int options)

  [<DllImport("libc")>]
  extern int posix_spawnattr_init(nativeint attr)

  [<DllImport("libc")>]
  extern int posix_spawnattr_destroy(nativeint attr)

  [<DllImport("libc")>]
  extern int posix_spawnattr_setflags(nativeint attr, int16 flags)

  [<DllImport("libc")>]
  extern int posix_spawnattr_setpgroup(nativeint attr, int pgroup)

  [<DllImport("libc")>]
  extern int posix_spawnattr_setsigdefault(nativeint attr, nativeint sigset)

  [<DllImport("libc")>]
  extern int posix_spawnattr_setsigmask(nativeint attr, nativeint sigset)

  [<DllImport("libc")>]
  extern int sigemptyset(nativeint set)

  [<DllImport("libc")>]
  extern int sigaddset(nativeint set, int signo)

  [<DllImport("libc")>]
  extern int posix_spawn_file_actions_init(nativeint actions)

  [<DllImport("libc")>]
  extern int posix_spawn_file_actions_destroy(nativeint actions)

  [<DllImport("libc")>]
  extern int posix_spawn_file_actions_addopen(nativeint actions, int fd, string path, int oflag, int mode)

  [<DllImport("libc")>]
  extern int posix_spawn_file_actions_adddup2(nativeint actions, int fd, int newfd)

  [<DllImport("libc")>]
  extern int posix_spawn_file_actions_addchdir_np(nativeint actions, string path)

  [<DllImport("libc")>]
  extern int posix_spawnp(int& pid, string file, nativeint actions, nativeint attr, nativeint argv, nativeint envp)

  [<DllImport("libc")>]
  extern int openpty(int& master, int& slave, nativeint name, nativeint term, nativeint win)

  [<DllImport("libc")>]
  extern int read(int fd, byte[] buf, int count)

  [<DllImport("libc")>]
  extern int close(int fd)

let SIGHUP = 1
let SIGINT = 2
let SIGQUIT = 3
let SIGKILL = 9
let SIGPIPE = 13
let SIGTERM = 15
let SIGCHLD = if isMac then 20 else 17
let EPERM = 1
let WNOHANG = 1
let O_WRONLY = 1
let O_CREAT = if isMac then 0x200 else 0x40
let O_APPEND = if isMac then 0x8 else 0x400
let POSIX_SPAWN_SETPGROUP = 0x2s
let POSIX_SPAWN_SETSIGDEF = 0x4s
let POSIX_SPAWN_SETSIGMASK = 0x8s

let lastErrno () = Marshal.GetLastPInvokeError()

/// A NULL-terminated char*[] of UTF-8 strings; free with freeStrings.
let allocStrings (items: string array) =
  let p = Marshal.AllocHGlobal((items.Length + 1) * IntPtr.Size)

  items
  |> Array.iteri (fun i s -> Marshal.WriteIntPtr(p, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8 s))

  Marshal.WriteIntPtr(p, items.Length * IntPtr.Size, 0n)
  p

let freeStrings (p: nativeint) count =
  for i in 0 .. count - 1 do
    Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(p, i * IntPtr.Size))

  Marshal.FreeHGlobal p

/// Copies the pty master onto the log as bytes arrive, so a build shows up while it is still running.
/// A pipe would stay block-buffered inside `dotnet`; a pty makes it flush each line.
let follow master log =
  let thread =
    Thread(
      fun () ->
        try
          use fs = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
          let buf = Array.zeroCreate 8192
          let mutable n = 1

          while n > 0 do
            n <- Native.read (master, buf, buf.Length)

            if n > 0 then
              fs.Write(buf, 0, n)
              fs.Flush()
        finally
          Native.close master |> ignore
    )

  thread.IsBackground <- true
  thread.Start()

/// Starts argv[0] (looked up on PATH) as the leader of a new process group, in cwd, with stdin from
/// /dev/null and stdout + stderr appended to log. Signal dispositions and mask are reset to the defaults
/// (.NET ignores SIGPIPE, which children would otherwise inherit). Returns the pid, also the group id.
let spawn (argv: string array) (env: string array) (cwd: string) (log: string) : Result<int, string> =
  resolver.Force()
  // opaque libc types: pointers on macOS, structs on glibc; 1 KiB covers both
  let attr = Marshal.AllocHGlobal 1024
  let actions = Marshal.AllocHGlobal 1024
  let signals = Marshal.AllocHGlobal 256
  let argvPtr = allocStrings argv
  let envPtr = allocStrings env

  try
    Native.posix_spawnattr_init attr |> ignore

    Native.posix_spawnattr_setflags (attr, POSIX_SPAWN_SETPGROUP ||| POSIX_SPAWN_SETSIGDEF ||| POSIX_SPAWN_SETSIGMASK)
    |> ignore

    Native.posix_spawnattr_setpgroup (attr, 0) |> ignore
    Native.sigemptyset signals |> ignore
    Native.posix_spawnattr_setsigmask (attr, signals) |> ignore

    [| SIGHUP; SIGINT; SIGQUIT; SIGPIPE; SIGTERM; SIGCHLD |]
    |> Array.iter (fun s -> Native.sigaddset (signals, s) |> ignore)

    Native.posix_spawnattr_setsigdefault (attr, signals) |> ignore
    Native.posix_spawn_file_actions_init actions |> ignore

    Native.posix_spawn_file_actions_addopen (actions, 0, "/dev/null", 0, 0)
    |> ignore

    let mutable master = 0
    let mutable slave = 0
    // 200 columns: a narrow default pty would wrap build lines
    let win = Marshal.AllocHGlobal 8
    Marshal.WriteInt16(win, 0, 40s)
    Marshal.WriteInt16(win, 2, 200s)
    let pty = Native.openpty (&master, &slave, 0n, 0n, win) = 0
    Marshal.FreeHGlobal win

    if pty then
      follow master log
      Native.posix_spawn_file_actions_adddup2 (actions, slave, 1) |> ignore
      Native.posix_spawn_file_actions_adddup2 (actions, slave, 2) |> ignore
    else
      Native.posix_spawn_file_actions_addopen (actions, 1, log, O_WRONLY ||| O_CREAT ||| O_APPEND, 0o644)
      |> ignore

      Native.posix_spawn_file_actions_adddup2 (actions, 1, 2) |> ignore
    Native.posix_spawn_file_actions_addchdir_np (actions, cwd) |> ignore
    let mutable pid = 0
    let rc = Native.posix_spawnp (&pid, argv[0], actions, attr, argvPtr, envPtr)

    if pty then
      Native.close slave |> ignore

    if rc = 0 then
      Ok pid
    else
      if pty then
        Native.close master |> ignore

      Error $"cannot start {argv[0]}: {Marshal.GetPInvokeErrorMessage rc}"
  finally
    Native.posix_spawn_file_actions_destroy actions |> ignore
    Native.posix_spawnattr_destroy attr |> ignore
    Marshal.FreeHGlobal attr
    Marshal.FreeHGlobal actions
    Marshal.FreeHGlobal signals
    freeStrings argvPtr argv.Length
    freeStrings envPtr env.Length

/// Collects pid if it is an exited child of this process (so it is not mistaken for a live zombie).
let reap pid =
  resolver.Force()
  let mutable status = 0
  pid > 0 && Native.waitpid (pid, &status, WNOHANG) = pid

let exists pid =
  resolver.Force()
  pid > 0 && (Native.kill (pid, 0) = 0 || lastErrno () = EPERM)

let alive pid = not (reap pid) && exists pid

/// True while any member of the process group runs. A zombie still answers `kill -0`, and so does a
/// group id this process may not signal, so this alone is not enough to keep an entry.
let groupAlive pgid =
  resolver.Force()
  reap pgid |> ignore
  pgid > 1 && (Native.kill (-pgid, 0) = 0 || lastErrno () = EPERM)

/// A `ps -o pgid=,stat=` listing has a non-zombie member of `pgid`.
let liveInGroup (psOutput: string) pgid =
  pgid > 1
  && psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
     |> Array.exists (fun line ->
       match line.Split(' ', StringSplitOptions.RemoveEmptyEntries) with
       | [| g; stat |] ->
         match Int32.TryParse g with
         | true, n -> n = pgid && not (stat.StartsWith 'Z')
         | _ -> false
       | _ -> false)

/// True when `ps` shows a live (not zombie) member. Falls back to `groupAlive` when `ps` cannot be read.
let groupHasLiveMember pgid =
  if pgid <= 1 then
    false
  else
    try
      use p = new System.Diagnostics.Process()
      p.StartInfo.FileName <- "ps"
      p.StartInfo.Arguments <- "-A -o pgid=,stat="
      p.StartInfo.RedirectStandardOutput <- true
      p.StartInfo.UseShellExecute <- false

      if not (p.Start()) then
        groupAlive pgid
      else
        let text = p.StandardOutput.ReadToEnd()
        p.WaitForExit()

        if p.ExitCode <> 0 || text = "" then
          groupAlive pgid
        else
          liveInGroup text pgid
    with _ ->
      groupAlive pgid

let processGroup pid =
  resolver.Force()
  Native.getpgid pid

let signal pid signo =
  resolver.Force()
  pid > 1 && pid <> Environment.ProcessId && Native.kill (pid, signo) = 0

let signalGroup pgid signo =
  resolver.Force()
  pgid > 1 && Native.kill (-pgid, signo) = 0
