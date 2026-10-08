---
title: Platform differences
---

<a id="platforms"></a>

`smoothdev-web` runs on Linux, macOS and Windows. The commands, the state file and the GUI are the same everywhere. What differs is how a component's process tree is started, found and stopped, because Windows has no process groups. The code for both lives in `ProcessManagement.fs`, as a `Posix` module and a `Windows` module; the functions at the bottom of that file pick one by operating system.

| | Linux and macOS | Windows |
| --- | --- | --- |
| Start | `posix_spawn` into a new process group | `cmd /d /c`, no console window |
| Group id | the leader's process group | the leader's pid (there are no groups) |
| Log | a pseudo-terminal copied onto the log file | the child's stdout and stderr appended to the log by `cmd` |
| Stdin | `/dev/null` | `NUL` |
| Process list | `ps -A -o pid=,ppid=,pgid=,stat=` | the Toolhelp32 snapshot |
| Stop | `SIGTERM` to the group and every member, `SIGKILL` after the timeout | `taskkill /T` on the tree, then `taskkill /T /F` after the timeout |
| Zombies | recognised and not counted as running | not applicable |
| Pid reuse | the entry only counts while the leader still leads the recorded group | the pid alone is checked |
| Open a URL | `open` (macOS), `xdg-open` (Linux) | `explorer` |

## Starting

On Linux and macOS each component is started as the leader of its own process group, so a later invocation, another terminal or the GUI can signal the whole tree. The child gets a pseudo-terminal for stdout and stderr, 200 columns wide. A pipe would leave `dotnet` block-buffering its output, and the pty makes it flush line by line. If no pty can be opened, the output is appended to the log file directly.

On Windows the command line runs under `cmd /d /c`. That is what lets `npm`, `pnpm` and `yarn` resolve, since they are `.cmd` shims that `CreateProcess` does not find on its own. `cmd` appends stdout and stderr to the log, so there is no pty: a tool that buffers when its output is not a terminal may show lines in the log later than on Linux or macOS. The environment still sets `FORCE_COLOR` and the related variables, so colour codes are kept.

## Finding what runs

The state file records `pid` and `pgid` for each component on every platform. On Windows `pgid` is the same as `pid`.

To list a component's members, the tool takes every process in its group and every descendant of those processes through parent pids. On Windows only the descendant walk applies. A process that detaches from its parent on Windows is no longer found, where on Linux and macOS it would still be in the group.

## Stopping

`stop` sends the polite signal first and escalates after a timeout. On Linux and macOS that is `SIGTERM` to the group and to each member, then `SIGKILL`. On Windows it is `taskkill /T` on the leader, then with `/F` once the timeout passes. A console-less process usually ignores the first `taskkill`, so on Windows a stop that takes the whole timeout is normal and the log reports it as killed.

## Installing while it runs

Windows locks the files of a running program, so `dotnet tool uninstall` fails while a GUI, TUI or `logs -f` of the installed tool is open. `dotnet fsi build.fsx -- install` therefore runs `release-running.ps1` first, which force-stops those instances. They do not get to stop what they started, so their components keep running and show as orphaned until `smoothdev-web stop` ends them.

Linux and macOS replace a running program's files without complaint, and the install there does nothing extra. A running instance keeps its old code until it is restarted.

## What is verified

The Windows path has been checked by starting a process through `ProcessManagement.spawn`, reading its log, listing its tree and killing it. The test project still exercises the POSIX path only, with `sleep` and `ps`, and the Windows path has not been run against a full dev start with Fable and Vite.
