#Requires -RunAsAdministrator
<#
.SYNOPSIS
    PONI - "Plain Open Network Interface". A portable Windows network-profile switcher.

.DESCRIPTION
    A single self-contained WPF application (French / English UI). It switches a PC's
    network configuration in a couple of clicks:
      - Save named IP profiles (address, mask, gateway, DNS) and apply one to a host
        network adapter or inside a running Hyper-V VM (via PowerShell Direct).
      - Route the physical RJ45 port to the host or to a VM (PONI manages the Hyper-V
        switch itself).
      - Edit adapter settings on the fly (DHCP/manual, Private/Public, IP/mask/gateway/DNS).

    No external dependency: PresentationFramework/WPF ships with the .NET Framework that
    is already present on Windows. Data is stored in %APPDATA%\PONI\profiles.json
    (independent of the .exe location). A legacy %APPDATA%\NetManager store is migrated
    once, automatically, on first run.

.NOTES
    Single source file: business logic + the WPF UI as embedded XAML here-strings.
    Compiled to a portable PONI.exe by Build.ps1 (PS2EXE). File must stay UTF-8 with BOM
    (accented French strings + logo injection marker).
    Repository: https://github.com/DoodzProg/PONI
#>

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

# ============================================================
# ASYNC MODULE PRE-WARM (Hyper-V / NetAdapter)
# ============================================================
# The first call to Get-VM / Get-NetAdapter lazily loads their PowerShell modules, which
# can take several seconds on a modest machine. Paid on the UI thread it means a blank
# window at startup and a slow first dialog. Doing it here, up front, in a separate
# runspace lets the modules load in parallel while the window is being built, so they are
# ready by the time the user clicks anything.
$script:PrewarmPS = [PowerShell]::Create()
$script:PrewarmPS.AddScript("Get-NetAdapter -ErrorAction SilentlyContinue | Out-Null; Get-VM -ErrorAction SilentlyContinue | Out-Null") | Out-Null
$script:PrewarmHandle = $script:PrewarmPS.BeginInvoke()

# ============================================================
# CONSTANTS / PATHS
# ============================================================
$StoreDir       = Join-Path $env:APPDATA "PONI"
$StorePath      = Join-Path $StoreDir "profiles.json"
# Legacy project name (NetManager): the existing profile store is migrated once to the
# new folder so nothing is lost across the rename.
$LegacyStorePath = Join-Path $env:APPDATA "NetManager\profiles.json"
$RJ45SwitchName = "RJ45-Switch"
$RepoUrl        = "https://github.com/DoodzProg/PONI"
$AuthorUrl      = "https://doodz.dev"
# Diagnostic log (append). Written by the runspace business blocks when an apply path
# fails in an unexpected way. Purely informative, never read back by the app; inspect or
# clear it by hand.
$DiagLogPath    = Join-Path $StoreDir "poni-diag.log"

$BrushConverter = New-Object System.Windows.Media.BrushConverter
$BrushAccent    = $BrushConverter.ConvertFromString("#0078D4")
$BrushBorder    = $BrushConverter.ConvertFromString("#E1DFDD")
$BrushMuted     = $BrushConverter.ConvertFromString("#605E5C")
$BrushSuccess   = $BrushConverter.ConvertFromString("#107C10")
$BrushError     = $BrushConverter.ConvertFromString("#D83B01")

# PONI logo (128x128 PNG) embedded as base64 - injected by Build.ps1 from PONI_icon.png so
# the .exe stays a single portable file. Used as the window icon and the top-left logo.
# When empty (running the raw script without injection) the app just starts without it.
$LogoPngBase64 = ''
function Get-LogoImageSource {
    if (-not $LogoPngBase64) { return $null }
    try {
        $bytes = [Convert]::FromBase64String($LogoPngBase64)
        $ms = New-Object System.IO.MemoryStream(,$bytes)
        $bi = New-Object System.Windows.Media.Imaging.BitmapImage
        $bi.BeginInit()
        $bi.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
        $bi.StreamSource = $ms
        $bi.EndInit()
        $bi.Freeze()
        return $bi
    } catch { return $null }
}

# ============================================================
# JSON STORE (AppData - independent of the .exe folder)
# ============================================================

# Load the profile store, creating it (and migrating the legacy one) if needed.
function Get-Store {
    if (-not (Test-Path $StoreDir)) { New-Item -ItemType Directory -Path $StoreDir -Force | Out-Null }
    if (-not (Test-Path $StorePath) -and (Test-Path $LegacyStorePath)) {
        Copy-Item -Path $LegacyStorePath -Destination $StorePath -Force
    }
    if (-not (Test-Path $StorePath)) {
        $empty = @{ Profiles = @(); RJ45 = @{ PhysicalAdapter = $null; CurrentTarget = "Host" }; Settings = @{ Language = "en" } }
        $empty | ConvertTo-Json -Depth 5 | Set-Content $StorePath -Encoding UTF8
    }
    $raw = Get-Content $StorePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $profiles = @()
    if ($raw.Profiles) { $profiles = @($raw.Profiles) }
    $settings = if ($raw.Settings) { $raw.Settings } else { [PSCustomObject]@{ Language = "en" } }
    $rj45 = $raw.RJ45
    # Older stores used the French sentinel "Hote"; normalize it so the rest of the code
    # only ever deals with "Host".
    if ($rj45 -and "$($rj45.CurrentTarget)" -eq 'Hote') { $rj45.CurrentTarget = 'Host' }
    # A VMCredentialHistory field from older files is no longer loaded or written back; it
    # disappears silently on the next Save-Store (you cannot get a VM's real account list
    # without already being connected to it).
    return [PSCustomObject]@{ Profiles = $profiles; RJ45 = $rj45; Settings = $settings }
}

# Persist the store object as pretty JSON.
function Save-Store($store) { $store | ConvertTo-Json -Depth 5 | Set-Content $StorePath -Encoding UTF8 }

# Names of the Hyper-V VMs on this machine (empty list if Hyper-V is unavailable).
function Get-DetectedVMs { try { return @(Get-VM | Select-Object -ExpandProperty Name) } catch { return @() } }

# Localize a VM state value (a .NET Hyper-V enum, always English) to a display label.
# Any unexpected state falls back to its raw value.
function Format-VMState($state) {
    switch ("$state") {
        "Running"  { T "vmstate_running" }
        "Off"      { T "vmstate_off" }
        "Saved"    { T "vmstate_saved" }
        "Paused"   { T "vmstate_paused" }
        "Starting" { T "vmstate_starting" }
        "Stopping" { T "vmstate_stopping" }
        default    { "$state" }
    }
}

function Test-IPv4([string]$ip) {
    if ([string]::IsNullOrWhiteSpace($ip)) { return $false }
    return $ip -match '^(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)(\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)){3}$'
}

# Convert a subnet mask (255.255.255.0) <-> a CIDR prefix length (24). Used internally
# by New-NetIPAddress / Invoke-Command.
function ConvertTo-PrefixLength([string]$mask) {
    if (-not (Test-IPv4 $mask)) { return $null }
    $bits = ($mask -split '\.' | ForEach-Object { [Convert]::ToString([int]$_, 2).PadLeft(8, '0') }) -join ''
    if ($bits -notmatch '^1*0*$') { return $null }
    return ($bits.ToCharArray() | Where-Object { $_ -eq '1' }).Count
}
function ConvertTo-SubnetMask([int]$prefix) {
    if ($prefix -lt 0 -or $prefix -gt 32) { return $null }
    $bits = ('1' * $prefix).PadRight(32, '0')
    $octets = for ($i = 0; $i -lt 4; $i++) { [Convert]::ToInt32($bits.Substring($i * 8, 8), 2) }
    return ($octets -join '.')
}

# ============================================================
# TRANSLATIONS (FR / EN)
# ============================================================
# The fr = @{ ... } sub-table below is the French translation catalogue; its string
# values are intentionally in French. Everything else in this file is English.
$Strings = @{
    fr = @{
        nav_profiles          = "Profils réseau"
        nav_rj45               = "Port RJ45 pour VM"
        nav_quit               = "Quitter"
        lang_label             = "Langue"
        profiles_header        = "Profils réseau enregistrés"
        btn_new                = "Nouveau"
        btn_edit               = "Modifier"
        btn_delete             = "Supprimer"
        btn_apply              = "Appliquer"
        btn_refresh            = "Actualiser"
        col_name               = "Nom"
        col_ipmask             = "IP / Masque"
        col_last_target        = "Dernière cible"
        rj45_header            = "Port RJ45 - Hôte / VMs"
        rj45_adapter_label     = "Carte physique RJ45 : {0}"
        rj45_adapter_undefined = "(non définie)"
        rj45_host_title        = "Hôte"
        rj45_host_subtitle     = "Carte : {0}"
        rj45_vm_title          = "VM : {0}"
        rj45_vm_subtitle       = "État : {0}"
        rj45_badge             = "RJ45 ICI"
        rj45_current_target    = "Cible actuelle"
        rj45_switch_here       = "Basculer ici"
        rj45_no_vm             = "(aucune VM détectée)"
        rj45_explain           = "Le port RJ45 physique (carte {0}) peut être relié soit à Windows (l'hôte), soit à une machine virtuelle - jamais aux deux. Tant qu'il est relié à une VM, l'hôte n'a plus de réseau sur cette carte. PONI crée/retire tout seul le commutateur nécessaire."
        rj45_state_host        = "Le port RJ45 est actuellement relié à l'HÔTE."
        rj45_state_vm          = "Le port RJ45 est actuellement relié à la VM {0}."
        rj45_state_unset       = "Le port RJ45 n'est pas encore configuré (relie-le à l'hôte ou à une VM ci-dessous)."
        err_rj45_adapter_unavailable = "La carte '{0}' n'est pas disponible (connexion Wi-Fi active, ou carte désactivée)."
        rj45_blocks_host_title = "Port RJ45 relié à une VM"
        rj45_blocks_host_msg   = "Le port RJ45 (carte '{0}') est actuellement relié à une VM. Impossible de configurer cette carte côté hôte tant que c'est le cas.`n`nRendre le port RJ45 à l'hôte maintenant, puis appliquer le profil ?"
        vmstate_running        = "En marche"
        vmstate_off            = "Arrêtée"
        vmstate_saved          = "Enregistrée"
        vmstate_paused         = "Suspendue"
        vmstate_starting       = "Démarrage..."
        vmstate_stopping       = "Arrêt..."
        status_ready           = "Prêt."
        grid_last_target_host  = "Hôte : {0}"
        grid_last_target_vm    = "VM : {0} ({1})"
        grid_never_applied     = "Jamais appliqué"
        status_list_refreshed  = "Liste actualisée."
        status_profile_created = "Profil '{0}' créé."
        status_profile_updated = "Profil '{0}' modifié."
        status_profile_deleted = "Profil '{0}' supprimé."
        status_select_apply    = "Sélectionne un profil à appliquer."
        status_select_edit     = "Sélectionne un profil à modifier."
        status_select_delete   = "Sélectionne un profil à supprimer."
        status_applying_host   = "Application en cours sur l'hôte..."
        status_applying_vm     = "Application en cours dans la VM '{0}'..."
        status_vm_missing      = "La VM '{0}' n'existe plus."
        status_rj45_switching  = "Basculement du port RJ45 en cours..."
        status_rj45_releasing  = "Le port RJ45 est rendu à l'hôte, puis le profil sera appliqué..."
        status_applied_host    = "IP appliquée sur l'hôte ({0})."
        status_applied_vm      = "IP appliquée dans la VM '{0}'."
        status_rj45_to_host    = "RJ45 rendu à l'hôte."
        status_rj45_to_vm      = "RJ45 associé à la VM '{0}'. Redémarre-la si la carte vient d'être ajoutée."
        status_failed          = "Échec : {0}"
        status_unknown_error   = "Erreur inconnue."
        status_vm_adapter_not_found = "Carte réseau introuvable dans la VM (vérifie qu'elle est démarrée et que la carte existe)."
        apply_vm_running_only  = "Seules les VMs actuellement démarrées peuvent recevoir un profil. Démarre la VM voulue (Gestionnaire Hyper-V) puis rouvre cette fenêtre."
        status_vm_not_running  = "La VM '{0}' n'est pas / plus démarrée. Démarre-la, puis réessaie."
        confirm_delete_title   = "Supprimer le profil"
        confirm_delete_msg     = "Supprimer le profil '{0}' ?"
        profile_new_title      = "Nouveau profil"
        profile_edit_title     = "Modifier le profil"
        profile_fetch_btn      = "Récupérer la config actuelle"
        fetch_adapter_prompt   = "Choisis une carte réseau pour importer sa configuration actuelle."
        apply_target_title     = "Cible d'application"
        apply_target_subtitle  = "Profil : {0} ({1})"
        field_name             = "Nom du profil"
        field_ip               = "Adresse IP"
        field_mask             = "Masque réseau"
        field_gateway          = "Passerelle (optionnel)"
        field_dns              = "DNS, séparés par une virgule (optionnel)"
        field_target           = "Cible"
        target_host            = "Hôte"
        target_vm              = "Machine virtuelle"
        field_adapter          = "Carte réseau"
        field_vm_adapter       = "Carte réseau de la VM"
        btn_cancel             = "Annuler"
        btn_save               = "Enregistrer"
        btn_validate           = "Valider"
        err_name_required      = "Le nom du profil est obligatoire."
        err_name_exists        = "Un profil '{0}' existe déjà."
        err_ip_invalid         = "Adresse IP invalide."
        err_mask_invalid       = "Masque réseau invalide (ex: 255.255.255.0)."
        err_gateway_invalid    = "Passerelle invalide."
        err_dns_invalid        = "DNS invalide : '{0}' n'est pas une adresse IPv4."
        err_choose_adapter     = "Choisis une carte réseau."
        err_choose_vm          = "Choisis une VM."
        err_choose_vm_adapter  = "Choisis une carte réseau de la VM."
        cred_title             = "Identifiants pour la VM '{0}'"
        cred_user              = "Utilisateur"
        cred_pass              = "Mot de passe"
        adapter_picker_prompt  = "Quelle carte réseau physique correspond au port RJ45 ?"
        btn_view_detailed      = "Vue détaillée"
        btn_view_simple        = "Vue simplifiée"
        btn_export             = "Exporter..."
        btn_import             = "Importer..."
        col_ip                 = "IP"
        col_mask               = "Masque"
        col_gateway            = "Passerelle"
        col_dns                = "DNS"
        col_adapter            = "Carte"
        col_attribution        = "Attribution"
        col_type               = "Type"
        attr_dhcp              = "Automatique (DHCP)"
        attr_manual            = "Manuelle"
        nettype_private        = "Privé"
        nettype_public         = "Public"
        nettype_domain         = "Domaine (géré par l'entreprise)"
        current_net_title      = "Configuration réseau actuelle du PC"
        current_net_none       = "Aucune connexion réseau active détectée sur ce PC."
        confirm_dhcp_title     = "Repasser en DHCP"
        confirm_dhcp_msg       = "Remettre la carte « {0} » en attribution automatique (DHCP) ?`n`nLa configuration IP fixe actuelle est retirée et la carte redemande une adresse. Bref coupure réseau sur cette carte."
        status_dhcp_working    = "Remise en DHCP de « {0} »..."
        status_dhcp_reset      = "Carte « {0} » repassée en DHCP."
        status_nettype_working = "Changement du type de réseau de « {0} »..."
        status_nettype_set     = "« {0} » : réseau passé en {1}."
        adhoc_title            = "Configurer la carte « {0} »"
        adhoc_warn             = "Attention : c'est peut-être la carte qui te connecte en ce moment. Une valeur incorrecte peut te déconnecter."
        adhoc_hint             = "Configuration appliquée directement, sans passer par un profil enregistré."
        btn_see_github         = "Voir sur GitHub"
        author_link            = "Créé par Doodz (doodz.dev)"
        export_dialog_title    = "Exporter les profils"
        import_dialog_title    = "Importer des profils"
        export_all             = "Tout exporter"
        export_selection       = "Exporter une sélection..."
        pselect_title          = "Choisis les profils à exporter"
        pselect_hint           = "Clique pour cocher/décocher. Plusieurs choix possibles."
        btn_export_go          = "Exporter"
        status_exported        = "{0} profil(s) exporté(s) vers {1}."
        status_imported        = "{0} profil(s) importé(s), {1} ignoré(s) (nom déjà utilisé)."
        status_import_failed   = "Import impossible : {0}"
        status_import_none     = "Aucun profil valide trouvé dans ce fichier."
        status_export_none     = "Aucun profil à exporter."
        btn_view_logs          = "Voir les logs"
        btn_clear_log          = "Effacer le journal"
        tooltip_click_edit     = "Cliquer pour modifier"
    }
    en = @{
        nav_profiles          = "Network profiles"
        nav_rj45               = "RJ45 port for VM"
        nav_quit               = "Quit"
        lang_label             = "Language"
        profiles_header        = "Registered network profiles"
        btn_new                = "New"
        btn_edit               = "Edit"
        btn_delete             = "Delete"
        btn_apply              = "Apply"
        btn_refresh            = "Refresh"
        col_name               = "Name"
        col_ipmask             = "IP / Mask"
        col_last_target        = "Last target"
        rj45_header            = "RJ45 port - Host / VMs"
        rj45_adapter_label     = "Physical RJ45 adapter: {0}"
        rj45_adapter_undefined = "(not set)"
        rj45_host_title        = "Host"
        rj45_host_subtitle     = "Adapter: {0}"
        rj45_vm_title          = "VM: {0}"
        rj45_vm_subtitle       = "State: {0}"
        rj45_badge             = "RJ45 HERE"
        rj45_current_target    = "Current target"
        rj45_switch_here       = "Switch here"
        rj45_no_vm             = "(no VM detected)"
        rj45_explain           = "The physical RJ45 port (adapter {0}) can be attached either to Windows (the host) or to a virtual machine - never both. While attached to a VM, the host has no network on that adapter. PONI creates/removes the needed switch automatically."
        rj45_state_host        = "The RJ45 port is currently attached to the HOST."
        rj45_state_vm          = "The RJ45 port is currently attached to VM {0}."
        rj45_state_unset       = "The RJ45 port is not configured yet (attach it to the host or a VM below)."
        err_rj45_adapter_unavailable = "Adapter '{0}' is not available (Wi-Fi connection active, or adapter disabled)."
        rj45_blocks_host_title = "RJ45 port attached to a VM"
        rj45_blocks_host_msg   = "The RJ45 port (adapter '{0}') is currently attached to a VM. This adapter cannot be configured on the host while that is the case.`n`nReturn the RJ45 port to the host now, then apply the profile?"
        vmstate_running        = "Running"
        vmstate_off            = "Off"
        vmstate_saved          = "Saved"
        vmstate_paused         = "Paused"
        vmstate_starting       = "Starting..."
        vmstate_stopping       = "Stopping..."
        status_ready           = "Ready."
        grid_last_target_host  = "Host: {0}"
        grid_last_target_vm    = "VM: {0} ({1})"
        grid_never_applied     = "Never applied"
        status_list_refreshed  = "List refreshed."
        status_profile_created = "Profile '{0}' created."
        status_profile_updated = "Profile '{0}' updated."
        status_profile_deleted = "Profile '{0}' deleted."
        status_select_apply    = "Select a profile to apply."
        status_select_edit     = "Select a profile to edit."
        status_select_delete   = "Select a profile to delete."
        status_applying_host   = "Applying on host..."
        status_applying_vm     = "Applying in VM '{0}'..."
        status_vm_missing      = "VM '{0}' no longer exists."
        status_rj45_switching  = "Switching RJ45 port..."
        status_rj45_releasing  = "Returning the RJ45 port to the host, then applying the profile..."
        status_applied_host    = "IP applied on host ({0})."
        status_applied_vm      = "IP applied in VM '{0}'."
        status_rj45_to_host    = "RJ45 returned to host."
        status_rj45_to_vm      = "RJ45 attached to VM '{0}'. Restart it if the adapter was just added."
        status_failed          = "Failed: {0}"
        status_unknown_error   = "Unknown error."
        status_vm_adapter_not_found = "Network adapter not found in the VM (check it's running and the adapter exists)."
        apply_vm_running_only  = "Only VMs that are currently running can receive a profile. Start the VM you want (Hyper-V Manager), then reopen this window."
        status_vm_not_running  = "VM '{0}' is not running (anymore). Start it, then try again."
        confirm_delete_title   = "Delete profile"
        confirm_delete_msg     = "Delete profile '{0}'?"
        profile_new_title      = "New profile"
        profile_edit_title     = "Edit profile"
        profile_fetch_btn      = "Import current config"
        fetch_adapter_prompt   = "Choose a network adapter to import its current configuration."
        apply_target_title     = "Application target"
        apply_target_subtitle  = "Profile: {0} ({1})"
        field_name             = "Profile name"
        field_ip               = "IP address"
        field_mask             = "Subnet mask"
        field_gateway          = "Gateway (optional)"
        field_dns              = "DNS, comma-separated (optional)"
        field_target           = "Target"
        target_host            = "Host"
        target_vm              = "Virtual machine"
        field_adapter          = "Network adapter"
        field_vm_adapter       = "VM's network adapter"
        btn_cancel             = "Cancel"
        btn_save               = "Save"
        btn_validate           = "Confirm"
        err_name_required      = "Profile name is required."
        err_name_exists        = "A profile named '{0}' already exists."
        err_ip_invalid         = "Invalid IP address."
        err_mask_invalid       = "Invalid subnet mask (e.g. 255.255.255.0)."
        err_gateway_invalid    = "Invalid gateway."
        err_dns_invalid        = "Invalid DNS: '{0}' is not an IPv4 address."
        err_choose_adapter     = "Choose a network adapter."
        err_choose_vm          = "Choose a VM."
        err_choose_vm_adapter  = "Choose the VM's network adapter."
        cred_title             = "Credentials for VM '{0}'"
        cred_user              = "Username"
        cred_pass              = "Password"
        adapter_picker_prompt  = "Which physical network adapter is the RJ45 port?"
        btn_view_detailed      = "Detailed view"
        btn_view_simple        = "Simple view"
        btn_export             = "Export..."
        btn_import             = "Import..."
        col_ip                 = "IP"
        col_mask               = "Mask"
        col_gateway            = "Gateway"
        col_dns                = "DNS"
        col_adapter            = "Adapter"
        col_attribution        = "Assignment"
        col_type               = "Type"
        attr_dhcp              = "Automatic (DHCP)"
        attr_manual            = "Manual"
        nettype_private        = "Private"
        nettype_public         = "Public"
        nettype_domain         = "Domain (managed by your org)"
        current_net_title      = "This PC's current network configuration"
        current_net_none       = "No active network connection detected on this PC."
        confirm_dhcp_title     = "Back to DHCP"
        confirm_dhcp_msg       = "Set adapter ""{0}"" back to automatic assignment (DHCP)?`n`nThe current static IP config is removed and the adapter asks for a lease again. Brief network drop on that adapter."
        status_dhcp_working    = "Setting ""{0}"" back to DHCP..."
        status_dhcp_reset      = "Adapter ""{0}"" set back to DHCP."
        status_nettype_working = "Changing network type of ""{0}""..."
        status_nettype_set     = """{0}"": network set to {1}."
        adhoc_title            = "Configure adapter ""{0}"""
        adhoc_warn             = "Warning: this may be the adapter you are connected through right now. A wrong value can disconnect you."
        adhoc_hint             = "Applied directly, without going through a saved profile."
        btn_see_github         = "View on GitHub"
        author_link            = "Made by Doodz (doodz.dev)"
        export_dialog_title    = "Export profiles"
        import_dialog_title    = "Import profiles"
        export_all             = "Export all"
        export_selection       = "Export a selection..."
        pselect_title          = "Choose the profiles to export"
        pselect_hint           = "Click to check/uncheck. Multiple choices allowed."
        btn_export_go          = "Export"
        status_exported        = "{0} profile(s) exported to {1}."
        status_imported        = "{0} profile(s) imported, {1} skipped (name already in use)."
        status_import_failed   = "Import failed: {0}"
        status_import_none     = "No valid profile found in that file."
        status_export_none     = "No profile to export."
        btn_view_logs          = "View logs"
        btn_clear_log          = "Clear log"
        tooltip_click_edit     = "Click to edit"
    }
}

function T {
    param([string]$Key, [object[]]$FormatArgs)
    $str = $Strings[$script:Lang][$Key]
    if (-not $str) { return $Key }
    if ($FormatArgs -and $FormatArgs.Count -gt 0) { return ($str -f $FormatArgs) }
    return $str
}

$script:Lang = (Get-Store).Settings.Language
if ($script:Lang -ne "fr" -and $script:Lang -ne "en") { $script:Lang = "en" }

# ============================================================
# BUSINESS LOGIC - run in a separate runspace (async)
# Passed as plain text: each block is self-contained, with no dependency on any external
# function - native cmdlets only. Each block returns a translation key (Key/Args) rather
# than a ready-made sentence, so the UI thread builds the localized message via T().
# ============================================================
$HostApplyScript = @'
param($AdapterName,$IPAddress,$PrefixLength,$Gateway,$DNS,$LogPath)
function Write-Diag($msg) {
    if (-not $LogPath) { return }
    try { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  [host-apply]  $msg" | Add-Content -Path $LogPath -Encoding UTF8 } catch { }
}
try {
    # Instrumentation for AUDIT.md bug #2 ("element not found" on the host): just before
    # New-NetIPAddress, capture the exact state that explains the failure - the adapter
    # received, whether the RJ45 switch is present (created with -AllowManagementOS $false,
    # it strips the physical adapter of a configurable IP stack), and the physical
    # adapters actually visible.
    Write-Diag "--- apply host profile ---"
    Write-Diag "AdapterName param      = '$AdapterName'"
    Write-Diag "target IP / prefix     = $IPAddress / $PrefixLength (gw='$Gateway')"
    # Dedicated try/catch: on a host WITHOUT the Hyper-V module, Get-VMSwitch throws
    # CommandNotFoundException (a terminating error not covered by -ErrorAction) - it must
    # never break applying a profile on the host, which has nothing to do with Hyper-V.
    $rjSw = $null
    try { $rjSw = Get-VMSwitch -Name 'RJ45-Switch' -ErrorAction SilentlyContinue } catch { }
    Write-Diag "RJ45-Switch present    = $([bool]$rjSw)"
    Write-Diag "physical adapters      = $(@(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) -join ' | ')"
    $ifChk = Get-NetAdapter -InterfaceAlias $AdapterName -ErrorAction SilentlyContinue
    Write-Diag "adapter '$AdapterName' visible = $([bool]$ifChk)$(if ($ifChk) { " (status=$($ifChk.Status))" })"

    # -ErrorAction Stop is mandatory here: by default these cmdlets emit a NON-terminating
    # error on failure, which the catch below would never intercept - the script would
    # carry on and report a false success.
    Get-NetIPAddress -InterfaceAlias $AdapterName -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
    # Remove-NetIPAddress does NOT remove the default route: without this cleanup,
    # re-applying the SAME gateway fails with "instance DefaultGateway already exists"
    # (e.g. changing the IP while keeping the same gateway).
    Get-NetRoute -InterfaceAlias $AdapterName -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
        Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
    if ($Gateway) { New-NetIPAddress -InterfaceAlias $AdapterName -IPAddress $IPAddress -PrefixLength $PrefixLength -DefaultGateway $Gateway -ErrorAction Stop | Out-Null }
    else { New-NetIPAddress -InterfaceAlias $AdapterName -IPAddress $IPAddress -PrefixLength $PrefixLength -ErrorAction Stop | Out-Null }
    # Empty DNS in the profile = REMOVE the adapter's static DNS servers (back to auto).
    # Otherwise an old DNS entry stayed in place when applying a profile with no DNS.
    if ($DNS -and @($DNS).Count -gt 0) {
        Set-DnsClientServerAddress -InterfaceAlias $AdapterName -ServerAddresses $DNS -ErrorAction Stop
    } else {
        Set-DnsClientServerAddress -InterfaceAlias $AdapterName -ResetServerAddresses -ErrorAction SilentlyContinue
    }
    Set-NetConnectionProfile -InterfaceAlias $AdapterName -NetworkCategory Private -ErrorAction SilentlyContinue
    $check = Get-NetIPAddress -InterfaceAlias $AdapterName -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -eq $IPAddress }
    if (-not $check) { throw "IP address not confirmed after applying (please check manually)." }
    Write-Diag "SUCCESS - ip applied on '$AdapterName'"
    [PSCustomObject]@{ Success = $true; Key = "status_applied_host"; Args = @($AdapterName) }
} catch {
    Write-Diag "FAILURE - $($_.Exception.Message)"
    [PSCustomObject]@{ Success = $false; Key = "status_failed"; Args = @($_.Exception.Message) }
}
'@

$VMApplyScript = @'
param($VMName,$Credential,$VMAdapterMac,$IPAddress,$PrefixLength,$Gateway,$DNS,$LogPath)
function Write-Diag($msg) {
    if (-not $LogPath) { return }
    try { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  [vm-apply]  $msg" | Add-Content -Path $LogPath -Encoding UTF8 } catch { }
}
try {
    # Instrumentation for AUDIT.md bug #5: log the RAW MAC received from the host
    # (Get-VMNetworkAdapter format, no separator); if resolution fails, the error message
    # also carries the list (name = MAC) of the adapters seen in the VM, to confirm we are
    # targeting the RJ45 adapter and not another one (e.g. the LMU-Dongle adapter).
    Write-Diag "--- apply vm profile ---  VM='$VMName'"
    Write-Diag "VMAdapterMac raw (host) = '$VMAdapterMac'"
    # PONI does not start the VM: it must already be running (checked on the UI side).
    # -ErrorAction Stop on Invoke-Command is mandatory: bad credentials (or a VM not ready
    # yet) produce a NON-terminating error by default, which the catch below would never
    # intercept - the script would carry on and report a false success even though nothing
    # was applied inside the VM.
    Invoke-Command -VMName $VMName -Credential $Credential -ErrorAction Stop -ScriptBlock {
        param($VMAdapterMac,$IPAddress,$PrefixLength,$Gateway,$DNS)
        # Resolve by MAC address: the guest-side name "Ethernet N" bears no relation to
        # the adapter name on the Hyper-V side (e.g. "Carte-RJ45") and changes every time
        # a virtual adapter is added/removed. The MAC address is stable and lets us find
        # the right adapter whatever its Windows name inside the VM.
        $macNorm = ($VMAdapterMac -replace '[:-]', '').ToUpper()
        $guestAdapter = Get-NetAdapter | Where-Object { ($_.MacAddress -replace '[:-]', '').ToUpper() -eq $macNorm } | Select-Object -First 1
        if (-not $guestAdapter) {
            $seen = (@(Get-NetAdapter | ForEach-Object { "$($_.Name)=$($_.MacAddress)" }) -join ' ; ')
            throw "AdapterNotFound|target=$macNorm|seen=$seen"
        }
        $AdapterName = $guestAdapter.Name
        Get-NetIPAddress -InterfaceAlias $AdapterName -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
        # Also clear the default route, otherwise re-applying the same gateway fails
        # ("instance DefaultGateway already exists").
        Get-NetRoute -InterfaceAlias $AdapterName -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
            Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
        if ($Gateway) { New-NetIPAddress -InterfaceAlias $AdapterName -IPAddress $IPAddress -PrefixLength $PrefixLength -DefaultGateway $Gateway -ErrorAction Stop | Out-Null }
        else { New-NetIPAddress -InterfaceAlias $AdapterName -IPAddress $IPAddress -PrefixLength $PrefixLength -ErrorAction Stop | Out-Null }
        # Empty DNS = remove the adapter's static DNS servers (back to auto).
        if ($DNS -and @($DNS).Count -gt 0) {
            Set-DnsClientServerAddress -InterfaceAlias $AdapterName -ServerAddresses $DNS -ErrorAction Stop
        } else {
            Set-DnsClientServerAddress -InterfaceAlias $AdapterName -ResetServerAddresses -ErrorAction SilentlyContinue
        }
        Set-NetConnectionProfile -InterfaceAlias $AdapterName -NetworkCategory Private -ErrorAction SilentlyContinue
        $check = Get-NetIPAddress -InterfaceAlias $AdapterName -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -eq $IPAddress }
        if (-not $check) { throw "IP address not confirmed after applying (please check manually)." }
        "guest adapter resolved = '$AdapterName' (mac=$($guestAdapter.MacAddress))"
    } -ArgumentList $VMAdapterMac,$IPAddress,$PrefixLength,$Gateway,$DNS | ForEach-Object { Write-Diag $_ }
    Write-Diag "SUCCESS - ip applied in VM '$VMName'"
    [PSCustomObject]@{ Success = $true; Key = "status_applied_vm"; Args = @($VMName) }
} catch {
    Write-Diag "FAILURE - $($_.Exception.Message)"
    if ($_.Exception.Message -match "AdapterNotFound") {
        [PSCustomObject]@{ Success = $false; Key = "status_vm_adapter_not_found"; Args = @() }
    } else {
        [PSCustomObject]@{ Success = $false; Key = "status_failed"; Args = @($_.Exception.Message) }
    }
}
'@

$RJ45ToHostScript = @'
param($SwitchName,$PhysicalAdapter,$VMName)
try {
    # Hard safeguard: PONI only ever removes its own RJ45 switch. The "LMU-Dongle-Switch"
    # (a permanent host<->VM link for a licensing service) must NEVER be removed, whatever
    # happens.
    if ($SwitchName -eq 'LMU-Dongle-Switch' -or $SwitchName -notmatch '^RJ45') {
        throw "Safety refusal: PONI only removes the RJ45 switch (requested: '$SwitchName')."
    }
    $sw = Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue
    if ($sw) {
        # -ErrorAction Stop: otherwise a failure (switch still in use, locked, etc.) goes
        # unnoticed and the app reports "RJ45 returned to the host" while the switch - and
        # therefore the routing to the VM - is still active.
        Remove-VMSwitch -Name $SwitchName -Force -ErrorAction Stop
    }
    # Explicit check: never report success without confirming the switch is really gone.
    $still = Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue
    if ($still) { throw "Switch '$SwitchName' still exists after the removal attempt." }
    [PSCustomObject]@{ Success = $true; Key = "status_rj45_to_host"; Args = @() }
} catch {
    [PSCustomObject]@{ Success = $false; Key = "status_failed"; Args = @($_.Exception.Message) }
}
'@

$RJ45ToVMScript = @'
param($SwitchName,$PhysicalAdapter,$VMName)
try {
    # 1. The RJ45 switch: created automatically if missing (external, on the RJ45 adapter,
    #    with no management-OS access - a deliberate choice, see AUDIT.md).
    $sw = Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue
    if (-not $sw) { New-VMSwitch -Name $SwitchName -NetAdapterName $PhysicalAdapter -AllowManagementOS $false -ErrorAction Stop | Out-Null }

    # 2. The RJ45 adapter in the VM: managed automatically. Created if missing; if
    #    duplicates exist (from past mishaps), keep the first and remove the others -
    #    these are PONI adapters, never the LMU adapter. "Carte-RJ45" is the legacy name
    #    from earlier versions and is still recognised.
    $adapterName  = "RJ45-Adapter"
    $legacyName   = "Carte-RJ45"
    $rjAdapters = @(Get-VMNetworkAdapter -VMName $VMName -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -eq $adapterName -or $_.Name -eq $legacyName })
    if ($rjAdapters.Count -eq 0) {
        Add-VMNetworkAdapter -VMName $VMName -SwitchName $SwitchName -Name $adapterName -ErrorAction Stop
        $keptName = $adapterName
    } else {
        if ($rjAdapters.Count -gt 1) {
            $rjAdapters | Select-Object -Skip 1 | ForEach-Object { Remove-VMNetworkAdapter -VMNetworkAdapter $_ -ErrorAction SilentlyContinue }
        }
        $keptName = $rjAdapters[0].Name
        Connect-VMNetworkAdapter -VMName $VMName -Name $keptName -SwitchName $SwitchName -ErrorAction SilentlyContinue
    }

    # 3. A synthetic adapter added hot sometimes stays "Disconnected" on the guest side
    #    until a full VM restart; a Disconnect/Connect cycle makes that case less frequent
    #    (a quiet, best-effort nudge). NB: Restart-VMNetworkAdapter does not exist in any
    #    version of the Hyper-V module.
    Disconnect-VMNetworkAdapter -VMName $VMName -Name $keptName -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Connect-VMNetworkAdapter -VMName $VMName -Name $keptName -SwitchName $SwitchName -ErrorAction SilentlyContinue

    # 4. Real check: at least one of the VM's adapters is actually on the RJ45 switch.
    $check = Get-VMNetworkAdapter -VMName $VMName -ErrorAction SilentlyContinue | Where-Object { $_.SwitchName -eq $SwitchName }
    if (-not $check) { throw "The VM network adapter is not confirmed on switch '$SwitchName'." }
    [PSCustomObject]@{ Success = $true; Key = "status_rj45_to_vm"; Args = @($VMName) }
} catch {
    [PSCustomObject]@{ Success = $false; Key = "status_failed"; Args = @($_.Exception.Message) }
}
'@

# Put a host adapter back on automatic assignment (DHCP) - the "repair" button.
$DhcpResetScript = @'
param($AdapterName)
try {
    Get-NetIPAddress -InterfaceAlias $AdapterName -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.PrefixOrigin -eq 'Manual' } |
        Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
    Get-NetRoute -InterfaceAlias $AdapterName -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
        Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
    Set-NetIPInterface -InterfaceAlias $AdapterName -AddressFamily IPv4 -Dhcp Enabled -ErrorAction Stop
    Set-DnsClientServerAddress -InterfaceAlias $AdapterName -ResetServerAddresses -ErrorAction Stop
    Restart-NetAdapter -InterfaceAlias $AdapterName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    $chk = Get-NetIPInterface -InterfaceAlias $AdapterName -AddressFamily IPv4 -ErrorAction SilentlyContinue
    if (-not $chk -or "$($chk.Dhcp)" -ne 'Enabled') { throw "DHCP not confirmed on '$AdapterName'." }
    [PSCustomObject]@{ Success = $true; Key = "status_dhcp_reset"; Args = @($AdapterName) }
} catch {
    [PSCustomObject]@{ Success = $false; Key = "status_failed"; Args = @($_.Exception.Message) }
}
'@

# Change a network's category (Private / Public). Domain = not changeable.
$NetCategoryScript = @'
param($AdapterName,$Category)
try {
    $prof = Get-NetConnectionProfile -InterfaceAlias $AdapterName -ErrorAction Stop
    if ("$($prof.NetworkCategory)" -eq 'DomainAuthenticated') {
        throw "Domain network (managed by the organization): category cannot be changed."
    }
    Set-NetConnectionProfile -InterfaceAlias $AdapterName -NetworkCategory $Category -ErrorAction Stop
    Start-Sleep -Milliseconds 500
    $now = (Get-NetConnectionProfile -InterfaceAlias $AdapterName -ErrorAction SilentlyContinue).NetworkCategory
    if ("$now" -ne $Category) { throw "Category not confirmed (blocked by a policy?)." }
    [PSCustomObject]@{ Success = $true; Key = "status_nettype_set"; Args = @($AdapterName, $Category) }
} catch {
    [PSCustomObject]@{ Success = $false; Key = "status_failed"; Args = @($_.Exception.Message) }
}
'@

# ============================================================
# ASYNCHRONOUS EXECUTION (in-process runspace, UI never blocked)
# ============================================================
function Invoke-Async {
    param(
        [string]$ScriptText,
        [hashtable]$Params,
        [array]$Buttons,
        [scriptblock]$OnDone
    )
    foreach ($b in $Buttons) { if ($b) { $b.IsEnabled = $false } }

    $ps = [PowerShell]::Create()
    $ps.AddScript($ScriptText) | Out-Null
    foreach ($k in $Params.Keys) { $ps.AddParameter($k, $Params[$k]) | Out-Null }
    $asyncResult = $ps.BeginInvoke()

    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds(300)

    $tick = {
        if ($asyncResult.IsCompleted) {
            $timer.Stop()
            $output = $null
            try { $output = $ps.EndInvoke($asyncResult) } catch { }
            $result = if ($output -and $output.Count -gt 0) {
                $output[$output.Count - 1]
            } else {
                $errMsg = if ($ps.Streams.Error.Count -gt 0) { $ps.Streams.Error[0].ToString() } else { T "status_unknown_error" }
                [PSCustomObject]@{ Success = $false; Key = "status_failed"; Args = @($errMsg) }
            }
            $ps.Dispose()
            foreach ($b in $Buttons) { if ($b) { $b.IsEnabled = $true } }
            & $OnDone $result
        }
    }.GetNewClosure()

    $timer.Add_Tick($tick)
    $timer.Start()
}

# ============================================================
# XAML - MAIN WINDOW
# ============================================================
$MainXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="PONI" Width="1040" Height="660" MinWidth="860" MinHeight="560"
        WindowStartupLocation="CenterScreen" Background="#F3F3F3" FontFamily="Segoe UI" FontSize="13">
    <Window.Resources>
        <Style x:Key="NavButton" TargetType="Button">
            <Setter Property="HorizontalContentAlignment" Value="Left"/>
            <Setter Property="Padding" Value="20,12"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Foreground" Value="#201F1E"/>
            <Setter Property="FontSize" Value="14"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="bd" Background="{TemplateBinding Background}" BorderThickness="4,0,0,0" BorderBrush="Transparent">
                            <ContentPresenter Margin="{TemplateBinding Padding}" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bd" Property="Background" Value="#EDEBE9"/>
                            </Trigger>
                            <Trigger Property="Tag" Value="Active">
                                <Setter TargetName="bd" Property="Background" Value="#E5F1FB"/>
                                <Setter TargetName="bd" Property="BorderBrush" Value="#0078D4"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        <Style x:Key="AccentButton" TargetType="Button">
            <Setter Property="Background" Value="#0078D4"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Padding" Value="16,8"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="4">
                            <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bd" Property="Background" Value="#106EBE"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter TargetName="bd" Property="Background" Value="#C7C6C4"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        <Style x:Key="FlatButton" TargetType="Button">
            <Setter Property="Background" Value="White"/>
            <Setter Property="Foreground" Value="#201F1E"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="BorderBrush" Value="#E1DFDD"/>
            <Setter Property="Padding" Value="16,8"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4">
                            <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bd" Property="Background" Value="#F3F2F1"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        <Style x:Key="SidebarButton" TargetType="Button">
            <Setter Property="Background" Value="#3B3A39"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="4" Padding="10,7">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bd" Property="Background" Value="#514F4D"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </Window.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition x:Name="LogRow" Height="112" MinHeight="46"/>
        </Grid.RowDefinitions>

        <Grid Grid.Row="0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="210"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <Border Grid.Column="0" Background="#FAFAFA" BorderBrush="#E1DFDD" BorderThickness="0,0,1,0">
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
                    <StackPanel Grid.Row="0" Margin="0,20,0,0">
                        <StackPanel Orientation="Horizontal" Margin="20,0,0,0">
                            <Image x:Name="LogoImage" Width="30" Height="30" Margin="0,0,10,0" VerticalAlignment="Center"
                                   RenderOptions.BitmapScalingMode="HighQuality"/>
                            <TextBlock VerticalAlignment="Center"><Run Text="PONI" FontSize="18" FontWeight="SemiBold" Foreground="#201F1E"/><Run Text="  v1.0" FontSize="11" Foreground="#605E5C"/></TextBlock>
                        </StackPanel>
                        <TextBlock Text="Plain Open Network Interface" FontSize="10" Foreground="#605E5C" Margin="20,1,0,12"/>

                        <Button x:Name="BtnGitHubLink" Style="{StaticResource SidebarButton}" Margin="20,0,20,0" ToolTip="github.com/DoodzProg/PONI">
                            <StackPanel Orientation="Horizontal">
                                <TextBlock x:Name="LblGitHub" FontSize="12" VerticalAlignment="Center" Margin="0,0,7,0"/>
                                <Path Width="13" Height="13" Stretch="Uniform" Fill="White"
                                      Data="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"/>
                            </StackPanel>
                        </Button>

                        <Button x:Name="NavProfiles" Style="{StaticResource NavButton}" Tag="Active" Margin="0,16,0,0"/>
                        <Button x:Name="NavRJ45" Style="{StaticResource NavButton}"/>
                        <Button x:Name="NavQuit" Style="{StaticResource NavButton}" Margin="0,40,0,0"/>
                    </StackPanel>
                    <StackPanel Grid.Row="1" Margin="20,0,20,16">
                        <TextBlock x:Name="LabelLang" FontSize="11" Foreground="#605E5C" Margin="0,0,0,4"/>
                        <ComboBox x:Name="LangCombo" Padding="6,4" FontSize="12"/>
                    </StackPanel>
                </Grid>
            </Border>

            <Grid Grid.Column="1" Margin="24">
                <Grid x:Name="ProfilesView" Visibility="Visible">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>

                    <TextBlock x:Name="CurNetHeader" Grid.Row="0" FontSize="20" FontWeight="SemiBold" Margin="0,0,0,10" Foreground="#201F1E"/>

                    <Border x:Name="CurrentNetBox" Grid.Row="1" Background="#FBFBFC" BorderBrush="#E1DFDD" BorderThickness="1" CornerRadius="4" Padding="12,10" Margin="0,0,0,0">
                        <ScrollViewer x:Name="CurNetScroller" HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled">
                            <Grid x:Name="CurNetGrid"/>
                        </ScrollViewer>
                    </Border>

                    <Border Grid.Row="2" Height="1" Background="#E1DFDD" Margin="0,18,0,18"/>

                    <TextBlock x:Name="ProfilesHeader" Grid.Row="3" FontSize="20" FontWeight="SemiBold" Margin="0,0,0,10" Foreground="#201F1E"/>

                    <StackPanel Grid.Row="4" Orientation="Horizontal" Margin="0,0,0,12">
                        <Button x:Name="BtnNewProfile" Style="{StaticResource AccentButton}" Margin="0,0,8,0"/>
                        <Button x:Name="BtnEditProfile" Style="{StaticResource FlatButton}" Margin="0,0,8,0"/>
                        <Button x:Name="BtnDeleteProfile" Style="{StaticResource FlatButton}" Margin="0,0,8,0"/>
                        <Button x:Name="BtnApplyProfile" Style="{StaticResource AccentButton}"/>
                    </StackPanel>

                    <Grid Grid.Row="5" Margin="0,0,0,6">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <StackPanel Grid.Column="0" Orientation="Horizontal" VerticalAlignment="Bottom">
                            <Button x:Name="BtnRefreshProfiles" Style="{StaticResource FlatButton}" Padding="12,6" FontSize="12" Margin="0,0,8,0"/>
                            <Button x:Name="BtnToggleView" Style="{StaticResource FlatButton}" Padding="12,6" FontSize="12"/>
                        </StackPanel>
                        <StackPanel Grid.Column="1" Orientation="Vertical" HorizontalAlignment="Right">
                            <Button x:Name="BtnExportProfiles" Style="{StaticResource FlatButton}" Padding="9,2" FontSize="11" Margin="0,0,0,3" HorizontalContentAlignment="Center"/>
                            <Button x:Name="BtnImportProfiles" Style="{StaticResource FlatButton}" Padding="9,2" FontSize="11" HorizontalContentAlignment="Center"/>
                        </StackPanel>
                    </Grid>

                    <Border Grid.Row="6" Background="White" BorderBrush="#E1DFDD" BorderThickness="1" CornerRadius="6">
                        <DataGrid x:Name="ProfilesGrid" AutoGenerateColumns="False" IsReadOnly="True"
                                  HeadersVisibility="Column" GridLinesVisibility="Horizontal" HorizontalGridLinesBrush="#F3F2F1"
                                  RowHeight="36" Background="Transparent" BorderThickness="0" CanUserAddRows="False"
                                  CanUserResizeRows="False" SelectionMode="Single" SelectionUnit="FullRow" Margin="1">
                            <DataGrid.Resources>
                                <SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}" Color="#E5F1FB"/>
                                <SolidColorBrush x:Key="{x:Static SystemColors.HighlightTextBrushKey}" Color="#201F1E"/>
                                <SolidColorBrush x:Key="{x:Static SystemColors.ControlBrushKey}" Color="#E5F1FB"/>
                                <SolidColorBrush x:Key="{x:Static SystemColors.ControlTextBrushKey}" Color="#201F1E"/>
                            </DataGrid.Resources>
                            <DataGrid.ColumnHeaderStyle>
                                <Style TargetType="DataGridColumnHeader">
                                    <Setter Property="Background" Value="#FAFAFA"/>
                                    <Setter Property="Foreground" Value="#605E5C"/>
                                    <Setter Property="FontWeight" Value="SemiBold"/>
                                    <Setter Property="Padding" Value="10,8"/>
                                    <Setter Property="HorizontalContentAlignment" Value="Left"/>
                                    <Setter Property="BorderBrush" Value="#E1DFDD"/>
                                    <Setter Property="BorderThickness" Value="0,0,0,1"/>
                                </Style>
                            </DataGrid.ColumnHeaderStyle>
                            <DataGrid.RowStyle>
                                <Style TargetType="DataGridRow">
                                    <Setter Property="Foreground" Value="#201F1E"/>
                                    <Style.Triggers>
                                        <Trigger Property="IsSelected" Value="True">
                                            <Setter Property="Background" Value="#E5F1FB"/>
                                            <Setter Property="Foreground" Value="#201F1E"/>
                                        </Trigger>
                                    </Style.Triggers>
                                </Style>
                            </DataGrid.RowStyle>
                            <DataGrid.CellStyle>
                                <Style TargetType="DataGridCell">
                                    <Setter Property="Padding" Value="10,0"/>
                                    <Setter Property="BorderThickness" Value="0"/>
                                    <Setter Property="Background" Value="Transparent"/>
                                    <Style.Triggers>
                                        <Trigger Property="IsSelected" Value="True">
                                            <Setter Property="Background" Value="Transparent"/>
                                            <Setter Property="Foreground" Value="#201F1E"/>
                                        </Trigger>
                                    </Style.Triggers>
                                </Style>
                            </DataGrid.CellStyle>
                            <DataGrid.Columns>
                                <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="1.2*"/>
                                <DataGridTextColumn Header="IP / Mask" Binding="{Binding IPMask}" Width="1.3*"/>
                                <DataGridTextColumn Header="Last target" Binding="{Binding LastTarget}" Width="1.8*"/>
                            </DataGrid.Columns>
                        </DataGrid>
                    </Border>
                </Grid>

                <Grid x:Name="RJ45View" Visibility="Collapsed">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <TextBlock x:Name="RJ45Header" Grid.Row="0" FontSize="20" FontWeight="SemiBold" Margin="0,0,0,6" Foreground="#201F1E"/>
                    <TextBlock x:Name="RJ45Explain" Grid.Row="1" Foreground="#605E5C" FontSize="12" TextWrapping="Wrap" Margin="0,0,0,10"/>
                    <TextBlock x:Name="RJ45AdapterLabel" Grid.Row="2" Foreground="#605E5C" Margin="0,0,0,8"/>
                    <Border x:Name="RJ45StateBox" Grid.Row="3" Background="#F3F9FD" BorderBrush="#C7E0F4" BorderThickness="1" CornerRadius="4" Padding="12,9" Margin="0,2,0,20">
                        <StackPanel Orientation="Horizontal">
                            <Border Width="18" Height="18" CornerRadius="9" Background="#0078D4" VerticalAlignment="Center" Margin="0,0,10,0">
                                <TextBlock Text="i" Foreground="White" FontWeight="Bold" FontSize="12" FontFamily="Georgia" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                            </Border>
                            <TextBlock x:Name="RJ45StateText" FontSize="13" Foreground="#201F1E" VerticalAlignment="Center" TextWrapping="Wrap"/>
                        </StackPanel>
                    </Border>
                    <ScrollViewer Grid.Row="4" VerticalScrollBarVisibility="Auto">
                        <StackPanel x:Name="RJ45CardsPanel"/>
                    </ScrollViewer>
                </Grid>

                <TextBlock x:Name="AuthorLink" HorizontalAlignment="Right" VerticalAlignment="Top" Panel.ZIndex="10"
                           FontStyle="Italic" FontSize="10" Foreground="#A6A6A6" Cursor="Hand" ToolTip="doodz.dev"
                           Margin="0,-2,0,0"/>
            </Grid>
        </Grid>

        <GridSplitter Grid.Row="1" Height="6" HorizontalAlignment="Stretch" VerticalAlignment="Center"
                      Background="#EDEBE9" ResizeDirection="Rows" ResizeBehavior="PreviousAndNext"/>

        <Border Grid.Row="2" Background="#FAFAFA" BorderBrush="#E1DFDD" BorderThickness="0,1,0,0">
            <Grid Margin="12,6">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <ScrollViewer x:Name="LogScroller" Grid.Column="0" VerticalScrollBarVisibility="Auto">
                    <ItemsControl x:Name="LogList"/>
                </ScrollViewer>
                <StackPanel Grid.Column="1" Orientation="Vertical" VerticalAlignment="Top" Margin="10,0,4,0">
                    <Button x:Name="BtnViewLogs" Style="{StaticResource FlatButton}" Padding="9,3" Margin="0,0,0,4" FontSize="11" HorizontalContentAlignment="Center"/>
                    <Button x:Name="BtnClearLog" Style="{StaticResource FlatButton}" Padding="9,3" FontSize="11" HorizontalContentAlignment="Center"/>
                </StackPanel>
            </Grid>
        </Border>
    </Grid>
</Window>
'@

# ============================================================
# XAML - PROFILE DIALOG (create / edit)
# ============================================================
$ProfileXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="PONI" Width="480" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" Background="#F3F3F3" FontFamily="Segoe UI" FontSize="13">
    <StackPanel Margin="20">
        <Grid Margin="0,0,0,16">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock x:Name="FormTitle" Grid.Column="0" FontWeight="SemiBold" FontSize="16" Foreground="#201F1E" VerticalAlignment="Center"/>
            <Button x:Name="BtnFetchConfig" Grid.Column="1" Padding="10,5" FontSize="12" Background="White" BorderBrush="#E1DFDD" BorderThickness="1" Cursor="Hand"/>
        </Grid>

        <TextBlock x:Name="LabelName" Foreground="#605E5C" Margin="0,0,0,4"/>
        <TextBox x:Name="FieldName" Padding="6" Margin="0,0,0,10"/>

        <TextBlock x:Name="LabelIP" Foreground="#605E5C" Margin="0,0,0,4"/>
        <TextBox x:Name="FieldIP" Padding="6" Margin="0,0,0,10"/>

        <TextBlock x:Name="LabelMask" Foreground="#605E5C" Margin="0,0,0,4"/>
        <TextBox x:Name="FieldMask" Padding="6" Margin="0,0,0,10" Text="255.255.255.0"/>

        <TextBlock x:Name="LabelGateway" Foreground="#605E5C" Margin="0,0,0,4"/>
        <TextBox x:Name="FieldGateway" Padding="6" Margin="0,0,0,10"/>

        <TextBlock x:Name="LabelDNS" Foreground="#605E5C" Margin="0,0,0,4"/>
        <TextBox x:Name="FieldDNS" Padding="6" Margin="0,0,0,14"/>

        <TextBlock x:Name="FormError" Foreground="#D83B01" TextWrapping="Wrap" Margin="0,4,0,10" Visibility="Collapsed"/>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,6,0,0">
            <Button x:Name="FormCancel" Padding="14,7" Margin="0,0,8,0" Background="White" BorderBrush="#E1DFDD" BorderThickness="1" Cursor="Hand"/>
            <Button x:Name="FormSave" Padding="14,7" Background="#0078D4" Foreground="White" BorderThickness="0" Cursor="Hand"/>
        </StackPanel>
    </StackPanel>
</Window>
'@

# ============================================================
# XAML - APPLY-TARGET DIALOG (Host or VM, chosen on every Apply)
# ============================================================
$ApplyTargetXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="PONI" Width="460" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" Background="#F3F3F3" FontFamily="Segoe UI" FontSize="13">
    <StackPanel Margin="20">
        <TextBlock x:Name="ApplyTitle" FontWeight="SemiBold" FontSize="16" Margin="0,0,0,4" Foreground="#201F1E"/>
        <TextBlock x:Name="ApplySubtitle" Foreground="#605E5C" Margin="0,0,0,16"/>

        <TextBlock x:Name="LabelTarget" Foreground="#605E5C" Margin="0,0,0,6"/>
        <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
            <RadioButton x:Name="TargetHost" GroupName="ApplyTarget" IsChecked="True" Margin="0,0,20,0"/>
            <RadioButton x:Name="TargetVM" GroupName="ApplyTarget"/>
        </StackPanel>

        <StackPanel x:Name="HostPanel" Visibility="Visible">
            <TextBlock x:Name="LabelAdapter" Foreground="#605E5C" Margin="0,0,0,4"/>
            <ComboBox x:Name="HostAdapterCombo" Padding="6" Margin="0,0,0,10"/>
        </StackPanel>

        <StackPanel x:Name="VMPanel" Visibility="Collapsed">
            <TextBlock x:Name="LabelVM" Foreground="#605E5C" Margin="0,0,0,4"/>
            <TextBlock x:Name="VMHint" Foreground="#605E5C" FontSize="11" TextWrapping="Wrap" Margin="0,0,0,4"/>
            <ComboBox x:Name="VMCombo" Padding="6" Margin="0,0,0,10"/>
            <TextBlock x:Name="LabelVMAdapter" Foreground="#605E5C" Margin="0,0,0,4"/>
            <ComboBox x:Name="VMAdapterCombo" Padding="6" Margin="0,0,0,10"/>
        </StackPanel>

        <TextBlock x:Name="ApplyError" Foreground="#D83B01" TextWrapping="Wrap" Margin="0,4,0,10" Visibility="Collapsed"/>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,6,0,0">
            <Button x:Name="ApplyCancel" Padding="14,7" Margin="0,0,8,0" Background="White" BorderBrush="#E1DFDD" BorderThickness="1" Cursor="Hand"/>
            <Button x:Name="ApplyOk" Padding="14,7" Background="#0078D4" Foreground="White" BorderThickness="0" Cursor="Hand"/>
        </StackPanel>
    </StackPanel>
</Window>
'@

# ============================================================
# XAML - VM CREDENTIALS DIALOG
# ============================================================
$CredentialXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="PONI" Width="380" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" Background="#F3F3F3" FontFamily="Segoe UI" FontSize="13">
    <StackPanel Margin="20">
        <TextBlock x:Name="CredTitle" FontWeight="SemiBold" FontSize="15" Margin="0,0,0,14" Foreground="#201F1E"/>
        <TextBlock x:Name="LabelUser" Foreground="#605E5C" Margin="0,0,0,4"/>
        <TextBox x:Name="CredUser" Padding="6" Margin="0,0,0,12"/>
        <TextBlock x:Name="LabelPass" Foreground="#605E5C" Margin="0,0,0,4"/>
        <PasswordBox x:Name="CredPass" Padding="6" Margin="0,0,0,20"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button x:Name="CredCancel" Padding="14,7" Margin="0,0,8,0" Background="White" BorderBrush="#E1DFDD" BorderThickness="1" Cursor="Hand"/>
            <Button x:Name="CredOk" Padding="14,7" Background="#0078D4" Foreground="White" BorderThickness="0" Cursor="Hand"/>
        </StackPanel>
    </StackPanel>
</Window>
'@

# ============================================================
# XAML - ON-THE-FLY CONFIG DIALOG (edit a host adapter without a saved profile)
# ============================================================
$AdhocConfigXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="PONI" Width="460" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" Background="#F3F3F3" FontFamily="Segoe UI" FontSize="13">
    <StackPanel Margin="20">
        <TextBlock x:Name="AhTitle" FontWeight="SemiBold" FontSize="16" Foreground="#201F1E" Margin="0,0,0,4"/>
        <TextBlock x:Name="AhHint" Foreground="#605E5C" FontSize="11" TextWrapping="Wrap" Margin="0,0,0,10"/>
        <Border x:Name="AhWarnBox" Background="#FFF4CE" BorderBrush="#E6C200" BorderThickness="1" CornerRadius="4" Padding="10,8" Margin="0,0,0,12" Visibility="Collapsed">
            <TextBlock x:Name="AhWarn" Foreground="#6B5900" FontSize="11" TextWrapping="Wrap"/>
        </Border>

        <TextBlock x:Name="AhLabelIP" Foreground="#605E5C" Margin="0,0,0,4"/>
        <TextBox x:Name="AhFieldIP" Padding="6" Margin="0,0,0,10"/>
        <TextBlock x:Name="AhLabelMask" Foreground="#605E5C" Margin="0,0,0,4"/>
        <TextBox x:Name="AhFieldMask" Padding="6" Margin="0,0,0,10" Text="255.255.255.0"/>
        <TextBlock x:Name="AhLabelGateway" Foreground="#605E5C" Margin="0,0,0,4"/>
        <TextBox x:Name="AhFieldGateway" Padding="6" Margin="0,0,0,10"/>
        <TextBlock x:Name="AhLabelDNS" Foreground="#605E5C" Margin="0,0,0,4"/>
        <TextBox x:Name="AhFieldDNS" Padding="6" Margin="0,0,0,14"/>

        <TextBlock x:Name="AhError" Foreground="#D83B01" TextWrapping="Wrap" Margin="0,4,0,10" Visibility="Collapsed"/>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,6,0,0">
            <Button x:Name="AhCancel" Padding="14,7" Margin="0,0,8,0" Background="White" BorderBrush="#E1DFDD" BorderThickness="1" Cursor="Hand"/>
            <Button x:Name="AhOk" Padding="14,7" Background="#0078D4" Foreground="White" BorderThickness="0" Cursor="Hand"/>
        </StackPanel>
    </StackPanel>
</Window>
'@

# ============================================================
# XAML - PHYSICAL-ADAPTER PICKER DIALOG (RJ45 / import config)
# ============================================================
$AdapterPickerXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="PONI" Width="380" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" Background="#F3F3F3" FontFamily="Segoe UI" FontSize="13">
    <StackPanel Margin="20">
        <TextBlock x:Name="AdapterPrompt" FontWeight="SemiBold" TextWrapping="Wrap" Margin="0,0,0,14" Foreground="#201F1E"/>
        <ComboBox x:Name="AdapterCombo" Padding="6" Margin="0,0,0,20"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button x:Name="AdapterCancel" Padding="14,7" Margin="0,0,8,0" Background="White" BorderBrush="#E1DFDD" BorderThickness="1" Cursor="Hand"/>
            <Button x:Name="AdapterOk" Padding="14,7" Background="#0078D4" Foreground="White" BorderThickness="0" Cursor="Hand"/>
        </StackPanel>
    </StackPanel>
</Window>
'@

# ============================================================
# XAML - PROFILE PICKER DIALOG (export a selection)
# ============================================================
$ProfileSelectXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="PONI" Width="380" Height="440" ResizeMode="CanResize" MinWidth="320" MinHeight="260"
        WindowStartupLocation="CenterOwner" Background="#F3F3F3" FontFamily="Segoe UI" FontSize="13">
    <DockPanel Margin="20">
        <TextBlock x:Name="PSelTitle" DockPanel.Dock="Top" FontWeight="SemiBold" FontSize="15" Margin="0,0,0,4" Foreground="#201F1E"/>
        <TextBlock x:Name="PSelHint" DockPanel.Dock="Top" FontSize="11" Foreground="#605E5C" Margin="0,0,0,12"/>
        <StackPanel x:Name="PSelButtons" DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button x:Name="PSelCancel" Padding="14,7" Margin="0,0,8,0" Background="White" BorderBrush="#E1DFDD" BorderThickness="1" Cursor="Hand"/>
            <Button x:Name="PSelOk" Padding="14,7" Background="#0078D4" Foreground="White" BorderThickness="0" Cursor="Hand"/>
        </StackPanel>
        <Border BorderBrush="#E1DFDD" BorderThickness="1" CornerRadius="4" Background="White">
            <ListBox x:Name="PSelList" SelectionMode="Multiple" BorderThickness="0" Margin="2"
                     ScrollViewer.VerticalScrollBarVisibility="Auto"/>
        </Border>
    </DockPanel>
</Window>
'@

# ============================================================
# XAML - CONFIRMATION DIALOG (replaces the default MessageBox)
# ============================================================
$ConfirmXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="PONI" Width="380" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" Background="#F3F3F3" FontFamily="Segoe UI" FontSize="13">
    <StackPanel Margin="20">
        <TextBlock x:Name="ConfirmTitle" FontWeight="SemiBold" FontSize="15" Margin="0,0,0,14" Foreground="#201F1E"/>
        <TextBlock x:Name="ConfirmMessage" TextWrapping="Wrap" Foreground="#201F1E" Margin="0,0,0,20"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button x:Name="ConfirmNo" Padding="14,7" Margin="0,0,8,0" Background="White" BorderBrush="#E1DFDD" BorderThickness="1" Cursor="Hand"/>
            <Button x:Name="ConfirmYes" Padding="14,7" Background="#0078D4" Foreground="White" BorderThickness="0" Cursor="Hand"/>
        </StackPanel>
    </StackPanel>
</Window>
'@

# ============================================================
# DIALOGS
# ============================================================
function Show-ConfirmDialog {
    param($Owner, [string]$Title, [string]$Message)

    $win = [Windows.Markup.XamlReader]::Parse($ConfirmXaml)
    $win.Owner = $Owner
    $titleBlock = $win.FindName("ConfirmTitle")
    $msgBlock   = $win.FindName("ConfirmMessage")
    $btnNo      = $win.FindName("ConfirmNo")
    $btnYes     = $win.FindName("ConfirmYes")

    $titleBlock.Text = $Title
    $msgBlock.Text = $Message
    $btnNo.Content = T "btn_cancel"
    $btnYes.Content = T "btn_validate"

    $resultBox = @{ Value = $false }
    $btnNo.Add_Click({ $win.DialogResult = $false; $win.Close() }.GetNewClosure())
    $btnYes.Add_Click({ $resultBox.Value = $true; $win.DialogResult = $true; $win.Close() }.GetNewClosure())

    $win.ShowDialog() | Out-Null
    return $resultBox.Value
}

function Show-ProfileDialog {
    param($Owner, $Existing)

    $win = [Windows.Markup.XamlReader]::Parse($ProfileXaml)
    $win.Owner = $Owner

    $formTitle      = $win.FindName("FormTitle")
    $btnFetchConfig = $win.FindName("BtnFetchConfig")
    $labelName      = $win.FindName("LabelName")
    $fName          = $win.FindName("FieldName")
    $labelIP        = $win.FindName("LabelIP")
    $fIP            = $win.FindName("FieldIP")
    $labelMask      = $win.FindName("LabelMask")
    $fMask          = $win.FindName("FieldMask")
    $labelGateway   = $win.FindName("LabelGateway")
    $fGateway       = $win.FindName("FieldGateway")
    $labelDNS       = $win.FindName("LabelDNS")
    $fDNS           = $win.FindName("FieldDNS")
    $formError      = $win.FindName("FormError")
    $btnSave        = $win.FindName("FormSave")
    $btnCancel      = $win.FindName("FormCancel")

    $formTitle.Text = $(if ($Existing) { T "profile_edit_title" } else { T "profile_new_title" })
    $btnFetchConfig.Content = T "profile_fetch_btn"
    $labelName.Text = T "field_name"
    $labelIP.Text = T "field_ip"
    $labelMask.Text = T "field_mask"
    $labelGateway.Text = T "field_gateway"
    $labelDNS.Text = T "field_dns"
    $btnCancel.Content = T "btn_cancel"
    $btnSave.Content = T "btn_save"

    $btnFetchConfig.Add_Click({
        $chosen = Show-AdapterPickerDialog -Owner $win -Prompt (T "fetch_adapter_prompt")
        if (-not $chosen) { return }
        $ipInfo = Get-NetIPAddress -InterfaceAlias $chosen -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($ipInfo) {
            $fIP.Text = $ipInfo.IPAddress
            $fMask.Text = ConvertTo-SubnetMask $ipInfo.PrefixLength
        }
        $route = Get-NetRoute -InterfaceAlias $chosen -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($route) { $fGateway.Text = $route.NextHop }
        $dnsInfo = Get-DnsClientServerAddress -InterfaceAlias $chosen -AddressFamily IPv4 -ErrorAction SilentlyContinue
        if ($dnsInfo -and $dnsInfo.ServerAddresses) { $fDNS.Text = ($dnsInfo.ServerAddresses -join ", ") }
    }.GetNewClosure())

    if ($Existing) {
        $fName.Text = $Existing.Name
        $fName.IsEnabled = $false
        $fIP.Text = $Existing.IPAddress
        $fMask.Text = ConvertTo-SubnetMask $Existing.PrefixLength
        $fGateway.Text = "$($Existing.Gateway)"
        $fDNS.Text = $(if ($Existing.DNS) { ($Existing.DNS -join ", ") } else { "" })
    }

    # A mutable box instead of a $script: variable: a $script: variable assigned from a
    # .GetNewClosure() handler writes to the closure's private copy, not the function's
    # real scope - the value never "escapes". A reference (hashtable) captured by the
    # closure and mutated (not reassigned) escapes correctly.
    $resultBox = @{ Value = $null }

    $btnCancel.Add_Click({ $win.DialogResult = $false; $win.Close() }.GetNewClosure())
    $btnSave.Add_Click({
        $formError.Visibility = 'Collapsed'
        $name    = $fName.Text.Trim()
        $ipVal   = $fIP.Text.Trim()
        $maskTxt = $fMask.Text.Trim()
        $gw      = $fGateway.Text.Trim()
        $dnsTxt  = $fDNS.Text.Trim()

        if ([string]::IsNullOrWhiteSpace($name)) {
            $formError.Text = T "err_name_required"; $formError.Visibility = 'Visible'; return
        }
        if (-not $Existing) {
            $storeCheck = Get-Store
            if ($storeCheck.Profiles | Where-Object { $_.Name -eq $name }) {
                $formError.Text = T "err_name_exists" $name; $formError.Visibility = 'Visible'; return
            }
        }
        if (-not (Test-IPv4 $ipVal)) {
            $formError.Text = T "err_ip_invalid"; $formError.Visibility = 'Visible'; return
        }
        $prefixNum = ConvertTo-PrefixLength $maskTxt
        if ($null -eq $prefixNum) {
            $formError.Text = T "err_mask_invalid"; $formError.Visibility = 'Visible'; return
        }
        if ($gw -and -not (Test-IPv4 $gw)) {
            $formError.Text = T "err_gateway_invalid"; $formError.Visibility = 'Visible'; return
        }

        $dnsArr = @()
        if ($dnsTxt) { $dnsArr = @($dnsTxt -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
        foreach ($d in $dnsArr) {
            if (-not (Test-IPv4 $d)) {
                $formError.Text = T "err_dns_invalid" $d; $formError.Visibility = 'Visible'; return
            }
        }

        $resultBox.Value = [PSCustomObject]@{
            Name              = $name
            IPAddress         = $ipVal
            PrefixLength      = $prefixNum
            Gateway           = $gw
            DNS               = $dnsArr
            CreatedOn         = if ($Existing) { $Existing.CreatedOn } else { (Get-Date -Format "yyyy-MM-dd HH:mm") }
            LastTarget        = if ($Existing) { $Existing.LastTarget } else { $null }
            LastAdapterName   = if ($Existing) { $Existing.LastAdapterName } else { $null }
            LastVMName        = if ($Existing) { $Existing.LastVMName } else { $null }
            LastVMAdapterName = if ($Existing) { $Existing.LastVMAdapterName } else { $null }
        }
        $win.DialogResult = $true
        $win.Close()
    }.GetNewClosure())

    $ok = $win.ShowDialog()
    if ($ok) { return $resultBox.Value } else { return $null }
}

function Show-ApplyTargetDialog {
    param($Owner, $Profile)

    $win = [Windows.Markup.XamlReader]::Parse($ApplyTargetXaml)
    $win.Owner = $Owner

    $applyTitle       = $win.FindName("ApplyTitle")
    $applySubtitle    = $win.FindName("ApplySubtitle")
    $labelTarget      = $win.FindName("LabelTarget")
    $targetHost       = $win.FindName("TargetHost")
    $targetVM         = $win.FindName("TargetVM")
    $hostPanel        = $win.FindName("HostPanel")
    $vmPanel          = $win.FindName("VMPanel")
    $labelAdapter     = $win.FindName("LabelAdapter")
    $hostAdapterCombo = $win.FindName("HostAdapterCombo")
    $labelVM          = $win.FindName("LabelVM")
    $vmHint           = $win.FindName("VMHint")
    $vmCombo          = $win.FindName("VMCombo")
    $labelVMAdapter   = $win.FindName("LabelVMAdapter")
    $vmAdapterCombo   = $win.FindName("VMAdapterCombo")
    $applyError       = $win.FindName("ApplyError")
    $btnOk            = $win.FindName("ApplyOk")
    $btnCancel        = $win.FindName("ApplyCancel")

    $applyTitle.Text = T "apply_target_title"
    $applySubtitle.Text = T "apply_target_subtitle" @($Profile.Name, "$($Profile.IPAddress) / $(ConvertTo-SubnetMask $Profile.PrefixLength)")
    $labelTarget.Text = T "field_target"
    $targetHost.Content = T "target_host"
    $targetVM.Content = T "target_vm"
    $labelAdapter.Text = T "field_adapter"
    $labelVM.Text = T "target_vm"
    $vmHint.Text = T "apply_vm_running_only"
    $labelVMAdapter.Text = T "field_vm_adapter"
    $btnCancel.Content = T "btn_cancel"
    $btnOk.Content = T "btn_validate"

    $physAdapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    foreach ($a in $physAdapters) { $hostAdapterCombo.Items.Add($a) | Out-Null }

    # Only running VMs are offered: applying a profile goes through PowerShell Direct (VM
    # must be running) and the adapter MAC is only reliable once the VM is running. PONI
    # never starts / stops a VM itself.
    $vms = @(Get-VM -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Running' } | Select-Object -ExpandProperty Name)
    foreach ($v in $vms) { $vmCombo.Items.Add($v) | Out-Null }

    # Hyper-V-side adapters of the chosen VM, shown as "Name (Switch)" to tell them apart
    # (a VM can have several virtual adapters on different switches).
    # ItemsSource+DisplayMemberPath keeps the full object (.Name) as SelectedItem.
    $vmCombo.Add_SelectionChanged({
        $vmAdapterCombo.Items.Clear()
        if ($vmCombo.SelectedItem) {
            $vAdapters = @(Get-VMNetworkAdapter -VMName $vmCombo.SelectedItem -ErrorAction SilentlyContinue | ForEach-Object {
                [PSCustomObject]@{ Display = "$($_.Name) ($($_.SwitchName))"; Name = $_.Name }
            })
            $vmAdapterCombo.DisplayMemberPath = "Display"
            foreach ($a in $vAdapters) { $vmAdapterCombo.Items.Add($a) | Out-Null }
            if ($vmAdapterCombo.Items.Count -gt 0) { $vmAdapterCombo.SelectedIndex = 0 }
        }
    }.GetNewClosure())

    $targetHost.Add_Checked({
        $hostPanel.Visibility = 'Visible'; $vmPanel.Visibility = 'Collapsed'
    }.GetNewClosure())
    $targetVM.Add_Checked({
        $hostPanel.Visibility = 'Collapsed'; $vmPanel.Visibility = 'Visible'
        if ($vmCombo.Items.Count -gt 0 -and -not $vmCombo.SelectedItem) { $vmCombo.SelectedIndex = 0 }
    }.GetNewClosure())

    # Pre-fill from the last target used for this profile, if it is still valid.
    if ($Profile.LastTarget -eq "VM" -and $Profile.LastVMName -and $vmCombo.Items.Contains($Profile.LastVMName)) {
        $targetVM.IsChecked = $true
        $vmCombo.SelectedItem = $Profile.LastVMName
        foreach ($item in $vmAdapterCombo.Items) {
            if ($item.Name -eq $Profile.LastVMAdapterName) { $vmAdapterCombo.SelectedItem = $item }
        }
    } else {
        $targetHost.IsChecked = $true
        if ($Profile.LastAdapterName -and $hostAdapterCombo.Items.Contains($Profile.LastAdapterName)) {
            $hostAdapterCombo.SelectedItem = $Profile.LastAdapterName
        } elseif ($hostAdapterCombo.Items.Count -gt 0) {
            $hostAdapterCombo.SelectedIndex = 0
        }
    }

    $resultBox = @{ Value = $null }

    $btnCancel.Add_Click({ $win.DialogResult = $false; $win.Close() }.GetNewClosure())
    $btnOk.Add_Click({
        $applyError.Visibility = 'Collapsed'
        if ($targetVM.IsChecked) {
            if (-not $vmCombo.SelectedItem) {
                $applyError.Text = T "err_choose_vm"; $applyError.Visibility = 'Visible'; return
            }
            if (-not $vmAdapterCombo.SelectedItem) {
                $applyError.Text = T "err_choose_vm_adapter"; $applyError.Visibility = 'Visible'; return
            }
            $resultBox.Value = [PSCustomObject]@{
                Target = "VM"; AdapterName = $null
                VMName = $vmCombo.SelectedItem; VMAdapterName = $vmAdapterCombo.SelectedItem.Name
            }
        } else {
            if (-not $hostAdapterCombo.SelectedItem) {
                $applyError.Text = T "err_choose_adapter"; $applyError.Visibility = 'Visible'; return
            }
            $resultBox.Value = [PSCustomObject]@{
                Target = "Host"; AdapterName = $hostAdapterCombo.SelectedItem
                VMName = $null; VMAdapterName = $null
            }
        }
        $win.DialogResult = $true
        $win.Close()
    }.GetNewClosure())

    $ok = $win.ShowDialog()
    if ($ok) { return $resultBox.Value } else { return $null }
}

function Show-CredentialDialog {
    param($Owner, [string]$VMName)

    $win = [Windows.Markup.XamlReader]::Parse($CredentialXaml)
    $win.Owner = $Owner
    $title     = $win.FindName("CredTitle")
    $labelUser = $win.FindName("LabelUser")
    $userBox   = $win.FindName("CredUser")
    $labelPass = $win.FindName("LabelPass")
    $passBox   = $win.FindName("CredPass")
    $btnOk     = $win.FindName("CredOk")
    $btnCancel = $win.FindName("CredCancel")

    $title.Text = T "cred_title" $VMName
    $labelUser.Text = T "cred_user"
    $labelPass.Text = T "cred_pass"
    $btnCancel.Content = T "btn_cancel"
    $btnOk.Content = T "btn_validate"

    # A mutable box instead of a $script: variable: a $script: variable assigned from a
    # .GetNewClosure() handler writes to the closure's private copy, not the function's
    # real scope - the value never "escapes". A reference (hashtable) captured by the
    # closure and mutated (not reassigned) escapes correctly.
    $resultBox = @{ Value = $null }

    $btnCancel.Add_Click({ $win.DialogResult = $false; $win.Close() }.GetNewClosure())
    $btnOk.Add_Click({
        if ([string]::IsNullOrWhiteSpace($userBox.Text)) { return }
        $secure = ConvertTo-SecureString -String $passBox.Password -AsPlainText -Force
        $resultBox.Value = New-Object System.Management.Automation.PSCredential($userBox.Text, $secure)
        $win.DialogResult = $true
        $win.Close()
    }.GetNewClosure())

    $ok = $win.ShowDialog()
    if ($ok) { return $resultBox.Value } else { return $null }
}

function Show-AdapterPickerDialog {
    param($Owner, [string]$Prompt = $null)

    $win = [Windows.Markup.XamlReader]::Parse($AdapterPickerXaml)
    $win.Owner = $Owner
    $promptBlock = $win.FindName("AdapterPrompt")
    $combo     = $win.FindName("AdapterCombo")
    $btnOk     = $win.FindName("AdapterOk")
    $btnCancel = $win.FindName("AdapterCancel")

    $promptBlock.Text = $(if ($Prompt) { $Prompt } else { T "adapter_picker_prompt" })
    $btnCancel.Content = T "btn_cancel"
    $btnOk.Content = T "btn_validate"

    $physAdapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    foreach ($a in $physAdapters) { $combo.Items.Add($a) | Out-Null }
    if ($combo.Items.Count -gt 0) { $combo.SelectedIndex = 0 }

    $resultBox = @{ Value = $null }

    $btnCancel.Add_Click({ $win.DialogResult = $false; $win.Close() }.GetNewClosure())
    $btnOk.Add_Click({
        if (-not $combo.SelectedItem) { return }
        $resultBox.Value = $combo.SelectedItem
        $win.DialogResult = $true
        $win.Close()
    }.GetNewClosure())

    $ok = $win.ShowDialog()
    if ($ok) { return $resultBox.Value } else { return $null }
}

# Edit a host adapter's IP config "on the fly" (without a saved profile).
# $Current = @{ IP; Mask; Gateway; DNS }. $Risky = $true -> show the warning.
# Returns @{ IPAddress; PrefixLength; Gateway; DNS } or $null.
function Show-AdhocConfigDialog {
    param($Owner, [string]$AdapterName, $Current, [bool]$Risky, [string]$Focus)

    $win = [Windows.Markup.XamlReader]::Parse($AdhocConfigXaml)
    $win.Owner = $Owner
    $title   = $win.FindName("AhTitle")
    $hint    = $win.FindName("AhHint")
    $warnBox = $win.FindName("AhWarnBox")
    $warn    = $win.FindName("AhWarn")
    $lIP     = $win.FindName("AhLabelIP");      $fIP  = $win.FindName("AhFieldIP")
    $lMask   = $win.FindName("AhLabelMask");    $fMask = $win.FindName("AhFieldMask")
    $lGw     = $win.FindName("AhLabelGateway"); $fGw  = $win.FindName("AhFieldGateway")
    $lDNS    = $win.FindName("AhLabelDNS");     $fDNS = $win.FindName("AhFieldDNS")
    $err     = $win.FindName("AhError")
    $btnCancel = $win.FindName("AhCancel")
    $btnOk     = $win.FindName("AhOk")

    $title.Text = T "adhoc_title" $AdapterName
    $hint.Text  = T "adhoc_hint"
    $lIP.Text = T "field_ip"; $lMask.Text = T "field_mask"
    $lGw.Text = T "field_gateway"; $lDNS.Text = T "field_dns"
    $btnCancel.Content = T "btn_cancel"
    $btnOk.Content = T "btn_validate"
    if ($Risky) { $warn.Text = T "adhoc_warn"; $warnBox.Visibility = 'Visible' }

    if ($Current) {
        if ($Current.IP)      { $fIP.Text = "$($Current.IP)" }
        if ($Current.Mask)    { $fMask.Text = "$($Current.Mask)" }
        if ($Current.Gateway) { $fGw.Text = "$($Current.Gateway)" }
        if ($Current.DNS)     { $fDNS.Text = "$($Current.DNS)" }
    }
    switch ($Focus) {
        "Mask"    { $fMask.Focus() | Out-Null }
        "Gateway" { $fGw.Focus() | Out-Null }
        "DNS"     { $fDNS.Focus() | Out-Null }
        default   { $fIP.Focus() | Out-Null }
    }

    $resultBox = @{ Value = $null }
    $btnCancel.Add_Click({ $win.DialogResult = $false; $win.Close() }.GetNewClosure())
    $btnOk.Add_Click({
        $err.Visibility = 'Collapsed'
        $ipVal = $fIP.Text.Trim(); $maskTxt = $fMask.Text.Trim()
        $gw = $fGw.Text.Trim(); $dnsTxt = $fDNS.Text.Trim()
        if (-not (Test-IPv4 $ipVal)) { $err.Text = T "err_ip_invalid"; $err.Visibility = 'Visible'; return }
        $prefixNum = ConvertTo-PrefixLength $maskTxt
        if ($null -eq $prefixNum) { $err.Text = T "err_mask_invalid"; $err.Visibility = 'Visible'; return }
        if ($gw -and -not (Test-IPv4 $gw)) { $err.Text = T "err_gateway_invalid"; $err.Visibility = 'Visible'; return }
        $dnsArr = @()
        if ($dnsTxt) { $dnsArr = @($dnsTxt -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
        foreach ($d in $dnsArr) {
            if (-not (Test-IPv4 $d)) { $err.Text = T "err_dns_invalid" $d; $err.Visibility = 'Visible'; return }
        }
        $resultBox.Value = [PSCustomObject]@{ IPAddress = $ipVal; PrefixLength = $prefixNum; Gateway = $gw; DNS = $dnsArr }
        $win.DialogResult = $true
        $win.Close()
    }.GetNewClosure())

    $ok = $win.ShowDialog()
    if ($ok) { return $resultBox.Value } else { return $null }
}

# Multi-select profile picker (export a selection). $Profiles = array of profile objects.
# Returns an array of checked names, or $null if cancelled / nothing checked.
function Show-ProfileSelectDialog {
    param($Owner, $Profiles)

    $win = [Windows.Markup.XamlReader]::Parse($ProfileSelectXaml)
    $win.Owner = $Owner
    $title  = $win.FindName("PSelTitle")
    $hint   = $win.FindName("PSelHint")
    $list   = $win.FindName("PSelList")
    $btnOk  = $win.FindName("PSelOk")
    $btnCancel = $win.FindName("PSelCancel")

    $title.Text = T "pselect_title"
    $hint.Text  = T "pselect_hint"
    $btnCancel.Content = T "btn_cancel"
    $btnOk.Content = T "btn_export_go"

    foreach ($p in $Profiles) { $list.Items.Add("$($p.Name)") | Out-Null }

    $resultBox = @{ Value = $null }
    $btnCancel.Add_Click({ $win.DialogResult = $false; $win.Close() }.GetNewClosure())
    $btnOk.Add_Click({
        if ($list.SelectedItems.Count -eq 0) { return }
        $resultBox.Value = @($list.SelectedItems | ForEach-Object { "$_" })
        $win.DialogResult = $true
        $win.Close()
    }.GetNewClosure())

    $ok = $win.ShowDialog()
    if ($ok) { return $resultBox.Value } else { return $null }
}

# ============================================================
# MAIN WINDOW - construction + logic
# ============================================================
$window = [Windows.Markup.XamlReader]::Parse($MainXaml)

# Logo: window icon (title bar + taskbar) + image in the top-left corner.
$logoSource = Get-LogoImageSource
if ($logoSource) {
    $window.Icon = $logoSource
    $li = $window.FindName("LogoImage")
    if ($li) { $li.Source = $logoSource }
}

# "View on GitHub" button (sidebar) + discreet "Made by Doodz" link in the top-right.
$btnGitHubLink = $window.FindName("BtnGitHubLink")
$lblGitHub     = $window.FindName("LblGitHub")
$authorLink    = $window.FindName("AuthorLink")
if ($btnGitHubLink) { $btnGitHubLink.Add_Click({ try { Start-Process $RepoUrl } catch {} }.GetNewClosure()) }
if ($authorLink)    { $authorLink.Add_MouseLeftButtonUp({ try { Start-Process $AuthorUrl } catch {} }.GetNewClosure()) }

$navProfiles        = $window.FindName("NavProfiles")
$navRJ45             = $window.FindName("NavRJ45")
$navQuit             = $window.FindName("NavQuit")
$labelLang           = $window.FindName("LabelLang")
$langCombo           = $window.FindName("LangCombo")
$langCombo.Items.Add("Français") | Out-Null
$langCombo.Items.Add("English") | Out-Null
$profilesView        = $window.FindName("ProfilesView")
$profilesHeader      = $window.FindName("ProfilesHeader")
$rj45View            = $window.FindName("RJ45View")
$rj45Header          = $window.FindName("RJ45Header")
$rj45Explain         = $window.FindName("RJ45Explain")
$profilesGrid        = $window.FindName("ProfilesGrid")
$curNetHeader        = $window.FindName("CurNetHeader")
$curNetGrid          = $window.FindName("CurNetGrid")
$rj45AdapterLabel    = $window.FindName("RJ45AdapterLabel")
$rj45StateBox        = $window.FindName("RJ45StateBox")
$rj45StateText       = $window.FindName("RJ45StateText")
$rj45CardsPanel      = $window.FindName("RJ45CardsPanel")
$logList             = $window.FindName("LogList")
$logScroller         = $window.FindName("LogScroller")
$btnViewLogs         = $window.FindName("BtnViewLogs")
$btnClearLog         = $window.FindName("BtnClearLog")
$btnNewProfile       = $window.FindName("BtnNewProfile")
$btnEditProfile      = $window.FindName("BtnEditProfile")
$btnDeleteProfile    = $window.FindName("BtnDeleteProfile")
$btnApplyProfile     = $window.FindName("BtnApplyProfile")
$btnRefreshProfiles  = $window.FindName("BtnRefreshProfiles")
$btnToggleView       = $window.FindName("BtnToggleView")
$btnExportProfiles   = $window.FindName("BtnExportProfiles")
$btnImportProfiles   = $window.FindName("BtnImportProfiles")

$script:DetailedView = $false

# Bottom bar = a small scrolling log (timestamped, colour-coded) instead of a single line.
function Set-Status([string]$msg, [string]$kind = "info") {
    $tb = New-Object System.Windows.Controls.TextBlock
    $tb.Text = "{0}  {1}" -f (Get-Date -Format "HH:mm:ss"), $msg
    $tb.TextWrapping = 'Wrap'
    $tb.Foreground = switch ($kind) {
        "success" { $BrushSuccess }
        "error"   { $BrushError }
        default   { $BrushMuted }
    }
    $logList.Items.Add($tb) | Out-Null
    while ($logList.Items.Count -gt 200) { $logList.Items.RemoveAt(0) }
    $logScroller.ScrollToBottom()
}

# Profiles-grid columns: the "simple" set (default) or the "detailed" set.
function Set-ProfileColumns {
    $profilesGrid.Columns.Clear()
    $mk = {
        param($header, $path, $w)
        $c = New-Object System.Windows.Controls.DataGridTextColumn
        $c.Header = $header
        $c.Binding = New-Object System.Windows.Data.Binding($path)
        $c.Width = New-Object System.Windows.Controls.DataGridLength([double]$w, [System.Windows.Controls.DataGridLengthUnitType]::Star)
        $profilesGrid.Columns.Add($c) | Out-Null
    }
    if ($script:DetailedView) {
        & $mk (T "col_name")        "Name"    1.1
        & $mk (T "col_ip")          "IP"      1.0
        & $mk (T "col_mask")        "Mask"    1.1
        & $mk (T "col_gateway")     "Gateway" 1.0
        & $mk (T "col_dns")         "DNS"     1.3
        & $mk (T "col_last_target") "LastTarget" 2.0
    } else {
        & $mk (T "col_name")        "Name"   1.2
        & $mk (T "col_ipmask")      "IPMask" 1.3
        & $mk (T "col_last_target") "LastTarget" 1.8
    }
}

# Toggle the simple <-> detailed view. Must go through a function: a
# "$script:DetailedView = ..." done DIRECTLY inside a .GetNewClosure() handler only
# writes to the closure's private copy and never "escapes" (known PONI trap).
function Toggle-ProfileView {
    $script:DetailedView = -not $script:DetailedView
    Set-ProfileColumns
    $btnToggleView.Content = $(if ($script:DetailedView) { T "btn_view_simple" } else { T "btn_view_detailed" })
}

# Export profiles. -All = all of them; otherwise open a multi-select picker.
function Export-Profiles {
    param([switch]$All)
    $store = Get-Store
    $allProfiles = @($store.Profiles)
    if ($allProfiles.Count -eq 0) { Set-Status (T "status_export_none") "error"; return }

    $toExport = $allProfiles
    if (-not $All) {
        $names = Show-ProfileSelectDialog -Owner $window -Profiles $allProfiles
        if (-not $names) { return }
        $toExport = @($allProfiles | Where-Object { $names -contains "$($_.Name)" })
        if ($toExport.Count -eq 0) { return }
    }

    $dlg = New-Object Microsoft.Win32.SaveFileDialog
    $dlg.Title = T "export_dialog_title"
    $dlg.Filter = "JSON (*.json)|*.json"
    $dlg.FileName = "poni-profiles.json"
    if ($dlg.ShowDialog()) {
        try {
            [PSCustomObject]@{ PONIProfiles = @($toExport) } | ConvertTo-Json -Depth 6 | Set-Content -Path $dlg.FileName -Encoding UTF8
            Set-Status (T "status_exported" @($toExport.Count, $dlg.FileName)) "success"
        } catch { Set-Status (T "status_import_failed" $_.Exception.Message) "error" }
    }
}

# An adapter is "risky" to reconfigure if it carries the default route (the one keeping
# you connected) or if it is Wi-Fi (often DHCP + 802.1X, which breaks on a static IP).
function Test-RiskyAdapter([string]$alias) {
    $hasDefault = [bool](Get-NetRoute -InterfaceAlias $alias -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
        Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' })
    if ($hasDefault) { return $true }
    $ad = Get-NetAdapter -Name $alias -ErrorAction SilentlyContinue
    return ($ad -and ("$($ad.PhysicalMediaType)" -like '*802.11*' -or "$($ad.InterfaceDescription)" -match 'Wi-?Fi|Wireless'))
}

# --- Actions on a host adapter from the "current PC network configuration" panel ---
function Reset-AdapterToDhcp([string]$alias) {
    if (-not (Show-ConfirmDialog -Owner $window -Title (T "confirm_dhcp_title") -Message (T "confirm_dhcp_msg" $alias))) { Refresh-CurrentNet; return }
    Set-Status (T "status_dhcp_working" $alias) "info"
    Invoke-Async -ScriptText $DhcpResetScript -Params @{ AdapterName = $alias } -Buttons @($navProfiles, $navRJ45) -OnDone {
        param($result)
        Set-Status (T $result.Key $result.Args) $(if ($result.Success) { "success" } else { "error" })
        try { Refresh-CurrentNet } catch {}
    }.GetNewClosure()
}

function Set-AdapterCategory([string]$alias, [string]$category) {
    Set-Status (T "status_nettype_working" $alias) "info"
    Invoke-Async -ScriptText $NetCategoryScript -Params @{ AdapterName = $alias; Category = $category } -Buttons @($navProfiles, $navRJ45) -OnDone {
        param($result)
        Set-Status (T $result.Key $result.Args) $(if ($result.Success) { "success" } else { "error" })
        try { Refresh-CurrentNet } catch {}
    }.GetNewClosure()
}

function Edit-AdapterAdhoc([string]$alias, [string]$focus) {
    $ipInfo = Get-NetIPAddress -InterfaceAlias $alias -AddressFamily IPv4 -ErrorAction SilentlyContinue |
              Where-Object { $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1
    $route = Get-NetRoute -InterfaceAlias $alias -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
             Sort-Object RouteMetric | Select-Object -First 1
    $dns = (Get-DnsClientServerAddress -InterfaceAlias $alias -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses
    $cur = @{
        IP      = $(if ($ipInfo) { $ipInfo.IPAddress } else { "" })
        Mask    = $(if ($ipInfo) { ConvertTo-SubnetMask $ipInfo.PrefixLength } else { "255.255.255.0" })
        Gateway = $(if ($route -and $route.NextHop -and $route.NextHop -ne '0.0.0.0') { $route.NextHop } else { "" })
        DNS     = $(if ($dns) { ($dns -join ", ") } else { "" })
    }
    $r = Show-AdhocConfigDialog -Owner $window -AdapterName $alias -Current $cur -Risky (Test-RiskyAdapter $alias) -Focus $focus
    if (-not $r) { Refresh-CurrentNet; return }
    Set-Status (T "status_applying_host") "info"
    $params = @{ AdapterName = $alias; IPAddress = $r.IPAddress; PrefixLength = $r.PrefixLength; Gateway = $r.Gateway; DNS = $r.DNS; LogPath = $DiagLogPath }
    Invoke-Async -ScriptText $HostApplyScript -Params $params -Buttons @($navProfiles, $navRJ45) -OnDone {
        param($result)
        Set-Status (T $result.Key $result.Args) $(if ($result.Success) { "success" } else { "error" })
        try { Refresh-CurrentNet } catch {}
    }.GetNewClosure()
}

# "Current PC network configuration" panel: an editable table. Rows Adapter / Assignment /
# Type / IP / Mask / Gateway / DNS; one column per adapter. Assignment and Type are
# drop-downs; IP/Mask/Gateway/DNS are clickable (on-the-fly editing).
function Refresh-CurrentNet {
    try {
        $curNetGrid.Children.Clear()
        $curNetGrid.ColumnDefinitions.Clear()
        $curNetGrid.RowDefinitions.Clear()

        $realIp = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' } |
            Select-Object -ExpandProperty InterfaceAlias -Unique)
        $adapters = @(Get-NetAdapter -ErrorAction SilentlyContinue |
            Where-Object {
                ($_.Status -eq 'Up' -or $_.Status -eq 'Disconnected') -and
                ($_.HardwareInterface -or ($realIp -contains $_.Name))
            } |
            Sort-Object @{ Expression = { -not $_.HardwareInterface } }, ifIndex)

        if ($adapters.Count -eq 0) {
            $tb = New-Object System.Windows.Controls.TextBlock
            $tb.Text = T "current_net_none"; $tb.FontSize = 12; $tb.Foreground = $BrushMuted
            $curNetGrid.Children.Add($tb) | Out-Null
            return
        }

        $rowLabels = @((T "col_adapter"), (T "col_attribution"), (T "col_type"), (T "col_ip"), (T "col_mask"), (T "col_gateway"), (T "col_dns"))
        $nrows = $rowLabels.Count

        $cd0 = New-Object System.Windows.Controls.ColumnDefinition
        $cd0.Width = [System.Windows.GridLength]::Auto
        $curNetGrid.ColumnDefinitions.Add($cd0) | Out-Null
        foreach ($a in $adapters) {
            $cd = New-Object System.Windows.Controls.ColumnDefinition
            $cd.Width = [System.Windows.GridLength]::Auto
            $curNetGrid.ColumnDefinitions.Add($cd) | Out-Null
        }
        for ($r = 0; $r -lt $nrows; $r++) {
            $rd = New-Object System.Windows.Controls.RowDefinition
            $rd.Height = [System.Windows.GridLength]::Auto
            $curNetGrid.RowDefinitions.Add($rd) | Out-Null
        }

        $addText = {
            param($text, $row, $col, $muted, $bold, $clickAlias, $clickField)
            $tb = New-Object System.Windows.Controls.TextBlock
            $tb.Text = "$text"
            $tb.FontSize = 12
            $tb.VerticalAlignment = 'Center'
            $tb.Margin = New-Object System.Windows.Thickness(0, 2, $(if ($col -eq 0) { 16 } else { 22 }), 2)
            $tb.Foreground = $(if ($muted) { $BrushMuted } else { $BrushConverter.ConvertFromString("#201F1E") })
            if ($bold) { $tb.FontWeight = 'SemiBold' }
            if ($clickAlias) {
                $tb.Cursor = 'Hand'
                $tb.ToolTip = T "tooltip_click_edit"
                $tb.Add_MouseLeftButtonUp({ Edit-AdapterAdhoc $clickAlias $clickField }.GetNewClosure())
            }
            [System.Windows.Controls.Grid]::SetRow($tb, $row)
            [System.Windows.Controls.Grid]::SetColumn($tb, $col)
            $curNetGrid.Children.Add($tb) | Out-Null
        }

        for ($r = 0; $r -lt $nrows; $r++) { & $addText $rowLabels[$r] $r 0 $true ($r -eq 0) $null $null }

        $col = 1
        foreach ($a in $adapters) {
            $alias = $a.Name
            $ipInfo = Get-NetIPAddress -InterfaceAlias $alias -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                      Where-Object { $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1
            if (-not $ipInfo) {
                $ipInfo = Get-NetIPAddress -InterfaceAlias $alias -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1
            }
            $route = Get-NetRoute -InterfaceAlias $alias -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
                     Sort-Object RouteMetric | Select-Object -First 1
            $dns = (Get-DnsClientServerAddress -InterfaceAlias $alias -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses
            $ipIf = Get-NetIPInterface -InterfaceAlias $alias -AddressFamily IPv4 -ErrorAction SilentlyContinue
            $isDhcp = ($ipIf -and "$($ipIf.Dhcp)" -eq 'Enabled')
            $cat = "$((Get-NetConnectionProfile -InterfaceAlias $alias -ErrorAction SilentlyContinue).NetworkCategory)"

            # Row 0: adapter name
            & $addText $alias 0 $col $false $true $null $null

            # Row 1: Assignment (DHCP / Manual)
            $cbA = New-Object System.Windows.Controls.ComboBox
            $cbA.FontSize = 12; $cbA.MinWidth = 150
            $cbA.Margin = New-Object System.Windows.Thickness(0, 2, 22, 4)
            $cbA.Items.Add((T "attr_dhcp")) | Out-Null
            $cbA.Items.Add((T "attr_manual")) | Out-Null
            $cbA.SelectedIndex = $(if ($isDhcp) { 0 } else { 1 })
            $wasDhcp = $isDhcp
            $cbA.Add_SelectionChanged({
                $chosenDhcp = ($cbA.SelectedIndex -eq 0)
                if ($chosenDhcp -eq $wasDhcp) { return }
                if ($chosenDhcp) { Reset-AdapterToDhcp $alias } else { Edit-AdapterAdhoc $alias $null }
            }.GetNewClosure())
            [System.Windows.Controls.Grid]::SetRow($cbA, 1)
            [System.Windows.Controls.Grid]::SetColumn($cbA, $col)
            $curNetGrid.Children.Add($cbA) | Out-Null

            # Row 2: Type (Private / Public, or Domain read-only)
            if ($cat -eq 'DomainAuthenticated') {
                & $addText (T "nettype_domain") 2 $col $true $false $null $null
            } elseif (-not $cat) {
                & $addText "-" 2 $col $true $false $null $null
            } else {
                $cbT = New-Object System.Windows.Controls.ComboBox
                $cbT.FontSize = 12; $cbT.MinWidth = 110
                $cbT.Margin = New-Object System.Windows.Thickness(0, 2, 22, 4)
                $cbT.Items.Add((T "nettype_private")) | Out-Null
                $cbT.Items.Add((T "nettype_public")) | Out-Null
                $cbT.SelectedIndex = $(if ($cat -eq 'Private') { 0 } else { 1 })
                $wasPrivate = ($cat -eq 'Private')
                $cbT.Add_SelectionChanged({
                    $chosenPrivate = ($cbT.SelectedIndex -eq 0)
                    if ($chosenPrivate -eq $wasPrivate) { return }
                    Set-AdapterCategory $alias $(if ($chosenPrivate) { 'Private' } else { 'Public' })
                }.GetNewClosure())
                [System.Windows.Controls.Grid]::SetRow($cbT, 2)
                [System.Windows.Controls.Grid]::SetColumn($cbT, $col)
                $curNetGrid.Children.Add($cbT) | Out-Null
            }

            # Rows 3-6: values, clickable for on-the-fly editing
            & $addText $(if ($ipInfo) { $ipInfo.IPAddress } else { "-" }) 3 $col $false $false $alias "IP"
            & $addText $(if ($ipInfo) { ConvertTo-SubnetMask $ipInfo.PrefixLength } else { "-" }) 4 $col $false $false $alias "Mask"
            & $addText $(if ($route -and $route.NextHop -and $route.NextHop -ne '0.0.0.0') { $route.NextHop } else { "-" }) 5 $col $false $false $alias "Gateway"
            & $addText $(if ($dns) { ($dns -join ", ") } else { "-" }) 6 $col $false $false $alias "DNS"
            $col++
        }
    } catch {
        try {
            $curNetGrid.Children.Clear()
            $tb = New-Object System.Windows.Controls.TextBlock
            $tb.Text = T "current_net_none"; $tb.FontSize = 12; $tb.Foreground = $BrushMuted
            $curNetGrid.Children.Add($tb) | Out-Null
        } catch {}
    }
}

function Update-UITexts {
    $navProfiles.Content = T "nav_profiles"
    $navRJ45.Content = T "nav_rj45"
    $navQuit.Content = T "nav_quit"
    $curNetHeader.Text = T "current_net_title"
    $lblGitHub.Text = T "btn_see_github"
    $authorLink.Text = T "author_link"
    $profilesHeader.Text = T "profiles_header"
    $btnNewProfile.Content = T "btn_new"
    $btnEditProfile.Content = T "btn_edit"
    $btnDeleteProfile.Content = T "btn_delete"
    $btnApplyProfile.Content = T "btn_apply"
    $btnRefreshProfiles.Content = "$([char]0x21BB)  " + (T "btn_refresh")
    $btnToggleView.Content = $(if ($script:DetailedView) { T "btn_view_simple" } else { T "btn_view_detailed" })
    $btnExportProfiles.Content = T "btn_export"
    $btnImportProfiles.Content = T "btn_import"
    if ($script:miExportAll) { $script:miExportAll.Header = T "export_all" }
    if ($script:miExportSel) { $script:miExportSel.Header = T "export_selection" }
    $btnViewLogs.Content = T "btn_view_logs"
    $btnClearLog.Content = T "btn_clear_log"
    Set-ProfileColumns
    $rj45Header.Text = T "rj45_header"
    $labelLang.Text = T "lang_label"
    $langCombo.SelectedIndex = $(if ($script:Lang -eq "fr") { 0 } else { 1 })
}

$script:SwitchingLang = $false
function Switch-Language([string]$lang) {
    # The "already in this language?" check MUST happen here (in a function) and NOT in
    # the SelectionChanged handler: .GetNewClosure() freezes the value of $script:Lang at
    # the moment the handler is created, so after a first change the handler always
    # compared against the old language -> only one change possible per session. Here, in
    # a function, $script:Lang is the live value.
    if ($script:SwitchingLang) { return }
    if ($lang -ne "fr" -and $lang -ne "en") { return }
    if ($lang -eq $script:Lang) { return }
    $script:SwitchingLang = $true
    try {
        $script:Lang = $lang
        $store = Get-Store
        $store.Settings.Language = $lang
        Save-Store $store
        Update-UITexts
        Refresh-ProfilesGrid
        try { Refresh-CurrentNet } catch {}
        # The RJ45 view is rebuilt on navigation (Show-View) with translated labels,
        # including the localized VM state (Format-VMState). Only refresh it here if it is
        # already visible - and never let an exception bubble up and break the language
        # switch.
        if ($rj45View.Visibility -eq 'Visible') {
            try { Refresh-RJ45View } catch {}
        }
        Set-Status (T "status_ready") "info"
    } finally {
        $script:SwitchingLang = $false
    }
}

function Refresh-ProfilesGrid {
    $store = Get-Store
    $rows = foreach ($p in $store.Profiles) {
        $lastTargetText = if ($p.LastTarget -eq "VM" -and $p.LastVMName) {
            T "grid_last_target_vm" @($p.LastVMName, $p.LastVMAdapterName)
        } elseif (($p.LastTarget -eq "Host" -or $p.LastTarget -eq "Hote") -and $p.LastAdapterName) {
            T "grid_last_target_host" $p.LastAdapterName
        } else {
            T "grid_never_applied"
        }
        if ($p.LastAppliedOn -and $p.LastTarget) { $lastTargetText = "$lastTargetText ($($p.LastAppliedOn))" }
        $dnsTxt = if ($p.DNS -and @($p.DNS).Count -gt 0) { (@($p.DNS) -join ", ") } else { "-" }
        [PSCustomObject]@{
            Name     = $p.Name
            IP       = $p.IPAddress
            Mask     = ConvertTo-SubnetMask $p.PrefixLength
            Gateway  = $(if ([string]::IsNullOrWhiteSpace("$($p.Gateway)")) { "-" } else { "$($p.Gateway)" })
            DNS      = $dnsTxt
            IPMask   = "$($p.IPAddress) / $(ConvertTo-SubnetMask $p.PrefixLength)"
            LastTarget = $lastTargetText
            _Profile = $p
        }
    }
    $profilesGrid.ItemsSource = @($rows)
}

function New-RJ45Card {
    param([string]$Title, [string]$Subtitle, [bool]$IsCurrent, [scriptblock]$OnAssign)

    $border = New-Object System.Windows.Controls.Border
    $border.Background = [System.Windows.Media.Brushes]::White
    $border.BorderBrush = $(if ($IsCurrent) { $BrushAccent } else { $BrushBorder })
    $border.BorderThickness = New-Object System.Windows.Thickness($(if ($IsCurrent) { 2 } else { 1 }))
    $border.CornerRadius = New-Object System.Windows.CornerRadius(6)
    $border.Padding = New-Object System.Windows.Thickness(14)
    $border.Margin = New-Object System.Windows.Thickness(0,0,0,10)

    $grid = New-Object System.Windows.Controls.Grid
    $col1 = New-Object System.Windows.Controls.ColumnDefinition
    $col1.Width = New-Object System.Windows.GridLength(1, [System.Windows.GridUnitType]::Star)
    $col2 = New-Object System.Windows.Controls.ColumnDefinition
    $col2.Width = [System.Windows.GridLength]::Auto
    $grid.ColumnDefinitions.Add($col1) | Out-Null
    $grid.ColumnDefinitions.Add($col2) | Out-Null

    $stack = New-Object System.Windows.Controls.StackPanel
    $t1 = New-Object System.Windows.Controls.TextBlock
    $t1.Text = $Title; $t1.FontWeight = 'SemiBold'; $t1.FontSize = 14; $t1.Foreground = "#201F1E"
    $t2 = New-Object System.Windows.Controls.TextBlock
    $t2.Text = $Subtitle; $t2.Foreground = $BrushMuted; $t2.Margin = New-Object System.Windows.Thickness(0,2,0,0)
    $stack.Children.Add($t1) | Out-Null
    $stack.Children.Add($t2) | Out-Null
    if ($IsCurrent) {
        $badge = New-Object System.Windows.Controls.TextBlock
        $badge.Text = T "rj45_badge"; $badge.Foreground = $BrushAccent; $badge.FontWeight = 'SemiBold'
        $badge.Margin = New-Object System.Windows.Thickness(0,4,0,0)
        $stack.Children.Add($badge) | Out-Null
    }
    [System.Windows.Controls.Grid]::SetColumn($stack, 0)
    $grid.Children.Add($stack) | Out-Null

    $btn = New-Object System.Windows.Controls.Button
    $btn.Content = $(if ($IsCurrent) { T "rj45_current_target" } else { T "rj45_switch_here" })
    $btn.IsEnabled = -not $IsCurrent
    $btn.Padding = New-Object System.Windows.Thickness(12,6,12,6)
    $btn.VerticalAlignment = 'Center'
    $btn.Background = $(if ($IsCurrent) { $BrushBorder } else { $BrushAccent })
    $btn.Foreground = $(if ($IsCurrent) { $BrushMuted } else { [System.Windows.Media.Brushes]::White })
    $btn.BorderThickness = New-Object System.Windows.Thickness(0)
    $btn.Cursor = 'Hand'
    if ($OnAssign) { $btn.Add_Click($OnAssign) }
    [System.Windows.Controls.Grid]::SetColumn($btn, 1)
    $grid.Children.Add($btn) | Out-Null

    $border.Child = $grid
    return $border
}

function Set-RJ45Target {
    param([string]$TargetType, [string]$VMName)

    $store = Get-Store
    if (-not $store.RJ45.PhysicalAdapter) {
        # Decision: the RJ45 port = the "Ethernet" adapter. Use it automatically if it
        # exists, otherwise (unusual setup) ask the user.
        if (Get-NetAdapter -Name "Ethernet" -ErrorAction SilentlyContinue) {
            $store.RJ45.PhysicalAdapter = "Ethernet"
        } else {
            $adapter = Show-AdapterPickerDialog -Owner $window
            if (-not $adapter) { return }
            $store.RJ45.PhysicalAdapter = $adapter
        }
        Save-Store $store
    }

    # Before creating a switch on the RJ45 adapter, make sure it is usable
    # (not disabled because we switched to Wi-Fi, not missing).
    if ($TargetType -ne "Host") {
        $pa = Get-NetAdapter -Name $store.RJ45.PhysicalAdapter -ErrorAction SilentlyContinue
        if (-not $pa -or $pa.Status -eq 'Disabled') {
            Set-Status (T "err_rj45_adapter_unavailable" $store.RJ45.PhysicalAdapter) "error"
            return
        }
    }

    $buttons = @($rj45CardsPanel, $navProfiles, $navRJ45)
    $params = @{ SwitchName = $RJ45SwitchName; PhysicalAdapter = $store.RJ45.PhysicalAdapter; VMName = $VMName }
    $scriptText = if ($TargetType -eq "Host") { $RJ45ToHostScript } else { $RJ45ToVMScript }

    Set-Status (T "status_rj45_switching") "info"
    Invoke-Async -ScriptText $scriptText -Params $params -Buttons $buttons -OnDone {
        param($result)
        if ($result.Success) {
            $s = Get-Store
            $s.RJ45.CurrentTarget = $(if ($TargetType -eq "Host") { "Host" } else { "VM: $VMName" })
            Save-Store $s
            Set-Status (T $result.Key $result.Args) "success"
        } else {
            Set-Status (T $result.Key $result.Args) "error"
        }
        Refresh-RJ45View
        # Creating / removing the RJ45 switch changes the network state of the "Ethernet"
        # adapter on the host side, so refresh the "current PC network configuration" panel.
        try { Refresh-CurrentNet } catch {}
    }.GetNewClosure()
}

function Refresh-RJ45View {
  # The whole body is guarded: this function is called from navigation and from the
  # language switch, and must never let an exception break the caller.
  try {
    $store = Get-Store
    $rj45CardsPanel.Children.Clear()
    $adapterTxt = $(if ($store.RJ45.PhysicalAdapter) { $store.RJ45.PhysicalAdapter } else { "Ethernet" })
    $rj45AdapterLabel.Text = T "rj45_adapter_label" $adapterTxt
    $rj45Explain.Text = T "rj45_explain" $adapterTxt

    $current = $store.RJ45.CurrentTarget

    # Information banner (not a button): where the RJ45 port points right now.
    # Light background, discreet colours depending on the state.
    if ($current -like "VM:*") {
        $rj45StateText.Text = T "rj45_state_vm" ($current -replace '^VM:\s*', '')
        $rj45StateBox.Background  = $BrushConverter.ConvertFromString("#F3F9FD")
        $rj45StateBox.BorderBrush = $BrushConverter.ConvertFromString("#C7E0F4")
    } elseif ([string]::IsNullOrWhiteSpace($current)) {
        $rj45StateText.Text = T "rj45_state_unset"
        $rj45StateBox.Background  = $BrushConverter.ConvertFromString("#FFF9EC")
        $rj45StateBox.BorderBrush = $BrushConverter.ConvertFromString("#F0E0B0")
    } else {
        $rj45StateText.Text = T "rj45_state_host"
        $rj45StateBox.Background  = $BrushConverter.ConvertFromString("#F1F8F1")
        $rj45StateBox.BorderBrush = $BrushConverter.ConvertFromString("#C8E6C9")
    }

    $hostCard = New-RJ45Card -Title (T "rj45_host_title") -Subtitle (T "rj45_host_subtitle" $adapterTxt) -IsCurrent ($current -eq "Host") -OnAssign ({
        Set-RJ45Target -TargetType "Host" -VMName $null
    }.GetNewClosure())
    $rj45CardsPanel.Children.Add($hostCard) | Out-Null

    $vms = Get-DetectedVMs
    if ($vms.Count -eq 0) {
        $tb = New-Object System.Windows.Controls.TextBlock
        $tb.Text = T "rj45_no_vm"
        $tb.Foreground = $BrushMuted
        $tb.Margin = New-Object System.Windows.Thickness(4,10,0,0)
        $rj45CardsPanel.Children.Add($tb) | Out-Null
    } else {
        foreach ($v in $vms) {
            $vname = $v
            $state = Format-VMState (Get-VM -Name $vname -ErrorAction SilentlyContinue).State
            $card = New-RJ45Card -Title (T "rj45_vm_title" $vname) -Subtitle (T "rj45_vm_subtitle" $state) -IsCurrent ($current -eq "VM: $vname") -OnAssign ({
                Set-RJ45Target -TargetType "VM" -VMName $vname
            }.GetNewClosure())
            $rj45CardsPanel.Children.Add($card) | Out-Null
        }
    }
  } catch {
    Set-Status (T "status_failed" $_.Exception.Message) "error"
  }
}

function Show-View([string]$name) {
    $profilesView.Visibility = $(if ($name -eq 'Profiles') { 'Visible' } else { 'Collapsed' })
    $rj45View.Visibility     = $(if ($name -eq 'RJ45') { 'Visible' } else { 'Collapsed' })
    $navProfiles.Tag = $(if ($name -eq 'Profiles') { 'Active' } else { $null })
    $navRJ45.Tag     = $(if ($name -eq 'RJ45') { 'Active' } else { $null })
    if ($name -eq 'RJ45') { Refresh-RJ45View }
    # Every time we return to the Profiles view, refresh the PC network configuration so
    # it is up to date without the user having to click "Refresh".
    if ($name -eq 'Profiles') { try { Refresh-CurrentNet } catch {} }
}

function Apply-SelectedProfile {
    $sel = $profilesGrid.SelectedItem
    if (-not $sel) { Set-Status (T "status_select_apply") "error"; return }
    $p = $sel._Profile

    $target = Show-ApplyTargetDialog -Owner $window -Profile $p
    if (-not $target) { return }

    # Remember the choice to pre-fill it next time (purely informative, not a frozen
    # property of the profile).
    $store = Get-Store
    $stamp = Get-Date -Format "dd/MM/yy-HH:mm:ss"
    for ($i = 0; $i -lt $store.Profiles.Count; $i++) {
        if ($store.Profiles[$i].Name -eq $p.Name) {
            $store.Profiles[$i] | Add-Member -NotePropertyName LastTarget -NotePropertyValue $target.Target -Force
            $store.Profiles[$i] | Add-Member -NotePropertyName LastAdapterName -NotePropertyValue $target.AdapterName -Force
            $store.Profiles[$i] | Add-Member -NotePropertyName LastVMName -NotePropertyValue $target.VMName -Force
            $store.Profiles[$i] | Add-Member -NotePropertyName LastVMAdapterName -NotePropertyValue $target.VMAdapterName -Force
            $store.Profiles[$i] | Add-Member -NotePropertyName LastAppliedOn -NotePropertyValue $stamp -Force
        }
    }
    Save-Store $store
    Refresh-ProfilesGrid

    $buttons = @($btnNewProfile, $btnEditProfile, $btnDeleteProfile, $btnApplyProfile, $btnRefreshProfiles, $navProfiles, $navRJ45)

    # ---------------- Target: HOST ----------------
    if ($target.Target -eq "Host") {
        $doHostApply = {
            Set-Status (T "status_applying_host") "info"
            $params = @{ AdapterName = $target.AdapterName; IPAddress = $p.IPAddress; PrefixLength = $p.PrefixLength; Gateway = $p.Gateway; DNS = $p.DNS; LogPath = $DiagLogPath }
            Invoke-Async -ScriptText $HostApplyScript -Params $params -Buttons $buttons -OnDone {
                param($result)
                Set-Status (T $result.Key $result.Args) $(if ($result.Success) { "success" } else { "error" })
                # Success or failure, the adapter's real state may have changed: refresh
                # the "current PC network configuration" panel right away.
                try { Refresh-CurrentNet } catch {}
            }.GetNewClosure()
        }.GetNewClosure()

        # Bug #2 (AUDIT.md): if the RJ45 port is attached to a VM (RJ45-Switch present)
        # AND the targeted adapter is exactly the RJ45 adapter, the host cannot configure
        # it (uplink of an external switch with no management-OS access). Offer to return
        # the port to the host, then chain the apply - a clear message instead of a
        # cryptic "element not found".
        $storeR = Get-Store
        $rjSwitchPresent = $false
        try { $rjSwitchPresent = [bool](Get-VMSwitch -Name $RJ45SwitchName -ErrorAction SilentlyContinue) } catch { }
        if ($rjSwitchPresent -and $target.AdapterName -and $storeR.RJ45.PhysicalAdapter -and ($target.AdapterName -eq $storeR.RJ45.PhysicalAdapter)) {
            if (-not (Show-ConfirmDialog -Owner $window -Title (T "rj45_blocks_host_title") -Message (T "rj45_blocks_host_msg" $target.AdapterName))) { return }
            Set-Status (T "status_rj45_releasing") "info"
            $relParams = @{ SwitchName = $RJ45SwitchName; PhysicalAdapter = $storeR.RJ45.PhysicalAdapter; VMName = $null }
            Invoke-Async -ScriptText $RJ45ToHostScript -Params $relParams -Buttons $buttons -OnDone {
                param($result)
                if ($result.Success) {
                    $s = Get-Store; $s.RJ45.CurrentTarget = "Host"; Save-Store $s
                    Refresh-RJ45View
                    & $doHostApply
                } else {
                    Set-Status (T $result.Key $result.Args) "error"
                }
            }.GetNewClosure()
            return
        }

        & $doHostApply
        return
    }

    # ---------------- Target: VM ----------------
    # PONI NEVER starts / stops a VM itself (lesson from testing: on this PC, juggling VM
    # power fails in confusing ways). The VM must already be running - the target popup
    # only offers running VMs; re-validate here just in case.
    $vm = Get-VM -Name $target.VMName -ErrorAction SilentlyContinue
    if (-not $vm) { Set-Status (T "status_vm_missing" $target.VMName) "error"; return }
    if ($vm.State -ne "Running") { Set-Status (T "status_vm_not_running" $target.VMName) "error"; return }

    $cred = Show-CredentialDialog -Owner $window -VMName $target.VMName
    if (-not $cred) { return }

    # MAC address of the adapter on the Hyper-V side: used to find the right adapter
    # inside the guest (see VMApplyScript), whatever its Windows name. The VM is running,
    # so the dynamic MAC is assigned.
    $vmAdapterMac = (Get-VMNetworkAdapter -VMName $target.VMName -Name $target.VMAdapterName -ErrorAction SilentlyContinue | Select-Object -First 1).MacAddress
    if (-not $vmAdapterMac -or $vmAdapterMac -eq '000000000000') { Set-Status (T "status_vm_adapter_not_found") "error"; return }

    Set-Status (T "status_applying_vm" $target.VMName) "info"
    $params = @{ VMName = $target.VMName; Credential = $cred; VMAdapterMac = $vmAdapterMac; IPAddress = $p.IPAddress; PrefixLength = $p.PrefixLength; Gateway = $p.Gateway; DNS = $p.DNS; LogPath = $DiagLogPath }
    Invoke-Async -ScriptText $VMApplyScript -Params $params -Buttons $buttons -OnDone {
        param($result)
        Set-Status (T $result.Key $result.Args) $(if ($result.Success) { "success" } else { "error" })
    }.GetNewClosure()
}

# ------------------------------------------------------------
# Event wiring
# ------------------------------------------------------------
$navProfiles.Add_Click({ Show-View 'Profiles' }.GetNewClosure())
$navRJ45.Add_Click({ Show-View 'RJ45' }.GetNewClosure())
$navQuit.Add_Click({ $window.Close() }.GetNewClosure())
$langCombo.Add_SelectionChanged({
    # Call Switch-Language unconditionally: IT decides whether there is a real change
    # (see the comment inside the function - the handler cannot test $script:Lang
    # reliably, .GetNewClosure() freezes its value).
    Switch-Language $(if ($langCombo.SelectedIndex -eq 0) { "fr" } else { "en" })
}.GetNewClosure())

$btnRefreshProfiles.Add_Click({ Refresh-ProfilesGrid; Refresh-CurrentNet; Set-Status (T "status_list_refreshed") "info" }.GetNewClosure())

$btnToggleView.Add_Click({ Toggle-ProfileView }.GetNewClosure())

$btnViewLogs.Add_Click({ try { Start-Process explorer.exe $StoreDir } catch {} }.GetNewClosure())
$btnClearLog.Add_Click({ $logList.Items.Clear() }.GetNewClosure())

# Small drop-down menu on "Export...": all / a selection.
$script:miExportAll = New-Object System.Windows.Controls.MenuItem
$script:miExportSel = New-Object System.Windows.Controls.MenuItem
$exportMenu = New-Object System.Windows.Controls.ContextMenu
$exportMenu.Items.Add($script:miExportAll) | Out-Null
$exportMenu.Items.Add($script:miExportSel) | Out-Null
$script:miExportAll.Add_Click({ Export-Profiles -All }.GetNewClosure())
$script:miExportSel.Add_Click({ Export-Profiles }.GetNewClosure())
$btnExportProfiles.Add_Click({
    $exportMenu.PlacementTarget = $btnExportProfiles
    $exportMenu.Placement = 'Bottom'
    $exportMenu.IsOpen = $true
}.GetNewClosure())

$btnImportProfiles.Add_Click({
    $dlg = New-Object Microsoft.Win32.OpenFileDialog
    $dlg.Title = T "import_dialog_title"
    $dlg.Filter = "JSON (*.json)|*.json|All files (*.*)|*.*"
    if (-not $dlg.ShowDialog()) { return }
    try {
        $raw = Get-Content -Path $dlg.FileName -Raw -Encoding UTF8 | ConvertFrom-Json
        $incoming = @()
        if ($raw.PONIProfiles) { $incoming = @($raw.PONIProfiles) }
        elseif ($raw.Profiles) { $incoming = @($raw.Profiles) }
        elseif ($raw -is [array]) { $incoming = @($raw) }
        $valid = @($incoming | Where-Object { $_.Name -and $_.IPAddress -and $_.PrefixLength })
        if ($valid.Count -eq 0) { Set-Status (T "status_import_none") "error"; return }
        $store = Get-Store
        $existingNames = @($store.Profiles | Select-Object -ExpandProperty Name)
        $added = 0; $skipped = 0
        foreach ($ip in $valid) {
            if ($existingNames -contains "$($ip.Name)") { $skipped++; continue }
            $store.Profiles = @($store.Profiles) + ([PSCustomObject]@{
                Name = "$($ip.Name)"; IPAddress = "$($ip.IPAddress)"; PrefixLength = [int]$ip.PrefixLength
                Gateway = "$($ip.Gateway)"; DNS = @($ip.DNS | Where-Object { $_ })
                CreatedOn = $(if ($ip.CreatedOn) { "$($ip.CreatedOn)" } else { (Get-Date -Format "yyyy-MM-dd HH:mm") })
                LastTarget = $null; LastAdapterName = $null; LastVMName = $null; LastVMAdapterName = $null; LastAppliedOn = $null
            })
            $existingNames += "$($ip.Name)"
            $added++
        }
        Save-Store $store
        Refresh-ProfilesGrid
        Set-Status (T "status_imported" @($added, $skipped)) "success"
    } catch {
        Set-Status (T "status_import_failed" $_.Exception.Message) "error"
    }
}.GetNewClosure())

$btnNewProfile.Add_Click({
    $result = Show-ProfileDialog -Owner $window -Existing $null
    if ($result) {
        $store = Get-Store
        $store.Profiles = @($store.Profiles) + $result
        Save-Store $store
        Refresh-ProfilesGrid
        Set-Status (T "status_profile_created" $result.Name) "success"
    }
}.GetNewClosure())

$btnEditProfile.Add_Click({
    $sel = $profilesGrid.SelectedItem
    if (-not $sel) { Set-Status (T "status_select_edit") "error"; return }
    $result = Show-ProfileDialog -Owner $window -Existing $sel._Profile
    if ($result) {
        $store = Get-Store
        for ($i = 0; $i -lt $store.Profiles.Count; $i++) {
            if ($store.Profiles[$i].Name -eq $result.Name) { $store.Profiles[$i] = $result }
        }
        Save-Store $store
        Refresh-ProfilesGrid
        Set-Status (T "status_profile_updated" $result.Name) "success"
    }
}.GetNewClosure())

$btnDeleteProfile.Add_Click({
    $sel = $profilesGrid.SelectedItem
    if (-not $sel) { Set-Status (T "status_select_delete") "error"; return }
    $confirm = Show-ConfirmDialog -Owner $window -Title (T "confirm_delete_title") -Message (T "confirm_delete_msg" $sel.Name)
    if ($confirm) {
        $store = Get-Store
        $store.Profiles = @($store.Profiles | Where-Object { $_.Name -ne $sel.Name })
        Save-Store $store
        Refresh-ProfilesGrid
        Set-Status (T "status_profile_deleted" $sel.Name) "success"
    }
}.GetNewClosure())

$btnApplyProfile.Add_Click({ Apply-SelectedProfile }.GetNewClosure())
$profilesGrid.Add_MouseDoubleClick({ Apply-SelectedProfile }.GetNewClosure())

$window.Add_Loaded({
    Update-UITexts
    # Background priority: let the window finish its first render before running
    # Refresh-ProfilesGrid (which calls Get-VM) - avoids a blank screen during the call.
    $window.Dispatcher.BeginInvoke([System.Windows.Threading.DispatcherPriority]::Background, [Action]{
        Refresh-ProfilesGrid
        Show-View 'Profiles'   # Show-View 'Profiles' already refreshes the PC network configuration
        # A deferred second pass: on the very first render an adapter may still be
        # acquiring an IP (DHCP, cable just plugged in). A single call ~1.5 s later locks
        # in the correct value, without looping.
        $t = New-Object System.Windows.Threading.DispatcherTimer
        $t.Interval = [TimeSpan]::FromMilliseconds(1500)
        $t.Add_Tick({ $t.Stop(); try { Refresh-CurrentNet } catch {} }.GetNewClosure())
        $t.Start()
    }.GetNewClosure()) | Out-Null
}.GetNewClosure())

# "Smart" refresh (no polling): when PONI regains focus while on the Profiles view,
# refresh the "current PC network configuration" panel - useful if the network changed
# while the user was in another window.
$window.Add_Activated({
    if ($profilesView.Visibility -eq 'Visible') { try { Refresh-CurrentNet } catch {} }
}.GetNewClosure())

# ============================================================
# ENTRY POINT
# ============================================================
try {
    $window.ShowDialog() | Out-Null
} catch {
    [System.Windows.MessageBox]::Show("Fatal error: $($_.Exception.Message)", "PONI", 'OK', 'Error') | Out-Null
}
