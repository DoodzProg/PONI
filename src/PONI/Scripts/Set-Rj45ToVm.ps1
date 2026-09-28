<#
    PONI - route the physical RJ45 port to a VM (embedded in PONI.exe, run in-process).

    Logic from PONI v1 (validated on real hardware), plus:
      - EXCLUSIVITY: every other VM is disconnected from the RJ45 switch;
      - the switch is only ever the one named RJ45-Switch (hard-coded here, never a parameter);
      - refuses to take an adapter already used by ANOTHER external switch (the user's own);
      - checks the real result before reporting success.

    Output: ONE object { Ok, Code, Detail }.
      Code: Ok (Detail = VM adapter name) | VmMissing | PhysicalMissing | PhysicalDisabled
            | SwitchOnOtherAdapter (Detail = its uplink) | AdapterUsedByOtherSwitch (Detail = that switch)
            | NotConfirmed | Failed
#>
param(
    [string]$VMName,
    [string]$PhysicalAdapter   # Windows name of the physical adapter, e.g. "Ethernet"
)

$ErrorActionPreference = 'Stop'
$SwitchName = 'RJ45-Switch'
$AdapterName = 'RJ45-Adapter'
$LegacyName = 'Carte-RJ45'

function New-Result([bool]$ok, [string]$code, [string]$detail = '') {
    [pscustomobject]@{ Ok = $ok; Code = $code; Detail = $detail }
}

try {
    $vm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    if (-not $vm) { return New-Result $false 'VmMissing' $VMName }

    $nic = Get-NetAdapter -Name $PhysicalAdapter -ErrorAction SilentlyContinue
    if (-not $nic) { return New-Result $false 'PhysicalMissing' $PhysicalAdapter }
    if ("$($nic.Status)" -eq 'Disabled') { return New-Result $false 'PhysicalDisabled' $PhysicalAdapter }

    # 1. The RJ45 switch: reuse it if it already sits on this adapter, create it otherwise.
    $switch = Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue
    if ($switch -and "$($switch.NetAdapterInterfaceDescription)" -ne "$($nic.InterfaceDescription)") {
        return New-Result $false 'SwitchOnOtherAdapter' "$($switch.NetAdapterInterfaceDescription)"
    }
    if (-not $switch) {
        $taken = Get-VMSwitch -SwitchType External -ErrorAction SilentlyContinue |
            Where-Object { "$($_.NetAdapterInterfaceDescription)" -eq "$($nic.InterfaceDescription)" } |
            Select-Object -First 1
        if ($taken) { return New-Result $false 'AdapterUsedByOtherSwitch' $taken.Name }
        # No host access on the port: the host and the VM must never share it (same MAC seen twice
        # by a customer's managed switch = "port security" block). Deliberate v1 choice, kept.
        New-VMSwitch -Name $SwitchName -NetAdapterName $PhysicalAdapter -AllowManagementOS $false | Out-Null
    }

    # 2. Exclusivity: no other VM keeps the port (v1 left the previous VM attached).
    Get-VM | Where-Object { $_.Name -ne $VMName } | Get-VMNetworkAdapter |
        Where-Object { $_.SwitchName -eq $SwitchName } |
        Disconnect-VMNetworkAdapter

    # 3. PONI's adapter in the VM: created if missing, duplicates from past mishaps removed.
    $mine = @(Get-VMNetworkAdapter -VMName $VMName | Where-Object { $_.Name -eq $AdapterName -or $_.Name -eq $LegacyName })
    if ($mine.Count -eq 0) {
        Add-VMNetworkAdapter -VMName $VMName -SwitchName $SwitchName -Name $AdapterName
        $kept = $AdapterName
    } else {
        $mine | Select-Object -Skip 1 | ForEach-Object { Remove-VMNetworkAdapter -VMNetworkAdapter $_ -ErrorAction SilentlyContinue }
        $kept = $mine[0].Name
        Connect-VMNetworkAdapter -VMName $VMName -Name $kept -SwitchName $SwitchName
    }

    # 4. A hot-added adapter sometimes stays "Disconnected" in the guest until a restart:
    #    a Disconnect/Connect cycle makes it less frequent (best effort; v1 fix #1).
    if ("$($vm.State)" -eq 'Running') {
        Disconnect-VMNetworkAdapter -VMName $VMName -Name $kept -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        Connect-VMNetworkAdapter -VMName $VMName -Name $kept -SwitchName $SwitchName -ErrorAction SilentlyContinue
    }

    # 5. Real check: this VM is on the switch, and it is the only one.
    $onSwitch = @(Get-VM | Get-VMNetworkAdapter | Where-Object { $_.SwitchName -eq $SwitchName } |
        Select-Object -ExpandProperty VMName -Unique)
    if ($onSwitch -notcontains $VMName) { return New-Result $false 'NotConfirmed' 'Target' }
    if ($onSwitch.Count -gt 1) { return New-Result $false 'NotConfirmed' 'Exclusive' }

    return New-Result $true 'Ok' $kept
}
catch {
    return New-Result $false 'Failed' $_.Exception.Message
}
