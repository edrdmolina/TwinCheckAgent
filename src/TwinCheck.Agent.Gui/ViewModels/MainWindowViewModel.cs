namespace TwinCheck.Agent.Gui.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public string AgentUrl => "https://localhost:3625";
    public string Status => "Not connected";
    public string CertificateStatus => "Certificate not checked";
    public string ActiveProfile => "No active profile";
    public string NasStatus => "NAS path not checked";
    public string LastOperation => "No scan operations yet";
}
