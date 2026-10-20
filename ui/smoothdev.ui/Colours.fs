/// Colour sets: named palettes of roles (background, stroke, fill, accent…) and the colours they take.
module smoothdev.ui.Colours

open System

type Rgb = { r: int; g: int; b: int }

type Palette =
  { name   : string
    /// Role and colour, in order (`"background", "#101418"`).
    roles  : (string * string) list }

let private hexDigit (c: char) =
  match c with
  | c when c >= '0' && c <= '9' -> Some(int c - int '0')
  | c when c >= 'a' && c <= 'f' -> Some(int c - int 'a' + 10)
  | c when c >= 'A' && c <= 'F' -> Some(int c - int 'A' + 10)
  | _ -> None

/// `#rgb` or `#rrggbb` (the `#` optional).
let parse (text: string) : Rgb option =
  let s = (if text.StartsWith "#" then text.Substring 1 else text).Trim()
  let s = if s.Length = 3 then String [| s[0]; s[0]; s[1]; s[1]; s[2]; s[2] |] else s
  if s.Length <> 6 then None
  else
    let ds = s.ToCharArray() |> Array.map hexDigit
    if ds |> Array.exists Option.isNone then None
    else
      let d = ds |> Array.map Option.get
      Some { r = d[0] * 16 + d[1]; g = d[2] * 16 + d[3]; b = d[4] * 16 + d[5] }

let private hex2 (n: int) =
  let digits = "0123456789abcdef"
  let n = max 0 (min 255 n)
  String [| digits[n / 16]; digits[n % 16] |]

let toHex (c: Rgb) = "#" + hex2 c.r + hex2 c.g + hex2 c.b

/// `a` towards `b` by `t` (0 is `a`, 1 is `b`).
let mix (t: float) (a: Rgb) (b: Rgb) =
  let m (x: int) (y: int) = int (Math.Round(float x + (float y - float x) * t))
  { r = m a.r b.r; g = m a.g b.g; b = m a.b b.b }

/// Relative luminance (WCAG 2), 0 black to 1 white.
let luminance (c: Rgb) =
  let ch (v: int) =
    let s = float v / 255.0
    if s <= 0.03928 then s / 12.92 else Math.Pow((s + 0.055) / 1.055, 2.4)
  0.2126 * ch c.r + 0.7152 * ch c.g + 0.0722 * ch c.b

/// Contrast ratio (WCAG 2), 1 to 21.
let contrast (a: Rgb) (b: Rgb) =
  let la, lb = luminance a, luminance b
  (max la lb + 0.05) / (min la lb + 0.05)

let palette name roles = { name = name; roles = roles }

let tryRole (role: string) (p: Palette) = p.roles |> List.tryFind (fun (k, _) -> k = role) |> Option.map snd

let setRole (role: string) (colour: string) (p: Palette) =
  if p.roles |> List.exists (fun (k, _) -> k = role) then
    { p with roles = p.roles |> List.map (fun (k, v) -> if k = role then k, colour else k, v) }
  else { p with roles = p.roles @ [ role, colour ] }

/// A palette's roles that are not valid colours.
let invalid (p: Palette) = p.roles |> List.filter (fun (_, c) -> (parse c).IsNone) |> List.map fst

/// A palette from a theme's colour tokens (the ones that parse as colours).
let ofTheme (t: Theme.Theme) = { name = t.name; roles = t.tokens |> List.filter (fun (_, v) -> (parse v).IsSome) }

/// Colour sets kept by name: pick one, edit it, add or remove.
type Sets = { palettes: Palette list; selected: string }

let select (name: string) (s: Sets) =
  if s.palettes |> List.exists (fun p -> p.name = name) then { s with selected = name } else s

let current (s: Sets) =
  s.palettes |> List.tryFind (fun p -> p.name = s.selected) |> Option.orElse (List.tryHead s.palettes)

let update (f: Palette -> Palette) (s: Sets) =
  match current s with
  | Some c -> { s with palettes = s.palettes |> List.map (fun p -> if p.name = c.name then f p else p) }
  | None -> s
