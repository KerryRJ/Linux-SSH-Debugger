using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace LinuxSSHDebugger
{
    internal partial class OptionsProvider
    {
        // Register the options with this attribute on your package class:
        // [ProvideOptionPage(typeof(OptionsProvider.GeneralOptions), "LinuxSSHDebugger", "General", 0, 0, true, SupportsProfiles = true)]
        [ComVisible(true)]
        public class GeneralOptions : BaseOptionPage<General> { }
    }

    public class General : BaseOptionModel<General>
    {
        [Category("SSH")]
        [DisplayName("SSH Host/IP")]
        [Description("The SSH device's Host name/IP address")]
        [DefaultValue("localhost")]
        public string SshHost { get; set; } = "localhost";

        [Category("SSH")]
        [DisplayName("SSH TCP/IP Port Number")]
        [Description("The SSH device's TCP/IP port number")]
        [DefaultValue(22)]
        public int SshPortNumber { get; set; } = 22;

        [Category("SSH")]
        [DisplayName("SSH User")]
        [Description("The SSH user to authenticate on the remote device")]
        [DefaultValue("pi")]
        public string SshUser { get; set; } = "pi";

        [Category("SSH")]
        [DisplayName("SSH Private Key")]
        [Description("The SSH user private key file. Use ssh-keygen -t ecdsa -m PEM to create it.")]
        public string SshPrivateKey { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh\\id_ecdsa");

        [Category("SSH")]
        [DisplayName("SSH Private Key Password (optional)")]
        [Description("Private key password (only if it was set).")]
        [DefaultValue("")]
        public string SshPrivateKeyPassword { get; set; } = "";

        [Category(".NET")]
        [DisplayName("Installation Folder")]
        [Description("The .NET installation folder on the remote device i.e. ~/.dotnet")]
        [DefaultValue("~/.dotnet")]
        public string RemoteNETInstallationFolder { get; set; } = "~/.dotnet";

        [Category(".NET")]
        [DisplayName("Deploy")]
        [Description("The .NET deployment on the remote device.")]
        [TypeConverter(typeof(EnumConverter))]
        [DefaultValue(Deployments.Runtime)]
        public Deployments RemoteNETDeployment { get; set; } = Deployments.Runtime;

        [Category("Visual Studio Debugger")]
        [DisplayName("Installation Folder")]
        [Description("The Visual Studio Debugger installation folder on the remote device i.e. ~/.vsdbg")]
        [DefaultValue("~/.vsdbg")]
        public string RemoteVsdbgInstallationFolder { get; set; } = "~/.vsdbg";

        [Category("Code")]
        [DisplayName("Deployment Folder")]
        [Description("The deployment folder on the remote device i.e. ~/apps")]
        [DefaultValue("~/apps")]
        public string RemoteDeploymentFolder { get; set; } = "~/apps";

    }
}
