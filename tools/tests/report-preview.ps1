# Shows a report's cover sheet the way Apache OpenOffice Calc renders it (machines with TIA Portal rarely have
# Excel). Starts a private headless OpenOffice, exports the first sheet of the workbook to PDF and, if Python with
# pymupdf is installed, renders page 1 to PNG next to it.
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\tests\report-preview.ps1 -Xlsx <report.xlsx> [-Pdf <out.pdf>]
param([Parameter(Mandatory)][string]$Xlsx, [string]$Pdf)
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$out = "$repo\tools\tests\out"
New-Item -ItemType Directory -Force $out | Out-Null
if (-not $Pdf) { $Pdf = "$out\" + [IO.Path]::GetFileNameWithoutExtension($Xlsx) + "_cover.pdf" }
$office = "${env:ProgramFiles(x86)}\OpenOffice 4\program"
if (-not (Test-Path "$office\soffice.exe")) { throw "Apache OpenOffice 4 not found in $office" }

# Own profile and port, so it never touches an OpenOffice the user has open; only this process is stopped afterwards.
$profileUrl = "file:///" + ("$out\aoo_profile" -replace '\\', '/')
$soffice = Start-Process "$office\soffice.exe" -PassThru -ArgumentList @(
  "-env:UserInstallation=$profileUrl", "-headless", "-invisible", "-norestore", "-nologo", "-nofirststartwizard",
  '"-accept=socket,host=127.0.0.1,port=2083;urp;StarOffice.ServiceManager"')
try {
  $env:PYTHONPATH = $office
  $env:URE_BOOTSTRAP = "vnd.sun.star.pathname:$office\fundamental.ini"
  & "$office\python.exe" "$PSScriptRoot\report_cover.py" (Resolve-Path $Xlsx).Path $Pdf
}
finally {
  Start-Sleep -Seconds 2
  Get-Process soffice.bin, soffice -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $soffice.StartTime } | Stop-Process -Force
}

$python = Get-Command python -ErrorAction SilentlyContinue
if ($python) {
  & $python.Source -c "import sys, pymupdf; d = pymupdf.open(sys.argv[1]); d[0].get_pixmap(dpi=110).save(sys.argv[1][:-4] + '.png')" $Pdf
}
"Cover: $Pdf"
