using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TaskStatusCenter;
using Microsoft.VisualStudio.Threading;
using Renci.SshNet;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinuxSSHDebugger
{
    [Command(PackageIds.DebugCommand)]
    internal sealed class DebugCommand : BaseCommand<DebugCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            var taskStatusCenter = await VS.Services.GetTaskStatusCenterAsync();

            var options = default(TaskHandlerOptions);
            options.Title = "Linux SSH Debugger";
            options.DisplayTaskDetails = (t) => t.FireAndForget();
            options.ActionsAfterCompletion = CompletionActions.RetainAndNotifyOnFaulted;

            TaskProgressData data = default;
            data.CanBeCanceled = true;

            var handler = taskStatusCenter.PreRegister(options, data);
            handler.RegisterTask(DebugAsync(data, handler));
        }

        private async Task DebugAsync(TaskProgressData taskProgressData, ITaskHandler taskHandler)
        {
            var outputPane = LinuxSSHDebuggerPackage.PackageOutputPane;
            await Package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var buildManager = await VS.Services.GetSolutionBuildManagerAsync();
            var project = await VS.Solutions.GetActiveProjectAsync();
            if (ErrorHandler.Succeeded(buildManager.get_StartupProject(out Microsoft.VisualStudio.Shell.Interop.IVsHierarchy startupProject)))
            {
                project = (Project)await SolutionItem.FromHierarchyAsync(startupProject, (uint)VSConstants.VSITEMID.Root);
            }
            if (project == null)
            {
                await outputPane.WriteLineAsync("A startup project is not set or no project is currently active");
                throw new ArgumentNullException("project", "A startup project is not set or no project is currently active");
            }

            if (!project.IsCapabilityMatch(".NET"))
            {
                await outputPane.WriteLineAsync($"Project {project.Name} is not .NET");
                return;
            }

            var isUpToDate = await VS.Build.ProjectIsUpToDateAsync(project);
            if (!isUpToDate)
            {
                await outputPane.WriteLineAsync($"Project is not built:- {project.Name}");
                return;
            }

            if (taskHandler.UserCancellation.IsCancellationRequested)
            {
                return;
            }

            var general = await General.GetLiveInstanceAsync();
            var passPhrase = general.SshPrivateKeyPassword;
            PrivateKeyFile[] privateKeys;
            try
            {
                privateKeys = new PrivateKeyFile[]
                {
                string.IsNullOrWhiteSpace(passPhrase) ?
                    new PrivateKeyFile(general.SshPrivateKey) : new PrivateKeyFile(general.SshPrivateKey, passPhrase)
                };
            }
            catch (Exception exception)
            {
                await outputPane.WriteLineAsync($"SSH: Failed :- {exception.Message}\r\n\r\nTry using ssh-keygen -t ecdsa -m PEM to create one");
                return;
            }

            using var ssh = new SshClient(general.SshHost, general.SshPortNumber, general.SshUser, privateKeys);
            try
            {
                // Connect to the remote device
                ssh.Connect();

                var result = await ssh.CreateCommand("echo ping").ExecuteAsync();
                if (result != "ping")
                {
                    await outputPane.WriteLineAsync($"SSH: Connect failed:- {result}");
                    taskProgressData.ProgressText = "Step 'Connect' failed";
                    taskHandler.Progress.Report(taskProgressData);
                    return;
                }
                await outputPane.WriteLineAsync("SSH: Connect completed");
                taskProgressData.ProgressText = "Step 'Connect' completed";
                taskHandler.Progress.Report(taskProgressData);

                if (taskHandler.UserCancellation.IsCancellationRequested)
                {
                    return;
                }

                await outputPane.WriteLineAsync("JSON: Generating");
                var assemblyName = await project.GetAttributeAsync("AssemblyName");
                // Generate temporary launch.json
                var json = new JsonObject
                {
                    ["version"] = "0.2.0",
                    ["adapter"] = "ssh.exe",
                    ["adapterArgs"] = $"-i {general.SshPrivateKey} -p {general.SshPortNumber} {general.SshUser}@{general.SshHost} {general.RemoteVsdbgInstallationFolder}/vsdbg --interpreter=vscode",
                    ["configurations"] = new JsonArray
                            {
                                new JsonObject
                                {
                                   ["name"] = ".NET Remote Launch - Framework-dependent",
                                   ["type"] = "coreclr",
                                   ["request"] = "launch",
                                   ["project"] = "default",
                                   ["program"] = $"{general.RemoteNETInstallationFolder}/dotnet",
                                   ["args"] = new JsonArray
                                   {
                                       JsonValue.Create($"./{assemblyName}.dll"),
                                   },
                                   ["cwd"] = $"{general.RemoteDeploymentFolder}/{project.Name}",
                                   ["stopAtEntry"] = false,
                                   ["console"] = "internalConsole",
                                },
                            }
                };

                var path = Path.Combine(Path.GetTempPath(), "launch.json");
                try
                {
                    File.AppendAllText(path, json.ToJsonString(new JsonSerializerOptions() { WriteIndented = true }), Encoding.UTF8);
                    await outputPane.WriteLineAsync("JSON: Generated successfuly");
                    taskProgressData.ProgressText = "Step 'Generate launch.json' completed";
                    taskHandler.Progress.Report(taskProgressData);

                    if (taskHandler.UserCancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    // Start debugging
                    if (!await VS.Commands.ExecuteAsync("DebugAdapterHost.Launch", $"/LaunchJson:\"{path}\""))
                    {
                        await outputPane.WriteLineAsync("VSDBG: Failed to launch the debugger ");
                        taskProgressData.ProgressText = "Step 'Launch VSDBG' failed";
                        taskHandler.Progress.Report(taskProgressData);
                        return;
                    }
                    taskProgressData.ProgressText = "Step 'Launch VSDBG' completed";
                    taskHandler.Progress.Report(taskProgressData);
                }
                finally
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (Exception exception)
            {
                await outputPane.WriteLineAsync($"SSH: Failed :- {exception.Message}");
                throw;
            }
            finally
            {
                if (ssh.IsConnected)
                {
                    ssh.Disconnect();
                }
            }
        }
    }
}
