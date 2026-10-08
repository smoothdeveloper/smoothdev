---
title: smoothdev.web
order: 2
---

`smoothdev-web` is a .NET global tool. One command starts the dev server under `dotnet watch`, Fable and Vite on free ports, builds the static bundle, runs the published production server, shows the logs, and stops the process tree again.

```bash frame="terminal"
smoothdev-web dev start
smoothdev-web status
smoothdev-web dist
smoothdev-web stop
```

Install it from a clone, in `tools/smoothdev-web`:

```bash frame="terminal"
dotnet fsi build.fsx -- install
```

`scan gui` walks a folder for Vite apps, Web SDK projects and `smoothdev.web.json` files, and opens one of them with the same controls as a single app. From this repository's build script, `dotnet fsi build.fsx -- watch` runs that scan under `dotnet watch`, using the directory the script was started from.

An ASP.NET server has to listen on the URL the tool passed. That contract is [ASP.NET server](server-url.md).
