<#
    PONI - repair VM network adapters that still point to a DELETED virtual switch.

    Such an adapter shows Connected = True with an empty SwitchName; Hyper-V then refuses to start
    the VM with a misleading "Insufficient system resources ... Ethernet switch not found" error
    (0x800705AA; real test of 2026-09-28: every RJ45 adapter of the VMs pointed to a removed
    RJ45-Switch, and another adapter to a long-gone internal switch).

    The only change: Disconnect-VMNetworkAdapter on those adapters (they point nowhere anyway).
    Adapters connected to an EXISTING switch are never touched. No switch is created or removed.

    Output: ONE object { Ok, Code, Detail }. Code: Ok (Detail = number fixed) | NotConfirmed | Failed
#>
$ErrorActionPreference = 'Stop'

function New-Result([bool]$ok, [string]$code, [string]$detail = '') {
    [pscustomobject]@{ Ok = $ok; Code = $code; Detail = $detail }
}

try {
    $dangling = @(Get-VM | Get-VMNetworkAdapter | Where-Object { $_.Connected -and -not "$($_.SwitchName)" })
    foreach ($adapter in $dangling) { Disconnect-VMNetworkAdapter -VMNetworkAdapter $adapter }

    $left = @(Get-VM | Get-VMNetworkAdapter | Where-Object { $_.Connected -and -not "$($_.SwitchName)" })
    if ($left.Count -gt 0) { return New-Result $false 'NotConfirmed' "$($left.Count)" }
    return New-Result $true 'Ok' "$($dangling.Count)"
}
catch {
    return New-Result $false 'Failed' $_.Exception.Message
}
