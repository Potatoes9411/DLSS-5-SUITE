namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks

/// Shared download path for the large compatibility packages. Downloads are
/// serialized, resumable, hash-verified and retried after a real network
/// stall. A completed verified cache file is reused without another transfer.
module VerifiedDownload =

    let private setupGate = new SemaphoreSlim(1, 1)

    let private sha256 path =
        use input = File.OpenRead(path)
        SHA256.HashData(input) |> Convert.ToHexString

    let private verified path digest =
        File.Exists(path)
        && String.Equals(sha256 path, digest, StringComparison.OrdinalIgnoreCase)

    let private mb (bytes: int64) = float bytes / 1048576.0

    /// Only one compatibility package mutates its cache/install tree at once.
    let runExclusive (progress: string -> float -> unit) (work: unit -> Task<'T>) = task {
        if setupGate.CurrentCount = 0 then
            progress "Waiting for the other compatibility setup to finish..." 1.0
        do! setupGate.WaitAsync()
        try return! work ()
        finally setupGate.Release() |> ignore
    }

    /// Download a GitHub release asset. A .part file survives interruptions;
    /// the next run resumes it with HTTP Range and still verifies the complete
    /// SHA-256 before the final filename becomes visible.
    let download
        (client: HttpClient)
        (name: string)
        (url: string)
        (digest: string)
        (expectedSize: int64)
        (destination: string)
        (progress: string -> float -> unit)
        (phaseStart: float)
        (phaseSize: float) = task {

        let uri = Uri(url)
        if uri.Scheme <> Uri.UriSchemeHttps || not (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) then
            failwith "Untrusted component download address"
        if digest.Length <> 64 then failwith "Invalid component digest"

        Directory.CreateDirectory(Path.GetDirectoryName(destination)) |> ignore
        if verified destination digest then
            progress (sprintf "Using verified cached %s (%.1f MB)" name (mb (FileInfo(destination).Length))) (phaseStart + phaseSize)
        else
            if File.Exists(destination) then File.Delete(destination)
            let partial = destination + ".part"
            if File.Exists(partial) && expectedSize > 0L && FileInfo(partial).Length > expectedSize then
                File.Delete(partial)
            if verified partial digest then
                File.Move(partial, destination, true)
                progress (sprintf "Using completed verified %s (%.1f MB)" name (mb (FileInfo(destination).Length))) (phaseStart + phaseSize)
            else
                let mutable complete = false
                let mutable attempt = 1
                let mutable lastError: exn option = None
                while not complete && attempt <= 3 do
                    try
                        let existing = if File.Exists(partial) then FileInfo(partial).Length else 0L
                        use request = new HttpRequestMessage(HttpMethod.Get, uri)
                        if existing > 0L then request.Headers.Range <- RangeHeaderValue(Nullable existing, Nullable())
                        use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                        let resumed = existing > 0L && response.StatusCode = HttpStatusCode.PartialContent
                        response.EnsureSuccessStatusCode() |> ignore
                        use! source = response.Content.ReadAsStreamAsync()
                        use target = new FileStream(partial, (if resumed then FileMode.Append else FileMode.Create), FileAccess.Write, FileShare.Read)
                        let total =
                            if expectedSize > 0L then expectedSize
                            else (if resumed then existing else 0L) + (response.Content.Headers.ContentLength |> Option.ofNullable |> Option.defaultValue 0L)
                        let mutable received = if resumed then existing else 0L
                        let buffer = Array.zeroCreate<byte> (256 * 1024)
                        let mutable reading = true
                        while reading do
                            // A connection that stops producing bytes is not
                            // allowed to leave the UI at "Downloading" forever.
                            use stall = new CancellationTokenSource(TimeSpan.FromSeconds(45.0))
                            let! count = source.ReadAsync(buffer.AsMemory(), stall.Token)
                            if count = 0 then reading <- false
                            else
                                do! target.WriteAsync(buffer.AsMemory(0, count))
                                received <- received + int64 count
                                let fraction = if total > 0L then min 1.0 (float received / float total) else 0.0
                                progress
                                    (sprintf "Downloading %s — %.1f / %.1f MB (%.0f%%)" name (mb received) (mb total) (fraction * 100.0))
                                    (phaseStart + fraction * phaseSize)
                        target.Flush(true)
                        if not (verified partial digest) then
                            File.Delete(partial)
                            failwithf "SHA-256 verification failed for %s" name
                        File.Move(partial, destination, true)
                        complete <- true
                    with ex ->
                        lastError <- Some ex
                        if attempt < 3 then
                            progress (sprintf "Download interrupted (%s). Retrying %s (%d/3)..." ex.Message name (attempt + 1)) phaseStart
                            do! Task.Delay(TimeSpan.FromSeconds(float (attempt * 2)))
                        attempt <- attempt + 1
                if not complete then
                    match lastError with
                    | Some ex -> return raise ex
                    | None -> return failwithf "Download failed for %s" name
    }
