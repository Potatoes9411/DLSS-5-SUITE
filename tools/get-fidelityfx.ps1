# AMD FidelityFX SDK 1.1.4's runtime for FSR 3.1 Frame Generation, fetched from
# AMD's own repository and never committed.
#
#   tools\get-fidelityfx.ps1                 into FIDELITYFX_SDK_DIR, else ..\fidelityfx-sdk beside the repo
#   tools\get-fidelityfx.ps1 -Into <folder>  into the folder named
#
# What it fetches, pinned to the v1.1.4 tag's commit and to each file's SHA-256:
#   bin\amd_fidelityfx_dx12.dll   the signed FidelityFX API runtime (FSR 3.1.4 upscaling and frame generation)
#   LICENSE.txt                   its MIT licence
# The headers the engines compile against (MIT, a few KB) are in the repo at
# external\fidelityfx\include; this is only the DLL they load at run time.
# tools\build-setup.sh copies the DLL and the licence beside the engines.
param([string]$Into = "")
$ErrorActionPreference = "Stop"

$commit = "c6efa6bf7f2027b3ec94f28578bb5965eabb9e55"   # GPUOpen-LibrariesAndSDKs/FidelityFX-SDK tag v1.1.4
$base = "https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/$commit"
$files = @(
    @{ From = "PrebuiltSignedDLL/amd_fidelityfx_dx12.dll"; To = "bin\amd_fidelityfx_dx12.dll"; Sha = "12A5081257EC95B0B53AD51B4A87FB3C03F97FE0BBB59F9496968F8D50EF93A6"; Size = 6675664 },
    @{ From = "LICENSE.txt"; To = "LICENSE.txt"; Sha = ""; Size = 0 }
)

if ($Into -eq "") { $Into = $env:FIDELITYFX_SDK_DIR }
if ($Into -eq "" -or $null -eq $Into) { $Into = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "fidelityfx-sdk" }
New-Item -ItemType Directory -Force (Join-Path $Into "bin") | Out-Null

foreach ($f in $files) {
    $target = Join-Path $Into $f.To
    if ($f.Sha -ne "" -and (Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $f.Sha) { "have $($f.To)"; continue }
    $temp = "$target.part"
    Invoke-WebRequest -Uri "$base/$($f.From)" -OutFile $temp -Headers @{ "User-Agent" = "DLSS5-SUITE-build" } -UseBasicParsing
    if ($f.Size -gt 0 -and (Get-Item $temp).Length -ne $f.Size) { Remove-Item $temp; throw "$($f.From): not the size expected" }
    if ($f.Sha -ne "" -and (Get-FileHash $temp -Algorithm SHA256).Hash -ne $f.Sha) { Remove-Item $temp; throw "$($f.From): not the file expected (SHA-256)" }
    Move-Item -Force $temp $target
    "got $($f.To)"
}

# The engines check AMD's signature again before they load it; this is so a bad copy is told here.
$dll = Join-Path $Into "bin\amd_fidelityfx_dx12.dll"
$sig = Get-AuthenticodeSignature $dll
if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notlike "CN=Advanced Micro Devices*") { throw "amd_fidelityfx_dx12.dll is not signed by AMD" }
"FidelityFX runtime ready in $Into"
