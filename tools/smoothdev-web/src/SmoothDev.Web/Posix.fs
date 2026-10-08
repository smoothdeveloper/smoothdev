/// The few POSIX calls process tracking needs: posix_spawn into a new process group, signals, reaping.
/// CliWrap runs the one-shot commands; it cannot start a child in its own process group, which is what
/// lets a later invocation stop a whole dev server tree, so long-running components start here.
module SmoothDev.Web.Posix

open System
open System.Reflection
open System.Runtime.InteropServices

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

    Native.posix_spawn_file_actions_addopen (actions, 1, log, O_WRONLY ||| O_CREAT ||| O_APPEND, 0o644)
    |> ignore

    Native.posix_spawn_file_actions_adddup2 (actions, 1, 2) |> ignore
    Native.posix_spawn_file_actions_addchdir_np (actions, cwd) |> ignore
    let mutable pid = 0
    let rc = Native.posix_spawnp (&pid, argv[0], actions, attr, argvPtr, envPtr)

    if rc = 0 then
      Ok pid
    else
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

/// True while any member of the process group runs.
let groupAlive pgid =
  resolver.Force()
  reap pgid |> ignore
  pgid > 1 && (Native.kill (-pgid, 0) = 0 || lastErrno () = EPERM)

let processGroup pid =
  resolver.Force()
  Native.getpgid pid

let signal pid signo =
  resolver.Force()
  pid > 1 && pid <> Environment.ProcessId && Native.kill (pid, signo) = 0

let signalGroup pgid signo =
  resolver.Force()
  pgid > 1 && Native.kill (-pgid, signo) = 0
