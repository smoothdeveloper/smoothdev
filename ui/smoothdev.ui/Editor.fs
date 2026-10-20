/// A code editor host, for any language: the text, where the cursor is, the problems, and the spans a
/// target colours. The language (its tokens, problems and hovers) is the owner's: it plugs in a
/// `Language`.
module smoothdev.ui.Editor

open System

[<RequireQualifiedAccess>]
type Severity =
  | Error
  | Warning
  | Info

/// A range of the text, by offsets (start included, end excluded).
type Range = { start: int; finish: int }

type Problem = { range: Range; severity: Severity; message: string }

/// A classified range (a keyword, a number, a comment): the class names a style.
type Token = { range: Range; kind: string }

type Language =
  { name     : string
    tokens   : string -> Token list
    problems : string -> Problem list
    /// Hover text at an offset.
    hover    : string -> int -> string option }

/// No colours, no problems, no hovers.
let plain = { name = "text"; tokens = (fun _ -> []); problems = (fun _ -> []); hover = (fun _ _ -> None) }

type State = { text: string; cursor: int; problems: Problem list }

let create (language: Language) (text: string) = { text = text; cursor = 0; problems = language.problems text }

/// The text changed: new problems.
let edit (language: Language) (text: string) (s: State) =
  { text = text; cursor = min s.cursor text.Length; problems = language.problems text }

/// Line and column, both from 1, of an offset.
let lineCol (text: string) (offset: int) =
  let offset = max 0 (min text.Length offset)
  let mutable line = 1
  let mutable col = 1
  for i in 0 .. offset - 1 do
    if text[i] = '\n' then
      line <- line + 1
      col <- 1
    else col <- col + 1
  line, col

/// The offset of a line and column (both from 1), clamped to the text.
let offsetOf (text: string) (line: int) (col: int) =
  let lines = text.Split '\n'
  let line = max 1 (min lines.Length line)
  let before = lines |> Array.take (line - 1) |> Array.sumBy (fun l -> l.Length + 1)
  before + max 0 (min lines[line - 1].Length (col - 1))

/// The text cut into consecutive spans, each with the token kind that covers it (or "" for none), for a
/// target to draw. Overlapping tokens: the first one wins.
let spans (text: string) (tokens: Token list) =
  let sorted =
    tokens
    |> List.filter (fun t -> t.range.finish > t.range.start)
    |> List.sortBy (fun t -> t.range.start)
  let rec go pos (ts: Token list) acc =
    match ts with
    | [] -> if pos < text.Length then (text.Substring pos, "") :: acc else acc
    | t :: rest when t.range.finish <= pos -> go pos rest acc
    | t :: rest ->
      let s = max pos (min text.Length t.range.start)
      let e = min text.Length t.range.finish
      let acc = if s > pos then (text.Substring(pos, s - pos), "") :: acc else acc
      let acc = if e > s then (text.Substring(s, e - s), t.kind) :: acc else acc
      go (max s e) rest acc
  go 0 sorted [] |> List.rev

let severityName =
  function
  | Severity.Error -> "error"
  | Severity.Warning -> "warning"
  | Severity.Info -> "info"

/// Problems by line, for the gutter and the problems list.
let problemLines (s: State) = s.problems |> List.map (fun p -> fst (lineCol s.text p.range.start), p)

/// A small tokenizer by words, for languages with a keyword list: words, numbers, `//` comments, strings.
let words (keywords: string list) (text: string) : Token list =
  let ks = Set.ofList keywords
  let n = text.Length
  let isWord (c: char) = Char.IsLetterOrDigit c || c = '_' || c = '-'
  let rec go i acc =
    if i >= n then List.rev acc
    elif i + 1 < n && text[i] = '/' && text[i + 1] = '/' then
      let e = match text.IndexOf('\n', i) with -1 -> n | e -> e
      go e ({ range = { start = i; finish = e }; kind = "comment" } :: acc)
    elif text[i] = '"' then
      let e = match text.IndexOf('"', i + 1) with -1 -> n | e -> e + 1
      go e ({ range = { start = i; finish = e }; kind = "string" } :: acc)
    elif Char.IsDigit text[i] then
      let mutable e = i
      while e < n && (Char.IsDigit text[e] || text[e] = '.') do e <- e + 1
      go e ({ range = { start = i; finish = e }; kind = "number" } :: acc)
    elif isWord text[i] then
      let mutable e = i
      while e < n && isWord text[e] do e <- e + 1
      let w = text.Substring(i, e - i)
      go e (if ks.Contains w then { range = { start = i; finish = e }; kind = "keyword" } :: acc else acc)
    else go (i + 1) acc
  go 0 []
