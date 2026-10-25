param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [Parameter(Mandatory=$true)][string]$AnchorChainDirectory,
    [ValidateSet('extension-main-api','extension-api-main','main-extension-api','main-api-extension','api-extension-main','api-main-extension','disabled')]
    [string]$Order = 'extension-main-api'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$repo = Split-Path $PSScriptRoot -Parent
$mainOutput = Join-Path $repo 'src\bin\Release\netstandard2.1'
$example = Join-Path $repo 'examples\ExternalAdapter\bin\Release\netstandard2.1\SeaPowerAIAdvisor.Example.dll'
$probe = Join-Path $repo 'tests\ExtensionLoading\bin\Release\netstandard2.1\SeaPowerAIAdvisor.ExtensionLoading.dll'
$managed = Join-Path $GameDirectory 'Sea Power_Data\Managed'
$core = Join-Path $GameDirectory 'BepInEx\core'
$runtime = Join-Path $GameDirectory 'MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll'
foreach ($file in @($runtime,$probe,$example,(Join-Path $mainOutput 'SeaPowerAIAdvisor.dll'),(Join-Path $mainOutput 'SeaPowerAIAdvisor.Api.dll'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Build the host, sample and loading probe first: $file" }
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('SeaPowerAIAdvisor-ExtensionLoading-' + [Guid]::NewGuid().ToString('N'))
$vector = [IntPtr]::Zero
$strings = @()
try {
    $hostFolder = New-Item -ItemType Directory -Path (Join-Path $scratch 'host')
    $extensionFolder = New-Item -ItemType Directory -Path (Join-Path $scratch 'extension')
    Copy-Item -LiteralPath (Join-Path $mainOutput 'SeaPowerAIAdvisor.dll') -Destination $hostFolder.FullName
    Copy-Item -LiteralPath (Join-Path $mainOutput 'SeaPowerAIAdvisor.Api.dll') -Destination $hostFolder.FullName
    Copy-Item -LiteralPath $example -Destination $extensionFolder.FullName
    Copy-Item -LiteralPath $probe -Destination $scratch
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class AdvisorExtensionMono {
    [DllImport(@"$runtime", CallingConvention=CallingConvention.Cdecl)] public static extern void mono_set_dirs(string assemblyPath, string configPath);
    [DllImport(@"$runtime", CallingConvention=CallingConvention.Cdecl)] public static extern void mono_set_assemblies_path(string path);
    [DllImport(@"$runtime", CallingConvention=CallingConvention.Cdecl)] public static extern int mono_main(int argc, IntPtr argv);
}
"@
    # Only game/framework dependencies are probed. The host API must not be found in advance.
    $search = @($managed,$core,$AnchorChainDirectory) -join ';'
    [Environment]::SetEnvironmentVariable('MONO_PATH',$search)
    [AdvisorExtensionMono]::mono_set_dirs($managed,(Join-Path $GameDirectory 'MonoBleedingEdge\etc'))
    [AdvisorExtensionMono]::mono_set_assemblies_path($search)
    $arguments = @('AdvisorExtensionMono','--runtime=v4.0.30319',(Join-Path $scratch 'SeaPowerAIAdvisor.ExtensionLoading.dll'),
        (Join-Path $hostFolder.FullName 'SeaPowerAIAdvisor.dll'),(Join-Path $hostFolder.FullName 'SeaPowerAIAdvisor.Api.dll'),
        (Join-Path $extensionFolder.FullName 'SeaPowerAIAdvisor.Example.dll'),$Order)
    $strings = @($arguments | ForEach-Object { [Runtime.InteropServices.Marshal]::StringToHGlobalAnsi($_) })
    $vector = [Runtime.InteropServices.Marshal]::AllocHGlobal([IntPtr]::Size * ($strings.Count + 1))
    for ($index=0; $index -lt $strings.Count; $index++) { [Runtime.InteropServices.Marshal]::WriteIntPtr($vector,[IntPtr]::Size*$index,$strings[$index]) }
    [Runtime.InteropServices.Marshal]::WriteIntPtr($vector,[IntPtr]::Size*$strings.Count,[IntPtr]::Zero)
    $result = [AdvisorExtensionMono]::mono_main($strings.Count,$vector)
    if ($result -ne 0) { throw "Extension loading failed: $Order (exit $result)" }
} finally {
    foreach ($pointer in $strings) { [Runtime.InteropServices.Marshal]::FreeHGlobal($pointer) }
    if ($vector -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::FreeHGlobal($vector) }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
