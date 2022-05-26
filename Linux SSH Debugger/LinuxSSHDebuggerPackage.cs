global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;
using System.Runtime.InteropServices;
using System.Threading;

namespace LinuxSSHDebugger
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.LinuxSSHDebuggerString)]
    [ProvideOptionPage(typeof(OptionsProvider.GeneralOptions), "Linux SSH Debugger", "General", 0, 0, true, SupportsProfiles = true)]
    public sealed class LinuxSSHDebuggerPackage : ToolkitPackage
    {
        public static OutputWindowPane PackageOutputPane { get; private set; }
        
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();
            PackageOutputPane = await VS.Windows.CreateOutputWindowPaneAsync("Linux SSH Debugger");
        }
    }
}