/// A scrubber and its transport: a position in a duration, playing or not, looping or not.
module smoothdev.ui.Scrubber

open System

type Scrubber =
  { position : float
    duration : float
    playing  : bool
    looping  : bool
    /// Speed while playing, 1 is real time.
    rate     : float
    /// The size of one step (a frame, a beat), for step forward and back.
    step     : float }

type Msg =
  | Play
  | Pause
  | Toggle
  | Restart
  | Seek of float
  /// Seek to a fraction of the duration, 0..1 (where a pointer is on the track).
  | SeekFraction of float
  | Step of int
  | Tick of seconds: float
  | SetRate of float
  | SetLooping of bool
  | SetDuration of float

let create (duration: float) =
  { position = 0.0; duration = max 0.0 duration; playing = false; looping = false; rate = 1.0; step = 1.0 / 30.0 }

let private clamp (s: Scrubber) (p: float) = max 0.0 (min s.duration p)

let fraction (s: Scrubber) = if s.duration <= 0.0 then 0.0 else s.position / s.duration

let atEnd (s: Scrubber) = s.position >= s.duration

let update (msg: Msg) (s: Scrubber) =
  match msg with
  | Play -> { s with playing = true; position = (if atEnd s && not s.looping then 0.0 else s.position) }
  | Pause -> { s with playing = false }
  | Toggle -> if s.playing then { s with playing = false } else { s with playing = true; position = (if atEnd s then 0.0 else s.position) }
  | Restart -> { s with position = 0.0 }
  | Seek p -> { s with position = clamp s p }
  | SeekFraction f -> { s with position = clamp s (f * s.duration) }
  | Step n -> { s with position = clamp s (s.position + float n * s.step); playing = false }
  | Tick dt when s.playing ->
    let p = s.position + dt * s.rate
    if p < s.duration then { s with position = max 0.0 p }
    elif s.looping && s.duration > 0.0 then { s with position = p % s.duration }
    else { s with position = s.duration; playing = false }
  | Tick _ -> s
  | SetRate r -> { s with rate = r }
  | SetLooping l -> { s with looping = l }
  | SetDuration d -> let d = max 0.0 d in { s with duration = d; position = min s.position d }

/// `m:ss.t`, or `h:mm:ss` from an hour.
let formatTime (seconds: float) =
  let t = max 0.0 seconds
  let whole = int (Math.Floor t)
  let tenths = int (Math.Floor((t - float whole) * 10.0))
  let h, m, sec = whole / 3600, (whole / 60) % 60, whole % 60
  let pad (n: int) = if n < 10 then "0" + string n else string n
  if h > 0 then $"{h}:{pad m}:{pad sec}" else $"{m}:{pad sec}.{tenths}"
