# Ищет папку игры R.E.P.O. во всех библиотеках Steam и печатает путь.
$roots = @()
$steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
if ($steam) {
  $steam = $steam -replace '/', '\'
  $roots += $steam
  $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
  if (Test-Path $vdf) {
    $text = Get-Content $vdf -Raw
    foreach ($m in [regex]::Matches($text, '\x22path\x22\s+\x22([^\x22]+)\x22')) {
      $roots += ($m.Groups[1].Value -replace '\\\\', '\')
    }
  }
}
foreach ($drive in (Get-PSDrive -PSProvider FileSystem)) {
  $roots += (Join-Path $drive.Root 'SteamLibrary')
  $roots += (Join-Path $drive.Root 'Steam')
}
foreach ($r in $roots) {
  $g = Join-Path $r 'steamapps\common\REPO'
  if (Test-Path (Join-Path $g 'REPO_Data\Managed')) { Write-Output $g; break }
}
