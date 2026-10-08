/// Experiment: read the same smoothdev.web.json document from the formats in
/// smoothdev-dev-root notes/config-format.md, into one record.
///
///   dotnet fsi experiments/config-formats.fsx
///
/// Run from tools/smoothdev-web. Libraries that exist on NuGet are tried.
/// Formats with no .NET parser are reported as such; their text is still here
/// so the note and the script stay on the same example.

#r "nuget: Tomlyn, 0.19.0"
#r "nuget: YamlDotNet, 16.3.0"
#r "nuget: Json5, 1.0.4"

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text.Json
open Tomlyn
open Tomlyn.Model
open YamlDotNet.Core
open YamlDotNet.RepresentationModel

// The file's data, before paths are resolved. Same cases as SmoothDev.Web.Fable / Client / Server / Config.
type Fable =
  | Plugin
  | NoFable
  | Cli of project: string * outDir: string * extension: string option

type Client =
  { dir      : string
    fable    : Fable
    port     : int
    dist     : string
    basePath : string option }

type Server =
  { project   : string
    port      : int
    prodPort  : int
    serveDist : bool }

type App =
  { name           : string
    packageManager : string
    client         : Client option
    server         : Server option
    previewPort    : int
    guiPort        : int }

// A loose tree, so each parser only has to produce this.
type Node =
  | M of Map<string, Node>
  | S of string
  | I of int
  | B of bool

let expected =
  { name = "shop"
    packageManager = "pnpm"
    client =
      Some
        { dir = "src/Client"
          fable = Plugin
          port = 5173
          dist = "dist"
          basePath = Some "/shop/" }
    server =
      Some
        { project = "src/Server/Server.fsproj"
          port = 5000
          prodPort = 8080
          serveDist = true }
    previewPort = 4173
    guiPort = 5399 }

module Samples =
  let json =
    """{
  "name": "shop",
  "packageManager": "pnpm",
  "client": {
    "dir": "src/Client",
    "fable": "plugin",
    "port": 5173,
    "dist": "dist",
    "base": "/shop/"
  },
  "server": {
    "project": "src/Server/Server.fsproj",
    "port": 5000,
    "prodPort": 8080,
    "serveDist": true
  },
  "previewPort": 4173,
  "guiPort": 5399
}"""

  let json5 =
    """{
  // lockfile wins when packageManager is omitted
  name: "shop",
  packageManager: "pnpm",
  client: {
    dir: "src/Client",
    fable: "plugin",
    port: 5173,
    dist: "dist",
    base: "/shop/",
  },
  server: {
    project: "src/Server/Server.fsproj",
    port: 5000,
    prodPort: 8080,
    serveDist: true,
  },
  previewPort: 4173,
  guiPort: 5399,
}"""

  let toml =
    """name = "shop"
packageManager = "pnpm"
previewPort = 4173
guiPort = 5399

[client]
dir = "src/Client"
fable = "plugin"
port = 5173
dist = "dist"
base = "/shop/"

[server]
project = "src/Server/Server.fsproj"
port = 5000
prodPort = 8080
serveDist = true
"""

  let yaml =
    """name: shop
packageManager: pnpm
previewPort: 4173
guiPort: 5399
client:
  dir: src/Client
  fable: plugin
  port: 5173
  dist: dist
  base: /shop/
server:
  project: src/Server/Server.fsproj
  port: 5000
  prodPort: 8080
  serveDist: true
"""

  // StrictYAML is a YAML subset: no implicit types, so numbers and bools are strings
  // unless a schema says otherwise. This sample is what the file looks like; the
  // script does not parse it (the parser is Python).
  let strictYaml = yaml

  let hcl =
    """name = "shop"
packageManager = "pnpm"
previewPort = 4173
guiPort = 5399

client {
  dir = "src/Client"
  fable = "plugin"
  port = 5173
  dist = "dist"
  base = "/shop/"
}

server {
  project = "src/Server/Server.fsproj"
  port = 5000
  prodPort = 8080
  serveDist = true
}
"""

  let cue =
    """name: "shop"
packageManager: "pnpm"
previewPort: 4173
guiPort: 5399
client: {
  dir: "src/Client"
  fable: "plugin"
  port: 5173
  dist: "dist"
  base: "/shop/"
}
server: {
  project: "src/Server/Server.fsproj"
  port: 5000
  prodPort: 8080
  serveDist: true
}
"""

  let dhall =
    """{ name = "shop"
, packageManager = "pnpm"
, previewPort = 4173
, guiPort = 5399
, client =
  { dir = "src/Client"
  , fable = "plugin"
  , port = 5173
  , dist = "dist"
  , base = "/shop/"
  }
, server =
  { project = "src/Server/Server.fsproj"
  , port = 5000
  , prodPort = 8080
  , serveDist = True
  }
}
"""

  let neon =
    """name: shop
packageManager: pnpm
previewPort: 4173
guiPort: 5399
client:
  dir: src/Client
  fable: plugin
  port: 5173
  dist: dist
  base: /shop/
server:
  project: src/Server/Server.fsproj
  port: 5000
  prodPort: 8080
  serveDist: true
"""

  let ucl =
    """name = "shop";
packageManager = "pnpm";
previewPort = 4173;
guiPort = 5399;
client {
  dir = "src/Client";
  fable = "plugin";
  port = 5173;
  dist = "dist";
  base = "/shop/";
}
server {
  project = "src/Server/Server.fsproj";
  port = 5000;
  prodPort = 8080;
  serveDist = true;
}
"""

let failAt where =
  failwith $"missing or wrong value at {where}"

let str where = function
  | S s -> s
  | other -> failwith $"{where}: expected a string, got {other}"

let int' where = function
  | I n -> n
  | S s ->
    match Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, n -> n
    | _ -> failwith $"{where}: expected an int, got {s}"
  | other -> failwith $"{where}: expected an int, got {other}"

let bool' where = function
  | B b -> b
  | S s when s = "true" -> true
  | S s when s = "false" -> false
  | other -> failwith $"{where}: expected a bool, got {other}"

let field name (map: Map<string, Node>) =
  Map.tryFind name map

let req name map =
  match field name map with
  | Some v -> v
  | None -> failAt name

let fableOf where node =
  match node with
  | S "plugin" -> Plugin
  | S "none" -> NoFable
  | S "cli" -> failwith $"{where}: \"cli\" needs the project detected from disk"
  | S other -> failwith $"{where}: fable must be plugin, none, cli or a table, not \"{other}\""
  | M m ->
    let project = req "project" m |> str "fable.project"
    let outDir = field "outDir" m |> Option.map (str "fable.outDir") |> Option.defaultValue "fable-out"
    let extension = field "extension" m |> Option.map (str "fable.extension")
    Cli(project, outDir, extension)
  | other -> failwith $"{where}: fable: {other}"

let clientOf node =
  match node with
  | M m ->
    { dir = req "dir" m |> str "client.dir"
      fable = req "fable" m |> fableOf "client.fable"
      port = req "port" m |> int' "client.port"
      dist = req "dist" m |> str "client.dist"
      basePath = field "base" m |> Option.map (str "client.base") }
  | _ -> failAt "client"

let serverOf node =
  match node with
  | M m ->
    { project = req "project" m |> str "server.project"
      port = req "port" m |> int' "server.port"
      prodPort = req "prodPort" m |> int' "server.prodPort"
      serveDist = req "serveDist" m |> bool' "server.serveDist" }
  | _ -> failAt "server"

let appOf node =
  match node with
  | M map ->
    { name = req "name" map |> str "name"
      packageManager = req "packageManager" map |> str "packageManager"
      client = field "client" map |> Option.map clientOf
      server = field "server" map |> Option.map serverOf
      previewPort = req "previewPort" map |> int' "previewPort"
      guiPort = req "guiPort" map |> int' "guiPort" }
  | _ -> failwith "the top level must be a table"

let ofJson (el: JsonElement) =
  let rec go (e: JsonElement) =
    match e.ValueKind with
    | JsonValueKind.Object ->
      e.EnumerateObject()
      |> Seq.map (fun p -> p.Name, go p.Value)
      |> Map.ofSeq
      |> M
    | JsonValueKind.String -> S (e.GetString() |> nonNull)
    | JsonValueKind.Number -> I (e.GetInt32())
    | JsonValueKind.True -> B true
    | JsonValueKind.False -> B false
    | other -> failwith $"json value {other}"
  go el

let parseJson (text: string) =
  let options = JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)
  use doc = JsonDocument.Parse(text, options)
  ofJson doc.RootElement |> appOf

let rec ofToml (value: obj) =
  match value with
  | :? TomlTable as table ->
    table
    |> Seq.map (fun (KeyValue(k, v)) -> k, ofToml v)
    |> Map.ofSeq
    |> M
  | :? string as s -> S s
  | :? bool as b -> B b
  | :? int64 as n -> I (int n)
  | :? int as n -> I n
  | other -> failwith $"toml value {other.GetType().Name}"

let parseToml (text: string) =
  Toml.Parse(text).ToModel() |> ofToml |> appOf

let rec ofYaml (node: YamlNode) =
  match node with
  | :? YamlMappingNode as map ->
    map.Children
    |> Seq.map (fun (KeyValue(k, v)) ->
      let key =
        match k with
        | :? YamlScalarNode as s -> s.Value |> nonNull
        | _ -> failwith "yaml key"
      key, ofYaml v)
    |> Map.ofSeq
    |> M
  | :? YamlScalarNode as s ->
    let raw = s.Value |> nonNull
    match s.Style with
    | ScalarStyle.Plain when raw = "true" -> B true
    | ScalarStyle.Plain when raw = "false" -> B false
    | ScalarStyle.Plain ->
      match Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
      | true, n -> I n
      | _ -> S raw
    | _ -> S raw
  | other -> failwith $"yaml node {other.GetType().Name}"

let parseYaml text =
  let stream = YamlStream()
  use reader = new StringReader(text)
  stream.Load reader
  ofYaml stream.Documents[0].RootNode |> appOf

let parseJson5 (text: string) =
  let parsed = Json5.Json5.Parse text
  JsonDocument.Parse(parsed.ToJsonString()).RootElement |> ofJson |> appOf

let show name result =
  match result with
  | Ok app when app = expected -> printfn "%-12s ok, same record" name
  | Ok app -> printfn "%-12s parsed, differs: %A" name app
  | Error msg -> printfn "%-12s %s" name msg

let tryParse name parse text =
  try Ok (parse text) with e -> Error e.Message
  |> show name

tryParse "json" parseJson Samples.json
tryParse "json5" parseJson5 Samples.json5
tryParse "toml" parseToml Samples.toml
tryParse "yaml" parseYaml Samples.yaml

[
  "strictyaml", "Python only (hitchdev/strictyaml); sample is YAML with no implicit types"
  "hcl", "no .NET parser (hashicorp/hcl is Go)"
  "cue", "no .NET parser (the cue binary)"
  "dhall", "no .NET parser (the dhall binary emits JSON)"
  "neon", "no .NET parser (PHP, ne-on.org)"
  "ucl", "no .NET parser (C, libucl)"
]
|> List.iter (fun (name, why) -> printfn "%-12s not parsed: %s" name why)
