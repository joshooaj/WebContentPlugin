Get-Date
& appwiz.cpl
ii C:\repos\WebContentPlugin
ii 'C:\Program Files\'
$msiPath = (Get-ChildItem -Path C:\repos\WebContentPlugin\output\Setup\*.msi).FullName
msiexec /i $msiPath /q /le c:\repos\WebContentPlugin\output\Setup\install.log