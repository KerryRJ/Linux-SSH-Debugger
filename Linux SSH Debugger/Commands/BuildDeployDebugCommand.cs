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
    [Command(PackageIds.BuildPublishDebugCommand)]
    internal sealed class BuildDeployDebugCommand : BaseCommand<BuildDeployDebugCommand>
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
            handler.RegisterTask(BuildPublishDebugAsync(data, handler));
        }

        private async Task BuildPublishDebugAsync(TaskProgressData taskProgressData, ITaskHandler taskHandler)
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
            if (isUpToDate)
            {
                await outputPane.WriteLineAsync($"Project, {project.Name}, is up to date, skipping build");
                taskProgressData.ProgressText = "Step 'Build' is up to date and will be skipped";
                taskHandler.Progress.Report(taskProgressData);
            }
            else
            {
                await outputPane.WriteLineAsync($"Building project:- {project.Name}");
                var result = await project.BuildAsync(BuildAction.Build);
                if (!result)
                {
                    await outputPane.WriteLineAsync("Build failed, check the ErrorList window");
                    await VS.Windows.FindOrShowToolWindowAsync(new Guid(WindowGuids.ErrorList));
                    taskProgressData.ProgressText = "Step 'Build' failed";
                    taskHandler.Progress.Report(taskProgressData);
                    return;
                }
                await outputPane.WriteLineAsync("Step 'Build' succeeded");
                taskProgressData.ProgressText = "Step 'Build' completed";
                taskHandler.Progress.Report(taskProgressData);
            }

            if (taskHandler.UserCancellation.IsCancellationRequested)
            {
                return;
            }

            //await outputPane.ActivateAsync();
            var configuration = await project.GetAttributeAsync("Configuration");
            var targetFramework = await project.GetAttributeAsync("TargetFramework");
            var publishFolder = $"{Path.GetDirectoryName(project.FullPath)}\\bin\\{configuration}\\{targetFramework}\\publish";
            await outputPane.WriteLineAsync($"Publishing project:- {project.Name}");
            using (var process = new Process())
            {
                process.StartInfo =
                    new("dotnet", $"publish {project.FullPath} -c {configuration} -o {publishFolder}")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    };
                process.Start();
                var exitCode = await process.WaitForExitAsync(taskHandler.UserCancellation).ConfigureAwait(true);
                if (exitCode != 0)
                {
                    await outputPane.WriteLineAsync($"Step 'Publish' failed:- exitCode = {exitCode}");
                    taskProgressData.ProgressText = "Step 'Publish' failed";
                    taskHandler.Progress.Report(taskProgressData);
                    return;
                }
                await outputPane.WriteLineAsync("Step 'Publish' completed");
                taskProgressData.ProgressText = "Step 'Publish' completed";
                taskHandler.Progress.Report(taskProgressData);
            }

            if (taskHandler.UserCancellation.IsCancellationRequested)
            {
                return;
            }

            // TODO Check that OpenSSH is installed preferrably from MSI and not Windows Features

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

                // Install/Upgrade dotnet
                await outputPane.WriteLineAsync("NET: Verify installation");
                var deployment = "--runtime ";

                if (project.IsCapabilityMatch("DotNetCoreWeb"))
                {
                    deployment += "aspnetcore";
                }
                else
                {
                    deployment += "dotnet";
                }
                result = await ssh.CreateCommand($"curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel Current {deployment} --install-dir {general.RemoteNETInstallationFolder}").ExecuteAsync();
                if (!(result.EndsWith("dotnet-install: Installation finished successfully.") || result.EndsWith("is already installed.")))
                {
                    await outputPane.WriteLineAsync($"NET: Install failed\r\n{result}\r\n");
                    taskProgressData.ProgressText = "Step '.NET Install' failed";
                    taskHandler.Progress.Report(taskProgressData);
                    return;
                }
                // Display the installed .NET version
                result = await ssh.CreateCommand($"export DOTNET_ROOT={general.RemoteNETInstallationFolder}; export PATH={general.RemoteNETInstallationFolder}; dotnet --info").ExecuteAsync();
                await outputPane.WriteLineAsync($"NET: Install info \r\n{result}\r\n");
                taskProgressData.ProgressText = "Step '.NET Install' completed";
                taskHandler.Progress.Report(taskProgressData);

                if (taskHandler.UserCancellation.IsCancellationRequested)
                {
                    return;
                }

                // Install vsdbg
                await outputPane.WriteLineAsync("VSDBG: Verify installation");
                result = await ssh.CreateCommand($"curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l {general.RemoteVsdbgInstallationFolder}").ExecuteAsync();
                if (!(result.Contains("Info: Successfully installed vsdbg") || result.EndsWith("Info: Skipping downloads")))
                {
                    await outputPane.WriteLineAsync($"VSDBG: Installation failed\r\n{result} \r\n\n");
                    taskProgressData.ProgressText = "Step 'VSDBG Install' failed";
                    taskHandler.Progress.Report(taskProgressData);
                    return;
                }
                await outputPane.WriteLineAsync("VSDBG: Installation succeeded");
                taskProgressData.ProgressText = "Step 'VSDBG Install' completed";
                taskHandler.Progress.Report(taskProgressData);

                if (taskHandler.UserCancellation.IsCancellationRequested)
                {
                    return;
                }

                // Create project folder if it does not exist
                await outputPane.WriteLineAsync("SSH: Creating folders");
                var folder = $"{general.RemoteDeploymentFolder}/{project.Name}";
                result = await ssh.CreateCommand($"if [ ! -d {folder} ]; then mkdir -p {folder}; else rm -r {folder}/*; fi").ExecuteAsync();
                if (!string.IsNullOrEmpty(result))
                {
                    await outputPane.WriteLineAsync($"SSH: Failure:- {result}");
                    taskProgressData.ProgressText = "Step 'Manage Folders' failed";
                    taskHandler.Progress.Report(taskProgressData);
                    return;
                }
                await outputPane.WriteLineAsync($"SSH: Folder {folder} created or cleaned");
                taskProgressData.ProgressText = "Step 'Manage Folders' completed";
                taskHandler.Progress.Report(taskProgressData);

                if (taskHandler.UserCancellation.IsCancellationRequested)
                {
                    return;
                }

                await outputPane.WriteLineAsync("SCP: Transferring files");
                var directoryInfo = new DirectoryInfo($"{publishFolder}");
                using (var scp = new ScpClient(general.SshHost, general.SshPortNumber, general.SshUser, privateKeys) { KeepAliveInterval = TimeSpan.FromSeconds(15), OperationTimeout = TimeSpan.FromSeconds(15) })
                {
                    try
                    {
                        scp.Connect();
                        await scp.UploadAsync(directoryInfo, $"{folder.Replace("~", $"/home/{general.SshUser}")}");
                    }
                    catch (Exception exception)
                    {
                        await outputPane.WriteLineAsync($"SCP: File transfer failed\r\n{exception.Message}\r\n");
                        taskProgressData.ProgressText = "Step 'File Transfer' failed";
                        taskHandler.Progress.Report(taskProgressData);
                        return;
                    }
                    finally
                    {
                        if (scp.IsConnected)
                        {
                            scp.Disconnect();
                        }
                    }
                }
                await outputPane.WriteLineAsync("SCP: File transfer succeeded");
                taskProgressData.ProgressText = "Step 'File Transfer' completed";
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
