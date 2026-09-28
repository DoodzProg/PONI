using System.Collections.Generic;
using System.Linq;
using Poni.Core;
using Poni.Core.Network;
using Poni.Infrastructure;
using Poni.Services;

namespace Poni.ViewModels
{
    public enum EditorMode { New, Edit, Duplicate, ConfigureAdapter }

    /// <summary>
    /// Create / edit / duplicate a profile, or type a configuration straight onto an adapter
    /// (ConfigureAdapter: no name). Result: the validated NetworkProfile, or null.
    /// Validation = Core.ProfileValidator (the same rules as import and migration).
    /// </summary>
    public sealed class ProfileEditorViewModel : DialogViewModel
    {
        private readonly List<string> _existingNames;
        private readonly string? _originalName;
        private string _name = "";
        private string _ip = "";
        private string _mask = "255.255.255.0";
        private string _gateway = "";
        private string _dns = "";
        private bool _showErrors;
        private AdapterInfo? _selectedSource;

        public ProfileEditorViewModel(EditorMode mode, NetworkProfile? source, IEnumerable<string> existingNames,
                                      IEnumerable<AdapterInfo> adapters, string? adapterName = null)
        {
            Mode = mode;
            _existingNames = existingNames.ToList();
            _originalName = mode == EditorMode.Edit ? source?.Name : null;
            AdapterName = adapterName;
            // "Fill from an adapter": only adapters with a real address are worth copying.
            SourceAdapters = adapters.Where(a => a.HasRealAddress).ToList();

            if (source != null)
            {
                _name = mode == EditorMode.Duplicate
                    ? LocalizationService.Format("Str.Editor.CopyName", source.Name)
                    : source.Name;
                _ip = source.IPAddress;
                _mask = source.SubnetMask;
                _gateway = source.Gateway ?? "";
                _dns = string.Join(", ", source.Dns);
            }

            SaveCommand = new RelayCommand(Save);
            FillFromCommand = new RelayCommand(p => SelectedSource = p as AdapterInfo);
            Validate();
        }

        public EditorMode Mode { get; }
        public string? AdapterName { get; }
        public bool ShowName => Mode != EditorMode.ConfigureAdapter;
        public bool ShowRiskWarning { get; set; }
        public IReadOnlyList<AdapterInfo> SourceAdapters { get; }
        public bool HasSourceAdapters => SourceAdapters.Count > 0 && Mode != EditorMode.ConfigureAdapter;

        public string Title => Mode switch
        {
            EditorMode.Edit => LocalizationService.Get("Str.Editor.EditTitle"),
            EditorMode.Duplicate => LocalizationService.Get("Str.Editor.DuplicateTitle"),
            EditorMode.ConfigureAdapter => LocalizationService.Format("Str.Editor.AdapterTitle", AdapterName),
            _ => LocalizationService.Get("Str.Editor.NewTitle"),
        };

        public string SaveText => LocalizationService.Get(Mode == EditorMode.ConfigureAdapter ? "Str.Common.Apply" : "Str.Common.Save");

        public string Name { get => _name; set { if (SetProperty(ref _name, value)) Validate(); } }
        public string IPAddress { get => _ip; set { if (SetProperty(ref _ip, value)) Validate(); } }
        public string Mask { get => _mask; set { if (SetProperty(ref _mask, value)) Validate(); } }
        public string Gateway { get => _gateway; set { if (SetProperty(ref _gateway, value)) Validate(); } }
        public string Dns { get => _dns; set { if (SetProperty(ref _dns, value)) Validate(); } }

        /// <summary>Copies the live configuration of the chosen adapter into the fields.</summary>
        public AdapterInfo? SelectedSource
        {
            get => _selectedSource;
            set
            {
                if (!SetProperty(ref _selectedSource, value) || value == null) return;
                var address = value.PrimaryAddress;
                if (address != null)
                {
                    IPAddress = address.Address;
                    Mask = Ipv4.PrefixToMaskString(address.PrefixLength);
                }
                Gateway = value.PrimaryGateway ?? "";
                Dns = string.Join(", ", value.DnsServers);
            }
        }

        public string? NameMessage { get; private set; }
        public string? IpMessage { get; private set; }
        public string? MaskMessage { get; private set; }
        public string? GatewayMessage { get; private set; }
        public string? DnsMessage { get; private set; }
        public bool GatewayIsWarning { get; private set; }
        public bool IpIsWarning { get; private set; }

        public RelayCommand SaveCommand { get; }
        public RelayCommand FillFromCommand { get; }

        public override double DialogWidth => 500;

        private ProfileValidationResult Check()
        {
            var input = new ProfileInput
            {
                Name = Mode == EditorMode.ConfigureAdapter ? (AdapterName ?? "adapter") : _name,
                IPAddress = _ip,
                Mask = _mask,
                Gateway = _gateway,
                Dns = _dns,
            };
            return ProfileValidator.Validate(input, Mode == EditorMode.ConfigureAdapter ? null : _existingNames, _originalName);
        }

        /// <summary>Messages appear under each field. Errors show once the user tried to save,
        /// or as soon as a field is not empty; warnings always show.</summary>
        private void Validate()
        {
            var result = Check();
            string? Message(ProfileField field, string text, out bool warning)
            {
                var issue = result.Issues.FirstOrDefault(i => i.Field == field && !i.IsWarning)
                            ?? result.Issues.FirstOrDefault(i => i.Field == field);
                warning = issue?.IsWarning ?? false;
                if (issue == null) return null;
                if (!issue.IsWarning && !_showErrors && string.IsNullOrWhiteSpace(text)) return null;
                return LocalizationService.Format(issue.Key, issue.Args);
            }

            NameMessage = Message(ProfileField.Name, _name, out _);
            IpMessage = Message(ProfileField.IPAddress, _ip, out var ipWarn);
            IpIsWarning = ipWarn;
            MaskMessage = Message(ProfileField.Mask, _mask, out _);
            GatewayMessage = Message(ProfileField.Gateway, _gateway, out var gwWarn);
            GatewayIsWarning = gwWarn;
            DnsMessage = Message(ProfileField.Dns, _dns, out _);

            foreach (var p in new[] { nameof(NameMessage), nameof(IpMessage), nameof(IpIsWarning), nameof(MaskMessage),
                                      nameof(GatewayMessage), nameof(GatewayIsWarning), nameof(DnsMessage) })
                OnPropertyChanged(p);
        }

        private void Save()
        {
            _showErrors = true;
            Validate();
            var result = Check();
            if (!result.IsValid || result.Profile == null) return;
            Close(result.Profile);
        }
    }
}
