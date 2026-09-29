using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

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
            SizeChanged += OnSizeChanged;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool narrow = e.NewSize.Width < 520;
            TermColumn.Width = narrow ? new GridLength(0) : new GridLength(180);
            Grid.SetColumn(TermText, narrow ? 1 : 0);
            Grid.SetRow(DefinitionText, narrow ? 1 : 0);
            DefinitionText.Margin = narrow ? new Thickness(0, 2, 0, 0) : new Thickness(12, 0, 0, 0);
        }

        /// <summary>Briefly highlights the row (green fade-out) after a deep-link scroll.</summary>
        public void Flash()
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x4D, 0x4a, 0xde, 0x80));
            RootBorder.Background = brush;

            var anim = new ColorAnimation
            {
                From = Color.FromArgb(0x4D, 0x4a, 0xde, 0x80),
                To = Color.FromArgb(0x00, 0x4a, 0xde, 0x80),
                Duration = new Duration(TimeSpan.FromMilliseconds(1500)),
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(anim, brush);
            Storyboard.SetTargetProperty(anim, "Color");

            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }
    }
}
