namespace DLSS_5_MANAGER.Services

open System
open System.IO
open System.Text.Json

/// A picture of the user's own behind the app, in place of the theme's space
/// plate. The picked file is copied under SUITE's own LocalAppData folder, so
/// moving or deleting the original never blanks the window, and Reset to
/// default deletes only that copy.
module BackgroundStore =

    [<CLIMutable>]
    type BackgroundSettings =
        { UseCustom: bool
          /// The copy under Backgrounds\, never the user's original.
          CustomFile: string
          /// Average luminance of the picture (0-255), measured once on import.
          CustomLuminance: float
          /// How bright the picture is allowed to show, 15-100. Kept low by
          /// default so a bright picture never tires the eyes.
          Brightness: int }

    let defaultBrightness = 25

    let defaults =
        { UseCustom = false
          CustomFile = ""
          CustomLuminance = 0.0
          Brightness = defaultBrightness }

    let clampBrightness (b: int) =
        if b <= 0 then defaultBrightness else max 15 (min 100 b)

    let private root () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Suite")

    let realBackgroundsDir () = Path.Combine(root (), "Backgrounds")
    let private settingsPath () = Path.Combine(root (), "background.json")

    /// Image types the picker offers and Avalonia can decode.
    let imageExtensions = [| ".png"; ".jpg"; ".jpeg"; ".bmp"; ".webp" |]

    let isImageFile (path: string) =
        not (String.IsNullOrWhiteSpace(path))
        && imageExtensions |> Array.contains (Path.GetExtension(path).ToLowerInvariant())

    let loadFrom (path: string) : BackgroundSettings =
        try
            if File.Exists(path) then
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true
                let s = JsonSerializer.Deserialize<BackgroundSettings>(File.ReadAllText(path), options)
                let file = if isNull s.CustomFile then "" else s.CustomFile
                { s with
                    // A copy that went missing falls back to the theme.
                    UseCustom = s.UseCustom && File.Exists(file)
                    CustomFile = file
                    Brightness = clampBrightness s.Brightness }
            else
                defaults
        with _ ->
            defaults

    let saveTo (path: string) (settings: BackgroundSettings) =
        try
            Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
            let options = JsonSerializerOptions()
            options.WriteIndented <- true
            File.WriteAllText(path, JsonSerializer.Serialize(settings, options))
        with ex ->
            AppLog.error "Saving the background" ex

    let load () = loadFrom (settingsPath ())
    let save (settings: BackgroundSettings) = saveTo (settingsPath ()) settings

    /// Copies the picture into `dir` as custom_<ticks>.<ext> and removes any
    /// earlier copy. Returns the new copy's path.
    let importCustomIn (dir: string) (source: string) : string option =
        try
            if not (File.Exists(source)) || not (isImageFile source) then
                None
            else
                Directory.CreateDirectory(dir) |> ignore
                let ext = Path.GetExtension(source).ToLowerInvariant()
                let target = Path.Combine(dir, sprintf "custom_%d%s" DateTime.UtcNow.Ticks ext)
                File.Copy(source, target, true)

                for old in Directory.GetFiles(dir, "custom_*") do
                    if not (String.Equals(old, target, StringComparison.OrdinalIgnoreCase)) then
                        try File.Delete(old) with _ -> ()

                Some target
        with ex ->
            AppLog.error "Importing a background" ex
            None

    /// Deletes SUITE's copies - only the custom_* files it made itself.
    let removeCustomIn (dir: string) =
        try
            if Directory.Exists(dir) then
                for f in Directory.GetFiles(dir, "custom_*") do
                    try File.Delete(f) with _ -> ()
        with _ ->
            ()

    let importCustom (source: string) = importCustomIn (realBackgroundsDir ()) source
    let removeCustom () = removeCustomIn (realBackgroundsDir ())

    /// Average luminance of a picture, read from a 32x18 thumbnail. 55 - a
    /// typical dark wallpaper - when it cannot be read.
    let measureLuminance (path: string) : float =
        try
            use image = System.Drawing.Image.FromFile(path)
            use thumb = new System.Drawing.Bitmap(32, 18)

            do
                use g = System.Drawing.Graphics.FromImage(thumb)
                g.InterpolationMode <- System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear
                g.DrawImage(image, 0, 0, 32, 18)

            let mutable sum = 0.0
            for y in 0 .. 17 do
                for x in 0 .. 31 do
                    let c = thumb.GetPixel(x, y)
                    sum <- sum + 0.2126 * float c.R + 0.7152 * float c.G + 0.0722 * float c.B

            sum / (32.0 * 18.0)
        with _ ->
            55.0

    /// How much of the picture shows (0-1): the brightness setting, pulled
    /// down further for a picture brighter than a typical dark wallpaper so a
    /// white photo never glares at the same setting a dark one looks right at.
    let exposure (brightness: int) (luminance: float) =
        let b = float (clampBrightness brightness) / 100.0
        let fit = if luminance <= 55.0 then 1.0 else 55.0 / luminance
        b * fit + (1.0 - fit) * b * b * b
