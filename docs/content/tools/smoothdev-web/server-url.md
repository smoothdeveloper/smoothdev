---
title: ASP.NET server
---

<a id="server-url"></a>

The tool picks a free port, sets `ASPNETCORE_URLS` to `http://127.0.0.1:<port>`, and waits for that URL to answer.

If the program then binds somewhere else (`UseUrls`, `Listen`, a launch profile, or its own search for a free port), startup can finish while the URL the tool passed stays closed. The tool reports that, quotes the listen URL from the log, and links here. It keeps showing the port it passed. It does not switch to the one in the log, and Vite keeps proxying to the URL the tool passed, so the proxy misses the server too.

Leave the URL to the host. `WebApplication.CreateBuilder()` reads `ASPNETCORE_URLS`. Dev is started with `--no-launch-profile`, so `launchSettings.json` is not applied.

```fsharp
// ❌ DON'T: the process searches for a port and binds that, after the tool already chose one.
let bound = pick "127.0.0.1" 8766
builder.WebHost.UseUrls(url bound) |> ignore
```

```fsharp
// ✅ DO: the host keeps ASPNETCORE_URLS. A fixed URL is only for a bare `dotnet run`.
let builder = WebApplication.CreateBuilder()
let given = Environment.GetEnvironmentVariable "ASPNETCORE_URLS"
if String.IsNullOrWhiteSpace given then
  builder.WebHost.UseUrls "http://127.0.0.1:5000" |> ignore
```