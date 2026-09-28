using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Threading.Tasks;
using Poni.Core.Network;

namespace Poni.Services.Network
{
    /// <summary>Outcome of a network change, ready to be shown (Key = a Str.* resource).</summary>
    public sealed class NetworkOperationResult
    {
        public bool Success { get; set; }
        public string MessageKey { get; set; } = "";
        public object[] Args { get; set; } = Array.Empty<object>();
        /// <summary>The apply failed and the previous configuration was put back.</summary>
        public bool RolledBack { get; set; }
        /// <summary>The apply failed AND the rollback failed too: the adapter may be misconfigured.</summary>
        public bool RollbackFailed { get; set; }
        public string? TechnicalDetail { get; set; }
    }

    /// <summary>Changes on HOST adapters: apply a configuration (with rollback), DHCP, category.</summary>
    public static class HostNetworkService
    {
        public static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static Task<List<AdapterInfo>> ReadAdaptersAsync() => Task.Run(WmiNetworkReader.ReadAdapters);

        /// <summary>
        /// Applies <paramref name="config"/> on <paramref name="adapter"/>. The adapter object must be a
        /// fresh read: it is the snapshot restored if anything fails.
        /// </summary>
        public static async Task<NetworkOperationResult> ApplyAsync(AdapterInfo adapter, Ipv4Config config, bool setPrivate, bool rollbackOnFailure)
        {
            var snapshot = Ipv4Config.SnapshotOf(adapter);
            Log.Info($"Apply on '{adapter.Name}' (#{adapter.InterfaceIndex}): {Describe(config)}; previous: {Describe(snapshot)}");

            var result = await RunSetAsync(adapter.InterfaceIndex, config, setPrivate).ConfigureAwait(false);
            Log.Info($"Apply result: {result}");
            if (result.Ok) return Success("Str.Net.Applied", adapter.Name);

            var failure = Failure(result, adapter.Name);
            if (result.Code == "NoIpv4Interface" || !rollbackOnFailure) return failure;

            // Put the previous configuration back. Right after a failure (an address conflict in
            // particular) Windows may still be resetting the interface: retry a few times.
            var restore = snapshot.IsRestorable ? snapshot : Ipv4Config.Dhcp();
            ScriptResult back = new ScriptResult(false, "Failed", "");
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                back = await RunSetAsync(adapter.InterfaceIndex, restore, false).ConfigureAwait(false);
                Log.Info($"Rollback attempt {attempt}: {back}");
                if (back.Ok) break;
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            if (back.Ok) failure.RolledBack = true;
            else
            {
                failure.RollbackFailed = true;
                failure.TechnicalDetail += " | rollback: " + back;
            }
            return failure;
        }

        public static async Task<NetworkOperationResult> ResetToDhcpAsync(AdapterInfo adapter)
        {
            Log.Info($"DHCP on '{adapter.Name}' (#{adapter.InterfaceIndex})");
            var result = await RunSetAsync(adapter.InterfaceIndex, Ipv4Config.Dhcp(), false).ConfigureAwait(false);
            Log.Info($"DHCP result: {result}");
            return result.Ok ? Success("Str.Net.DhcpDone", adapter.Name) : Failure(result, adapter.Name);
        }

        public static async Task<NetworkOperationResult> SetCategoryAsync(AdapterInfo adapter, NetworkCategory category)
        {
            var name = category == NetworkCategory.Private ? "Private" : "Public";
            Log.Info($"Category {name} on '{adapter.Name}' (#{adapter.InterfaceIndex})");
            var result = await PowerShellHost.RunAsync("Set-NetworkCategory.ps1", new Dictionary<string, object?>
            {
                ["InterfaceIndex"] = adapter.InterfaceIndex,
                ["Category"] = name,
            }).ConfigureAwait(false);
            Log.Info($"Category result: {result}");
            if (result.Ok)
                return Success(category == NetworkCategory.Private ? "Str.Net.CategoryPrivateDone" : "Str.Net.CategoryPublicDone", adapter.Name);
            return result.Code switch
            {
                "Domain" => Fail("Str.Net.CategoryDomain", adapter.Name),
                "NoProfile" => Fail("Str.Net.CategoryNoNetwork", adapter.Name),
                "NotConfirmed" => Fail("Str.Net.CategoryBlocked", adapter.Name),
                _ => Failure(result, adapter.Name),
            };
        }

        private static Task<ScriptResult> RunSetAsync(int index, Ipv4Config config, bool setPrivate) =>
            PowerShellHost.RunAsync("Set-Ipv4Config.ps1", new Dictionary<string, object?>
            {
                ["InterfaceIndex"] = index,
                ["UseDhcp"] = config.UseDhcp,
                ["Addresses"] = config.Addresses.ToArray(),
                ["Gateway"] = config.Gateway ?? "",
                ["Dns"] = config.Dns.ToArray(),
                ["SetPrivate"] = setPrivate,
            });

        private static NetworkOperationResult Failure(ScriptResult r, string adapter) => r.Code switch
        {
            "NoIpv4Interface" => Fail("Str.Net.NoIpv4", adapter),
            "Duplicate" => Fail("Str.Net.Duplicate", r.Detail, adapter),
            "NotConfirmed" => Fail("Str.Net.NotConfirmed." + (string.IsNullOrEmpty(r.Detail) ? "IP" : r.Detail), adapter),
            _ => new NetworkOperationResult { MessageKey = "Str.Net.Failed", Args = new object[] { adapter, r.Detail }, TechnicalDetail = r.ToString() },
        };

        private static NetworkOperationResult Success(string key, params object[] args) =>
            new NetworkOperationResult { Success = true, MessageKey = key, Args = args };

        private static NetworkOperationResult Fail(string key, params object[] args) =>
            new NetworkOperationResult { Success = false, MessageKey = key, Args = args };

        private static string Describe(Ipv4Config c) => c.UseDhcp
            ? "DHCP"
            : $"{string.Join(",", c.Addresses)} gw={c.Gateway ?? "-"} dns={(c.Dns.Count == 0 ? "auto" : string.Join(",", c.Dns))}";
    }
}
