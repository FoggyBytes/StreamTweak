using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StreamTweak.Views
{
    public sealed partial class PlaceholderPage : Page
    {
        public PlaceholderPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // e.Parameter is the tag string passed from MainWindow navigation
            if (e.Parameter is string title)
                PageTitle.Text = title;
        }
    }
}
