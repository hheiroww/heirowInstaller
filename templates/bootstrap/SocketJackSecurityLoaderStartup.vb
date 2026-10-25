Imports System
Imports System.Threading
Imports System.Threading.Tasks

Namespace Global.SocketJack
    ' Call before protected application functionality with the installed private bootstrap's async entrypoint.
    Public NotInheritable Class SocketJackSecurityLoaderStartup
        Private Sub New()
        End Sub

        Public Shared Function InitializeAsync(initializePrivateBootstrap As Func(Of CancellationToken, Task), Optional cancellationToken As CancellationToken = Nothing) As Task
            ArgumentNullException.ThrowIfNull(initializePrivateBootstrap)
            cancellationToken.ThrowIfCancellationRequested()
            ' Authentication and protected payload handling stay in the private bootstrap; failures propagate.
            Return initializePrivateBootstrap(cancellationToken)
        End Function
    End Class
End Namespace
