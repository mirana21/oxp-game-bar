Option Explicit
Dim shell, fileSystem, root, launcher, result
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")
root = fileSystem.GetParentFolderName(WScript.ScriptFullName)
launcher = root & "\MaintenanceLauncher.exe"
' Keep the scheduled action alive while its private-pipe parent runs.
result = shell.Run("""" & launcher & """ startup", 0, True)
WScript.Quit result
