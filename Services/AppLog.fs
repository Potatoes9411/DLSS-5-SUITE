namespace DLSS_5_MANAGER.Services

open System
open System.IO

/// SUITE's own log, for testing and bug reports. Nothing here is shown to the
/// user; it only ever writes to %LOCALAPPDATA%\DLSS5Suite\logs\suite.log.
///
/// Logging must never be the thing that breaks the app, so every write is
/// wrapped and a failure to log is dropped.
module AppLog =

    /// About 2 MB of history, then the file is kept once as suite.old.log.
    let private maxBytes = 2L * 1024L * 1024L

    let private gate = obj ()

    let dataFolder () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS 5 SUITE")

    let logFolder () = Path.Combine(dataFolder (), "logs")
    let logPath () = Path.Combine(logFolder (), "suite.log")

    let private write (level: string) (message: string) =
        lock gate (fun () ->
            try
                Directory.CreateDirectory(logFolder ()) |> ignore
                let path = logPath ()
                if File.Exists(path) && FileInfo(path).Length > maxBytes then
                    File.Move(path, Path.Combine(logFolder (), "suite.old.log"), true)
                let line = sprintf "%s [%s] %s%s" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")) level message Environment.NewLine
                File.AppendAllText(path, line)
            with _ ->
                ())

    let info (message: string) = write "INFO" message
    let warn (message: string) = write "WARN" message

    /// An exception in full, stack trace included - the part a bug report needs.
    let error (context: string) (ex: exn) = write "ERROR" (context + ": " + ex.ToString())
