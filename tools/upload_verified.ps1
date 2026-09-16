param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string[]]$Files
)
# Replaces release assets on GitHub and PROVES it worked.
#
# The earlier upload scripts reported "Uploaded" whether or not anything was
# uploaded. GitHub stores "DLSS 5 SUITE Setup v1.exe" as "DLSS.5.SUITE.Setup.v1.exe",
# so deleting by the spaced name matched nothing, the new upload was rejected as
# a duplicate, and curl's output went to Out-Null. This version:
#   * deletes the existing asset under either spelling,
#   * checks the upload's HTTP status and stops on anything but 201,
#   * re-reads the release afterwards and confirms name, size and digest.
#
# The token comes from the GITHUB_TOKEN environment variable and is never
# printed. It is not stored in any file.
$ErrorActionPreference = 'Stop'

$Repo = 'Potatoes9411/DLSS-5-SUITE'
$Token = $env:GITHUB_TOKEN
if (-not $Token) { throw 'Set the GITHUB_TOKEN environment variable first.' }
$Headers = @{ Authorization = "Bearer $Token"; Accept = 'application/vnd.github+json' }

function Get-Release {
    try {
        return Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" -Headers $Headers
    }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }

        # GitHub's release-by-tag endpoint does not return draft releases even
        # to their owner. The authenticated releases list does, so a new release
        # can stay private until every large asset has uploaded and verified.
        $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=100" -Headers $Headers
        $draft = $releases | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
        if (-not $draft) { throw "release not found for tag $Tag" }
        return $draft
    }
}

$release = Get-Release
Write-Host "release $Tag  id=$($release.id)"

foreach ($file in $Files) {
    if (-not (Test-Path $file)) { throw "missing file: $file" }
    $local = Get-Item $file
    $spaced = $local.Name
    $dotted = $spaced.Replace(' ', '.')

    foreach ($a in (Get-Release).assets) {
        if ($a.name -ieq $spaced -or $a.name -ieq $dotted) {
            Invoke-RestMethod -Uri $a.url -Method Delete -Headers $Headers | Out-Null
            Write-Host "  deleted old asset: $($a.name)"
        }
    }

    $url = "https://uploads.github.com/repos/$Repo/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($dotted))"
    $respFile = [System.IO.Path]::GetTempFileName()
    $code = & curl.exe -sS -o $respFile -w '%{http_code}' -X POST $url `
        -H "Authorization: Bearer $Token" `
        -H 'Content-Type: application/octet-stream' `
        --data-binary "@$($local.FullName)"
    if ($code -ne '201') {
        $body = Get-Content $respFile -Raw
        Remove-Item $respFile -ErrorAction SilentlyContinue
        throw "upload of $dotted failed: HTTP $code  $body"
    }
    Remove-Item $respFile -ErrorAction SilentlyContinue
    Write-Host ("  uploaded {0}  ({1:N0} MB)  HTTP {2}" -f $dotted, ($local.Length / 1MB), $code)
}

# Verify against what GitHub now actually serves.
Write-Host "--- verification ---"
$after = Get-Release
$failed = $false
foreach ($file in $Files) {
    $local = Get-Item $file
    $dotted = $local.Name.Replace(' ', '.')
    $a = $after.assets | Where-Object { $_.name -ieq $dotted } | Select-Object -First 1
    if (-not $a) { Write-Host "  MISSING  $dotted"; $failed = $true; continue }
    $sizeOk = ($a.size -eq $local.Length)
    $sha = (Get-FileHash $local.FullName -Algorithm SHA256).Hash.ToLower()
    $digestOk = ($a.digest -eq "sha256:$sha")
    Write-Host ("  {0}  size={1} digest={2}  updated={3}" -f $dotted, $(if ($sizeOk) {'OK'} else {'MISMATCH'}), $(if ($digestOk) {'OK'} else {"MISMATCH ($($a.digest))"}), $a.updated_at)
    if (-not ($sizeOk -and $digestOk)) { $failed = $true }
}
if ($failed) { throw 'verification failed' }
Write-Host 'all assets verified'
