<#
    PONI - give the physical RJ45 port back to Windows (remove PONI's RJ45-Switch).

    HARD SAFEGUARD: the switch name is written here, never received as a parameter. PONI never
    removes any other switch (e.g. a permanent licence link between the host and a VM).

    Output: ONE object { Ok, Code, Detail }. Code: Ok | NotConfirmed | Failed
#>
param(
    [string]$PhysicalAdapter = ''   # optional: wait for its IPv4 stack to come back
)

$ErrorActionPreference = 'Stop'

function New-Result([bool]$ok, [string]$code, [string]$detail = '') {
    [pscustomobject]@{ Ok = $ok; Code = $code; Detail = $detail }
}

try {
    if (Get-VMSwitch -Name 'RJ45-Switch' -ErrorAction SilentlyContinue) {
        # Unplug the VMs FIRST. Removing a switch with VM adapters still on it leaves those adapters
        # pointing to a switch that no longer exists, and Hyper-V then refuses to start the VM
        # ("Insufficient system resources ... Ethernet switch 'RJ45-Switch' not found", 0x800705AA;
        # real test of 2026-09-28). v1 (and the first v2 build) had this defect.
        Get-VM | Get-VMNetworkAdapter | Where-Object { $_.SwitchName -eq 'RJ45-Switch' } | Disconnect-VMNetworkAdapter
        Remove-VMSwitch -Name 'RJ45-Switch' -Force
    }
    if (Get-VMSwitch -Name 'RJ45-Switch' -ErrorAction SilentlyContinue) {
        return New-Result $false 'NotConfirmed' 'Switch'
    }

    # The adapter gets its IPv4 stack back a moment later: wait for it, so that applying a
    # profile right after (v1 bug #2 flow) finds a configurable adapter.
    if ($PhysicalAdapter) {
        $deadline = (Get-Date).AddSeconds(15)
        do {
            $nic = Get-NetAdapter -Name $PhysicalAdapter -ErrorAction SilentlyContinue
            $ipIf = if ($nic) { Get-NetIPInterface -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue }
            if ($ipIf) { break }
            Start-Sleep -Milliseconds 400
        } while ((Get-Date) -lt $deadline)
        if (-not $ipIf) { return New-Result $true 'Ok' 'NoIpv4Yet' }
    }
    return New-Result $true 'Ok'
}
catch {
    return New-Result $false 'Failed' $_.Exception.Message
}
