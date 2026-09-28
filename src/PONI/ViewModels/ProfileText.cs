using System;
using System.Globalization;
using Poni.Core;
using Poni.Services;

namespace Poni.ViewModels
{
    /// <summary>Localized display texts for profiles (built on the UI thread, rebuilt on language change).</summary>
    public static class ProfileText
    {
        public static string Initial(string name)
        {
            foreach (char ch in name)
                if (char.IsLetterOrDigit(ch)) return char.ToUpper(ch, CultureInfo.CurrentCulture).ToString();
            return "#";
        }

        /// <summary>"192.168.1.220 / 24  ·  Passerelle 192.168.1.1  ·  DNS 1.1.1.1"</summary>
        public static string Meta(NetworkProfile p)
        {
            var gateway = string.IsNullOrEmpty(p.Gateway)
                ? LocalizationService.Get("Str.Profile.NoGateway")
                : LocalizationService.Format("Str.Profile.Gateway", p.Gateway);
            var dns = p.Dns.Count == 0
                ? LocalizationService.Get("Str.Profile.AutoDns")
                : LocalizationService.Format("Str.Profile.Dns", string.Join(", ", p.Dns));
            return $"{p.IPAddress} / {p.PrefixLength}  ·  {gateway}  ·  {dns}";
        }

        public static string Target(LastTarget? t)
        {
            if (t == null) return LocalizationService.Get("Str.Profile.NeverApplied");
            return t.Kind == TargetKind.Host
                ? LocalizationService.Format("Str.Profile.TargetHost", t.Adapter)
                : string.IsNullOrEmpty(t.VMAdapter)
                    ? LocalizationService.Format("Str.Profile.TargetVm", t.VM)
                    : LocalizationService.Format("Str.Profile.TargetVmAdapter", t.VM, t.VMAdapter);
        }

        /// <summary>"Aujourd'hui, 14:32" / "Hier, 09:15" / "03/09/26, 17:07".</summary>
        public static string When(DateTime? date)
        {
            if (date == null) return "";
            var d = date.Value;
            var time = d.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (d.Date == DateTime.Today) return LocalizationService.Format("Str.Time.Today", time);
            if (d.Date == DateTime.Today.AddDays(-1)) return LocalizationService.Format("Str.Time.Yesterday", time);
            var day = d.ToString(LocalizationService.IsFrench ? "dd/MM/yy" : "MM/dd/yy", CultureInfo.InvariantCulture);
            return day + ", " + time;
        }
    }
}
