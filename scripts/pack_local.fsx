// Packs the UI libraries (smoothdev.ui, smoothdev.ui.fable.webcomponents) into the local NuGet feed, until they are on nuget.org.
//
//   dotnet fsi scripts/pack_local.fsx
//
// Packs the committed HEAD (a fresh temporary clone, so work in progress is left alone) as
// 0.0.0.1-alpha.g<short sha>, with the F# sources under fable/ so Fable can compile it, into
// ~/.nuget/local-feed. Consumers list that feed in their nuget.config and pin the exact version: every
// pack is 0.0.0.1-alpha.<something>, and NuGet orders those labels alphabetically, not by date, so a
// floating or minimum version would pick an arbitrary commit.
open System
open System.Diagnostics
open System.IO

let source = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
let feed = Path.Combine(home, ".nuget", "local-feed")

let run (dir: string) (exe: string) (args: string) =
  let psi = ProcessStartInfo(exe, args, WorkingDirectory = dir, RedirectStandardOutput = true)
  use p = Process.Start psi
  let out = p.StandardOutput.ReadToEnd()
  p.WaitForExit()
  if p.ExitCode <> 0 then failwithf "%s %s failed (%d) in %s\n%s" exe args p.ExitCode dir out
  out.Trim()

let sha = run source "git" "rev-parse --short HEAD"
// the sha gets a "g" (as git describe writes it): a numeric part of a version may not start with 0
let version = $"0.0.0.1-alpha.g{sha}"
let work = Path.Combine(Path.GetTempPath(), $"smoothdev-pack-{sha}")
if Directory.Exists work then Directory.Delete(work, true)
run (Path.GetTempPath()) "git" $"clone -q \"{source}\" \"{work}\"" |> ignore
try
  run work "dotnet" "tool restore" |> ignore
  run work "dotnet" "paket restore" |> ignore
  Directory.CreateDirectory feed |> ignore
  for p in [ "smoothdev.ui"; "smoothdev.ui.fable.webcomponents" ] do
    run work "dotnet" $"pack ui/{p} -c Release -p:Version={version} -o \"{feed}\" --nologo -v q" |> ignore
    printfn "%s" (Path.Combine(feed, $"{p}.{version}.nupkg"))
finally
  Directory.Delete(work, true)
printfn "packed %s (smoothdev %s)" version sha
