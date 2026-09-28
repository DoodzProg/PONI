<#
    PONI - set the network category (Private / Public) of a HOST adapter.
    Output: ONE object { Ok, Code, Detail }. Code: Ok | Domain | NoProfile | NotConfirmed | Failed
#>
param(
    [int]$InterfaceIndex,
    [ValidateSet('Private', 'Public')]
    [string]$Category
)

$ErrorActionPreference = 'Stop'

function New-Result([bool]$ok, [string]$code, [string]$detail = '') {
    [pscustomobject]@{ Ok = $ok; Code = $code; Detail = $detail }
}

try {
    $connection = Get-NetConnectionProfile -InterfaceIndex $InterfaceIndex -ErrorAction SilentlyContinue
    if (-not $connection) { return New-Result $false 'NoProfile' }
    # Windows does not let anyone force a domain network (it is set by Active Directory).
    if ("$($connection.NetworkCategory)" -eq 'DomainAuthenticated') { return New-Result $false 'Domain' }

    Set-NetConnectionProfile -InterfaceIndex $InterfaceIndex -NetworkCategory $Category
    Start-Sleep -Milliseconds 300
    $now = (Get-NetConnectionProfile -InterfaceIndex $InterfaceIndex).NetworkCategory
    # A group policy can silently override the change.
    if ("$now" -ne $Category) { return New-Result $false 'NotConfirmed' "$now" }
    return New-Result $true 'Ok'
}
catch {
    return New-Result $false 'Failed' $_.Exception.Message
}
