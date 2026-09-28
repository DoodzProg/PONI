using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Poni.Views.Dialogs
{
    public partial class ProfileEditorDialog : UserControl
    {
        public ProfileEditorDialog()
        {
            InitializeComponent();
            // Ready to type: the name when there is one, otherwise the IP address.
            Loaded += (_, __) =>
            {
                var target = NameBox.IsVisible ? NameBox : IpBox;
                target.Focus();
                Keyboard.Focus(target);
                target.SelectAll();
            };
        }
    }
}
