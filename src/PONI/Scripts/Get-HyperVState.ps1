<#
    PONI - read Hyper-V (read-only): VMs, their network adapters, virtual switches.
    Output: ONE object { Ok, Code, Detail }. Code: Ok (Detail = compact JSON) | NoModule | NoAccess | Failed
#>
$ErrorActionPreference = 'Stop'

function New-Result([bool]$ok, [string]$code, [string]$detail = '') {
    [pscustomobject]@{ Ok = $ok; Code = $code; Detail = $detail }
}

try {
    if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) { return New-Result $false 'NoModule' }

    $vms = @(Get-VM | ForEach-Object { [pscustomobject]@{ Name = $_.Name; State = "$($_.State)" } })
    $adapters = @(Get-VM | Get-VMNetworkAdapter | ForEach-Object {
        # Connected + empty SwitchName = the adapter still points to a switch that no longer exists:
        # Hyper-V then refuses to start the VM ("Insufficient system resources ... Ethernet switch
        # not found", 0x800705AA; real test of 2026-09-28).
        [pscustomobject]@{ VMName = $_.VMName; Name = $_.Name; SwitchName = "$($_.SwitchName)"; Mac = "$($_.MacAddress)"; Connected = [bool]$_.Connected }
    })
    $switches = @(Get-VMSwitch | ForEach-Object {
        [pscustomobject]@{ Name = $_.Name; Type = "$($_.SwitchType)"; Uplink = "$($_.NetAdapterInterfaceDescription)"; HostAccess = [bool]$_.AllowManagementOS }
    })

    $json = [pscustomobject]@{ Vms = $vms; Adapters = $adapters; Switches = $switches } | ConvertTo-Json -Depth 4 -Compress
    return New-Result $true 'Ok' $json
}
catch {
    # Not an administrator nor a member of "Hyper-V Administrators".
    if ($_.Exception.Message -match 'permission|autorisation|droits|access is denied|acc.s refus') {
        return New-Result $false 'NoAccess' $_.Exception.Message
    }
    return New-Result $false 'Failed' $_.Exception.Message
}
