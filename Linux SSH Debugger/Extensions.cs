using Renci.SshNet;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LinuxSSHDebugger
{
    public static class Extensions
    {
        public static async Task<int> WaitForExitAsync(this Process process, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Process_Exited(object sender, EventArgs e)
            {
                tcs.TrySetResult(true);
            }

            process.EnableRaisingEvents = true;
            process.Exited += Process_Exited;

            try
            {
                if (process.HasExited)
                {
                    return process.ExitCode;
                }

                using (cancellationToken.Register(() => tcs.TrySetCanceled()))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                process.Exited -= Process_Exited;
            }

            return process.ExitCode;
        }

        public static async Task<string> ExecuteAsync(this SshCommand command)
        {
            var async = command.BeginExecute();
            var result = await Task.Factory.FromAsync(async, (async) => command.EndExecute(async));
            return result.TrimEnd(new char[] { '\r', '\n' });
        }

        public static Task UploadAsync(this ScpClient scpClient, DirectoryInfo directoryInfo, string path)
        {
            return Task.Run(() => {
                scpClient.Upload(directoryInfo, path);
            });
        }
    }
}
