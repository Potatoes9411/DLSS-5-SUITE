namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Security.Cryptography

module ShaderGlassDetector =

    [<Literal>]
    let OfficialDownloadUrl = "https://github.com/mausimus/ShaderGlass/releases/tag/v1.3.0"

    [<Literal>]
    let OfficialExecutableSha256 = "0C3606F48D8296106B07E179E76B35C587EF6130662611C15E5A737E1E04C2C6"

    type Status =
        | Missing
        | Verified of executablePath: string
        | Unverified of executablePath: string * sha256: string

    let private sha256 path =
        use stream = File.OpenRead(path)
        use hasher = SHA256.Create()
        hasher.ComputeHash(stream) |> Convert.ToHexString

    let private candidateDirectories () =
        let user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        let app = AppContext.BaseDirectory
        [ Path.Combine(app, "ShaderGlass")
          Path.Combine(user, "Downloads", "ShaderGlass-1.3.0-win-x64")
          Path.Combine(user, "Desktop", "ShaderGlass-1.3.0-win-x64")
          Path.Combine(user, "Documents", "ShaderGlass-1.3.0-win-x64") ]
        |> List.distinct

    let discover () =
        candidateDirectories ()
        |> Seq.map (fun folder -> Path.Combine(folder, "ShaderGlass.exe"))
        |> Seq.tryFind File.Exists
        |> function
            | None -> Missing
            | Some executable ->
                try
                    let hash = sha256 executable
                    if String.Equals(hash, OfficialExecutableSha256, StringComparison.OrdinalIgnoreCase) then
                        Verified executable
                    else
                        Unverified(executable, hash)
                with _ -> Unverified(executable, "hash unavailable")

    let describe = function
        | Missing ->
            "ShaderGlass was not found. Download v1.3.0 from the official project, extract it without running unknown helper executables, then check again."
        | Verified executable ->
            sprintf "Verified official ShaderGlass v1.3.0.\n%s\nSafe to launch; no MV 2.7 or eurotrucks2.exe is used." executable
        | Unverified(executable, hash) ->
            sprintf "ShaderGlass is blocked because its executable does not match the verified official v1.3.0 build.\n%s\nSHA-256: %s" executable hash

