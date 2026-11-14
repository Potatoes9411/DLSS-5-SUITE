namespace DLSS_5_MANAGER.Services

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Space Press: makes a game take less room on disk without changing how it
/// plays. Ported from NODIX TECH's Squish It (with his permission).
///
/// Windows does the actual work. `compact.exe /exe` marks each file as
/// backed by the Windows Overlay Filter (WOF), which stores it compressed and
/// unpacks it on the fly when the game reads it - nothing in the game folder
/// is rewritten, a Steam "verify files" still passes, and undoing it is just
/// asking Windows to store the file plainly again. On a hard drive a smaller
/// game usually loads faster; on a fast SSD the XPRESS algorithms cost almost
/// nothing to read back, and LZX trades some CPU for the most space.
///
/// Fixed against the original:
///   - its WOF check tested the wrong provider number, so a properly squished
///     file always read as plain "WOF" - the file provider is 2, not 1;
///   - unsquishing a folder with no record ran one compact.exe per ordinary
///     file; here a plain file is simply skipped;
///   - stop did not reach the fallback processes;
///   - the record listed files that were skipped, not squished;
///   - every core at High priority made anything else on the PC stutter.
/// And it refuses where the original did not: non-NTFS drives, a game that is
/// running, anti-cheat folders and drivers (.sys).
module StorageCompressor =

    type Algorithm =
        | Xpress4K
        | Xpress8K
        | Xpress16K
        | Lzx

    let algorithms = [ Xpress8K; Xpress4K; Xpress16K; Lzx ]

    let algorithmName =
        function
        | Xpress4K -> "XPRESS4K"
        | Xpress8K -> "XPRESS8K"
        | Xpress16K -> "XPRESS16K"
        | Lzx -> "LZX"

    let algorithmBlurb =
        function
        | Xpress8K -> "XPRESS 8K - balanced (recommended)"
        | Xpress4K -> "XPRESS 4K - fastest to read back"
        | Xpress16K -> "XPRESS 16K - smaller"
        | Lzx -> "LZX - smallest, heaviest on the CPU"

    type FolderStatus =
        | Plain
        | Partly
        | Squished of algorithm: string

    type FolderInfo =
        { /// What the files add up to.
          OriginalBytes: int64
          /// What they actually take on disk now.
          OnDiskBytes: int64
          /// A rough guess at what squishing would save, by file type.
          EstimatedSavingBytes: int64
          Files: int
          Status: FolderStatus }

    type Progress =
        { CurrentFile: string
          DoneFiles: int
          TotalFiles: int
          DoneBytes: int64
          TotalBytes: int64 }

        member this.Percent =
            if this.TotalBytes <= 0L then 0.0
            else Math.Clamp(float this.DoneBytes / float this.TotalBytes * 100.0, 0.0, 100.0)

    // ------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------
    module private Native =
        [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
        extern uint32 GetCompressedFileSizeW(string fileName, uint32& fileSizeHigh)

        [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
        extern uint32 GetFileAttributesW(string fileName)

        [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
        extern nativeint CreateFileW(string fileName, uint32 access, uint32 share, nativeint security, uint32 creation, uint32 flags, nativeint template)

        [<DllImport("kernel32.dll", SetLastError = true)>]
        extern bool DeviceIoControl(nativeint device, uint32 code, nativeint inBuffer, uint32 inSize, nativeint outBuffer, uint32 outSize, uint32& returned, nativeint overlapped)

        [<DllImport("kernel32.dll", SetLastError = true)>]
        extern bool CloseHandle(nativeint handle)

        [<DllImport("ntdll.dll")>]
        extern int NtSuspendProcess(nativeint processHandle)

        [<DllImport("ntdll.dll")>]
        extern int NtResumeProcess(nativeint processHandle)

    let private InvalidHandle = nativeint -1
    let private AttributeCompressed = 0x800u
    let private InvalidAttributes = 0xFFFFFFFFu
    let private FsctlGetExternalBacking = 0x90310u
    let private FsctlDeleteExternalBacking = 0x90314u
    let private FsctlSetCompression = 0x9C040u
    let private WofProviderFile = 2

    /// What a file takes on disk, compressed or not.
    let private onDisk (path: string) (length: int64) =
        let mutable high = 0u
        let low = Native.GetCompressedFileSizeW(path, &high)
        if low = 0xFFFFFFFFu && Marshal.GetLastWin32Error() <> 0 then length
        else (int64 high <<< 32) ||| int64 low

    let private openFile (path: string) (write: bool) =
        let access = if write then 0xC0000000u else 0x80000000u
        // FILE_FLAG_BACKUP_SEMANTICS lets the handle open read-only files too.
        Native.CreateFileW(path, access, 3u, 0n, 3u, 0x02000000u, 0n)

    /// "NTFS" for the old per-file compression, the algorithm for a WOF file,
    /// or None for a plain one.
    let probe (path: string) : string option =
        let attributes = Native.GetFileAttributesW(path)
        if attributes <> InvalidAttributes && attributes &&& AttributeCompressed <> 0u then
            Some "NTFS"
        else
            let handle = openFile path false
            if handle = InvalidHandle then None
            else
                let buffer = Marshal.AllocHGlobal(512)
                try
                    let mutable returned = 0u
                    if Native.DeviceIoControl(handle, FsctlGetExternalBacking, 0n, 0u, buffer, 512u, &returned, 0n) && returned >= 16u then
                        // WOF_EXTERNAL_INFO { Version; Provider } then
                        // FILE_PROVIDER_EXTERNAL_INFO_V1 { Version; Algorithm; Flags }.
                        if Marshal.ReadInt32(buffer, 4) = WofProviderFile then
                            match Marshal.ReadInt32(buffer, 12) with
                            | 0 -> Some "XPRESS4K"
                            | 1 -> Some "LZX"
                            | 2 -> Some "XPRESS8K"
                            | 3 -> Some "XPRESS16K"
                            | _ -> Some "WOF"
                        else Some "WOF"
                    else None
                finally
                    Marshal.FreeHGlobal(buffer)
                    Native.CloseHandle(handle) |> ignore

    /// Stores one file plainly again. True when it is plain afterwards.
    let private unsquishFile (path: string) =
        match probe path with
        | None -> true
        | Some kind ->
            let handle = openFile path true
            if handle = InvalidHandle then false
            else
                try
                    let mutable returned = 0u
                    if kind = "NTFS" then
                        let format = Marshal.AllocHGlobal(2)
                        try
                            Marshal.WriteInt16(format, 0s)
                            Native.DeviceIoControl(handle, FsctlSetCompression, format, 2u, 0n, 0u, &returned, 0n)
                        finally
                            Marshal.FreeHGlobal(format)
                    else
                        Native.DeviceIoControl(handle, FsctlDeleteExternalBacking, 0n, 0u, 0n, 0u, &returned, 0n)
                finally
                    Native.CloseHandle(handle) |> ignore

    // ------------------------------------------------------------------
    // What is left alone
    // ------------------------------------------------------------------
    let private extensionSet (items: string) =
        HashSet<string>(items.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)

    /// Saves and databases are rewritten constantly; drivers must stay plain.
    let private neverSquish = extensionSet ".sav .save .db .sqlite .sqlite3 .lock .sys"

    /// Already compressed by their own format - squishing gains nothing.
    let private mediaFiles =
        extensionSet ".mp4 .mkv .avi .webm .wmv .png .jpg .jpeg .webp .ogg .wav .mp3 .aac .flac .zip .7z .rar .gz .tar .opus .bik .bk2 .fev .fsb .bank .w3speech"

    /// Game archives, mostly compressed already, and shader caches the game rewrites.
    let private packageFiles =
        extensionSet ".rpf .pak .bundle .vpk .ba2 .pck .bnk .wem .cache .toc .dx12 .psocache .ushaderbytecode .pso .cpk .forge .bdt .bhd .arc .big .bsa .cas .cat .sb .ucas .utoc .unity3d .assetbundle"

    /// Folders an anti-cheat checks its own files in.
    let private antiCheatFolders =
        [ "easyanticheat"; "battleye"; "\\eac\\"; "vanguard"; "pnkbstr"; "punkbuster"; "gameguard"; "nprotect"; "xigncode"; "anticheat"; "anti-cheat" ]

    /// Places nobody should squish wholesale.
    let isProtected (folder: string) =
        let p = (try Path.GetFullPath(folder) with _ -> folder).TrimEnd('\\').ToLowerInvariant() + "\\"
        let root = (try Path.GetPathRoot(p) with _ -> "").ToLowerInvariant()
        p = root
        || p.StartsWith(root + "windows\\")
        || [ "program files\\"; "program files (x86)\\" ] |> List.exists (fun f -> p = root + f)
        || [ "\\system32\\"; "\\syswow64\\"; "\\programdata\\microsoft\\"; "\\$recycle.bin\\"; "\\system volume information\\"
             "\\windowsapps\\"; "\\nvidia corporation\\"; "\\drivers\\"; "\\driverstore\\" ]
           |> List.exists p.Contains

    /// The reason a folder cannot be squished right now, or None.
    let refusal (folder: string) : string option =
        if String.IsNullOrWhiteSpace(folder) || not (Directory.Exists(folder)) then
            Some "The game folder was not found."
        elif isProtected folder then
            Some "This is a Windows or system folder - Space Press leaves those alone."
        else
            let format = try DriveInfo(Path.GetPathRoot(folder)).DriveFormat with _ -> ""
            if not (format.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) then
                Some(sprintf "This drive is %s. Windows can only squish files on NTFS drives." (if format = "" then "not NTFS" else format))
            else
                let prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\"
                let running =
                    Process.GetProcesses()
                    |> Array.exists (fun p ->
                        try
                            let file = p.MainModule.FileName
                            not (isNull file) && file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        with _ -> false)
                if running then Some "The game is running - close it first." else None

    let private skipped (skipMedia: bool) (skipPackages: bool) (file: string) =
        let ext = Path.GetExtension(file)
        let lower = file.ToLowerInvariant()
        neverSquish.Contains(ext)
        || (skipMedia && mediaFiles.Contains(ext))
        || (skipPackages && packageFiles.Contains(ext))
        || antiCheatFolders |> List.exists lower.Contains

    /// Shannon entropy of a file's first 64 KB, in bits per byte. Above ~7.85
    /// the data is already packed and squishing it would only waste time.
    let private entropy (path: string) =
        try
            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            let buffer = Array.zeroCreate<byte> 65536
            let read = stream.Read(buffer, 0, buffer.Length)
            if read = 0 then 0.0
            else
                let counts = Array.zeroCreate<int> 256
                for i in 0 .. read - 1 do counts.[int buffer.[i]] <- counts.[int buffer.[i]] + 1
                counts
                |> Array.sumBy (fun c ->
                    if c = 0 then 0.0
                    else
                        let p = float c / float read
                        -p * Math.Log2(p))
        with _ -> 0.0

    let private allFiles (folder: string) =
        try
            Directory.GetFiles(folder, "*", EnumerationOptions(RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint))
        with _ -> [||]

    // ------------------------------------------------------------------
    // Measuring
    // ------------------------------------------------------------------
    let private savingFactor (ext: string) =
        if mediaFiles.Contains(ext) then 0.0
        else
            match ext.ToLowerInvariant() with
            | ".exe" | ".dll" | ".so" | ".bin" | ".dat" -> 0.45
            | ".json" | ".xml" | ".lua" | ".txt" | ".cfg" | ".ini" | ".yaml" | ".yml" | ".html" | ".js" -> 0.60
            | _ -> 0.25

    /// Sizes, status and an estimate, read from the file system's metadata.
    ///
    /// Nothing here opens a file but the one probe that names the method.
    /// Opening files is what costs: an antivirus scans each one as it is
    /// opened, and doing that to thousands of files in every game made the
    /// whole PC drag. Whether a file is squished shows in its size on disk,
    /// which the directory listing and one metadata call give without opening it.
    let analyse (folder: string) : FolderInfo =
        let files =
            try
                DirectoryInfo(folder).EnumerateFiles("*", EnumerationOptions(RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint))
                |> Array.ofSeq
            with _ -> [||]
        let mutable original = 0L
        let mutable disk = 0L
        let mutable estimate = 0.0
        let mutable found: string option = None
        let mutable detected = 0

        for file in files do
            try
                let length = file.Length
                let size = onDisk file.FullName length
                original <- original + length
                disk <- disk + size
                estimate <- estimate + float length * savingFactor file.Extension
                // Small files can sit inside the file table and read smaller
                // on disk without being squished; only count real savings.
                if length > 8192L && size < length then
                    detected <- detected + 1
                    if found.IsNone then
                        match probe file.FullName with
                        | Some kind when kind <> "WOF" -> found <- Some kind
                        | _ -> ()
            with _ -> ()

        let status =
            if detected > 0 && original > 1048576L && disk < original * 85L / 100L then
                Squished(defaultArg found "WOF")
            elif detected = 0 && (original <= 1048576L || disk >= original * 95L / 100L) then
                Plain
            else Partly

        { OriginalBytes = max original disk
          OnDiskBytes = disk
          EstimatedSavingBytes = int64 estimate
          Files = files.Length
          Status = status }

    // ------------------------------------------------------------------
    // Records: which files a squish touched, so unsquish knows
    // ------------------------------------------------------------------
    // Public on purpose: System.Text.Json writes a private F# record as "{}".
    type SpacePressRecord =
        { FolderPath: string
          Algorithm: string
          Timestamp: string
          Files: string[] }

    let private recordPath (folder: string) =
        let key = Path.GetFullPath(folder).TrimEnd('\\').ToLowerInvariant()
        let hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()
        Path.Combine(AppLog.dataFolder (), "SpacePress", hash + ".json")

    let private saveRecord (folder: string) (algorithm: Algorithm) (files: string[]) =
        try
            let path = recordPath folder
            Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
            let record =
                { FolderPath = folder
                  Algorithm = algorithmName algorithm
                  Timestamp = DateTime.UtcNow.ToString("o")
                  Files = files }
            File.WriteAllText(path, JsonSerializer.Serialize(record))
        with ex ->
            AppLog.error "Saving the Space Press record failed" ex

    let private loadRecord (folder: string) : string[] option =
        try
            let path = recordPath folder
            if File.Exists(path) then
                let record = JsonSerializer.Deserialize<SpacePressRecord>(File.ReadAllText(path))
                if isNull (box record) || isNull record.Files then None else Some record.Files
            else None
        with _ -> None

    let private dropRecord (folder: string) =
        try File.Delete(recordPath folder) with _ -> ()

    // ------------------------------------------------------------------
    // A job: one squish or unsquish, pausable and stoppable
    // ------------------------------------------------------------------
    type Job() =
        let gate = new ManualResetEventSlim(true)
        let processes = ConcurrentDictionary<int, Process>()
        let mutable cancelled = false
        let mutable paused = false

        member _.IsCancelled = cancelled
        member _.IsPaused = paused
        member internal _.Gate = gate
        member internal _.Processes = processes

        member _.Pause() =
            paused <- true
            gate.Reset()
            for p in processes.Values do
                try Native.NtSuspendProcess(p.Handle) |> ignore with _ -> ()

        member _.Resume() =
            paused <- false
            gate.Set()
            for p in processes.Values do
                try Native.NtResumeProcess(p.Handle) |> ignore with _ -> ()

        member _.Stop() =
            cancelled <- true
            gate.Set()
            for p in processes.Values do
                try
                    Native.NtResumeProcess(p.Handle) |> ignore
                    p.Kill(true)
                with _ -> ()
            processes.Clear()

    /// Leave a core free so the rest of the PC stays responsive.
    let private workers () = max 1 (Environment.ProcessorCount - 1)

    let private compactExe () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "compact.exe")

    /// Squishes every eligible file in a folder. Returns false when stopped.
    let squish (folder: string) (algorithm: Algorithm) (skipMedia: bool) (skipPackages: bool) (job: Job) (report: Progress -> unit) : bool =
        let files = allFiles folder |> Array.filter (fun f -> not (skipped skipMedia skipPackages f))
        let sizes = files |> Array.map (fun f -> try FileInfo(f).Length with _ -> 0L)
        let totalBytes = Array.sum sizes
        let total = files.Length
        let mutable doneBytes = 0L
        let mutable doneFiles = 0
        let mutable lastReport = 0L
        let squished = ConcurrentBag<string>()
        let lockObj = obj ()

        let advance (file: string) (bytes: int64) (count: int) =
            lock lockObj (fun () ->
                doneBytes <- doneBytes + bytes
                doneFiles <- doneFiles + count
                let now = Environment.TickCount64
                if now - lastReport >= 50L || doneFiles >= total then
                    lastReport <- now
                    report { CurrentFile = file; DoneFiles = doneFiles; TotalFiles = total; DoneBytes = doneBytes; TotalBytes = totalBytes })

        // What still needs doing: already squished files are done, and big
        // files that are already packed are not worth the time.
        let pending = ResizeArray<string * int64>()
        for i in 0 .. files.Length - 1 do
            let file, size = files.[i], sizes.[i]
            // Already squished shows in the size on disk - no need to open it.
            if size > 8192L && onDisk file size < size then
                squished.Add(file)
                advance file size 1
            elif size > 5242880L && entropy file > 7.85 then
                advance file size 1
            else
                pending.Add((file, size))

        // compact.exe takes many files at once; keep each command line short
        // and give big files a call of their own so progress keeps moving.
        let batches = ResizeArray<(string * int64) list>()
        let mutable current = []
        let mutable chars = 0
        for (file, size) in pending do
            if size > 52428800L then batches.Add([ file, size ])
            else
                if not current.IsEmpty && (chars + file.Length + 4 > 6000 || List.length current >= 50) then
                    batches.Add(List.rev current)
                    current <- []
                    chars <- 0
                current <- (file, size) :: current
                chars <- chars + file.Length + 4
        if not current.IsEmpty then batches.Add(List.rev current)

        let arg = algorithmName(algorithm).ToLowerInvariant()
        let options = ParallelOptions(MaxDegreeOfParallelism = workers ())
        Parallel.ForEach(batches, options, Action<(string * int64) list, ParallelLoopState>(fun batch state ->
            if job.IsCancelled then state.Stop()
            else
                job.Gate.Wait()
                if not job.IsCancelled then
                    let psi = ProcessStartInfo(compactExe ())
                    for a in [ "/c"; "/i"; "/q"; "/a"; "/exe:" + arg ] do psi.ArgumentList.Add(a)
                    for (file, _) in batch do psi.ArgumentList.Add(file)
                    psi.UseShellExecute <- false
                    psi.CreateNoWindow <- true
                    psi.RedirectStandardOutput <- true
                    try
                        use p = Process.Start(psi)
                        try p.PriorityClass <- ProcessPriorityClass.BelowNormal with _ -> ()
                        job.Processes.[p.Id] <- p
                        p.StandardOutput.ReadToEnd() |> ignore
                        p.WaitForExit()
                        job.Processes.TryRemove(p.Id) |> ignore
                    with ex ->
                        AppLog.error "Running compact.exe failed" ex
                    if not job.IsCancelled then
                        for (file, _) in batch do squished.Add(file)
                        advance (fst (List.last batch)) (batch |> List.sumBy snd) batch.Length))
        |> ignore

        // Record what was asked of Windows, so unsquish knows where to look,
        // even after a stop - those files may well be squished already.
        if not squished.IsEmpty then saveRecord folder algorithm (squished.ToArray())
        not job.IsCancelled

    /// Stores every squished file in a folder plainly again. Returns false
    /// when stopped.
    let unsquish (folder: string) (job: Job) (report: Progress -> unit) : bool =
        let files =
            match loadRecord folder with
            | Some recorded -> recorded |> Array.filter File.Exists
            | None -> allFiles folder
        let total = files.Length
        let mutable doneFiles = 0
        let mutable lastReport = 0L
        let lockObj = obj ()
        let options = ParallelOptions(MaxDegreeOfParallelism = workers ())
        Parallel.ForEach(files, options, Action<string, ParallelLoopState>(fun file state ->
            if job.IsCancelled then state.Stop()
            else
                job.Gate.Wait()
                if not job.IsCancelled then
                    unsquishFile file |> ignore
                    lock lockObj (fun () ->
                        doneFiles <- doneFiles + 1
                        let now = Environment.TickCount64
                        if now - lastReport >= 50L || doneFiles >= total then
                            lastReport <- now
                            report { CurrentFile = file; DoneFiles = doneFiles; TotalFiles = total; DoneBytes = int64 doneFiles; TotalBytes = int64 (max total 1) })))
        |> ignore
        if not job.IsCancelled then dropRecord folder
        not job.IsCancelled

    /// "12.4 GB" and the like, in powers of 1024.
    let formatBytes (bytes: int64) =
        let b = float (max bytes 0L)
        if b >= 1099511627776.0 then sprintf "%.2f TB" (b / 1099511627776.0)
        elif b >= 1073741824.0 then sprintf "%.1f GB" (b / 1073741824.0)
        elif b >= 1048576.0 then sprintf "%.0f MB" (b / 1048576.0)
        elif b >= 1024.0 then sprintf "%.0f KB" (b / 1024.0)
        else sprintf "%.0f B" b
