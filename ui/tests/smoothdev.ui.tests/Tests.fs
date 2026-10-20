module smoothdev.ui.tests.Tests

open Expecto
open smoothdev.ui

let theme =
  testList "theme" [
    test "dark and light give every token" {
      Expect.isEmpty (Theme.missing Theme.dark) "dark"
      Expect.isEmpty (Theme.missing Theme.light) "light"
      Expect.equal (Theme.dark.tokens |> List.map fst) (Theme.light.tokens |> List.map fst) "same names, same order"
    }
    test "an application extends a theme with its own tokens and overrides" {
      let t = Theme.dark |> Theme.extend [ "site", "#bc8cff"; "accent", "#ff0000" ]
      Expect.equal (Theme.tryGet "site" t) (Some "#bc8cff") "added"
      Expect.equal (Theme.tryGet "accent" t) (Some "#ff0000") "replaced"
      Expect.equal (t.tokens |> List.findIndex (fun (k, _) -> k = "accent")) (Theme.dark.tokens |> List.findIndex (fun (k, _) -> k = "accent")) "in place"
    }
  ]

let colours =
  testList "colours" [
    test "hex parses and prints back" {
      Expect.equal (Colours.parse "#58a6ff" |> Option.map Colours.toHex) (Some "#58a6ff") "six digits"
      Expect.equal (Colours.parse "#fA0" |> Option.map Colours.toHex) (Some "#ffaa00") "three digits"
      Expect.isNone (Colours.parse "rgba(1,2,3,.5)") "not hex"
    }
    test "mix and contrast" {
      let black, white = Option.get (Colours.parse "#000"), Option.get (Colours.parse "#fff")
      Expect.equal (Colours.mix 0.5 black white |> Colours.toHex) "#808080" "half way"
      Expect.floatClose Accuracy.medium (Colours.contrast black white) 21.0 "black on white"
    }
    test "sets: select, edit the current one, invalid roles" {
      let sets: Colours.Sets =
        { palettes = [ Colours.palette "ink" [ "background", "#101418"; "stroke", "#e0e0e0" ]; Colours.ofTheme Theme.light ]
          selected = "ink" }
      let sets = sets |> Colours.update (Colours.setRole "accent" "nope")
      Expect.equal (Colours.current sets |> Option.map Colours.invalid) (Some [ "accent" ]) "the bad one"
      Expect.equal (Colours.select "missing" sets).selected "ink" "unknown names are ignored"
      Expect.isFalse ((Colours.ofTheme Theme.light).roles |> List.exists (fun (k, _) -> k = "mono" || k = "glow")) "only hex colours from a theme"
    }
  ]

let scrubber =
  testList "scrubber" [
    test "plays, stops at the end, loops when asked" {
      let s = Scrubber.create 2.0 |> Scrubber.update Scrubber.Play |> Scrubber.update (Scrubber.Tick 1.5)
      Expect.equal s.position 1.5 "advanced"
      let s = s |> Scrubber.update (Scrubber.Tick 1.0)
      Expect.equal (s.position, s.playing) (2.0, false) "stopped at the end"
      let s = s |> Scrubber.update Scrubber.Toggle
      Expect.equal (s.position, s.playing) (0.0, true) "play at the end starts over"
      let s = { s with looping = true; position = 1.5 } |> Scrubber.update (Scrubber.Tick 1.0)
      Expect.floatClose Accuracy.high s.position 0.5 "wrapped"
    }
    test "seek and step stay in range" {
      let s = Scrubber.create 10.0
      Expect.equal (s |> Scrubber.update (Scrubber.Seek 12.0)).position 10.0 "clamped"
      Expect.equal (s |> Scrubber.update (Scrubber.SeekFraction 0.25)).position 2.5 "by fraction"
      Expect.equal ({ s with step = 0.5 } |> Scrubber.update (Scrubber.Step -3)).position 0.0 "not below 0"
      Expect.equal (Scrubber.formatTime 75.25, Scrubber.formatTime 3725.0) ("1:15.2", "1:02:05") "times"
    }
  ]

let rows =
  let columns =
    [ Rows.column "radius" "radius" (Rows.NumberKind(0.0, 100.0, 0.5)) (Rows.Number 10.0)
      Rows.column "shape" "shape" (Rows.Choice [ "circle"; "petal" ]) (Rows.Text "circle") ]
  testList "rows" [
    test "add, duplicate, move, remove within the limits" {
      let r = Rows.create columns 1 3 [ Rows.blank columns ]
      let r = r |> Rows.set 0 "radius" (Rows.Number 33.3) |> Rows.duplicate 0 |> Rows.set 1 "shape" (Rows.Text "petal")
      Expect.equal (r.rows |> List.map (fun row -> row["radius"], row["shape"])) [ Rows.Number 33.5, Rows.Text "circle"; Rows.Number 33.5, Rows.Text "petal" ] "snapped, duplicated"
      let r = r |> Rows.add false
      Expect.isFalse (Rows.canAdd r) "at the maximum"
      Expect.equal (Rows.add false r).rows.Length 3 "no more"
      let r = r |> Rows.move 2 0
      Expect.equal (Rows.get 0 "radius" r) (Some(Rows.Number 10.0)) "the blank one moved first"
      let r = r |> Rows.remove 0 |> Rows.remove 0 |> Rows.remove 0
      Expect.equal r.rows.Length 1 "not below the minimum"
    }
    test "values fit their column" {
      Expect.equal (Rows.fit columns[0] (Rows.Number 250.0)) (Rows.Number 100.0) "clamped"
      Expect.equal (Rows.fit columns[1] (Rows.Text "square")) (Rows.Text "circle") "unknown choice"
      Expect.equal (Rows.fit columns[0] (Rows.Text "x")) (Rows.Number 10.0) "wrong kind"
    }
  ]

let sortable =
  testList "sortable" [
    test "where a drop lands" {
      let middles = [ 10.0; 30.0; 50.0 ]
      Expect.equal ([ 0.0; 15.0; 45.0; 99.0 ] |> List.map (Sortable.dropIndex middles)) [ 0; 1; 2; 3 ] "before each middle"
    }
    test "move and insert" {
      let xs = [ "a"; "b"; "c"; "d" ]
      Expect.equal (Sortable.moveBefore 0 3 xs) [ "b"; "c"; "a"; "d" ] "down: before d"
      Expect.equal (Sortable.moveBefore 3 0 xs) [ "d"; "a"; "b"; "c" ] "up: first"
      Expect.equal (Sortable.moveBefore 1 4 xs) [ "a"; "c"; "d"; "b" ] "to the end"
      Expect.equal (Sortable.drop (Sortable.Insert "x") 2 xs) [ "a"; "b"; "x"; "c"; "d" ] "from a toolbar"
    }
  ]

let tree =
  let t =
    Tree.create [
      Tree.node "sites" "sites" [ Tree.leaf "docs" "docs"; Tree.node "app" "app" [ Tree.leaf "vite" "vite :5173" ] ]
      Tree.leaf "others" "others" ]
  let labels (t: Tree.Tree) = Tree.rows t |> List.map (fun r -> String.replicate r.depth "." + r.node.label)
  testList "tree" [
    test "rows under expanded nodes only" {
      Expect.equal (labels t) [ "sites"; "others" ] "closed"
      Expect.equal (labels (t |> Tree.expand "sites")) [ "sites"; ".docs"; ".app"; "others" ] "one open"
    }
    test "a filter shows matches and their ancestors" {
      Expect.equal (labels (t |> Tree.setFilter "VITE")) [ "sites"; ".app"; "..vite :5173" ] "case-insensitive"
    }
    test "keys: down, right opens then enters, left goes up then closes" {
      let t = t |> Tree.key Tree.Down
      Expect.equal t.selected (Some "sites") "first row"
      let t = t |> Tree.key Tree.Right
      Expect.isTrue (t.expanded.Contains "sites") "opened"
      let t = t |> Tree.key Tree.Right
      Expect.equal t.selected (Some "docs") "first child"
      let t = t |> Tree.key Tree.Left
      Expect.equal t.selected (Some "sites") "parent"
      let t = t |> Tree.key Tree.Left
      Expect.isFalse (t.expanded.Contains "sites") "closed"
      Expect.equal (t |> Tree.key Tree.End).selected (Some "others") "end"
    }
    test "reveal opens the way; expanded ids are saved and restored" {
      let t = t |> Tree.reveal "vite"
      Expect.equal (Set.toList t.expanded) [ "app"; "sites" ] "ancestors opened"
      Expect.equal (Tree.create t.roots |> Tree.restore (Tree.save t)).expanded t.expanded "remembered"
    }
  ]

let tabs =
  testList "tabs" [
    test "next and previous wrap; closing selects a neighbour" {
      let t = Tabs.create [ Tabs.tab "vite" "vite"; Tabs.tab "server" "server"; Tabs.tab "nacara" "nacara" ]
      Expect.equal (Tabs.current (Tabs.previous t)) (Some "nacara") "wraps back"
      let t = t |> Tabs.select "nacara" |> Tabs.next
      Expect.equal (Tabs.current t) (Some "vite") "wraps forward"
      let t = t |> Tabs.select "nacara" |> Tabs.close "nacara"
      Expect.equal (Tabs.current t) (Some "server") "the one before the last"
    }
    test "replacing the tabs keeps the selection when it remains" {
      let t = Tabs.create [ Tabs.tab "a" "a"; Tabs.tab "b" "b" ] |> Tabs.select "b"
      Expect.equal (Tabs.current (Tabs.setTabs [ Tabs.tab "b" "b"; Tabs.tab "c" "c" ] t)) (Some "b") "kept"
      Expect.equal (Tabs.current (Tabs.setTabs [ Tabs.tab "c" "c" ] t)) (Some "c") "gone: the first"
    }
  ]

let editor =
  let lang =
    { Editor.plain with
        name = "toy"
        tokens = Editor.words [ "let"; "in" ]
        problems =
          fun text ->
            match text.IndexOf "??" with
            | -1 -> []
            | i -> [ { range = { start = i; finish = i + 2 }; severity = Editor.Severity.Error; message = "unknown" } ] }
  testList "editor" [
    test "lines and columns, both ways" {
      let text = "let x = 1\nin x ??"
      Expect.equal (Editor.lineCol text 13) (2, 4) "the x on line 2"
      Expect.equal (Editor.offsetOf text 2 4) 13 "and back"
      Expect.equal (Editor.offsetOf text 9 99) text.Length "clamped"
    }
    test "spans cover the text, coloured by tokens" {
      let text = "let x = 12 // note"
      let spans = Editor.spans text (lang.tokens text)
      Expect.equal (spans |> List.map fst |> String.concat "") text "nothing lost"
      Expect.equal (spans |> List.filter (fun (_, k) -> k <> "")) [ "let", "keyword"; "12", "number"; "// note", "comment" ] "kinds"
    }
    test "problems follow edits" {
      let s = Editor.create lang "let x = 1"
      Expect.isEmpty s.problems "clean"
      let s = Editor.edit lang "let x = ??" s
      Expect.equal (Editor.problemLines s |> List.map (fun (l, p) -> l, p.message)) [ 1, "unknown" ] "one, on line 1"
    }
  ]

/// The web layer's pure parts (the rest needs a browser: see ui/gallery).
let css =
  // the console's palette before it moved onto the tokens, without its own (site, vite, server, config)
  let before = """
:root {
  --sd-bg: #0d1117; --sd-panel: #161b22; --sd-panel-2: #1c2128; --sd-line: #30363d; --sd-ink: #c9d1d9;
  --sd-bright: #f0f6fc; --sd-muted: #8b949e; --sd-accent: #58a6ff; --sd-accent-2: #bc8cff;
  --sd-ok: #3fb950; --sd-warn: #d29922; --sd-bad: #f85149; --sd-glow: rgba(88, 166, 255, .18);
  --sd-mono: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace;
  --sd-sans: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
  color-scheme: dark;
}
:root[data-theme="light"] {
  --sd-bg: #f6f8fa; --sd-panel: #ffffff; --sd-panel-2: #f1f3f5; --sd-line: #d0d7de; --sd-ink: #1f2328;
  --sd-bright: #0d1117; --sd-muted: #656d76; --sd-accent: #0969da; --sd-accent-2: #8250df;
  --sd-ok: #1a7f37; --sd-warn: #9a6700; --sd-bad: #cf222e; --sd-glow: rgba(9, 105, 218, .12);
  color-scheme: light;
}"""
  let blocks (text: string) =
    text.Split('}', System.StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun b -> b.Substring(b.IndexOf '{' + 1).Split(';') |> Array.map (fun d -> d.Trim()) |> Array.filter (fun d -> d <> "") |> Set.ofArray)
    |> Array.filter (fun s -> not s.IsEmpty)
  testList "css" [
    test "the tokens give the console's palette, dark and light" {
      let after = blocks (smoothdev.ui.fable.webcomponents.Css.stylesheet "sd" Theme.dark Theme.light)
      let before = blocks before
      Expect.equal after.Length 2 "two blocks"
      Expect.isEmpty (Set.difference before[0] after[0]) "dark: nothing lost"
      // the light block of the console set no fonts: they came from :root
      Expect.isEmpty (Set.difference before[1] after[1]) "light: nothing lost"
      Expect.isEmpty (Set.difference after[1] before[1] |> Set.filter (fun d -> not (d.StartsWith "--sd-mono" || d.StartsWith "--sd-sans"))) "light: nothing added but the fonts"
    }
  ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv (testList "smoothdev.ui" [ theme; colours; scrubber; rows; sortable; tree; tabs; editor; css ])
