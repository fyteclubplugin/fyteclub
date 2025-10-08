$asm = [Reflection.Assembly]::LoadFrom('c:\Users\Me\git\Penumbra.Api\bin\Debug\Penumbra.Api.dll')
$types = $asm.GetExportedTypes() | Where-Object { $_.FullName -like '*TrySetModSetting*' }
foreach ($t in $types) {
    Write-Host "Type: $($t.FullName)"
    $methods = $t.GetMethods() | Where-Object { $_.Name -eq 'Invoke' }
    foreach ($m in $methods) {
        Write-Host " Method: $($m.ToString())"
        foreach ($p in $m.GetParameters()) {
            Write-Host "  - $($p.ParameterType.FullName)"
        }
    }
}
