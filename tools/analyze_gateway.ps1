$path = "d:\AegisComputerUse\tools\Antigravity_Gateway.exe"
$bytes = [System.IO.File]::ReadAllBytes($path)
$assembly = [System.Reflection.Assembly]::Load($bytes)

Write-Host "=== ASSEMBLY INFO ==="
Write-Host "FullName: " $assembly.FullName

Write-Host "`n=== TYPES & MEMBERS ==="
foreach ($type in $assembly.GetTypes()) {
    Write-Host "`nType: $($type.FullName)"
    $methods = $type.GetMethods([System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static') | Where-Object { $_.DeclaringType -eq $type }
    foreach ($m in $methods) {
        $params = ($m.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Host "  Method: $($m.ReturnType.Name) $($m.Name)($params)"
    }
    $fields = $type.GetFields([System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static') | Where-Object { $_.DeclaringType -eq $type }
    foreach ($f in $fields) {
        $val = try { $f.GetValue($null) } catch { "instance" }
        Write-Host "  Field: $($f.FieldType.Name) $($f.Name) = $val"
    }
}
