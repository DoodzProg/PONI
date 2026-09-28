using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;

namespace Poni.Services
{
    /// <summary>Result object every embedded script returns: { Ok, Code, Detail }.</summary>
    public sealed class ScriptResult
    {
        public ScriptResult(bool ok, string code, string detail)
        {
            Ok = ok;
            Code = code;
            Detail = detail;
        }

        public bool Ok { get; }
        public string Code { get; }
        public string Detail { get; }

        public override string ToString() => $"{(Ok ? "OK" : "KO")} {Code} {Detail}".Trim();
    }

    /// <summary>
    /// Windows PowerShell 5.1 hosted inside PONI.exe (nothing launched, nothing to install).
    /// One runspace, opened in the background as soon as the user heads for a change (see
    /// UiOperations.WarmUpForChanges; ~80 MB, so not at startup), so the modules are warm by the
    /// time he confirms; scripts run one at a time (never two network changes at once).
    /// </summary>
    public static class PowerShellHost
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static Task<Runspace>? _runspace;

        /// <summary>Starts opening the runspace (and loading the network modules) in the background.</summary>
        public static void Prewarm()
        {
            if (_runspace == null) _runspace = Task.Run(OpenRunspace);
        }

        /// <summary>Runs an embedded script (Scripts/*.ps1) and returns its { Ok, Code, Detail } object.</summary>
        public static async Task<ScriptResult> RunAsync(string scriptName, IDictionary<string, object?> parameters)
        {
            Prewarm();
            string script;
            Runspace runspace;
            try
            {
                script = LoadScript(scriptName);
                runspace = await _runspace!.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ScriptResult(false, "Failed", ex.Message);
            }

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    using var ps = PowerShell.Create();
                    ps.Runspace = runspace;
                    ps.AddScript(script);
                    foreach (var pair in parameters) ps.AddParameter(pair.Key, pair.Value);

                    var output = ps.Invoke();
                    var last = output.LastOrDefault(o => o?.Properties["Ok"] != null);
                    if (last != null)
                    {
                        return new ScriptResult(
                            last.Properties["Ok"].Value is bool ok && ok,
                            last.Properties["Code"]?.Value?.ToString() ?? "Failed",
                            last.Properties["Detail"]?.Value?.ToString() ?? "");
                    }
                    var error = ps.Streams.Error.FirstOrDefault()?.ToString() ?? "No result from " + scriptName;
                    return new ScriptResult(false, "Failed", error);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ScriptResult(false, "Failed", ex.Message);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>Runs a small inline command (module preloading...). Errors are only logged.</summary>
        public static async Task RunTextAsync(string command)
        {
            Prewarm();
            try
            {
                var runspace = await _runspace!.ConfigureAwait(false);
                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await Task.Run(() =>
                    {
                        using var ps = PowerShell.Create();
                        ps.Runspace = runspace;
                        ps.AddScript(command);
                        ps.Invoke();
                    }).ConfigureAwait(false);
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("PowerShell command failed: " + command, ex);
            }
        }

        private static Runspace OpenRunspace()
        {
            // CreateDefault (full session), validated on real hardware. CreateDefault2 was measured in
            // with no memory gain.
            var iss = InitialSessionState.CreateDefault();
            // Our scripts are embedded text, but the Windows modules they use load format/type
            // files: make sure a restrictive user policy cannot block them (a machine GPO still wins).
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddScript("Import-Module NetTCPIP, DnsClient, NetConnection, NetAdapter -ErrorAction SilentlyContinue");
                ps.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warn("PowerShell prewarm: modules not preloaded", ex);
            }
            return runspace;
        }

        /// <summary>Text of an embedded script (also sent into VMs by Invoke-VmIpv4.ps1).</summary>
        public static string LoadScript(string name)
        {
            var resource = "Poni.Scripts." + name;
            using var stream = typeof(PowerShellHost).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Embedded script not found: " + resource);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
