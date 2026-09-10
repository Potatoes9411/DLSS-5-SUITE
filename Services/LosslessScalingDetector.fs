namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Diagnostics
open System.Reflection.PortableExecutable
open System.Text.RegularExpressions
open Microsoft.Win32

/// Read-only discovery. Presence is never evidence of a working NR pipeline.
module LosslessScalingDetector =
    type Installation =
        { Executable: string
          Version: string
          Architecture: string
          ExistingComponents: string list
          Diagnostics: string list }

    type ScanResult =
        { Installations: Installation list
          Warnings: string list }

    let private pairs (text: string) =
        Regex.Matches(text, "\"([^\"]+)\"\\s+\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(1.0))
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value.Replace(@"\\", @"\"))
        |> Seq.toList

    /// Steam's modern path entries and legacy numbered path entries.
    let parseLibraryPaths text =
        pairs text
        |> List.choose (fun (key, value) ->
            let numbered = key |> Seq.forall Char.IsDigit
            if (key = "path" || numbered) && Path.IsPathFullyQualified(value) then Some value else None)
        |> List.distinctBy (fun p -> p.TrimEnd('\\', '/').ToUpperInvariant())

    /// Reject rooted paths and traversal rather than trusting manifest text.
    let tryInstallDirectory (library: string) text =
        let fields = pairs text
        match List.tryFind (fun (key, _) -> key = "appid") fields,
              List.tryFind (fun (key, _) -> key = "installdir") fields with
        | Some (_, "993090"), Some (_, name)
            when not (String.IsNullOrWhiteSpace(name))
                 && name <> "." && name <> ".."
                 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                 && not (name.Contains('\\') || name.Contains('/')) ->
            Some (Path.Combine(Path.GetFullPath(library), "steamapps", "common", name))
        | _ -> None

    let private readSmallFile path =
        if FileInfo(path).Length > 4L * 1024L * 1024L then
            invalidOp "Steam metadata exceeds the supported size."
        File.ReadAllText(path)

    let private inspectExecutable path =
        use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        use pe = new PEReader(stream)
        let header = pe.PEHeaders
        if isNull header.PEHeader || not (header.CoffHeader.Characteristics.HasFlag(Characteristics.ExecutableImage))
           || header.CoffHeader.Characteristics.HasFlag(Characteristics.Dll) then
            invalidOp "LosslessScaling.exe is not a valid Windows executable."
        match header.CoffHeader.Machine with
        | Machine.Amd64 -> "x64"
        | Machine.I386 -> "x86"
        | Machine.Arm64 -> "ARM64"
        | _ -> "Unknown"

    /// These are historical observations, never a current-session health verdict.
    let feederLogNotes (text: string) =
        [ if text.Contains("DLSS5_Feed.fx is not loaded", StringComparison.OrdinalIgnoreCase) then
              "Feeder reported DLSS5_Feed.fx was not loaded. Check shader search paths and shader compilation errors in ReShade."
          if text.Contains("-> none (not installed)", StringComparison.OrdinalIgnoreCase) then
              "Feeder reported no motion-vector provider. Verify the provider required by the selected Feeder release." ]

    let private diagnostics directory components =
        let notes = ResizeArray<string>()
        let has name = components |> List.exists (fun item -> String.Equals(item, name, StringComparison.OrdinalIgnoreCase))
        if has "dlss5-feed.addon64" && has "renodx-dlss.addon64" then
            notes.Add("Potential conflict: Feeder and renodx-dlss are both present. Verify which add-ons ReShade enables; renodx-dlss is not renodx-dlss5.")
        let log = Path.Combine(directory, "dlss5-feed.log")
        if File.Exists(log) then
            try
                use stream = File.Open(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                // Read a bounded tail, even when a long-running session produced a huge log.
                stream.Seek(max 0L (stream.Length - 131072L), SeekOrigin.Begin) |> ignore
                use reader = new StreamReader(stream)
                let history = feederLogNotes (reader.ReadToEnd())
                if not history.IsEmpty then
                    notes.Add(sprintf "Historical Feeder log (last written %s UTC):" (File.GetLastWriteTimeUtc(log).ToString("yyyy-MM-dd HH:mm")))
                    notes.AddRange(history)
            with ex -> notes.Add("Could not read Feeder log: " + ex.Message)
        List.ofSeq notes

    let scanRoots (roots: string list) =
        let warnings = ResizeArray<string>()
        let libraries = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for root in roots do
            try
                if Path.IsPathFullyQualified(root) && Directory.Exists(root) then
                    libraries.Add(Path.GetFullPath(root).TrimEnd('\\', '/')) |> ignore
                    let vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf")
                    if File.Exists(vdf) then
                        for library in parseLibraryPaths (readSmallFile vdf) do
                            libraries.Add(Path.GetFullPath(library).TrimEnd('\\', '/')) |> ignore
            with ex -> warnings.Add(sprintf "Could not inspect Steam library %s: %s" root ex.Message)

        let found = ResizeArray<Installation>()
        for library in libraries |> Seq.sort do
            try
                let manifest = Path.Combine(library, "steamapps", "appmanifest_993090.acf")
                if File.Exists(manifest) then
                    match tryInstallDirectory library (readSmallFile manifest) with
                    | None -> warnings.Add(sprintf "Invalid Lossless Scaling manifest: %s" manifest)
                    | Some directory ->
                        let executable = Path.Combine(directory, "LosslessScaling.exe")
                        if not (File.Exists(executable)) then
                            warnings.Add(sprintf "Steam lists Lossless Scaling, but the executable is missing: %s" executable)
                        else
                            let architecture = inspectExecutable executable
                            let version = FileVersionInfo.GetVersionInfo(executable).FileVersion
                            let components =
                                Directory.EnumerateFiles(directory)
                                |> Seq.map Path.GetFileName
                                |> Seq.filter (fun name ->
                                    let lower = name.ToLowerInvariant()
                                    lower.EndsWith(".addon64") || lower.EndsWith(".addon32")
                                    || lower.StartsWith("nvngx_")
                                    || List.contains lower [ "dxgi.dll"; "d3d11.dll"; "d3d12.dll"; "dinput8.dll"; "version.dll"; "reshade.ini"; "optiscaler.ini" ])
                                |> Seq.sort |> Seq.toList
                            found.Add
                                { Executable = executable
                                  Version = if String.IsNullOrWhiteSpace(version) then "Unknown" else version
                                  Architecture = architecture
                                  ExistingComponents = components
                                  Diagnostics = diagnostics directory components }
            with ex -> warnings.Add(sprintf "Could not inspect Lossless Scaling in %s: %s" library ex.Message)
        { Installations = List.ofSeq found; Warnings = List.ofSeq warnings }

    let discover () =
        let warnings = ResizeArray<string>()
        let registryRoots =
            [ Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"
              Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"
              Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath" ]
            |> List.choose (fun (hive, path, name) ->
                try
                    use key = hive.OpenSubKey(path)
                    if isNull key then None
                    else match key.GetValue(name) with :? string as value -> Some value | _ -> None
                with ex ->
                    warnings.Add(sprintf "Could not read Steam location: %s" ex.Message)
                    None)
        let fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        let result = scanRoots (fallback :: registryRoots)
        { result with Warnings = List.ofSeq warnings @ result.Warnings }

    let describe result =
        let installations =
            result.Installations
            |> List.map (fun item ->
                sprintf "Found Lossless Scaling %s (%s)\n%s\nExisting proxy/add-on/runtime files: %s\nNR status: not runtime-verified.\n%s"
                    item.Version item.Architecture item.Executable
                    (if item.ExistingComponents.IsEmpty then "none detected" else String.concat ", " item.ExistingComponents)
                    (String.concat "\n" item.Diagnostics))
        let status =
            if installations.IsEmpty then
                if result.Warnings.IsEmpty then "Lossless Scaling was not found in the Steam libraries checked."
                else "Lossless Scaling discovery is incomplete. See details below."
            else String.concat "\n\n" installations
        String.concat "\n\n" (status :: result.Warnings)
