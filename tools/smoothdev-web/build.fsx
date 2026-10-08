// Build entry for smoothdev.web (Partas.Build). From this folder:
//   dotnet fsi build.fsx -- --help
//   dotnet fsi build.fsx -- check      tools + paket restore, build, tests
//   dotnet fsi build.fsx -- test       tests only
//   dotnet fsi build.fsx -- pack       the dotnet tool package, into artifacts/
//   dotnet fsi build.fsx -- install    pack, then install or replace the global tool
//   dotnet fsi build.fsx -- watch      dotnet watch run -- scan gui
#r "nuget: Partas.Build, 0.6.5"

open System
open System.IO
open Partas.Build

let here = __SOURCE_DIRECTORY__
let repo = Path.GetFullPath(Path.Combine(here, "..", ".."))
/// The shell directory the script was started from. `dotnet run` would otherwise
/// use the project folder, so `scan gui` would not see this tree.
let launchDir = Directory.GetCurrentDirectory()

module Stages =
  let restore =
    stage "restore" {
      workingDir repo
      run "dotnet tool restore"
      run "dotnet paket restore"
    }

  let build =
    stage "build" {
      workingDir here
      run "dotnet build src/SmoothDev.Web -c Release --nologo"
    }

  let test =
    stage "test" {
      workingDir here
      run "dotnet run --project tests/SmoothDev.Web.Tests -c Release -- --summary"
    }

  let pack =
    stage "pack" {
      workingDir here
      run "dotnet pack src/SmoothDev.Web -c Release --nologo"
    }

  /// Same version can be reinstalled: `dotnet tool update` refuses a package it already has.
  /// `run` starts the program directly, so the shell has to be the program. `||` is the same
  /// in sh and in cmd: a missing tool is not a failed install.
  let uninstall =
    let drop = "dotnet tool uninstall --global smoothdev.web"

    if OperatingSystem.IsWindows() then
      $"cmd /c \"{drop} || ver > nul\""
    else
      $"sh -c \"{drop} || true\""

  /// Windows locks a running program's files, which fails the uninstall: stop running instances first.
  let release =
    if OperatingSystem.IsWindows() then
      "powershell -NoProfile -ExecutionPolicy Bypass -File release-running.ps1"
    else
      // POSIX replaces a running program's files without complaint; a stage cannot skip a `run`
      // conditionally, so this is a no-op placeholder.
      "sh -c true"

  let install =
    stage "install" {
      workingDir here
      run release
      run uninstall
      run "dotnet tool install --global smoothdev.web --add-source ./artifacts --version 0.1.0"
    }

  let watch =
    // `dotnet watch` treats `-p` as `--project`, so the MSBuild property has to be `--property`.
    // No `--project`: the stage directory is the project, which is what watch expects.
    let root = if launchDir.Contains ' ' then "\"" + launchDir + "\"" else launchDir
    stage "watch" {
      workingDir (Path.Combine(here, "src", "SmoothDev.Web"))
      run
        $"dotnet watch --non-interactive --property:RunWorkingDirectory={root} run -- scan gui"
    }

/// `dotnet fsi` appends --preferreduilang:<culture> to the script's arguments on some setups,
/// which the command parser rejects as an unknown option.
let scriptArgs =
  Args.script ()
  |> Array.filter (fun a -> not (a.StartsWith "--preferreduilang"))

do
  exit (
    rootCommand (scriptArgs) {
      description "smoothdev.web build"

      command "check" {
        description "Restore, build and tests"
        Stages.restore
        Stages.build
        Stages.test
      }

      command "test" {
        description "Run the Expecto tests"
        Stages.test
      }

      command "pack" {
        description "Pack the dotnet tool into artifacts/"
        Stages.restore
        Stages.pack
      }

      command "install" {
        description "Pack and install the global tool (replaces the current one)"
        Stages.restore
        Stages.pack
        Stages.install
      }

      command "watch" {
        description "dotnet watch run -- scan gui, scanning the folder the script was started from"
        Stages.watch
      }
    }
  )
