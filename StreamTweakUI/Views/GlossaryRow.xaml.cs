using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StreamTweak.Views
{
    public sealed partial class GlossaryRow : UserControl
    {
        public static readonly DependencyProperty TermProperty =
            DependencyProperty.Register(nameof(Term), typeof(string), typeof(GlossaryRow),
                new PropertyMetadata(string.Empty, (d, e) => ((GlossaryRow)d).TermText.Text = (string)e.NewValue));

        public static readonly DependencyProperty DefinitionProperty =
            DependencyProperty.Register(nameof(Definition), typeof(string), typeof(GlossaryRow),
                new PropertyMetadata(string.Empty, (d, e) => ((GlossaryRow)d).DefinitionText.Text = (string)e.NewValue));

        // IsLast: set to true on the last row to remove the bottom margin
        public static readonly DependencyProperty IsLastProperty =
            DependencyProperty.Register(nameof(IsLast), typeof(bool), typeof(GlossaryRow),
                new PropertyMetadata(false));

        public string Term
        {
            get => (string)GetValue(TermProperty);
            set => SetValue(TermProperty, value);
        }

        public string Definition
        {
            get => (string)GetValue(DefinitionProperty);
            set => SetValue(DefinitionProperty, value);
        }

        public bool IsLast
        {
            get => (bool)GetValue(IsLastProperty);
            set => SetValue(IsLastProperty, value);
        }

        public GlossaryRow()
        {
            this.InitializeComponent();
        }
    }
}
