Imports System
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text.Json
Imports System.Threading

Namespace Global.SocketJack
    ' Capture before background startup; an empty snapshot clears the prior login.
    Public NotInheritable Class HeirowUpdaterBootstrap
        Private Shared sequence As Long
        Private Sub New()
        End Sub

        Public Shared Function CaptureSession(Optional bearerToken As String = Nothing, Optional sessionCookie As String = Nothing) As HeirowUpdaterSession
            Return New HeirowUpdaterSession(bearerToken, sessionCookie, Interlocked.Increment(sequence))
        End Function

        Public Shared Function StartAfterExit(Optional bearerToken As String = Nothing, Optional sessionCookie As String = Nothing, Optional appRoot As String = Nothing, Optional replaceCredentials As Boolean = False) As Boolean
            Return StartCompanion("after-exit", If(replaceCredentials OrElse bearerToken IsNot Nothing OrElse sessionCookie IsNot Nothing, CaptureSession(bearerToken, sessionCookie), Nothing), appRoot)
        End Function

        Public Shared Function CheckForUpdates(Optional bearerToken As String = Nothing, Optional sessionCookie As String = Nothing, Optional appRoot As String = Nothing, Optional replaceCredentials As Boolean = False) As Boolean
            Return StartCompanion("check-for-updates", If(replaceCredentials OrElse bearerToken IsNot Nothing OrElse sessionCookie IsNot Nothing, CaptureSession(bearerToken, sessionCookie), Nothing), appRoot)
        End Function

        Public Shared Function StartAfterExit(session As HeirowUpdaterSession, Optional appRoot As String = Nothing) As Boolean
            Return StartCompanion("after-exit", session, appRoot)
        End Function

        Public Shared Function CheckForUpdates(session As HeirowUpdaterSession, Optional appRoot As String = Nothing) As Boolean
            Return StartCompanion("check-for-updates", session, appRoot)
        End Function

        Private Shared Function StartCompanion(command As String, session As HeirowUpdaterSession, appRoot As String) As Boolean
            Try
                Dim host = Environment.ProcessPath
                Dim root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(If(appRoot, If(host IsNot Nothing AndAlso Not Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase), Path.GetDirectoryName(host), AppContext.BaseDirectory))))
                Dim configuration = Path.Combine(root, "heirowUpdater.json")
                If Not File.Exists(configuration) Then Return False
                Dim updater = Resolve(root, configuration)
                If Not File.Exists(updater) Then Return False
                Using application = Process.GetCurrentProcess()
                    Dim start As New ProcessStartInfo(updater) With {.UseShellExecute = False, .CreateNoWindow = True, .WorkingDirectory = root}
                    start.ArgumentList.Add(command)
                    start.ArgumentList.Add("--config") : start.ArgumentList.Add(configuration)
                    start.ArgumentList.Add("--application-pid") : start.ArgumentList.Add(application.Id.ToString(CultureInfo.InvariantCulture))
                    start.ArgumentList.Add("--application-start") : start.ArgumentList.Add(application.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture))
                    If host IsNot Nothing AndAlso Not Path.GetFullPath(host).StartsWith(root & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then
                        start.ArgumentList.Add("--application-host") : start.ArgumentList.Add(Path.GetFullPath(host))
                    End If
                    For Each name In {"HEIROWUPDATER_TOKEN", "HEIROWUPDATER_COOKIE", "HEIROWUPDATER_SESSION_SUPPLIED", "HEIROWUPDATER_SESSION_CAPTURED_AT", "HEIROWUPDATER_SESSION_ID", "HEIROWUPDATER_SESSION_SOURCE_PID", "HEIROWUPDATER_SESSION_SOURCE_START", "HEIROWUPDATER_SESSION_SEQUENCE"}
                        start.Environment.Remove(name)
                    Next
                    session?.Apply(start)
                    Using companion = Process.Start(start)
                        Return companion IsNot Nothing
                    End Using
                End Using
            Catch
                Return False ' Optional updating must never block the application from opening.
            End Try
        End Function

        Private Shared Function Resolve(root As String, configuration As String) As String
            NoLinks(configuration)
            If New FileInfo(configuration).Length > 65536 Then Throw New InvalidDataException("Updater profile is too large.")
            Using document = JsonDocument.Parse(File.ReadAllText(configuration))
                Dim profile = document.RootElement
                If Not Path.GetFullPath(profile.GetProperty("installDirectory").GetString()).Equals(root, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("Updater root mismatch.")
                Dim runtime As JsonElement
                If Not profile.TryGetProperty("sharedRuntime", runtime) OrElse runtime.ValueKind = JsonValueKind.Null Then Return Path.Combine(root, "heirowUpdater.exe")
                Dim version = runtime.GetProperty("version").GetString(), hash = runtime.GetProperty("sha256").GetString()
                Dim size = runtime.GetProperty("size").GetInt64()
                Dim parsedVersion As System.Version = Nothing
                If Not System.Version.TryParse(version, parsedVersion) OrElse version.Length > 64 OrElse version.Any(Function(c) Not Char.IsAsciiDigit(c) AndAlso c <> "."c) OrElse hash.Length <> 64 OrElse Not hash.All(Function(c) Uri.IsHexDigit(c)) OrElse size <= 0 OrElse size > 536870912 Then Throw New InvalidDataException("Invalid updater runtime binding.")
                Dim scope = profile.GetProperty("scope").GetString()
                If scope <> "user" AndAlso scope <> "machine" Then Throw New InvalidDataException("Invalid updater scope.")
                Dim isolatedValue As JsonElement
                Dim isolated = profile.TryGetProperty("isolated", isolatedValue) AndAlso isolatedValue.GetBoolean()
                Dim parent = If(isolated, Path.GetDirectoryName(Path.GetFullPath(profile.GetProperty("stateRoot").GetString())), If(scope = "machine", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "heirowInstaller", "state"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowInstaller", "state")))
                Dim executable = Path.Combine(parent, ".updater-runtime", version & "-" & hash.ToLowerInvariant(), "heirowUpdater.exe")
                NoLinks(executable)
                Using input = File.OpenRead(executable)
                    If input.Length <> size OrElse Not Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase) OrElse ReadFileVersion(executable) <> version Then Throw New CryptographicException("Updater runtime integrity failed.")
                End Using
                Return executable
            End Using
        End Function

        Private Shared Function ReadFileVersion(pathValue As String) As String
            ' Version metadata uses a native API that requires an extended path for long Windows filenames.
            Dim absolute = Path.GetFullPath(pathValue)
            If OperatingSystem.IsWindows() AndAlso Not absolute.StartsWith("\\?\", StringComparison.Ordinal) Then
                absolute = If(absolute.StartsWith("\\", StringComparison.Ordinal), "\\?\UNC\" & absolute.Substring(2), "\\?\" & absolute)
            End If
            Return FileVersionInfo.GetVersionInfo(absolute).FileVersion
        End Function

        Private Shared Sub NoLinks(pathValue As String)
            Dim current = Path.GetFullPath(pathValue)
            While current IsNot Nothing
                If (File.Exists(current) OrElse Directory.Exists(current)) AndAlso (File.GetAttributes(current) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("Updater paths cannot follow links.")
                current = Path.GetDirectoryName(current)
            End While
        End Sub
    End Class

    Public NotInheritable Class HeirowUpdaterSession
        Private ReadOnly bearer, cookie As String
        Private ReadOnly captured As Long = DateTime.UtcNow.Ticks
        Private ReadOnly sourceStart, sequence As Long
        Private ReadOnly sourcePid As Integer = Environment.ProcessId
        Private ReadOnly id As String = Guid.NewGuid().ToString("N")
        Friend Sub New(bearer As String, cookie As String, sequence As Long)
            For Each value In {bearer, cookie}
                If value IsNot Nothing AndAlso (value.Length > 6144 OrElse value.Any(Function(c) AscW(c) < 32 OrElse AscW(c) = 127)) Then Throw New ArgumentException("Invalid session header data.")
            Next
            Me.bearer = bearer : Me.cookie = cookie : Me.sequence = sequence
            Using application = Process.GetCurrentProcess()
                sourceStart = application.StartTime.ToUniversalTime().Ticks
            End Using
        End Sub
        Friend Sub Apply(start As ProcessStartInfo)
            start.Environment("HEIROWUPDATER_SESSION_SUPPLIED") = "1"
            start.Environment("HEIROWUPDATER_SESSION_CAPTURED_AT") = captured.ToString(CultureInfo.InvariantCulture)
            start.Environment("HEIROWUPDATER_SESSION_ID") = id
            start.Environment("HEIROWUPDATER_SESSION_SOURCE_PID") = sourcePid.ToString(CultureInfo.InvariantCulture)
            start.Environment("HEIROWUPDATER_SESSION_SOURCE_START") = sourceStart.ToString(CultureInfo.InvariantCulture)
            start.Environment("HEIROWUPDATER_SESSION_SEQUENCE") = sequence.ToString(CultureInfo.InvariantCulture)
            If bearer IsNot Nothing Then start.Environment("HEIROWUPDATER_TOKEN") = bearer
            If cookie IsNot Nothing Then start.Environment("HEIROWUPDATER_COOKIE") = cookie
        End Sub
        Public Overrides Function ToString() As String
            Return "Application session snapshot (redacted)"
        End Function
    End Class
End Namespace
