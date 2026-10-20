---
title: Launches and ports
---

Every smoothdev-web invocation that starts something (`dev start`, `site dev`, `site preview`, `gui`, `scan gui`...) is a *launch*. Launches are recorded in one registry per user, `~/.smoothdev/launches/`, whatever folder they run in. The registry answers two questions: which ports are taken, and who started what.

## Ports are claimed before they are bound

A port is chosen under a lock (`~/.smoothdev/launches/.lock`). The tool skips the ports of the app's config, every port a live launch has claimed, and every port something already listens on. It writes the claim to its launch file before releasing the lock, and only then starts the process that binds it. Two `site dev` started at the same moment, in one folder or two, therefore get different ports. Without the claim, both would see 8080 free and the second one would fail to bind it seconds later.

## What a launch records

One JSON file per launch, `~/.smoothdev/launches/<id>.json`, written atomically (a temporary file, then a rename):

| field | what it is |
|---|---|
| `id` | `yyyyMMdd-HHmmss-<pid>` |
| `pid`, `ppid` | the smoothdev-web process and its parent |
| `ancestors` | the parent processes, nearest first: `pid`, `name`, `command` (see [Who started it](#who-started-it)) |
| `launcher` | `SMOOTHDEV_WEB_LAUNCHER`, when the caller set it |
| `user`, `cwd`, `dir` | who, where it was started, the app or site folder |
| `command` | the arguments |
| `startedAt` | when |
| `ports` | `port`, `role` (`site`, `front`, `Vite (<app>)`, `dev server`, `gui`...), `url`, the `pid` that binds it |
| `children` | the processes it started: `name`, `pid`, `root` (the folder whose `.smoothdev/web/state.json` tracks it), `port`, `url`, `log` |

A launch is *active* while the smoothdev-web process runs, *running* when only the processes it started still do (`site dev` returns and leaves its servers running), and *ended* when nothing is left. An ended launch is moved to `~/.smoothdev/launches/ended/` on the next read and its ports are free again. The newest 50 are kept.

## Commands

```
smoothdev-web launches             # what runs, by whom, on which ports
smoothdev-web launches --all       # also the ended ones
smoothdev-web launches --json      # the records, with "status" and "by" added
smoothdev-web launches stop <id>   # stop what that launch started (a prefix of the id is enough)
smoothdev-web who 8080             # the launch holding the port, else the process listening on it
```

`by` is the launcher name, else the first parent process that is not a shell, a package manager or the dotnet host (an editor, a terminal, an agent, a build tool). The web console (`scan gui`) lists the same launches.

## Who started it

smoothdev-web reads its parent processes as it starts, before it claims a port or starts anything, so the chain is the one that ran it even when its parent exits right away (a command started with `(smoothdev-web site dev &)`) or it detaches. When its parent is already gone, the leader of its process group (the shell that ran it) and the processes above it are recorded instead. Every process it starts inherits the chain in `SMOOTHDEV_WEB_ANCESTRY`, so one that runs detached (`gui --detach`) and has lost its parent still records who started the launch. `SMOOTHDEV_WEB_LAUNCHER` still names the launcher when it is set.

## Other running instances

```
smoothdev-web others               # dev and web servers listening here that no launch holds
smoothdev-web others --all         # every listening process, the system's too
smoothdev-web others --json
smoothdev-web others stop <pid>    # SIGTERM to one of them
smoothdev-web launches --others    # the same as others
```

It reads the listening TCP sockets (`lsof -nP -iTCP -sTCP:LISTEN`, else `ss -ltnp` on Linux; nothing on Windows yet) and, for each process, its command line, working folder, parents, process group and age. A process is left out when a live launch holds it: its port is claimed, or the process, one of its parents or its process group is one a launch started.

Each one gets a kind from its command line: `vite`, `webpack`, `next`, `nacara` (its project references Nacara), `dotnet watch`, `fable watch`, `npm script` (`pnpm`, `npm`, `yarn` or `bun` running `dev`, `start`, `serve` or `preview`), `python http.server`, `live-server`, `jupyter`, `antigravity`, `asp.net`, and the generic `dotnet`, `node`, `python`, `other`. The folder is shown relative to its git repository. The URL is `http://127.0.0.1:<port>` plus a base when one is known: Vite's `--base`, else the base in its config, or a Nacara site's `baseUrl`. The page's `<title>` is read with a short GET.

By default, generic processes are listed only when they run in a folder of yours. Application bundles, the system, `~/Library` and `/` are left out; `--all` shows them. `stop` only signals a process this list shows.

The web console shows them in an *Other running instances* card under *Launches*: project and folder, kind, URLs, pid, age, who started it, with *open* and *stop* (after a confirmation). It refreshes every few seconds; the tool reads the sockets at most every four seconds.

## Environment

| variable | effect |
|---|---|
| `SMOOTHDEV_WEB_LAUNCHER` | set by a tool that runs smoothdev-web (for example `SMOOTHDEV_WEB_LAUNCHER=my-build-tool`): recorded as `launcher` and shown as `by` |
| `SMOOTHDEV_WEB_LAUNCH` | set by smoothdev-web for every process it starts: the id of the launch that started it |
| `SMOOTHDEV_WEB_ANCESTRY` | set by smoothdev-web for every process it starts: the chain of processes that started it, as JSON, used when a process has lost its parent |
| `SMOOTHDEV_HOME` | the registry's folder instead of `~/.smoothdev` (the tests use a temporary one) |

## Limits

A process id is trusted as long as a process with that id runs. A server started from a shell that has exited shows `launchd` (or `init`) as the process that started it. The registry does not record process start times yet, so a reused id can keep a dead launch listed until that process exits.
