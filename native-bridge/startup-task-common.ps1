$ErrorActionPreference = 'Stop'
function Assert-Oxp3AuthPath([string]$ItemPath) {
    # Inspect every existing ancestor, including LocalAppData and its parents.
    # Never follow a user-created junction/symlink during elevated startup.
    $current = [IO.Path]::GetFullPath($ItemPath)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'An authentication path contains a reparse point; token synchronization refused.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}
function Protect-Oxp3TokenPath([string]$ItemPath, [bool]$Directory) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    if ($Directory) { $acl = [Security.AccessControl.DirectorySecurity]::new() }
    else { $acl = [Security.AccessControl.FileSecurity]::new() }
    $acl.SetOwner($sid)
    $acl.SetAccessRuleProtection($true, $false)
    if ($Directory) {
        $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, $propagation, 'Allow'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($systemSid, 'FullControl', $inherit, $propagation, 'Allow'))
    } else {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($systemSid, 'FullControl', 'Allow'))
    }
    Set-Acl -LiteralPath $ItemPath -AclObject $acl -ErrorAction Stop
}
function Startup-Hash([string]$ItemPath) {
    (Get-FileHash -LiteralPath $ItemPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Startup-TextHash([string]$Text) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { -join ($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)) | ForEach-Object { $_.ToString('x2') }) }
    finally { $hash.Dispose() }
}
function Assert-StartupAdmin {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Startup changes require elevation as the signed-in user.' }
}
function Protect-StartupCode([string]$ItemPath, [bool]$Directory) {
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    if ($Directory) { $acl = [Security.AccessControl.DirectorySecurity]::new() }
    else { $acl = [Security.AccessControl.FileSecurity]::new() }
    $acl.SetOwner($administrators)
    $acl.SetAccessRuleProtection($true, $false)
    if ($Directory) {
        $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($administrators, 'FullControl', $inherit, $propagation, 'Allow'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($systemSid, 'FullControl', $inherit, $propagation, 'Allow'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users, 'ReadAndExecute', $inherit, $propagation, 'Allow'))
    } else {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($administrators, 'FullControl', 'Allow'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($systemSid, 'FullControl', 'Allow'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users, 'ReadAndExecute', 'Allow'))
    }
    Set-Acl -LiteralPath $ItemPath -AclObject $acl
}
function Assert-NoStartupReparse([string]$ItemPath) {
    if ((Test-Path -LiteralPath $ItemPath) -and ((Get-Item -LiteralPath $ItemPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw ('Startup code/backup path is a reparse point: ' + $ItemPath)
    }
}
function Get-Oxp3PausePreferencePaths {
    return @(
        (Join-Path $env:LOCALAPPDATA 'OXP3PowerWidget\remembered-watts.json'),
        (Join-Path $env:LOCALAPPDATA 'Packages\OXP3.PowerWidget_pv4gfha69c7qe\LocalCache\Local\OXP3PowerWidget\remembered-watts.json')
    )
}
function Assert-AutomationPaused {
    # Check both physical locations. Windows redirects the packaged full-trust
    # helper's preference reads; a stale native-root copy is not sufficient.
    foreach ($preferencesPath in (Get-Oxp3PausePreferencePaths)) {
        Assert-Oxp3AuthPath $preferencesPath
        if (Test-Path -LiteralPath $preferencesPath) {
            try { $preferences = Get-Content -LiteralPath $preferencesPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
            catch { throw 'Cannot verify paused automation: preferences are invalid or unreadable.' }
            if ($preferences.enabled -isnot [bool] -or $preferences.enabled -ne $false) { throw 'Pause automatic wattage switching before changing native startup.' }
        }
    }
}
function Get-ReviewedStartupXml([string]$Text) {
    [xml]$xml = $Text
    $manager = [Xml.XmlNamespaceManager]::new($xml.NameTable)
    $manager.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    $actions = $xml.SelectNodes('/t:Task/t:Actions/t:Exec', $manager)
    if ($actions.Count -ne 1 -or $xml.SelectNodes('/t:Task/t:Actions/*', $manager).Count -ne 1) { throw 'Unreviewed native scheduled-task actions.' }
    $command = $actions[0].SelectSingleNode('t:Command', $manager).InnerText
    $arguments = $actions[0].SelectSingleNode('t:Arguments', $manager).InnerText
    $working = $actions[0].SelectSingleNode('t:WorkingDirectory', $manager)
    if ($command -ne 'wscript.exe' -or $arguments -ne '"C:\Program Files\OneXConsole\resources\resources\tools\OneXConsoleStartup.vbs"' -or ($working -and $working.InnerText)) {
        throw 'Native startup action changed since review.'
    }
    $principal = $xml.SelectSingleNode('/t:Task/t:Principals/t:Principal', $manager)
    if (-not $principal -or $principal.SelectSingleNode('t:RunLevel', $manager).InnerText -ne 'HighestAvailable') { throw 'Native startup is not the reviewed Highest task.' }
    $account = $principal.SelectSingleNode('t:UserId', $manager).InnerText
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($account -match '^S-1-') { $taskSid = $account }
    else { $taskSid = [Security.Principal.NTAccount]::new($account).Translate([Security.Principal.SecurityIdentifier]).Value }
    if ($taskSid -ne $currentSid) { throw 'Native startup belongs to a different Windows user.' }
    return @{ Xml = $xml; Manager = $manager; Action = $actions[0] }
}
