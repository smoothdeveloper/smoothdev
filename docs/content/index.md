---
title: smoothdev
description: Tooling for smooth development
layout: splash
---

<section class="sd-hero">
<h1>Small, sharp tools<br><span class="sd-hero__gradient">for the daily loop</span></h1>
<p>Tooling for smooth development, in F# (and Rust where it fits). Each tool is small, does one job, and is meant to be used from the command line.</p>
<div class="sd-hero__actions">
<a class="sd-btn sd-btn--primary" href="/smoothdev/guide/introduction/">Get started →</a>
<a class="sd-btn sd-btn--secondary" href="/smoothdev/tools/smoothdev-web/">smoothdev-web</a>
<a class="sd-btn sd-btn--secondary" href="https://github.com/smoothdeveloper/smoothdev">GitHub</a>
</div>
<div class="sd-install"><code>smoothdev-web dev start</code></div>
</section>

## smoothdev-web

Run a Vite + Fable + .NET web app in dev and prod from one place.

<div class="sd-features">
<div class="sd-feature">
<div class="sd-feature__title">Ports are never a reason to fail</div>
<div class="sd-feature__text">The preferred port is probed and the next free one is used, then handed to the server and to Vite.</div>
</div>
<div class="sd-feature">
<div class="sd-feature__title">Process groups</div>
<div class="sd-feature__text">Every component is its own process group, recorded in a state file, so <code>stop</code> ends the whole tree.</div>
</div>
<div class="sd-feature">
<div class="sd-feature__title">One entry point per app</div>
<div class="sd-feature__text">Dev, prod, static preview and bundle, scriptable with <code>status --json</code>.</div>
</div>
<div class="sd-feature">
<div class="sd-feature__title">TUI and web GUI</div>
<div class="sd-feature__text">From the command line, a terminal UI, or a small web GUI it serves itself.</div>
</div>
</div>
