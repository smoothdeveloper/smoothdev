/// Live pages for the contracts this tool checks. Paths are files under
/// `docs/content/tools/smoothdev-web/`, and the site root is `docs/site-root.txt`.
/// Both are read through the filesystem type provider, so renaming or moving
/// a file fails the build.
module SmoothDev.Web.DocLink

open System.IO
open Partas.TypeProvider.BuildHelper

type Repo = BuildHelperProvider<"../../../..">

let private routeOf (page: FileInfo) =
  let content = Repo.FileSystem.docs.content.ToString()
  let rel = Path.GetRelativePath(content, page.FullName).Replace('\\', '/')
  if rel.EndsWith ".md" then rel.Substring(0, rel.Length - 3) else rel

let private published fragment (page: FileInfo) =
  let lines =
    File.ReadAllLines(Repo.FileSystem.docs.``site-root.txt``.FullName)
    |> Array.map (fun line -> line.Trim())
    |> Array.filter (fun line -> line <> "")
  let origin = lines.[0].TrimEnd('/')
  let basePath = lines.[1].Trim('/')
  $"{origin}/{basePath}/{routeOf page}/#{fragment}"

/// The ASP.NET listen-URL contract.
let page = Repo.FileSystem.docs.content.tools.``smoothdev-web``.``server-url.md``

let url = published "server-url" page

/// The Vite `server.proxy` contract.
let vitePage = Repo.FileSystem.docs.content.tools.``smoothdev-web``.``vite-proxy.md``

let viteUrl = published "vite-proxy" vitePage
