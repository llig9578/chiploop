param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'CHIPLOOP.exe'),
    [string]$PackageCache = (Join-Path ([IO.Path]::GetTempPath()) 'chiploop-codecs')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $PackageCache -Force | Out-Null
$PackageCache = (Resolve-Path -LiteralPath $PackageCache).Path
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)

$packages = @(
    @('NVorbis', '0.10.5', 'net45', 'D432921DC2BE29C062A95CAE247E12955C9B709FD347823D01DFD68A16C5387A'),
    @('Concentus', '2.2.2', 'net452', 'F487F890BEF65757AF1CECF420E357F00488CBFAC0B409BA461CCBCEEF81481F'),
    @('Concentus.Oggfile', '1.0.7', 'net452', 'D7A7B1B9B88AD9A62ECFB52C742190A522E14BCBD1275A36D33EBE4C3644C07C'),
    @('System.Memory', '4.5.4', 'net461', 'DEC0847F33B8823E4260672D97D657411461C00ADA3107EC7BBCB32A845EEB91'),
    @('System.Buffers', '4.5.1', 'net461', 'C30B3DD2C7E2F4CEE4B823D692FD42118309B42AB1F5007F923D329A5B0D6B12'),
    @('System.Numerics.Vectors', '4.5.0', 'net46', 'A9D49320581FDA1B4F4BE6212C68C01A22CDF228026099C20A8EABEFCF90F9CF'),
    @('System.Runtime.CompilerServices.Unsafe', '4.5.3', 'net461', '96764C52A44EE1161151E48EF07489F72047A851CB55B99E9F01D6908536D1A9'),
    @('System.ValueTuple', '4.5.0', 'net47', '9E21FA9767D4E76BC0CEE065C1D40CC34384A114BFEC4D70E6C981168A926802')
)

$arguments = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    '/reference:Accessibility.dll', "/out:$OutputPath"
)

foreach ($package in $packages) {
    $name, $version, $framework, $expectedHash = $package
    $id = $name.ToLowerInvariant()
    $file = "$id.$version.nupkg"
    $path = Join-Path $PackageCache $file
    if (-not (Test-Path -LiteralPath $path)) {
        $uri = "https://api.nuget.org/v3-flatcontainer/$id/$version/$file"
        if ($name -eq 'Concentus.Oggfile') {
            $uri = "https://www.nuget.org/api/v2/package/$name/$version"
        }
        & curl.exe --fail --silent --show-error --location --retry 2 --connect-timeout 10 --max-time 45 $uri --output $path
        if ($LASTEXITCODE -ne 0) { throw "Could not download $file." }
    }

    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expectedHash) {
        throw "Unexpected SHA-256 for $path. Remove the file and build again."
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $archive.GetEntry("lib/$framework/$name.dll")
        if ($null -eq $entry) { throw "Missing $name.dll in $file." }
        $assemblyPath = Join-Path $PackageCache "$name.dll"
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $assemblyPath, $true)
    }
    finally {
        $archive.Dispose()
    }
    $arguments += "/reference:$assemblyPath", "/resource:$assemblyPath,Codec.$name.dll"
}

$notices = Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt'
$arguments += "/resource:$notices,THIRD-PARTY-NOTICES.txt"
foreach ($file in 'Studio.cs', 'AudioImport.cs', 'OggAudio.cs') {
    $arguments += (Join-Path $PSScriptRoot $file)
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "CHIPLOOP compilation failed ($LASTEXITCODE)." }
Get-Item -LiteralPath $OutputPath
