using System.Security;
using Poni.Infrastructure;
using Poni.Services;

namespace Poni.ViewModels
{
    /// <summary>
    /// Credentials of an account INSIDE the VM, for PowerShell Direct. Plain text field for the
    /// user (v1 lesson: no drop-down of past attempts), pre-filled with the last name that worked.
    /// The password never leaves memory (SecureString, never saved, never logged).
    /// </summary>
    public sealed class VmCredentialsViewModel : DialogViewModel
    {
        public sealed class Result
        {
            public Result(string user, SecureString password)
            {
                User = user;
                Password = password;
            }

            public string User { get; }
            public SecureString Password { get; }
        }

        private string _user;

        public VmCredentialsViewModel(string vmName, string? lastUser)
        {
            VmName = vmName;
            _user = lastUser ?? "";
            HasRememberedUser = !string.IsNullOrEmpty(lastUser);
            ConnectCommand = new RelayCommand(Connect, () => !string.IsNullOrWhiteSpace(_user));
        }

        public string VmName { get; }
        public bool HasRememberedUser { get; }
        public string Title => LocalizationService.Format("Str.Vm.CredTitle", VmName);

        public string User
        {
            get => _user;
            set => SetProperty(ref _user, value);
        }

        /// <summary>Set by the view (PasswordBox cannot be bound).</summary>
        public SecureString Password { get; set; } = new SecureString();

        public RelayCommand ConnectCommand { get; }

        public override double DialogWidth => 440;

        private void Connect()
        {
            if (string.IsNullOrWhiteSpace(_user)) return;
            Password.MakeReadOnly();
            Close(new Result(_user.Trim(), Password));
        }
    }
}
