---
title: Docs sites and Vite apps
---

A documentation site built with [Nacara](https://mangelmaxime.github.io/Nacara/) often hosts Vite apps: a playground linked from the navbar, an explorer built into the site's static folder. Each app then needs its own dev server under the right base, something that serves the site's pages next to it, and a build step that puts the app's bundle under the site. `smoothdev-web` finds the site and the apps, says where each app goes and why, and runs them.

```bash frame="terminal"
smoothdev-web site            # what was found, where each app mounts, why, and the dev wiring
smoothdev-web site dev        # the site's watch, each app's Vite under its route, one front port
smoothdev-web site dist       # the site's build with each app built into its route
smoothdev-web site preview    # that dist, served as a static host would
smoothdev-web site stop
```

`scan` lists sites next to the other hits, and `scan gui` shows sites, mounts, the front's routing, processes and logs on one page, with the same actions.

## Finding a site

A site is an F# project that references `Nacara.Core` (`PackageReference` or `paket.references`) and has a source file calling `Nacara.run`. From its sources the tool reads:

| what | from | default |
|---|---|---|
| name | `Site.create "<name>"` | the project's folder |
| base | `Site.baseUrl "..."`, else line 2 of `site-root.txt` | `/` |
| output | `Site.output "..."` | `output` |
| static folder | `Site.staticFiles "..."` (`Site.noStaticFiles`: none) | `public/` when it exists |
| content | `Theme.docs theme "..."` | `content` |
| navbar routes | `NavbarLink` and `NavbarSection` routes | |
| pages | the content folder's `.md` files, as routes | |
| build hooks | `<Exec Command="...">` in the project | |

The reading is textual: a site that computes these values in code gets the defaults.

## Finding the apps

A Vite app is a folder with a Vite config or a `package.json` depending on `vite`. The tool reads its `base`, `build.outDir`, `server.port`, `server.proxy`, whether Fable runs as a Vite plugin, and whether the config adds dev routes (`configureServer`). Apps are looked for under the scanned folder and under each site's repository.

## Where an app mounts

Rules, first match wins:

1. A library build (`build.lib`) or an app without a page is not mounted.
2. An app outside the site's repository is not mounted.
3. `smoothdev.site.json` names its route, or says `"mount": false`.
4. Its `build.outDir` is inside the site's static folder: it mounts where that folder publishes it (high confidence).
5. Its `build.outDir` is inside the site's output: same, at medium confidence (the site prunes its output on every build, so this works only when the app builds after the site).
6. A navbar link, or a top-level page, ends with the app's name (its folder, its package name, or its package name without a `<site>-` prefix): medium.
7. Otherwise, by convention: under the deepest content section that matches the app's path in the repository (`tools/web/gui` with content in `tools/web/` mounts at `tools/web/gui/`), else at its folder name: low.

A build hook in the site's project that builds the app (`pnpm --filter <package>`, or the app's path) is listed as a reason and raises a low guess to medium. Every mount carries its reasons; `site --json` prints them.

When the site has a page at the app's route (say `content/playground/index.md`, which loads a bundle built some other way), the mount is *page-owned*: in dev the app answers at that route; in dist the site's page is kept and the app's build is not copied over it.

## Dev: one front port

`site dev` starts:

- the site in watch mode: `dotnet run --project <site> -- watch --port <free> --host 127.0.0.1`;
- each mounted app: `vite --base <mount URL> --port <free> --strictPort --host 127.0.0.1`, so the app's own URLs, HMR included, already carry the route it will have in the site;
- the front, on one port (from 5400): the longest matching mount prefix goes to that app's Vite, the rest to the site. Upgrade requests pass through, so Vite's HMR socket and the site's live reload both work. `/` redirects to the site's base; a route without its trailing slash redirects to the one with it. When something behind the front is not up yet, it answers a page that reloads itself.

Apps no longer need dev middleware that serves the site's output: the front does it.

Processes, logs and state live in the site's repository, in `.smoothdev/web` (add `.smoothdev/` to the repository's `.gitignore`). `site status` lists them and `site stop` ends them.

## Dist

`site dist` builds the site (`dotnet run --project <site> -- build`), copies its output to `.smoothdev/web/site-dist`, and builds each mounted app that is not page-owned with `vite build --base <mount URL> --outDir <dist>/<route>`. The site's own build hooks still run, so apps the site already builds into its static folder arrive both ways, at the same place. `site preview` serves the result under the site's base.

## Overrides

`smoothdev.site.json` at the repository root, all keys optional:

```json
{
  "frontPort": 5400,
  "apps": {
    "tools/web/gui": { "route": "console/" },
    "experiments/sketch": { "mount": false }
  }
}
```

Keys of `apps` are app folders relative to the repository root. A route is relative to the site's base.

## This repository

This site is found the same way: `docs/` with base `/smoothdev/`, no static folder, and pages for this tool under `content/tools/smoothdev-web/`. The console page of `scan gui` is a Vite app in `tools/smoothdev-web/gui`, so it mounts by convention at `/smoothdev/tools/smoothdev-web/gui/`. From the repository root:

```bash frame="terminal"
smoothdev-web site dev     # the docs with live reload, and the console page with HMR, on one port
smoothdev-web site dist    # the docs, with the console page published under them (it shows demo data there)
```

`dotnet run --project docs -- watch` still runs the docs alone.
