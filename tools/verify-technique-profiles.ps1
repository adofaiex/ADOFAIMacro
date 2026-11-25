param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\ADOFAIMacro\bin\Release\ADOFAIMacro.dll')
)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath))
$profileType = $assembly.GetType('ADOFAIMacro.Settings.TechniqueProfile', $true)
$profile = [Activator]::CreateInstance($profileType)
$profile.groupingStrategyId = 'tempoWindow'
$profile.runHandPolicyId = 'recoveredPreferred'
$profile.singleFingerKps = [float]7
$profile.lazyRelease = $true

$serializer = [Xml.Serialization.XmlSerializer]::new($profileType)
$writer = [IO.StringWriter]::new()
try {
    $serializer.Serialize($writer, $profile)
    $reader = [IO.StringReader]::new($writer.ToString())
    try { $roundtrip = $serializer.Deserialize($reader) }
    finally { $reader.Dispose() }
}
finally { $writer.Dispose() }

if ($roundtrip.groupingStrategyId -ne 'tempoWindow' -or
    $roundtrip.runHandPolicyId -ne 'recoveredPreferred' -or
    $roundtrip.singleFingerKps -ne 7 -or $roundtrip.lazyRelease -ne $true) {
    throw 'Technique profile XML roundtrip lost strategy parameters'
}
$clone = $profile.Clone()
if ($clone.groupingStrategyId -ne 'tempoWindow' -or
    $clone.runHandPolicyId -ne 'recoveredPreferred' -or $clone.singleFingerKps -ne 7 -or $clone.lazyRelease -ne $true) {
    throw 'Technique profile cloning lost strategy parameters'
}

$legacyReader = [IO.StringReader]::new('<TechniqueProfile><name>old</name></TechniqueProfile>')
try { $legacy = $serializer.Deserialize($legacyReader) }
finally { $legacyReader.Dispose() }
if ($legacy.groupingStrategyId -ne 'beatGrid' -or
    $legacy.runHandPolicyId -ne 'alternating' -or $legacy.singleFingerKps -ne 6.5 -or $legacy.lazyRelease -ne $false) {
    throw 'Legacy profile missing-field defaults changed'
}

Write-Output 'PASS technique profile XML roundtrip, clone and legacy defaults (compiled mod assembly)'
