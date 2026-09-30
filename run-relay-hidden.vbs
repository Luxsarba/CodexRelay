Option Explicit

Dim fileSystem, shell, scriptDirectory, command, exitCode
Set fileSystem = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
scriptDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
command = "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -File """ & scriptDirectory & "\run-relay.ps1"""
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode