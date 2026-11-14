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

        [<DllImport("ntdll.dll")>]
        extern int NtSetInformationProcess(nativeint processHandle, int infoClass, int& info, int length)

        [<DllImport("kernel32.dll")>]
        extern nativeint GetCurrentThread()

        [<DllImport("kernel32.dll")>]
        extern bool SetThreadPriority(nativeint thread, int priority)

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

    /// The reason a folder cannot be squished, or None. Whether the game is
    /// running is asked by name by the caller - looking inside every process
    /// on the PC to find out was slow enough to freeze the window.
    let refusal (folder: string) : string option =
        if String.IsNullOrWhiteSpace(folder) || not (Directory.Exists(folder)) then
            Some "The game folder was not found."
        elif isProtected folder then
            Some "This is a Windows or system folder - Space Press leaves those alone."
        else
            let format = try DriveInfo(Path.GetPathRoot(folder)).DriveFormat with _ -> ""
            if not (format.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) then
                Some(sprintf "This drive is %s. Windows can only squish files on NTFS drives." (if format = "" then "not NTFS" else format))
            else None

    /// Is a program with this name running? Names only - cheap.
    let isRunning (processName: string) =
        if String.IsNullOrWhiteSpace(processName) then false
        else
            let found = Process.GetProcessesByName(processName)
            let any = found.Length > 0
            for p in found do p.Dispose()
            any

    let private skipped (skipMedia: bool) (skipPackages: bool) (file: string) =
        let ext = Path.GetExtension(file)
        let lower = file.ToLowerInvariant()
        neverSquish.Contains(ext)
        || (skipMedia && mediaFiles.Contains(ext))
        || (skipPackages && packageFiles.Contains(ext))
        || antiCheatFolders |> List.exists lower.Contains

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

    /// True when Space Press squished this folder (and has not undone it).
    let hasRecord (folder: string) = try File.Exists(recordPath folder) with _ -> false

    let private dropRecord (folder: string) =
        try File.Delete(recordPath folder) with _ -> ()

    // ------------------------------------------------------------------
    // Speed
    // ------------------------------------------------------------------
    /// How hard a squish may push the PC.
    type Speed =
        /// Idle priority, a couple at once - you will not notice it.
        | Gentle
        | Balanced
        /// As fast as the drive and CPU allow; the PC will feel busy.
        | FullSpeed
        /// Gentle while you use the PC, full speed once you step away.
        | Auto

    let speeds = [ Auto; Gentle; Balanced; FullSpeed ]

    let speedName =
        function
        | Auto -> "AUTO"
        | Gentle -> "GENTLE"
        | Balanced -> "BALANCED"
        | FullSpeed -> "FULL"

    let speedBlurb =
        function
        | Auto -> "Auto - full speed when you step away"
        | Gentle -> "Gentle - you won't notice it"
        | Balanced -> "Balanced"
        | FullSpeed -> "Full speed - fastest, the PC feels busy"

    /// What a speed means at this moment.
    type Pace =
        { Workers: int
          Priority: ProcessPriorityClass
          /// ProcessIoPriority: 0 very low, 1 low, 2 normal.
          IoPriority: int
          /// Run the folder walks in Windows' background mode.
          Background: bool
          Label: string }

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private LastInput = { mutable Size: uint32; mutable Time: uint32 }

    [<DllImport("user32.dll")>]
    extern bool private GetLastInputInfo(LastInput& info)

    /// Seconds since the last keyboard or mouse input anywhere on the PC.
    let idleSeconds () =
        try
            let mutable info = { Size = 8u; Time = 0u }
            if GetLastInputInfo(&info) then float (uint32 Environment.TickCount - info.Time) / 1000.0 else 0.0
        with _ -> 0.0

    /// How long without input before Auto counts you as away.
    let awayAfterSeconds = 120.0

    let rec paceOf (speed: Speed) : Pace =
        let cores = Environment.ProcessorCount
        let gentle = { Workers = 2; Priority = ProcessPriorityClass.Idle; IoPriority = 0; Background = true; Label = "GENTLE" }
        let full = { Workers = Math.Clamp(cores - 2, 2, 16); Priority = ProcessPriorityClass.Normal; IoPriority = 2; Background = false; Label = "FULL SPEED" }
        match speed with
        | Gentle -> gentle
        | Balanced -> { Workers = Math.Clamp(cores / 4, 2, 6); Priority = ProcessPriorityClass.BelowNormal; IoPriority = 1; Background = false; Label = "BALANCED" }
        | FullSpeed -> full
        | Auto ->
            // Balanced while you are at the PC rather than Gentle: Gentle
            // was so careful that a big game took hours. Balanced leaves
            // most of the CPU and the drive's priority to whatever you do.
            if idleSeconds () >= awayAfterSeconds then { full with Label = "FULL SPEED WHILE YOU'RE AWAY" }
            else { paceOf Balanced with Label = "BALANCED WHILE YOU USE THE PC" }

    // ------------------------------------------------------------------
    // A job: one squish or unsquish, pausable and stoppable
    // ------------------------------------------------------------------
    type Job() =
        let gate = new ManualResetEventSlim(true)
        let processes = ConcurrentDictionary<int, Process>()
        let mutable cancelled = false
        let mutable paused = false
        let slotLock = obj ()
        let mutable running = 0

        member _.IsCancelled = cancelled
        member _.IsPaused = paused
        member internal _.Gate = gate
        member internal _.Processes = processes

        /// Changeable while the job runs - it applies from the next batch.
        member val Speed = Auto with get, set

        /// Waits for a free slot under the current pace's limit. Re-checked
        /// twice a second, so Auto speeds up when you step away and backs
        /// off as soon as you are back.
        member internal this.Acquire() =
            lock slotLock (fun () ->
                while not cancelled && running >= (paceOf this.Speed).Workers do
                    Monitor.Wait(slotLock, 500) |> ignore
                running <- running + 1)

        member internal _.Release() =
            lock slotLock (fun () ->
                running <- running - 1
                Monitor.PulseAll(slotLock))

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

    let private compactExe () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "compact.exe")

    /// Sets a compact.exe's CPU and disk priority for the pace in force.
    let private applyPace (pace: Pace) (p: Process) =
        try p.PriorityClass <- pace.Priority with _ -> ()
        try
            let mutable io = pace.IoPriority
            Native.NtSetInformationProcess(p.Handle, 33, &io, 4) |> ignore
        with _ -> ()

    /// Runs work on the calling thread in Windows' background mode: lowest
    /// CPU, disk and memory priority. For the long walks over game folders.
    let inBackground (work: unit -> 'T) : 'T =
        let thread = Native.GetCurrentThread()
        let began = Native.SetThreadPriority(thread, 0x00010000)
        try work ()
        finally
            if began then Native.SetThreadPriority(thread, 0x00020000) |> ignore

    /// Squishes every eligible file in a folder. Returns false when stopped.
    ///
    /// Nothing here opens a file - compact.exe reads each one once and that is
    /// all. An antivirus scans every file a program opens, so a check that
    /// opened them first doubled the work and made the whole PC drag.
    let squish (folder: string) (algorithm: Algorithm) (skipMedia: bool) (skipPackages: bool) (job: Job) (report: Progress -> unit) : bool =
        // Listing: sizes come with the directory listing itself - asking for
        // each file's size separately was a call per file, and an engine
        // install has hundreds of thousands. Counted out loud as it goes.
        // The walk follows the speed as it changes: in Windows' background
        // mode while Gentle (or Auto with you at the PC), at normal priority
        // otherwise - re-checked a few times a second.
        let thread = Native.GetCurrentThread()
        let mutable backgroundMode = false
        let followPace () =
            let want = (paceOf job.Speed).Background
            if want <> backgroundMode then
                Native.SetThreadPriority(thread, (if want then 0x00010000 else 0x00020000)) |> ignore
                backgroundMode <- want
        followPace ()
        let listed = ResizeArray<string * int64>()
        let mutable lastListed = Environment.TickCount64
        try
            for info in DirectoryInfo(folder).EnumerateFiles("*", EnumerationOptions(RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint)) do
                if not job.IsCancelled && not (skipped skipMedia skipPackages info.FullName) then
                    listed.Add((info.FullName, info.Length))
                    if Environment.TickCount64 - lastListed > 250L then
                        lastListed <- Environment.TickCount64
                        followPace ()
                        report { CurrentFile = sprintf "Listing files... %s found" (listed.Count.ToString("N0")); DoneFiles = 0; TotalFiles = 0; DoneBytes = 0L; TotalBytes = 0L }
        with _ -> ()
        let files = listed |> Seq.map fst |> Array.ofSeq
        let sizes = listed |> Seq.map snd |> Array.ofSeq
        let totalBytes = Array.sum sizes
        let total = files.Length
        let mutable doneBytes = 0L
        let mutable doneFiles = 0
        let mutable lastReport = 0L
        let squished = ConcurrentBag<string>()
        let lockObj = obj ()

        let send (file: string) =
            report { CurrentFile = file; DoneFiles = doneFiles; TotalFiles = total; DoneBytes = doneBytes; TotalBytes = totalBytes }

        let advance (file: string) (bytes: int64) (count: int) =
            lock lockObj (fun () ->
                doneBytes <- doneBytes + bytes
                doneFiles <- doneFiles + count
                let now = Environment.TickCount64
                if now - lastReport >= 100L || doneFiles >= total then
                    lastReport <- now
                    send file)

        // Already squished files are done; that shows in their size on disk.
        // One metadata call per file, spread over the pace's workers - an
        // engine install is a quarter of a million of them.
        let already = Array.zeroCreate<bool> files.Length
        let checkedCount = ref 0
        let mutable lastChecked = Environment.TickCount64
        Parallel.For(0, files.Length, ParallelOptions(MaxDegreeOfParallelism = (paceOf job.Speed).Workers), fun i (state: ParallelLoopState) ->
            if job.IsCancelled then state.Stop()
            else
                already.[i] <- sizes.[i] > 8192L && onDisk files.[i] sizes.[i] < sizes.[i]
                let n = Interlocked.Increment(&checkedCount.contents)
                if n % 2000 = 0 then
                    lock lockObj (fun () ->
                        if Environment.TickCount64 - lastChecked > 250L then
                            lastChecked <- Environment.TickCount64
                            report { CurrentFile = sprintf "Checking files... %s of %s" (n.ToString("N0")) (total.ToString("N0"))
                                     DoneFiles = doneFiles; TotalFiles = total; DoneBytes = doneBytes; TotalBytes = totalBytes }))
        |> ignore
        followPace ()
        let pending = ResizeArray<string * int64>()
        for i in 0 .. files.Length - 1 do
            if already.[i] then
                squished.Add(files.[i])
                advance files.[i] sizes.[i] 1
            else
                pending.Add((files.[i], sizes.[i]))

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
        // As many threads as Full speed could use; each batch then waits for
        // a slot under whatever the speed allows at that moment.
        let options = ParallelOptions(MaxDegreeOfParallelism = (paceOf FullSpeed).Workers)
        Parallel.ForEach(batches, options, Action<(string * int64) list, ParallelLoopState>(fun batch state ->
            if job.IsCancelled then state.Stop()
            else
                job.Gate.Wait()
                job.Acquire()
                try
                  if not job.IsCancelled then
                    // Say what is being worked on before it starts, so a single
                    // multi-gigabyte file does not look like nothing is happening.
                    lock lockObj (fun () -> send (fst (List.head batch)))
                    let psi = ProcessStartInfo(compactExe ())
                    for a in [ "/c"; "/i"; "/q"; "/a"; "/exe:" + arg ] do psi.ArgumentList.Add(a)
                    for (file, _) in batch do psi.ArgumentList.Add(file)
                    psi.UseShellExecute <- false
                    psi.CreateNoWindow <- true
                    psi.RedirectStandardOutput <- true
                    try
                        use p = Process.Start(psi)
                        applyPace (paceOf job.Speed) p
                        job.Processes.[p.Id] <- p
                        p.StandardOutput.ReadToEnd() |> ignore
                        p.WaitForExit()
                        job.Processes.TryRemove(p.Id) |> ignore
                    with ex ->
                        AppLog.error "Running compact.exe failed" ex
                    if not job.IsCancelled then
                        for (file, _) in batch do squished.Add(file)
                        advance (fst (List.last batch)) (batch |> List.sumBy snd) batch.Length
                finally
                    job.Release()))
        |> ignore

        // The thread goes back to the pool as it came.
        if backgroundMode then Native.SetThreadPriority(thread, 0x00020000) |> ignore

        // Record a finished squish, so unsquish knows where to look and the
        // hourly check knows to keep it squished. A stopped one records
        // nothing: the user chose to stop, and unsquish finds squished files
        // without a record anyway.
        if not squished.IsEmpty && not job.IsCancelled then saveRecord folder algorithm (squished.ToArray())
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
        let options = ParallelOptions(MaxDegreeOfParallelism = (paceOf FullSpeed).Workers)
        Parallel.ForEach(files, options, Action<string, ParallelLoopState>(fun file state ->
            if job.IsCancelled then state.Stop()
            else
                job.Gate.Wait()
                job.Acquire()
                try
                  if not job.IsCancelled then
                    unsquishFile file |> ignore
                    lock lockObj (fun () ->
                        doneFiles <- doneFiles + 1
                        let now = Environment.TickCount64
                        if now - lastReport >= 50L || doneFiles >= total then
                            lastReport <- now
                            report { CurrentFile = file; DoneFiles = doneFiles; TotalFiles = total; DoneBytes = int64 doneFiles; TotalBytes = int64 (max total 1) })
                finally
                    job.Release()))
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
