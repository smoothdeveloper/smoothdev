/// The live page for the server URL contract. The path is the file
/// `docs/content/tools/smoothdev-web/server-url.md`, and the site root is
/// `docs/site-root.txt`. Both are read through the filesystem type provider,
/// so renaming or moving that file fails the build.
module SmoothDev.Web.DocLink

open System.IO
open Partas.TypeProvider.BuildHelper

type Repo = BuildHelperProvider<"../../../..">

let page = Repo.FileSystem.docs.content.tools.``smoothdev-web``.``server-url.md``

let route =
  let content = Repo.FileSystem.docs.content.ToString()
  let rel = Path.GetRelativePath(content, page.FullName).Replace('\\', '/')
  if rel.EndsWith ".md" then rel.Substring(0, rel.Length - 3) else rel

let url =
  let lines =
    File.ReadAllLines(Repo.FileSystem.docs.``site-root.txt``.FullName)
    |> Array.map (fun line -> line.Trim())
    |> Array.filter (fun line -> line <> "")
  let origin = lines.[0].TrimEnd('/')
  let basePath = lines.[1].Trim('/')
  $"{origin}/{basePath}/{route}/#server-url"
