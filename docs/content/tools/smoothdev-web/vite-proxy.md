---
title: Vite proxy
---

<a id="vite-proxy"></a>

The tool picks a free port for the dev server, sets `SMOOTHDEV_WEB_SERVER_URL` to `http://127.0.0.1:<port>`, and starts Vite with that value in the environment.

Vite loads `server.proxy` through `resolveConfig`. At least one prefix has to target that URL. `localhost` and `127.0.0.1` on the same port count as that server. When some prefix already does, there is no warning.

If `server.proxy` is missing or empty, the tool says Vite has no `server.proxy` and names the dev server URL. If there are proxies and none of them is that server, it lists every prefix and target and says none is the dev server. Both messages link here.

A fixed `http://localhost:5000` target misses the server the same way a hard-coded `UseUrls` misses `ASPNETCORE_URLS`. The tool owns the port. Vite keeps the target the config resolved, so a page request through that prefix never reaches the process the tool started.

```js
// ❌ DON'T: a fixed port. The tool already chose one and put it in SMOOTHDEV_WEB_SERVER_URL.
export default defineConfig({
  server: { proxy: { "/api": "http://localhost:5000" } },
});
```

```js
// ✅ DO: proxy to the URL the tool passed. A fixed URL is only for a bare `vite`.
const server = process.env.SMOOTHDEV_WEB_SERVER_URL ?? "http://127.0.0.1:5000";

export default defineConfig({
  server: { proxy: { "/api": server } },
});
```
