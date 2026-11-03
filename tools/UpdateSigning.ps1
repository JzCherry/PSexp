# Public signing tools. The private key is kept outside the repository and protected by Windows DPAPI.
Set-StrictMode -Version Latest

function Get-PSexpSigningKeyPath {
    if (-not [string]::IsNullOrWhiteSpace($env:PSexpSigningKeyFile)) {
        return [IO.Path]::GetFullPath($env:PSexpSigningKeyFile)
    }
    # Resolve the Windows known folder, not a possibly stale LOCALAPPDATA inherited from a launcher.
    $localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localData)) {
        throw 'Windows could not resolve the current user LocalApplicationData folder.'
    }
    return Join-Path $localData 'PSexp\ReleaseSigning\update-key.dpapi'
}

function Get-PSexpSigningBackupPath {
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ([string]::IsNullOrWhiteSpace($userProfile)) {
        throw 'Windows could not resolve the current user profile for the protected key backup.'
    }
    return Join-Path $userProfile '.psexp-release-signing\update-key.dpapi'
}

function Read-PSexpProtectedSigningKey([string]$Path, [string]$RepoPrefix) {
    if ($Path.StartsWith($RepoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Private signing keys must stay outside the source repository.'
    }
    return ,([IO.File]::ReadAllBytes($Path))
}

function Get-PSexpUpdateSigner([string]$Repository) {
    $keyFile = Get-PSexpSigningKeyPath
    $repoPrefix = [IO.Path]::GetFullPath($Repository).TrimEnd('\') + '\'

    $trustPath = Join-Path $Repository 'PSdodo\Resources\Updates\update-public-key.json'
    $trusted = [IO.File]::ReadAllText($trustPath) | ConvertFrom-Json
    if ($trusted.algorithm -ne 'RSA-SHA256-PKCS1') { throw 'Unsupported release signing algorithm.' }
    Add-Type -AssemblyName System.Security
    $plain = $null
    $protectedBytes = $null
    $rsa = $null
    try {
        try {
            try {
                $protectedBytes = Read-PSexpProtectedSigningKey $keyFile $repoPrefix
            } catch {
                $cause = $_.Exception.GetBaseException()
                if (-not [string]::IsNullOrWhiteSpace($env:PSexpSigningKeyFile) -or
                    ($cause -isnot [IO.FileNotFoundException] -and $cause -isnot [IO.DirectoryNotFoundException])) {
                    throw
                }
                # Only a missing default path can use the same user's verified DPAPI backup.
                Write-Host ('[signing] Default key path unavailable: ' + $cause.Message)
                $keyFile = Get-PSexpSigningBackupPath
                Write-Host ('[signing] Trying protected local backup: ' + $keyFile)
                $protectedBytes = Read-PSexpProtectedSigningKey $keyFile $repoPrefix
            }
        } catch {
            $cause = $_.Exception.GetBaseException()
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            throw ('Cannot read the original protected signing key. File: ' + $keyFile +
                '. Windows error: ' + $cause.GetType().FullName + ' / 0x' + $cause.HResult.ToString('X8') +
                ' / ' + $cause.Message + '. User SID: ' + $identity.User.Value +
                '. Keep the original key; do not generate a replacement.')
        }
        Write-Host ('[signing] Protected key read: OK (' + $keyFile + ')')
        $plain = [Security.Cryptography.ProtectedData]::Unprotect(
            $protectedBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(3072)
        $rsa.PersistKeyInCsp = $false
        $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($plain))
        if ($rsa.KeySize -ne 3072 -or $rsa.ToXmlString($false) -cne $trusted.public_key_xml) {
            throw 'Signing key does not match the public key embedded in this source tree.'
        }
        return $rsa
    } catch {
        if ($null -ne $rsa) { $rsa.Dispose() }
        if ($_.Exception.GetBaseException() -is [Security.Cryptography.CryptographicException]) {
            throw ('Could not decrypt or load the original signing key for Windows account ' +
                [Security.Principal.WindowsIdentity]::GetCurrent().Name +
                '. File: ' + $keyFile + '. Restore its original Windows user/DPAPI environment; do not replace the key.')
        }
        throw
    } finally {
        if ($null -ne $plain) { [Array]::Clear($plain, 0, $plain.Length) }
        if ($null -ne $protectedBytes) { [Array]::Clear($protectedBytes, 0, $protectedBytes.Length) }
    }
}

function Assert-PSexpSigningReady([string]$Repository) {
    $keyFile = Get-PSexpSigningKeyPath
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $source = 'Windows current-user LocalApplicationData'
    if (-not [string]::IsNullOrWhiteSpace($env:PSexpSigningKeyFile)) { $source = 'PSexpSigningKeyFile override' }
    Write-Host ('[signing] Windows account: ' + $identity)
    Write-Host ('[signing] Windows user SID: ' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    Write-Host ('[signing] Key location source: ' + $source)
    Write-Host ('[signing] Protected key file: ' + $keyFile)
    # Paths and account names only. Never print private key bytes or DPAPI plaintext.
    $rsa = Get-PSexpUpdateSigner $Repository
    try { Write-Host 'Release signing identity available; private key will not be uploaded.' }
    finally { $rsa.Dispose() }
}

function New-PSexpSignedUpdateManifest([System.Collections.IDictionary]$Manifest, [string]$Repository) {
    $rsa = Get-PSexpUpdateSigner $Repository
    try {
        $trusted = [IO.File]::ReadAllText((Join-Path $Repository 'PSdodo\Resources\Updates\update-public-key.json')) | ConvertFrom-Json
        $payload = [Text.Encoding]::UTF8.GetBytes(($Manifest | ConvertTo-Json -Depth 8 -Compress))
        if ($payload.Length -gt 49152) { throw 'Signed update payload is too large.' }
        $prefix = [Text.Encoding]::UTF8.GetBytes("PSexp update manifest v1`n")
        $authenticated = [byte[]]::new($prefix.Length + $payload.Length)
        [Array]::Copy($prefix, 0, $authenticated, 0, $prefix.Length)
        [Array]::Copy($payload, 0, $authenticated, $prefix.Length, $payload.Length)
        $signature = $rsa.SignData($authenticated, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $result = [ordered]@{}
        # Legacy readers can see flat fields; new clients use only the authenticated inner payload.
        foreach ($field in $Manifest.Keys) { $result[$field] = $Manifest[$field] }
        $result['signed_update'] = [ordered]@{
            schema = 1; key_id = $trusted.key_id; algorithm = 'RSA-SHA256-PKCS1'
            payload = [Convert]::ToBase64String($payload); signature = [Convert]::ToBase64String($signature)
        }
        return $result
    } finally { $rsa.Dispose() }
}
