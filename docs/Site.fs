module Docs.Site

open Feliz.ViewEngine
open Nacara.Core
open Nacara.Plugins
open Nacara.Theme

let theme =
  Theme.defaults
  |> Theme.navbar [ NavbarSection("Guide", "guide", "/guide/introduction/") ]
  |> Theme.navbarEnd
    [
      NavbarDynamicWidget Search.trigger
      NavbarIcon("GitHub", "https://github.com/smoothdeveloper/smoothdev", Icons.github)
    ]
  |> Theme.editUrl "https://github.com/smoothdeveloper/smoothdev/edit/main/docs"
  |> Theme.footer (Html.p [ Html.text "Built with Nacara" ])

let site =
  Site.create "smoothdev"
  |> Site.baseUrl "/"
  |> Site.origin "https://smoothdeveloper.github.io"
  |> Site.output "output"
  |> Markdown.register
  |> TreeSitter.register
  |> Search.register
  |> Sitemap.register
  |> LightningCss.register
  |> Esbuild.register
  |> Nuglify.minifyHtml
  |> GitHubPages.register
  |> Theme.register theme
  |> Site.collection (Theme.docs theme "content")

[<EntryPoint>]
let main argv = Nacara.run site argv
