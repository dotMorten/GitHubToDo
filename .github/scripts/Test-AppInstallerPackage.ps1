param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$AppInstallerPath
)

$ErrorActionPreference = 'Stop'
[xml]$installer = Get-Content -LiteralPath $AppInstallerPath -Raw
$feed = $installer.AppInstaller.MainBundle
if (-not $feed -or $installer.AppInstaller.MainPackage) {
    throw 'The App Installer feed must describe a MainBundle.'
}
if ($feed.Version -cne $installer.AppInstaller.Version) {
    throw 'The feed and bundle versions must match.'
}
$bundle = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))

function Read-Entry([IO.Compression.ZipArchive]$Archive, [string]$Name) {
    $entry = $Archive.GetEntry($Name)
    if (-not $entry) { throw "Missing package entry: $Name." }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Assert-Identity($Identity, $Expected) {
    foreach ($attribute in 'Name','Publisher','Version') {
        if ($Identity.GetAttribute($attribute) -cne $Expected.GetAttribute($attribute)) {
            throw "Package identity mismatch: $attribute."
        }
    }
}

try {
    [xml]$bundleManifest = Read-Entry $bundle 'AppxMetadata/AppxBundleManifest.xml'
    Assert-Identity $bundleManifest.Bundle.Identity $feed
    $architectures = @()
    foreach ($entry in @($bundleManifest.Bundle.Packages.Package | Where-Object Type -eq 'application')) {
        $architecture = $entry.Architecture
        if ($architecture -notin 'x64','arm64' -or $architecture -in $architectures) {
            throw "Unexpected or duplicate application architecture: $architecture."
        }
        $architectures += $architecture
        if ($entry.Version -cne $feed.Version) { throw 'Bundled package version mismatch.' }
        $content = $bundle.GetEntry($entry.FileName)
        if (-not $content) { throw "Missing bundled package: $($entry.FileName)." }
        $stream = [IO.MemoryStream]::new()
        try {
            $source = $content.Open()
            try { $source.CopyTo($stream) } finally { $source.Dispose() }
            $stream.Position = 0
            $package = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
            try {
                [xml]$manifest = Read-Entry $package 'AppxManifest.xml'
                Assert-Identity $manifest.Package.Identity $feed
                if ($manifest.Package.Identity.ProcessorArchitecture -cne $architecture) {
                    throw 'Bundled package architecture mismatch.'
                }
                if ((Read-Entry $package 'Update.appinstaller') -cne (Get-Content -LiteralPath $AppInstallerPath -Raw)) {
                    throw 'The embedded App Installer file differs from the published feed.'
                }
                $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
                $namespaces.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
                $namespaces.AddNamespace('uap13', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/13')
                $reference = $manifest.SelectSingleNode('/p:Package/p:Properties/uap13:AutoUpdate/uap13:AppInstaller', $namespaces)
                if (-not $reference -or $reference.GetAttribute('File') -cne 'Update.appinstaller') {
                    throw 'The package manifest must reference the embedded Update.appinstaller.'
                }
            } finally { $package.Dispose() }
        } finally { $stream.Dispose() }
    }
    foreach ($required in 'x64','arm64') {
        if ($required -notin $architectures) {
            throw "The bundle is missing the $required application package."
        }
    }
    if ([uri]::UnescapeDataString(([uri]$feed.Uri).Segments[-1]) -cne
        [IO.Path]::GetFileName($PackagePath)) {
        throw 'The download URI must name the published MSIX bundle.'
    }
} finally {
    $bundle.Dispose()
}
Write-Host "Verified update configuration in $PackagePath"
