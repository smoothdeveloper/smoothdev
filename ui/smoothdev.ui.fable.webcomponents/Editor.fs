/// <sd-editor .language=… .value=…>: a code editor host. A textarea over a coloured copy of the text (the
/// language's tokens as classes `t-<kind>`), a toolbar slot, the problems below (click one to go there) and
/// the hover text at the cursor. `sd-change` tells the text after each edit. The language is the owner's.
module smoothdev.ui.fable.webcomponents.Editor

open Fable.Core
open Fable.Core.JsInterop
open Browser.Types
open Firelight
open type Firelight.Lit
open smoothdev.ui

[<AttachMembers>]
type SdEditor() =
  inherit LitElement()

  static member properties =
    PropertyDeclarations.create [
      "value"   , PropertyDeclaration<string>()
      "language", PropertyDeclaration<Editor.Language>(attribute = !^false)
      "cursor"  , PropertyDeclaration<int>(state = true)
    ]

  static member styles =
    css $"""
:host {{ display: block; font: 13px/1.5 var(--sd-mono, monospace); color: var(--sd-ink, #ccc); }}
.bar {{ display: flex; gap: 6px; align-items: center; padding: 4px 0; font: 12px var(--sd-sans, sans-serif); color: var(--sd-muted, #888); }}
.bar .lang {{ margin-left: auto; }}
.area {{ position: relative; border: 1px solid var(--sd-line, #444); border-radius: 8px; background: var(--sd-bg, #111); min-height: 120px; }}
pre, textarea {{ margin: 0; padding: 10px 12px; font: inherit; white-space: pre-wrap; word-wrap: break-word; box-sizing: border-box; tab-size: 2; }}
pre {{ pointer-events: none; min-height: 120px; }}
textarea {{ position: absolute; inset: 0; width: 100%%; height: 100%%; resize: none; border: none; outline: none; background: transparent;
  color: transparent; caret-color: var(--sd-bright, #fff); overflow: hidden; }}
.t-keyword {{ color: var(--sd-accent-2, #bc8cff); }} .t-number {{ color: var(--sd-ok, #3fb950); }}
.t-string {{ color: var(--sd-warn, #d29922); }} .t-comment {{ color: var(--sd-muted, #888); font-style: italic; }}
.t-name {{ color: var(--sd-accent, #58a6ff); }}
ul {{ list-style: none; margin: 6px 0 0; padding: 0; font: 12px var(--sd-sans, sans-serif); }}
li {{ padding: 2px 4px; cursor: pointer; border-radius: 4px; }}
li:hover {{ background: var(--sd-panel-2, #222); }}
.error {{ color: var(--sd-bad, #f85149); }} .warning {{ color: var(--sd-warn, #d29922); }} .info {{ color: var(--sd-muted, #888); }}
.hover {{ min-height: 1.5em; font: 12px var(--sd-sans, sans-serif); color: var(--sd-muted, #888); }}
"""

  member val value = "" with get, set
  member val language: Editor.Language = Editor.plain with get, set
  member val cursor = 0 with get, set

  member this.textarea : HTMLTextAreaElement = emitJsExpr this "$0.renderRoot.querySelector('textarea')"

  member this.goTo (offset: int) =
    let ta = this.textarea
    if not (isNull ta) then
      ta.focus ()
      ta.setSelectionRange (offset, offset)
      this.cursor <- offset

  override this.render() =
    let text = this.value
    let lang = this.language
    let spans =
      [ for (t, kind) in Editor.spans text (lang.tokens text) ->
          if kind = "" then html $"""{t}""" else html $"""<span class={"t-" + kind}>{t}</span>""" ]
    let state: Editor.State = { text = text; cursor = this.cursor; problems = lang.problems text }
    let problems =
      [ for (line, p) in Editor.problemLines state ->
          let sev = Editor.severityName p.severity
          html $"""<li class={sev} @click={fun (_: Event) -> this.goTo p.range.start}>{sev} · line {string line}: {p.message}</li>""" ]
    let input (e: Event) =
      let ta = e.target :?> HTMLTextAreaElement
      this.value <- ta.value
      this.cursor <- int ta.selectionStart
      Events.emit this "sd-change" ta.value
    let moved (e: Event) = this.cursor <- int (e.target :?> HTMLTextAreaElement).selectionStart
    let line, col = Editor.lineCol text this.cursor
    let hover = lang.hover text this.cursor |> Option.defaultValue ""
    html $"""
<div class="bar"><slot name="toolbar"></slot><span class="lang">{lang.name} · {string line}:{string col}</span></div>
<div class="area"><pre aria-hidden="true">{spans}{"\n"}</pre>
<textarea spellcheck="false" .value={text} @input={input} @keyup={moved} @click={moved} aria-label="code"></textarea></div>
<div class="hover">{hover}</div>
<ul>{problems}</ul>"""
