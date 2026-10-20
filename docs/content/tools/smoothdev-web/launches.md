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
| `ancestors` | the parent processes, nearest first: `pid`, `name`, `command` |
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

`by` is the launcher name, else the first parent process that is not a shell or the dotnet host (an editor, a terminal, a build tool). The web console (`scan gui`) lists the same launches.

## Environment

| variable | effect |
|---|---|
| `SMOOTHDEV_WEB_LAUNCHER` | set by a tool that runs smoothdev-web (for example `SMOOTHDEV_WEB_LAUNCHER=my-build-tool`): recorded as `launcher` and shown as `by` |
| `SMOOTHDEV_WEB_LAUNCH` | set by smoothdev-web for every process it starts: the id of the launch that started it |
| `SMOOTHDEV_HOME` | the registry's folder instead of `~/.smoothdev` (the tests use a temporary one) |

## Limits

A process id is trusted as long as a process with that id runs. The registry does not record process start times yet, so a reused id can keep a dead launch listed until that process exits.
