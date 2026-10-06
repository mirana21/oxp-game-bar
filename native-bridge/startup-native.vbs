Option Explicit
Dim shell, fileSystem, root, powerShell, command, result
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")
root = fileSystem.GetParentFolderName(WScript.ScriptFullName)
powerShell = shell.ExpandEnvironmentStrings("%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe")
command = """" & powerShell & """ -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & root & "\startup-native.ps1"""
' Keep the scheduled action alive while its private-pipe parent runs.
result = shell.Run(command, 0, True)
WScript.Quit result
