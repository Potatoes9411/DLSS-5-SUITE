namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text.Json

/// Where and how big the main window was when SUITE last closed, so it opens
/// there again. A placement that would leave the window off every screen is
/// dropped and the window centres as before.
module WindowPlacement =

    [<CLIMutable>]
    type Saved =
        { X: int
          Y: int
          Width: float
          Height: float
          Maximized: bool }

    /// The window's own minimum size; anything smaller is a broken record.
    let minWidth = 900.0
    let minHeight = 600.0

    let private realPath () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite", "window.json")

    let isUsable (s: Saved) =
        s.Width >= minWidth && s.Height >= minHeight
        && not (Double.IsNaN s.Width) && not (Double.IsNaN s.Height)
        && s.Width < 20000.0 && s.Height < 20000.0

    let loadFrom (path: string) : Saved option =
        try
            if File.Exists(path) then
                let s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))
                if not (isNull (box s)) && isUsable s then Some s else None
            else
                None
        with _ ->
            None

    let saveTo (path: string) (s: Saved) =
        try
            if isUsable s then
                Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
                File.WriteAllText(path, JsonSerializer.Serialize(s))
        with _ ->
            ()

    let load () = loadFrom (realPath ())
    let save (s: Saved) = saveTo (realPath ()) s

    /// Whether enough of the window's title strip (the top 40 px, at least
    /// 120 px of it across) lies inside one of the screens' working areas
    /// (x, y, width, height) to grab and move it.
    let isReachable (screens: (int * int * int * int) list) (s: Saved) =
        let left, top, right = s.X, s.Y, s.X + int s.Width

        screens
        |> List.exists (fun (sx, sy, sw, sh) ->
            let overlap = (min right (sx + sw)) - (max left sx)
            overlap >= 120 && top >= sy - 10 && top + 40 <= sy + sh)
