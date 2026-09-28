using System.Windows.Controls;
using System.Windows.Input;
using Poni.ViewModels;

namespace Poni.Views.Dialogs
{
    public partial class VmCredentialsDialog : UserControl
    {
        public VmCredentialsDialog()
        {
            InitializeComponent();
            // PasswordBox cannot be data-bound: hand its SecureString to the view model.
            PasswordBox.PasswordChanged += (_, __) =>
            {
                if (DataContext is VmCredentialsViewModel vm) vm.Password = PasswordBox.SecurePassword;
            };
            // Remembered user -> straight to the password.
            Loaded += (_, __) =>
            {
                var remembered = DataContext is VmCredentialsViewModel vm && vm.HasRememberedUser;
                Control target = remembered ? PasswordBox : UserBox;
                target.Focus();
                Keyboard.Focus(target);
            };
        }
    }
}
