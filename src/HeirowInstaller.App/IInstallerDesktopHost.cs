namespace HeirowInstaller.App;

// The installation controller is shared by the Project-Z desktop and the
// Windows CPU-rendered compatibility wizard. Neither host owns install policy.
internal interface IInstallerDesktopHost
{
    string Title { set; }
    void Exit();
    void Enqueue(Action action);
    Task OnUiAsync(Action action);
}
