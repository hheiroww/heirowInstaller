Imports System
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO

Namespace Global.SocketJack
    ' Call StartAfterExit once during application startup. No updater dependency is required.
    Public NotInheritable Class HeirowUpdaterBootstrap
        Private Sub New()
        End Sub

        Public Shared Function StartAfterExit(Optional bearerToken As String = Nothing, Optional sessionCookie As String = Nothing, Optional appRoot As String = Nothing) As Boolean
            Return StartCompanion("after-exit", bearerToken, sessionCookie, appRoot)
        End Function

        Public Shared Function CheckForUpdates(Optional bearerToken As String = Nothing, Optional sessionCookie As String = Nothing, Optional appRoot As String = Nothing) As Boolean
            Return StartCompanion("check-for-updates", bearerToken, sessionCookie, appRoot)
        End Function

        Private Shared Function StartCompanion(command As String, bearerToken As String, sessionCookie As String, appRoot As String) As Boolean
            Try
                Dim host = Environment.ProcessPath
                Dim root = Path.GetFullPath(If(appRoot, If(host IsNot Nothing AndAlso Not Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase), Path.GetDirectoryName(host), AppContext.BaseDirectory)))
                Dim updater = Path.Combine(root, "heirowUpdater.exe")
                Dim configuration = Path.Combine(root, "heirowUpdater.json")
                If Not File.Exists(updater) OrElse Not File.Exists(configuration) Then Return False
                Using application = Process.GetCurrentProcess()
                    Dim start As New ProcessStartInfo(updater) With {.UseShellExecute = False, .CreateNoWindow = True, .WorkingDirectory = root}
                    start.ArgumentList.Add(command)
                    start.ArgumentList.Add("--config") : start.ArgumentList.Add(configuration)
                    start.ArgumentList.Add("--application-pid") : start.ArgumentList.Add(application.Id.ToString(CultureInfo.InvariantCulture))
                    start.ArgumentList.Add("--application-start") : start.ArgumentList.Add(application.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture))
                    If host IsNot Nothing AndAlso Not Path.GetFullPath(host).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then
                        start.ArgumentList.Add("--application-host") : start.ArgumentList.Add(Path.GetFullPath(host))
                    End If
                    If bearerToken IsNot Nothing Then start.Environment("HEIROWUPDATER_TOKEN") = bearerToken
                    If sessionCookie IsNot Nothing Then start.Environment("HEIROWUPDATER_COOKIE") = sessionCookie
                    Using companion = Process.Start(start)
                        Return companion IsNot Nothing
                    End Using
                End Using
            Catch
                Return False
            End Try
        End Function
    End Class
End Namespace
