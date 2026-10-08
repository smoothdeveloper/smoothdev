/// Process tracking per platform: components run as the leader of a process tree that a later
/// invocation can find and stop. `Posix` uses posix_spawn into a new process group and signals;
/// `Windows` uses `cmd /c` plus `taskkill /T`, with the leader's pid standing in for the group id.
/// The functions at the bottom pick the right one for the running OS.
module SmoothDev.Web.ProcessManagement

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Threading

/// Windows: no process groups. A component's tree is found through parent pids and ended with `taskkill /T`.
/// The child runs under `cmd /c` so PATHEXT shims (npm.cmd, pnpm.cmd) resolve and stdout + stderr append to the log.
module Windows =
  open System
  open System.Diagnostics
  open System.Runtime.InteropServices

  module Native =
    [<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type ProcessEntry32 =
      val mutable dwSize: uint32
      val mutable cntUsage: uint32
      val mutable th32ProcessID: uint32
      val mutable th32DefaultHeapID: nativeint
      val mutable th32ModuleID: uint32
      val mutable cntThreads: uint32
      val mutable th32ParentProcessID: uint32
      val mutable pcPriClassBase: int
      val mutable dwFlags: uint32
      [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)>]
      val mutable szExeFile: string | null

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint CreateToolhelp32Snapshot(uint32 flags, uint32 pid)

    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool Process32FirstW(nativeint snapshot, ProcessEntry32& entry)

    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool Process32NextW(nativeint snapshot, ProcessEntry32& entry)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CloseHandle(nativeint handle)

  let private TH32CS_SNAPPROCESS = 2u

  /// (pid, ppid, pgid) rows; pgid is the pid itself, since there are no groups.
  let processRows () : (int * int * int) array =
    let snap = Native.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0u)
    let rows = ResizeArray()

    if snap <> -1n then
      try
        let mutable e = Native.ProcessEntry32()
        e.dwSize <- uint32 (Marshal.SizeOf<Native.ProcessEntry32>())
        let mutable ok = Native.Process32FirstW(snap, &e)

        while ok do
          rows.Add((int e.th32ProcessID, int e.th32ParentProcessID, int e.th32ProcessID))
          ok <- Native.Process32NextW(snap, &e)
      finally
        Native.CloseHandle snap |> ignore

    rows.ToArray()

  let private quote (s: string) =
    if s.Length > 0 && s.IndexOfAny [| ' '; '\t'; '"'; '&'; '('; ')'; '^'; '%' |] < 0 then
      s
    else
      "\"" + s.Replace("\"", "\\\"") + "\""

  /// Starts argv through `cmd /c`, in cwd, stdin from NUL, stdout + stderr appended to log, with no console
  /// window. Returns the pid of the cmd process, the root of the component's tree.
  let spawn (argv: string array) (env: string array) (cwd: string) (log: string) : Result<int, string> =
    try
      let line = argv |> Array.map quote |> String.concat " "
      let psi = ProcessStartInfo("cmd.exe")
      // /s keeps the outer quotes as the only ones cmd strips
      psi.Arguments <- $"/d /s /c \"{line} >> \"{log}\" 2>&1 < NUL\""
      psi.WorkingDirectory <- cwd
      psi.UseShellExecute <- false
      psi.CreateNoWindow <- true
      psi.Environment.Clear()

      for kv in env do
        match kv.IndexOf '=' with
        | i when i > 0 -> psi.Environment[kv.Substring(0, i)] <- kv.Substring(i + 1)
        | _ -> ()

      use p = nonNull (Process.Start psi)
      Ok p.Id
    with e ->
      Error $"cannot start {argv[0]}: {e.Message}"

  let exists pid =
    pid > 0
    && (try
          use p = Process.GetProcessById pid
          not p.HasExited
        with _ ->
          false)

  /// taskkill on the pid's tree; `force` is the SIGKILL analogue (a console-less child ignores the gentle one).
  let kill pid force =
    pid > 4
    && pid <> Environment.ProcessId
    && (try
          use p = new Process()
          p.StartInfo.FileName <- "taskkill"
          let flag = if force then "/F " else ""
          p.StartInfo.Arguments <- $"/T {flag}/PID {pid}"
          p.StartInfo.UseShellExecute <- false
          p.StartInfo.CreateNoWindow <- true
          p.StartInfo.RedirectStandardOutput <- true
          p.StartInfo.RedirectStandardError <- true
          p.Start() |> ignore
          p.StandardOutput.ReadToEnd() |> ignore
          p.StandardError.ReadToEnd() |> ignore
          p.WaitForExit()
          p.ExitCode = 0
        with _ ->
          false)

/// POSIX: posix_spawn into a new process group, signals, reaping.
module Posix =
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

let SIGTERM = Posix.SIGTERM
let SIGKILL = Posix.SIGKILL

let spawn argv env cwd log =
  if OperatingSystem.IsWindows() then Windows.spawn argv env cwd log else Posix.spawn argv env cwd log

/// Collects pid if it is an exited child of this process; a no-op on Windows.
let reap pid =
  if OperatingSystem.IsWindows() then false else Posix.reap pid

let alive pid =
  if OperatingSystem.IsWindows() then Windows.exists pid else Posix.alive pid

let groupAlive pgid =
  if OperatingSystem.IsWindows() then Windows.exists pgid else Posix.groupAlive pgid

let groupHasLiveMember pgid =
  if OperatingSystem.IsWindows() then Windows.exists pgid else Posix.groupHasLiveMember pgid

/// The group id of pid: the pid itself on Windows.
let processGroup pid =
  if OperatingSystem.IsWindows() then pid else Posix.processGroup pid

let signal pid signo =
  if OperatingSystem.IsWindows() then Windows.kill pid (signo = SIGKILL) else Posix.signal pid signo

let signalGroup pgid signo =
  if OperatingSystem.IsWindows() then Windows.kill pgid (signo = SIGKILL) else Posix.signalGroup pgid signo
