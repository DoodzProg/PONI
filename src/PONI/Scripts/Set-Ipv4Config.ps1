<#
    PONI - apply an IPv4 configuration to a HOST adapter (embedded in PONI.exe, run in-process).

    Used both to apply a profile and to roll back to the previous configuration.
    Logic taken from PONI v1 (validated on real hardware), plus: the adapter is targeted by its
    interface index (names can be renamed), duplicate-address detection, and every part of the
    result is checked before reporting success.

    Output: ONE object { Ok, Code, Detail }.
      Code: Ok | NoIpv4Interface | Duplicate | NotConfirmed (Detail = IP|Gateway|Dns|Dhcp) | Failed (Detail = message)
#>
param(
    [int]$InterfaceIndex,
    [bool]$UseDhcp,
    [string[]]$Addresses = @(),   # "192.168.1.10/24"; the first one is the main address
    [string]$Gateway = '',
    [string[]]$Dns = @(),         # empty = automatic DNS
    [bool]$SetPrivate = $false
)

$ErrorActionPreference = 'Stop'

function New-Result([bool]$ok, [string]$code, [string]$detail = '') {
    [pscustomobject]@{ Ok = $ok; Code = $code; Detail = $detail }
}

# Right after an address conflict (and after a DHCP switch) Windows briefly drops and recreates
# the interface's IPv4 object: a cmdlet run at that moment fails with "no MSFT_NetIPInterface
# object found" (seen in real-hardware testing, 2026-09-28). Wait for it, then retry.
function Wait-Ipv4Interface([int]$seconds = 6) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $found = Get-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
        if ($found) { return $found }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Invoke-Retry([scriptblock]$action) {
    for ($attempt = 1; ; $attempt++) {
        try { return & $action }
        catch {
            if ($attempt -ge 3 -or $_.Exception.Message -notmatch 'MSFT_NetIPInterface') { throw }
            Start-Sleep -Milliseconds 800
            [void](Wait-Ipv4Interface 5)
        }
    }
}

# Static address, working around a Windows quirk (seen in real-hardware testing, 2026-09-28): on a
# DISCONNECTED adapter coming from DHCP, "Set-NetIPInterface -Dhcp Disabled" does not update the
# persistent configuration (registry EnableDHCP stays 1) and New-NetIPAddress then fails with
# "Inconsistent parameters PolicyStore PersistentStore and Dhcp Enabled".
# Fix, in order: registry EnableDHCP=0 + persistent store, retry; last resort netsh.
# Beware (real test round 2, 2026-09-28): the failing New-NetIPAddress still creates the address
# in the ACTIVE store, so a plain retry then fails with "Instance MSFT_NetIPAddress already
# exists". Remove that half-created address (and its route) before retrying.
function Remove-HalfCreated([hashtable]$params) {
    Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -IPAddress $params.IPAddress -ErrorAction SilentlyContinue |
        Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
    Get-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
}

function Add-StaticAddress([hashtable]$params) {
    try {
        New-NetIPAddress @params | Out-Null
        return 'cmdlet'
    }
    catch {
        if ($_.Exception.Message -notmatch 'PolicyStore') { throw }
    }

    Remove-HalfCreated $params
    $guid = (Get-NetAdapter -InterfaceIndex $InterfaceIndex).InterfaceGuid
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$guid" -Name EnableDHCP -Value 0 -Type DWord
    Set-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -Dhcp Disabled -PolicyStore PersistentStore -ErrorAction SilentlyContinue
    try {
        New-NetIPAddress @params | Out-Null
        return 'registry'
    }
    catch {
        if ($_.Exception.Message -notmatch 'PolicyStore|already exists|existe d') { throw }
    }

    # netsh replaces the adapter's whole IPv4 configuration (both stores) in one go.
    Remove-HalfCreated $params

    $bits = ('1' * $params.PrefixLength).PadRight(32, '0')
    $mask = (0..3 | ForEach-Object { [Convert]::ToInt32($bits.Substring($_ * 8, 8), 2) }) -join '.'
    $netshArgs = @('interface', 'ipv4', 'set', 'address', "name=$InterfaceIndex", 'source=static', "address=$($params.IPAddress)", "mask=$mask")
    if ($params.ContainsKey('DefaultGateway')) { $netshArgs += @("gateway=$($params.DefaultGateway)", 'gwmetric=0') }
    $out = & netsh.exe @netshArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "netsh: $out" }
    return 'netsh'
}

try {
    $ipIf = Wait-Ipv4Interface 4
    if (-not $ipIf) {
        # No IPv4 stack: typically the uplink of a Hyper-V external switch (RJ45 given to a VM).
        return New-Result $false 'NoIpv4Interface'
    }
    $adapter = Get-NetAdapter -InterfaceIndex $InterfaceIndex -ErrorAction SilentlyContinue
    $connected = $adapter -and "$($adapter.MediaConnectionState)" -eq 'Connected'

    if ($UseDhcp) {
        Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { "$($_.PrefixOrigin)" -eq 'Manual' } |
            Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
        # Remove-NetIPAddress never removes the default route: do it explicitly.
        Get-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
        Invoke-Retry { Set-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -Dhcp Enabled }
        Invoke-Retry { Set-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -ResetServerAddresses }
        # Ask for a lease right away (brief drop), only when a cable / network is there.
        if ($connected) { Restart-NetAdapter -InputObject $adapter -Confirm:$false -ErrorAction SilentlyContinue }

        $check = Wait-Ipv4Interface 6
        if ("$($check.Dhcp)" -ne 'Enabled') { return New-Result $false 'NotConfirmed' 'Dhcp' }
        return New-Result $true 'Ok'
    }

    if ($Addresses.Count -eq 0) { return New-Result $false 'Failed' 'No address to apply.' }

    # 1. Static mode, clean slate (addresses AND default route: otherwise re-applying the
    #    same gateway fails with "DefaultGateway instance already exists").
    Invoke-Retry { Set-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -Dhcp Disabled }
    Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
    Get-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue

    # 2. Addresses (+ gateway on the first one).
    $first = $true
    $method = ''
    foreach ($entry in $Addresses) {
        $ip, $prefix = $entry -split '/'
        $params = @{ InterfaceIndex = $InterfaceIndex; AddressFamily = 'IPv4'; IPAddress = $ip; PrefixLength = [int]$prefix }
        if ($first -and $Gateway) { $params.DefaultGateway = $Gateway }
        $used = Invoke-Retry { Add-StaticAddress $params }
        if ($first) { $method = $used }
        $first = $false
    }

    # 3. DNS: static list, or back to automatic (never leave an old DNS behind).
    if ($Dns.Count -gt 0) {
        Invoke-Retry { Set-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -ServerAddresses $Dns }
    } else {
        Invoke-Retry { Set-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -ResetServerAddresses }
    }

    # 4. Network category: only when the user asked for it in the settings.
    if ($SetPrivate) {
        Set-NetConnectionProfile -InterfaceIndex $InterfaceIndex -NetworkCategory Private -ErrorAction SilentlyContinue
    }

    # 5. Check the real state. On a connected network Windows first probes the address
    #    (Tentative) and flags it Duplicate if another machine answers: wait a few seconds.
    $mainIp = ($Addresses[0] -split '/')[0]
    $deadline = (Get-Date).AddSeconds($(if ($connected) { 4 } else { 0 }))
    do {
        $found = Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -IPAddress $mainIp -ErrorAction SilentlyContinue
        if ($found -and "$($found.AddressState)" -eq 'Duplicate') { return New-Result $false 'Duplicate' $mainIp }
        if ($found -and "$($found.AddressState)" -eq 'Preferred') { break }
        if ((Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    } while ((Get-Date) -lt $deadline)
    if (-not $found) { return New-Result $false 'NotConfirmed' 'IP' }

    if ($Gateway) {
        $route = Get-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Where-Object { $_.NextHop -eq $Gateway }
        if (-not $route) { return New-Result $false 'NotConfirmed' 'Gateway' }
    }

    if ($Dns.Count -gt 0) {
        $current = @((Get-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4).ServerAddresses)
        foreach ($server in $Dns) {
            if ($current -notcontains $server) { return New-Result $false 'NotConfirmed' 'Dns' }
        }
    }

    # Detail = how the address was set (cmdlet | registry | netsh): written to PONI's log.
    return New-Result $true 'Ok' $method
}
catch {
    return New-Result $false 'Failed' $_.Exception.Message
}
