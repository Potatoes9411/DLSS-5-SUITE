namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Runtime.InteropServices

/// The click each panel of the edition menu makes as it unfolds - the snap a
/// concertina photo wallet makes when it drops open, one per panel.
///
/// The clicks are made here rather than shipped as a recording: a few
/// milliseconds of noise for the snap over a short low tone for the body,
/// each one a little higher than the last so the ladder climbs as it opens.
/// winmm plays them from memory and returns at once, so nothing touches the
/// disk and nothing waits on the UI thread.
module MenuSound =

    module private Native =
        [<DllImport("winmm.dll")>]
        extern bool PlaySound(nativeint sound, nativeint hmod, uint32 flags)

    [<Literal>]
    let private SndAsync = 0x0001u

    [<Literal>]
    let private SndNoDefault = 0x0002u

    [<Literal>]
    let private SndMemory = 0x0004u

    let private flagPath () = Path.Combine(AppLog.dataFolder (), "menu-sounds-off")

    let isEnabled () = not (File.Exists(flagPath ()))

    let setEnabled (on: bool) =
        try
            Directory.CreateDirectory(AppLog.dataFolder ()) |> ignore
            if on then
                if File.Exists(flagPath ()) then File.Delete(flagPath ())
            else
                File.WriteAllText(flagPath (), "")
        with ex ->
            AppLog.error "Changing the menu sound setting failed" ex

    [<Literal>]
    let private Rate = 44100

    /// One click as a complete 16-bit mono WAV file.
    let private click (step: int) : byte[] =
        let samples = Rate * 42 / 1000
        let rng = Random(9411 + step)
        let body = 165.0 * Math.Pow(1.07, float step)
        let ring = 2400.0 * Math.Pow(1.05, float step)
        use ms = new MemoryStream()
        use w = new BinaryWriter(ms)
        w.Write("RIFF"B)
        w.Write(36 + samples * 2)
        w.Write("WAVEfmt "B)
        w.Write(16)
        w.Write(1s)
        w.Write(1s)
        w.Write(Rate)
        w.Write(Rate * 2)
        w.Write(2s)
        w.Write(16s)
        w.Write("data"B)
        w.Write(samples * 2)
        for i in 0 .. samples - 1 do
            let t = float i / float Rate
            let snap = (rng.NextDouble() * 2.0 - 1.0) * exp (-t / 0.0032) * 0.55
            let tick = sin (2.0 * Math.PI * ring * t) * exp (-t / 0.005) * 0.22
            let thud = sin (2.0 * Math.PI * body * t) * exp (-t / 0.016) * 0.5
            // A few samples of fade-in so the very first one is not a pop.
            let attack = min 1.0 (float i / 12.0)
            let v = (snap + tick + thud) * attack * 0.42
            w.Write(int16 (Math.Clamp(v, -1.0, 1.0) * 32767.0))
        w.Flush()
        ms.ToArray()

    // winmm reads an asynchronous sound from memory while it plays, so each
    // buffer is pinned for the life of the app - eight small clicks, made once.
    let private clicks =
        lazy
            [| for step in 0..7 ->
                   let bytes = click step
                   GCHandle.Alloc(bytes, GCHandleType.Pinned) |]

    /// Plays the click for panel `step` (0 is the first), if sounds are on.
    let play (step: int) =
        if isEnabled () then
            try
                let handles = clicks.Force()
                let h = handles.[Math.Clamp(step, 0, handles.Length - 1)]
                Native.PlaySound(h.AddrOfPinnedObject(), 0n, SndAsync ||| SndNoDefault ||| SndMemory) |> ignore
            with _ ->
                ()
