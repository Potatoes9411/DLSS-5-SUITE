<#
  Sends commands to a SUITE started with --bridge and prints each answer.

    .\tools\bridge.ps1 "get EditionTitle" "call SelectEdition storage" "snap C:\temp\a.png"

  See Services/TestBridge.fs for the command list.
#>
param([Parameter(Mandatory, ValueFromRemainingArguments)] [string[]] $Commands)
# Set DLSS5_BRIDGE_PIPE to talk to a copy started with the same variable.
$pipeName = if ($env:DLSS5_BRIDGE_PIPE) { $env:DLSS5_BRIDGE_PIPE } else { 'DLSS5SuiteBridge' }
$ErrorActionPreference = 'Stop'
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(5000)
$utf8 = New-Object System.Text.UTF8Encoding($false)
$writer = New-Object System.IO.StreamWriter($pipe, $utf8); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($pipe, $utf8)
foreach ($c in $Commands) {
    $writer.WriteLine($c)
    "> $c"
    "  " + $reader.ReadLine()
}
$pipe.Dispose()
