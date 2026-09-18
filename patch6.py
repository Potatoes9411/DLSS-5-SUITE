import re

with open('Services/GtaDlss5.fs', 'r') as f:
    content = f.read()

insertion = """
        // Copy standard reshade shaders
        let shadersSrc = Path.Combine(ModInstaller.modFilesRoot (), "reshade-shaders")
        let shadersDst = Path.Combine(targetFolder, "reshade-shaders")
        try
            if Directory.Exists(shadersSrc) then
                Directory.CreateDirectory(shadersDst) |> ignore
                for f in Directory.GetFiles(shadersSrc, "*", SearchOption.AllDirectories) do
                    let relative = f.Substring(shadersSrc.Length).TrimStart('\\', '/')
                    let target = Path.Combine(shadersDst, relative)
                    Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
                    File.Copy(f, target, true)
        with _ -> ()

        // Configure ReShade.ini to silence the tutorial, set effects path, and change overlay key
        let ini = Path.Combine(targetFolder, "ReShade.ini")
        let wanted =
            [ "EffectSearchPaths", ".\\\\reshade-shaders\\\\Shaders\\\\**"
              "TextureSearchPaths", ".\\\\reshade-shaders\\\\Textures\\\\**"
              "KeyOverlay", "35,0,0,0"
              "TutorialProgress", "4" ]
        try
            let startsWithKey (key: string) (line: string) = line.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
            let existing = if File.Exists(ini) then File.ReadAllLines(ini) |> List.ofArray else []
            let updated =
                wanted
                |> List.fold (fun lines (key, value) ->
                    if lines |> List.exists (startsWithKey key) then
                        lines |> List.map (fun l -> if startsWithKey key l then key + "=" + value else l)
                    else
                        let section = if key = "KeyOverlay" then "[INPUT]" else "[GENERAL]"
                        let index = lines |> List.tryFindIndex (fun l -> l.Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
                        match index with
                        | Some i -> List.truncate (i + 1) lines @ [ key + "=" + value ] @ List.skip (i + 1) lines
                        | None -> [ section; key + "=" + value ] @ lines
                ) existing
            File.WriteAllLines(ini, updated)
        with _ -> ()
"""

content = content.replace(
    '        writeManifest targetFolder recorded',
    '        writeManifest targetFolder recorded\n' + insertion
)

with open('Services/GtaDlss5.fs', 'w') as f:
    f.write(content)
