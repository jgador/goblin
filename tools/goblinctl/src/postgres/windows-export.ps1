$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
try {
    $inputData = [Console]::In.ReadToEnd() | ConvertFrom-Json
    if ($inputData.profile -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,47}$' -or $inputData.role -notin @('app', 'admin')) { throw 'Invalid export' }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    function Assert-Path($path) {
        $current = $path
        while ($current) {
            if ((Test-Path -LiteralPath $current) -and ((Get-Item -Force -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse point in export path' }
            $current = Split-Path -Parent $current
        }
    }
    function Private-Directory($path) {
        Assert-Path $path
        [IO.Directory]::CreateDirectory($path) | Out-Null
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner($sid)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        Set-Acl -LiteralPath $path -AclObject $acl
    }
    function Assert-Identity($identity) {
        foreach ($field in @('cluster', 'volume', 'ca_sha256')) {
            if (-not $identity.$field -or $identity.$field -cne $inputData.identity.$field) { throw 'Windows profile belongs to a different database. Use a different profile name.' }
        }
    }
    function Write-PrivateFile($path, $value) {
        Assert-Path $path
        if (-not (Test-Path -LiteralPath $path)) { [IO.File]::Create($path).Dispose() }
        $acl = New-Object Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner($sid)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'Allow')))
        Set-Acl -LiteralPath $path -AclObject $acl
        [IO.File]::WriteAllText($path, $value, $encoding)
    }
    function Remove-Generation($path) {
        Assert-Path $path
        if (Test-Path -LiteralPath $path) {
            foreach ($file in Get-ChildItem -Force -LiteralPath $path) {
                Assert-Path $file.FullName
                if ($file.PSIsContainer -or $file.Name -notin @('tls.crt', 'tls.key', 'connection.json')) { throw 'Unexpected files in export generation' }
            }
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
    $store = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Goblin\postgres'
    Private-Directory $store
    $profile = Join-Path $store $inputData.profile
    Private-Directory $profile
    # Serialize exports from multiple WSL distributions to the same Windows profile.
    $lockPath = Join-Path $profile '.lock'
    Assert-Path $lockPath
    $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        $identityPath = Join-Path $profile 'identity.json'
        Assert-Path $identityPath
        $hasIdentity = Test-Path -LiteralPath $identityPath
        if ($hasIdentity) { Assert-Identity (Get-Content -Raw -LiteralPath $identityPath | ConvertFrom-Json) }
        foreach ($name in @('app', 'admin', '.previous-app', '.previous-admin')) {
            $generation = Join-Path $profile $name
            Assert-Path $generation
            if (Test-Path -LiteralPath $generation) {
                if (-not $hasIdentity) { throw 'Windows profile is missing its database identity. Recreate the export.' }
                $settings = Join-Path $generation 'connection.json'
                Assert-Path $settings
                Assert-Identity ((Get-Content -Raw -LiteralPath $settings | ConvertFrom-Json).identity)
            }
        }
        $caPath = Join-Path $profile 'ca.crt'
        Assert-Path $caPath
        if ((Test-Path -LiteralPath $caPath) -and [IO.File]::ReadAllText($caPath) -cne $inputData.ca) { throw 'Windows export has a different CA' }
        $role = Join-Path $profile $inputData.role
        $staging = Join-Path $profile ('.export-' + $inputData.role)
        $previous = Join-Path $profile ('.previous-' + $inputData.role)
        foreach ($path in @($role, $staging, $previous)) { Assert-Path $path }
        # Recover an interrupted directory replacement before starting a new export.
        if (Test-Path -LiteralPath $previous) {
            Remove-Generation $role
            [IO.Directory]::Move($previous, $role)
        }
        Remove-Generation $staging
        Private-Directory $staging
        $encoding = New-Object Text.UTF8Encoding($false)
        $settings = @{
            host = 'localhost'; port = $inputData.port; database = 'goblin'; username = ('goblin_' + $inputData.role)
            sslmode = 'verify-full'; sslrootcert = $caPath
            sslcert = (Join-Path $role 'tls.crt'); sslkey = (Join-Path $role 'tls.key'); identity = $inputData.identity
        } | ConvertTo-Json -Depth 5
        # All new files inherit the private staging ACL before any key bytes are written.
        Write-PrivateFile (Join-Path $staging 'tls.crt') $inputData.certificate
        Write-PrivateFile (Join-Path $staging 'tls.key') $inputData.key
        Write-PrivateFile (Join-Path $staging 'connection.json') $settings
        if (-not (Test-Path -LiteralPath $caPath)) { Write-PrivateFile $caPath $inputData.ca }
        Write-PrivateFile $identityPath ($inputData.identity | ConvertTo-Json)
        if (Test-Path -LiteralPath $role) { [IO.Directory]::Move($role, $previous) }
        try { [IO.Directory]::Move($staging, $role) }
        catch {
            if (Test-Path -LiteralPath $previous) { [IO.Directory]::Move($previous, $role) }
            throw
        }
        Remove-Generation $previous
        [Console]::Out.WriteLine((Join-Path $role 'connection.json'))
    } finally { $lock.Dispose() }
} catch { [Console]::Error.WriteLine('Cannot write private Windows PostgreSQL export. Check its identity, permissions, and whether another export is running.'); exit 1 }
