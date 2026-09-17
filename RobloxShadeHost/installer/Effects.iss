function IniSections(const FileName: String): TArrayOfString;
var
  Lines: TArrayOfString;
  Index, Count: Integer;
  Line: String;
begin
  if not LoadStringsFromFile(FileName, Lines) then
    RaiseException('Could not read ' + FileName);
  SetArrayLength(Result, 0);
  for Index := 0 to GetArrayLength(Lines) - 1 do begin
    Line := Trim(Lines[Index]);
    if (Length(Line) > 2) and (Line[1] = '[') and (Line[Length(Line)] = ']') then begin
      Count := GetArrayLength(Result);
      SetArrayLength(Result, Count + 1);
      Result[Count] := Copy(Line, 2, Length(Line) - 2);
    end;
  end;
  if GetArrayLength(Result) = 0 then
    RaiseException('The download list is empty.');
end;

// Match ReShade's directory selection: named folders first, then the shallowest file.
procedure FindPackageFolders(const Directory: String; var Shaders, Textures,
  ShaderFallback, TextureFallback: String);
var
  Entry: TFindRec;
  Path, Extension: String;
begin
  if FindFirst(Directory + '\*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          Path := Directory + '\' + Entry.Name;
          if Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then begin
            if (Shaders = '') and (CompareText(Entry.Name, 'Shaders') = 0) then
              Shaders := Path;
            if (Textures = '') and (CompareText(Entry.Name, 'Textures') = 0) then
              Textures := Path;
            FindPackageFolders(Path, Shaders, Textures, ShaderFallback, TextureFallback);
          end else begin
            Extension := Lowercase(ExtractFileExt(Entry.Name));
            if (Extension = '.fx') and
              ((ShaderFallback = '') or (Length(Directory) < Length(ShaderFallback))) then
              ShaderFallback := Directory;
            if ((Extension = '.png') or (Extension = '.jpg') or (Extension = '.jpeg')) and
              ((TextureFallback = '') or (Length(Directory) < Length(TextureFallback))) then
              TextureFallback := Directory;
          end;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

procedure CopyPackageFiles(const Source, Destination, Denied: String);
var
  Entry: TFindRec;
  Extension: String;
begin
  if not ForceDirectories(Destination) then
    RaiseException('Could not create ' + Destination);
  if FindFirst(Source + '\*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          if Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
            CopyPackageFiles(Source + '\' + Entry.Name, Destination + '\' + Entry.Name, Denied)
          else begin
            Extension := Lowercase(ExtractFileExt(Entry.Name));
            if (Pos(',' + Lowercase(Entry.Name) + ',', ',' + Lowercase(Denied) + ',') = 0) and
              (Extension <> '.addon') and (Extension <> '.addon32') and
              (Extension <> '.addon64') and (Extension <> '.dll') and (Extension <> '.exe') then
              if not FileCopy(Source + '\' + Entry.Name, Destination + '\' + Entry.Name, False) then
                RaiseException('Could not copy ' + Entry.Name);
          end;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function PackageDestination(const RelativePath, Kind: String): String;
var
  Root: String;
begin
  Root := ExpandConstant('{tmp}\effects-stage\reshade-shaders\') + Kind;
  Result := ExpandFileName(ExpandConstant('{tmp}\effects-stage\') + RelativePath);
  if (CompareText(Result, Root) <> 0) and
    (Pos(Lowercase(Root + '\'), Lowercase(Result)) <> 1) then
    RaiseException('Invalid effect package destination: ' + RelativePath);
end;

procedure PrepareEffects;
var
  Sections: TArrayOfString;
  Index: Integer;
  Catalog, Section, Url, ExtractRoot, Shaders, Textures, ShaderFallback,
    TextureFallback, Name, ShaderDest, TextureDest, Denied: String;
begin
  if EffectsDownloaded then
    exit;
  Download('{#EffectPackagesUrl}', 'EffectPackages.ini', '');
  Catalog := ExpandConstant('{tmp}\EffectPackages.ini');
  Sections := IniSections(Catalog);
  for Index := 0 to GetArrayLength(Sections) - 1 do begin
    Section := Sections[Index];
    Name := GetIniString(Section, 'PackageName', Section, Catalog);
    DownloadPage.SetText('Downloading ReShade effects',
      Format('%d of %d: %s', [Index + 1, GetArrayLength(Sections), Name]));
    Url := GetIniString(Section, 'DownloadUrl', '', Catalog);
    if Pos('https://', Url) <> 1 then
      RaiseException('Invalid download URL for ' + Name);
    ShaderDest := PackageDestination(GetIniString(Section, 'InstallPath', '', Catalog), 'Shaders');
    TextureDest := PackageDestination(GetIniString(Section, 'TextureInstallPath', '', Catalog), 'Textures');
    Download(Url, 'effects.zip', '');
    ExtractRoot := ExpandConstant('{tmp}\effect-package-') + IntToStr(Index);
    // This directory is private temporary storage, never the installed shader folder.
    if DirExists(ExtractRoot) and not DelTree(ExtractRoot, True, True, True) then
      RaiseException('Could not clear temporary effect files.');
    ExtractArchive(ExpandConstant('{tmp}\effects.zip'), ExtractRoot, '', True, nil);
    Shaders := '';
    Textures := '';
    ShaderFallback := '';
    TextureFallback := '';
    FindPackageFolders(ExtractRoot, Shaders, Textures, ShaderFallback, TextureFallback);
    if Shaders = '' then Shaders := ShaderFallback;
    if Textures = '' then Textures := TextureFallback;
    if Shaders = '' then
      RaiseException('No shaders found in ' + Name);
    Denied := GetIniString(Section, 'DenyEffectFiles', '', Catalog);
    CopyPackageFiles(Shaders, ShaderDest, Denied);
    if Textures <> '' then
      CopyPackageFiles(Textures, TextureDest, '');
    Log('Effect package installed: ' + Name);
    DelTree(ExtractRoot, True, True, True);
  end;
  EffectsDownloaded := True;
end;

function FindEffect(const Directory, FileName: String): Boolean;
var
  Entry: TFindRec;
begin
  Result := FileExists(Directory + '\' + FileName);
  if Result then exit;
  if FindFirst(Directory + '\*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') and
          (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) then begin
          Result := FindEffect(Directory + '\' + Entry.Name, FileName);
          if Result then exit;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

procedure PreparePresets;
var
  Names, Lines: TArrayOfString;
  Index, LineIndex, Separator: Integer;
  Catalog, FileName, Techniques, Technique, Shader, Hash: String;
begin
  if PresetsDownloaded then exit;
  DownloadPage.SetText('Downloading presets', 'Downloading RobloxShadeHost presets.');
  Download('{#PresetsBaseUrl}/downloads.ini', 'preset-downloads.ini', '');
  Catalog := ExpandConstant('{tmp}\preset-downloads.ini');
  Names := IniSections(Catalog);
  if not ForceDirectories(ExpandConstant('{tmp}\presets')) then
    RaiseException('Could not prepare the presets folder.');
  for Index := 0 to GetArrayLength(Names) - 1 do begin
    FileName := Names[Index];
    if (ExtractFileName(FileName) <> FileName) or (Pos(':', FileName) > 0) or
      (Lowercase(ExtractFileExt(FileName)) <> '.ini') then
      RaiseException('Invalid preset filename.');
    Hash := Lowercase(GetIniString(FileName, 'sha256', '', Catalog));
    if Length(Hash) <> 64 then RaiseException('Invalid preset checksum.');
    for LineIndex := 1 to Length(Hash) do
      if Pos(Hash[LineIndex], '0123456789abcdef') = 0 then
        RaiseException('Invalid preset checksum.');
    Download('{#PresetsBaseUrl}/' + FileName, 'preset.ini', Hash);
    // ReShade keeps Techniques outside a section; Windows INI functions cannot read it.
    if not LoadStringsFromFile(ExpandConstant('{tmp}\preset.ini'), Lines) then
      RaiseException('Could not read ' + FileName);
    Techniques := '';
    for LineIndex := 0 to GetArrayLength(Lines) - 1 do begin
      Technique := Trim(Lines[LineIndex]);
      if Copy(Technique, 1, 1) = '[' then break;
      if Pos('techniques=', Lowercase(Technique)) = 1 then
        Techniques := Copy(Technique, 12, Length(Technique));
    end;
    if Techniques = '' then RaiseException('No effects specified in ' + FileName);
    while Techniques <> '' do begin
      Separator := Pos(',', Techniques);
      if Separator = 0 then Separator := Length(Techniques) + 1;
      Technique := Trim(Copy(Techniques, 1, Separator - 1));
      Delete(Techniques, 1, Separator);
      Separator := Pos('@', Technique);
      if Separator = 0 then RaiseException('Missing shader filename in ' + FileName);
      Shader := Copy(Technique, Separator + 1, Length(Technique));
      if (ExtractFileName(Shader) <> Shader) or
        not FindEffect(ExpandConstant('{tmp}\effects-stage\reshade-shaders\Shaders'), Shader) then
        RaiseException(FileName + ' needs an unavailable effect: ' + Shader);
    end;
    if not FileCopy(ExpandConstant('{tmp}\preset.ini'), ExpandConstant('{tmp}\presets\') + FileName, False) then
      RaiseException('Could not prepare ' + FileName);
  end;
  PresetsDownloaded := True;
end;

