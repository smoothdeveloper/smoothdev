/// <sd-scrubber duration=… position=…>: restart, step back, play or pause, step forward, loop, a track to
/// drag, and the time. While playing it advances with the display's frames; `sd-position` tells each change
/// (detail: position in seconds), `sd-play` each start and stop (detail: playing).
module smoothdev.ui.fable.webcomponents.Scrubber

open Fable.Core
open Fable.Core.JsInterop
open Browser
open Browser.Types
open Firelight
open type Firelight.Lit
open smoothdev.ui

[<AttachMembers>]
type SdScrubber() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "duration", PropertyDeclaration<float>()
      "position", PropertyDeclaration<float>()
      "playing" , PropertyDeclaration<bool>(reflect = true)
      "looping" , PropertyDeclaration<bool>(reflect = true)
      "step"    , PropertyDeclaration<float>()
      "rate"    , PropertyDeclaration<float>()
    ]

  static member styles =
    css $"""
:host {{ display: flex; align-items: center; gap: 6px; font: 12px var(--sd-mono, monospace); color: var(--sd-ink, #ccc); }}
button {{ font: 13px var(--sd-mono, monospace); min-width: 28px; height: 26px; color: var(--sd-ink, #ccc); background: var(--sd-panel-2, #222);
  border: 1px solid var(--sd-line, #444); border-radius: 6px; cursor: pointer; }}
button:hover {{ border-color: var(--sd-accent, #58a6ff); }}
button.on {{ color: var(--sd-bright, #fff); border-color: var(--sd-accent, #58a6ff); background: var(--sd-glow, transparent); }}
input[type=range] {{ flex: 1; accent-color: var(--sd-accent, #58a6ff); min-width: 80px; }}
.time {{ min-width: 110px; text-align: right; color: var(--sd-muted, #888); white-space: nowrap; }}
"""

  member val duration = 10.0 with get, set
  member val position = 0.0 with get, set
  member val playing = false with get, set
  member val looping = false with get, set
  member val step = 1.0 / 30.0 with get, set
  member val rate = 1.0 with get, set
  member val private frame = 0.0 with get, set
  member val private last = -1.0 with get, set

  member this.model : Scrubber.Scrubber =
    { position = this.position; duration = this.duration; playing = this.playing; looping = this.looping; rate = this.rate; step = this.step }

  member this.apply (msg: Scrubber.Msg) =
    let before = this.model
    let after = Scrubber.update msg before
    this.position <- after.position
    this.looping <- after.looping
    if after.position <> before.position then Events.emit this "sd-position" after.position
    if after.playing <> before.playing then
      this.playing <- after.playing
      Events.emit this "sd-play" after.playing
      if after.playing then this.loop () else this.stop ()

  member this.loop () =
    this.stop ()
    this.last <- -1.0
    let rec tick (now: float) =
      if this.playing then
        if this.last >= 0.0 then this.apply (Scrubber.Tick((now - this.last) / 1000.0))
        this.last <- now
        if this.playing then this.frame <- window.requestAnimationFrame tick
    this.frame <- window.requestAnimationFrame tick

  member this.stop () =
    if this.frame <> 0.0 then window.cancelAnimationFrame this.frame
    this.frame <- 0.0

  override this.connectedCallback() =
    base.connectedCallback ()
    if this.playing then this.loop ()

  override this.disconnectedCallback() =
    base.disconnectedCallback ()
    this.stop ()

  override this.render() =
    let m = this.model
    let send msg = fun (_: Event) -> this.apply msg
    let seek (e: Event) = this.apply (Scrubber.Seek(float (e.target :?> HTMLInputElement).value))
    let key (e: KeyboardEvent) =
      match e.key with
      | " " -> e.preventDefault (); this.apply Scrubber.Toggle
      | "ArrowLeft" -> e.preventDefault (); this.apply (Scrubber.Step -1)
      | "ArrowRight" -> e.preventDefault (); this.apply (Scrubber.Step 1)
      | "Home" -> e.preventDefault (); this.apply Scrubber.Restart
      | _ -> ()
    html $"""
<button title="restart (Home)" @click={send Scrubber.Restart}>⏮</button>
<button title="step back (←)" @click={send (Scrubber.Step -1)}>◀︎</button>
<button class={if m.playing then "on" else ""} title="play / pause (space)" @click={send Scrubber.Toggle}>{if m.playing then "❚❚" else "▶︎"}</button>
<button title="step forward (→)" @click={send (Scrubber.Step 1)}>▶︎|</button>
<button class={if m.looping then "on" else ""} title="loop" @click={send (Scrubber.SetLooping(not m.looping))}>⟲</button>
<input type="range" min="0" max={string m.duration} step="any" .value={string m.position} @input={seek} @keydown={key} aria-label="position">
<span class="time">{Scrubber.formatTime m.position} / {Scrubber.formatTime m.duration}</span>"""
