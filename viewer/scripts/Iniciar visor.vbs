' Lanzador del visor de Soporte LAN: arranca el bridge sin ventana de consola
' y abre el navegador. Pensado para ejecutarse con doble clic.
Option Explicit

Dim shell, fso, base, node, command, found

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

base = fso.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = base

If Not fso.FileExists(base & "\visor.cjs") Then
    MsgBox "No encuentro visor.cjs junto a este acceso." & vbCrLf & _
           "Descomprime la carpeta completa del visor.", 16, "Soporte LAN"
    WScript.Quit 1
End If

If fso.FileExists(base & "\node.exe") Then
    node = """" & base & "\node.exe"""
Else
    node = "node.exe"
    found = shell.Run("cmd /c where node >nul 2>nul", 0, True)
    If found <> 0 Then
        MsgBox "No encuentro Node.js." & vbCrLf & vbCrLf & _
               "Usa la carpeta completa del visor (incluye node.exe) " & _
               "o instala Node.js 20 o superior.", 16, "Soporte LAN"
        WScript.Quit 1
    End If
End If

command = node & " """ & base & "\visor.cjs"" --open"
shell.Run command, 0, False
