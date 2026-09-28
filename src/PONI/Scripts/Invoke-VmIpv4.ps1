<#
    PONI - apply an IPv4 configuration INSIDE a running VM through PowerShell Direct.

    1. Opens a PowerShell Direct session with the credentials typed by the user.
    2. Finds the guest adapter by MAC address (its "Ethernet N" name inside the guest is
       unrelated to the Hyper-V side name and changes over time), and reads its current
       configuration (snapshot for the rollback).
    3. Runs, inside the guest, the SAME Set-Ipv4Config.ps1 as on the host (received as text).
    4. On failure, puts the snapshot back (if asked).
    5. On success, creates (AllowPing) or removes PONI's own guest firewall rule 'PONI-Ping-In'
       (ICMPv4 echo in, local subnet only). A failure there does not undo the IP.

    Output: ONE object { Ok, Code, Detail }.
      Code: Ok (Detail = guest adapter name | method | ping=allowed/removed/off/failed:...) | VmMissing | VmNotRunning | Session (Detail = message)
            | AdapterNotFound (Detail = adapters seen) | <any Set-Ipv4Config code> (Detail = ...|rollback=ok/failed/off)
#>
param(
    [string]$VMName,
    [pscredential]$Credential,
    [string]$Mac,                 # Hyper-V side MAC, 12 hex digits
    [bool]$UseDhcp,
    [string[]]$Addresses = @(),
    [string]$Gateway = '',
    [string[]]$Dns = @(),
    [bool]$Rollback = $true,
    [bool]$AllowPing = $true,     # PONI's own guest firewall rule 'PONI-Ping-In': create / remove
    [string]$SetScript            # text of Set-Ipv4Config.ps1
)

$ErrorActionPreference = 'Stop'

function New-Result([bool]$ok, [string]$code, [string]$detail = '') {
    [pscustomobject]@{ Ok = $ok; Code = $code; Detail = $detail }
}

try {
    $vm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    if (-not $vm) { return New-Result $false 'VmMissing' $VMName }
    # PONI never starts or stops a VM (v1 decision: juggling VM power failed in confusing ways).
    if ("$($vm.State)" -ne 'Running') { return New-Result $false 'VmNotRunning' $VMName }

    try {
        $session = New-PSSession -VMName $VMName -Credential $Credential -ErrorAction Stop
    }
    catch {
        return New-Result $false 'Session' $_.Exception.Message
    }

    try {
        # Guest adapter by MAC + snapshot of its configuration.
        $info = Invoke-Command -Session $session -ArgumentList $Mac -ScriptBlock {
            param($mac)
            $want = ($mac -replace '[:-]', '').ToUpper()
            $nic = Get-NetAdapter | Where-Object { ($_.MacAddress -replace '[:-]', '').ToUpper() -eq $want } | Select-Object -First 1
            if (-not $nic) {
                return [pscustomobject]@{ Found = $false; Seen = ((@(Get-NetAdapter | ForEach-Object { "$($_.Name)=$($_.MacAddress)" })) -join ' ; ') }
            }
            $ipIf = Get-NetIPInterface -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
            $manual = @(Get-NetIPAddress -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object { "$($_.PrefixOrigin)" -eq 'Manual' } | ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" })
            $route = Get-NetRoute -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                Sort-Object RouteMetric | Select-Object -First 1
            $key = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$($nic.InterfaceGuid)"
            $staticDns = "$((Get-ItemProperty -Path $key -Name NameServer -ErrorAction SilentlyContinue).NameServer)".Trim()
            [pscustomobject]@{
                Found = $true; Name = $nic.Name; Index = [int]$nic.ifIndex
                Dhcp = ("$($ipIf.Dhcp)" -eq 'Enabled'); Addresses = $manual
                Gateway = "$($route.NextHop)"; Dns = @($staticDns -split '[,\s]+' | Where-Object { $_ })
            }
        }
        if (-not $info.Found) { return New-Result $false 'AdapterNotFound' "$($info.Seen)" }

        $set = [scriptblock]::Create($SetScript)
        $result = Invoke-Command -Session $session -ScriptBlock $set -ArgumentList $info.Index, $UseDhcp, $Addresses, $Gateway, $Dns, $false
        if ($result.Ok) {
            # 5. Ping (only once the IP is applied). PONI's OWN firewall rule only, never Windows' rules:
            #    allowed = created (again, so its definition is always the right one); off = removed.
            $ping = Invoke-Command -Session $session -ArgumentList $AllowPing -ScriptBlock {
                param($allow)
                try {
                    if (Get-NetFirewallRule -Name 'PONI-Ping-In' -ErrorAction SilentlyContinue) {
                        Remove-NetFirewallRule -Name 'PONI-Ping-In' -ErrorAction Stop
                        if (-not $allow) { return 'removed' }
                    }
                    elseif (-not $allow) { return 'off' }
                    New-NetFirewallRule -Name 'PONI-Ping-In' -DisplayName 'PONI - Ping entrant (ICMPv4)' `
                        -Description 'Created by PONI: answers ping (ICMPv4 echo) from the local network only.' `
                        -Direction Inbound -Protocol ICMPv4 -IcmpType 8 -RemoteAddress LocalSubnet -Profile Any -Action Allow `
                        -ErrorAction Stop | Out-Null
                    return 'allowed'
                }
                catch { return "failed:$($_.Exception.Message)" }
            }
            return New-Result $true 'Ok' "$($info.Name)|$($result.Detail)|ping=$ping"
        }

        $rollbackState = 'off'
        if ($Rollback) {
            $restoreDhcp = [bool]$info.Dhcp -or @($info.Addresses).Count -eq 0
            $gw = if ("$($info.Gateway)" -eq '0.0.0.0') { '' } else { "$($info.Gateway)" }
            $back = Invoke-Command -Session $session -ScriptBlock $set -ArgumentList $info.Index, $restoreDhcp, @($info.Addresses), $gw, @($info.Dns), $false
            $rollbackState = if ($back.Ok) { 'ok' } else { "failed:$($back.Code) $($back.Detail)" }
        }
        return New-Result $false "$($result.Code)" "$($info.Name)|$($result.Detail)|rollback=$rollbackState"
    }
    finally {
        Remove-PSSession $session -ErrorAction SilentlyContinue
    }
}
catch {
    return New-Result $false 'Failed' $_.Exception.Message
}
