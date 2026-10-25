Imports System
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports SocketJack

Module Program
    Function Main(args As String()) As Integer
        If args.Length = 2 AndAlso args(0) = "--capture-root" Then
            Dim root = Guard(args(1))
            Environment.SetEnvironmentVariable("HEIROWUPDATER_TOKEN", "fixture-inherited-browser")
            Dim snapshot = HeirowUpdaterBootstrap.CaptureSession(sessionCookie:="wshare_hash=fixture-bootstrap-native")
            Thread.Sleep(150)
            Dim queuedAt = DateTime.UtcNow.Ticks
            If Not HeirowUpdaterBootstrap.CheckForUpdates(snapshot, root) Then Return 2
            Dim deadline = DateTime.UtcNow.AddSeconds(15), receipt = Path.Combine(root, "child.json")
            While Not File.Exists(receipt) AndAlso DateTime.UtcNow < deadline
                Thread.Sleep(20)
            End While
            If Not File.Exists(receipt) Then Return 3
            Using result = JsonDocument.Parse(File.ReadAllText(receipt))
                Dim data = result.RootElement
                Return If(data.GetProperty("cookieCorrect").GetBoolean() AndAlso data.GetProperty("noBearer").GetBoolean() AndAlso data.GetProperty("capturedAt").GetInt64() < queuedAt AndAlso data.GetProperty("sourcePid").GetInt32() = Environment.ProcessId AndAlso data.GetProperty("sequence").GetInt64() > 0, 0, 4)
            End Using
        End If
        Dim configIndex = Array.IndexOf(args, "--config")
        If args.FirstOrDefault() <> "check-for-updates" OrElse configIndex < 0 OrElse configIndex + 1 >= args.Length Then Return 5
        Dim childRoot = Guard(Path.GetDirectoryName(Path.GetFullPath(args(configIndex + 1))))
        Dim temporary = Path.Combine(childRoot, "child.json.tmp")
        File.WriteAllText(temporary, JsonSerializer.Serialize(New With {
            .cookieCorrect = Environment.GetEnvironmentVariable("HEIROWUPDATER_COOKIE") = "wshare_hash=fixture-bootstrap-native",
            .noBearer = Environment.GetEnvironmentVariable("HEIROWUPDATER_TOKEN") Is Nothing,
            .capturedAt = Long.Parse(Environment.GetEnvironmentVariable("HEIROWUPDATER_SESSION_CAPTURED_AT")),
            .sourcePid = Integer.Parse(Environment.GetEnvironmentVariable("HEIROWUPDATER_SESSION_SOURCE_PID")),
            .sequence = Long.Parse(Environment.GetEnvironmentVariable("HEIROWUPDATER_SESSION_SEQUENCE"))}))
        File.Move(temporary, Path.Combine(childRoot, "child.json"))
        Return 0
    End Function
    Private Function Guard(pathValue As String) As String
        Dim root = Path.GetFullPath(pathValue)
        If Not root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) OrElse Not root.Contains("heirowInstaller-tests-", StringComparison.Ordinal) OrElse File.ReadAllText(Path.Combine(root, ".bootstrap-probe")) <> "heirowInstaller bootstrap acceptance" & vbLf Then Throw New InvalidDataException("Invalid fixture root.")
        Dim directory As New DirectoryInfo(root)
        While directory IsNot Nothing
            If (directory.Attributes And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("Invalid fixture root.")
            directory = directory.Parent
        End While
        Return root
    End Function
End Module
