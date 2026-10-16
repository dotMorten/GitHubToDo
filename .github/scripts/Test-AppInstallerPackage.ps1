param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$AppInstallerPath
)

$ErrorActionPreference = 'Stop'
[xml]$installer = Get-Content -LiteralPath $AppInstallerPath -Raw
$package = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
try {
    $manifestEntry = $package.GetEntry('AppxManifest.xml')
    $embeddedEntry = $package.GetEntry('Update.appinstaller')
    if (-not $manifestEntry -or -not $embeddedEntry) {
        throw 'The MSIX must contain AppxManifest.xml and Update.appinstaller.'
    }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $reader = [IO.StreamReader]::new($embeddedEntry.Open())
    try { $embedded = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($embedded -cne (Get-Content -LiteralPath $AppInstallerPath -Raw)) {
        throw 'The embedded App Installer file differs from the published feed.'
    }
    foreach ($attribute in 'Name','Publisher','Version','ProcessorArchitecture') {
        if ($manifest.Package.Identity.GetAttribute($attribute) -cne
            $installer.AppInstaller.MainPackage.GetAttribute($attribute)) {
            throw "Package identity mismatch: $attribute."
        }
    }
    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('uap13', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/13')
    $reference = $manifest.SelectSingleNode('/p:Package/p:Properties/uap13:AutoUpdate/uap13:AppInstaller', $namespaces)
    if (-not $reference -or $reference.GetAttribute('File') -cne 'Update.appinstaller') {
        throw 'The package manifest must reference the embedded Update.appinstaller.'
    }
    if ([uri]::UnescapeDataString(([uri]$installer.AppInstaller.MainPackage.Uri).Segments[-1]) -cne
        [IO.Path]::GetFileName($PackagePath)) {
        throw 'The download URI must name the published MSIX.'
    }
} finally {
    $package.Dispose()
}
Write-Host "Verified update configuration in $PackagePath"
