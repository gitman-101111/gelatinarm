using Gelatinarm.Helpers;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Gelatinarm.Controls
{
    public sealed partial class LoadingOverlay : UserControl
    {
        public static readonly DependencyProperty IsLoadingProperty =
            DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(LoadingOverlay),
                new PropertyMetadata(false, OnIsLoadingChanged));

        public static readonly DependencyProperty LoadingTextProperty =
            DependencyProperty.Register(nameof(LoadingText), typeof(string), typeof(LoadingOverlay),
                new PropertyMetadata(null));

        public LoadingOverlay()
        {
            InitializeComponent();
        }

        public bool IsLoading
        {
            get => (bool)GetValue(IsLoadingProperty);
            set => SetValue(IsLoadingProperty, value);
        }

        public string LoadingText
        {
            get => (string)GetValue(LoadingTextProperty);
            set => SetValue(LoadingTextProperty, value);
        }

        private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var overlay = d as LoadingOverlay;
            if (overlay != null && e.NewValue is bool isLoading)
            {
                if (isLoading)
                {
                    // When loading starts, capture focus to prevent navigation
                    AsyncHelper.FireAndForget(async () =>
                    {
                        await UIHelper.RunOnUIThreadAsync(() =>
                        {
                            overlay.FocusCapture.Focus(FocusState.Programmatic);
                        }, overlay.Dispatcher);
                    }, null, typeof(LoadingOverlay));
                }
            }
        }

        private void FocusCapture_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Allow back button (Escape and GamepadB) to work during loading
            if (e.Key == VirtualKey.Escape || e.Key == VirtualKey.GamepadB)
            {
                // Don't handle these keys - let them propagate for back navigation
                return;
            }

            // Prevent all other key navigation while loading
            e.Handled = true;

            // Keep focus on the loading overlay
            if (e.Key == VirtualKey.Tab ||
                e.Key == VirtualKey.Up ||
                e.Key == VirtualKey.Down ||
                e.Key == VirtualKey.Left ||
                e.Key == VirtualKey.Right ||
                e.Key == VirtualKey.GamepadDPadUp ||
                e.Key == VirtualKey.GamepadDPadDown ||
                e.Key == VirtualKey.GamepadDPadLeft ||
                e.Key == VirtualKey.GamepadDPadRight ||
                e.Key == VirtualKey.GamepadLeftThumbstickUp ||
                e.Key == VirtualKey.GamepadLeftThumbstickDown ||
                e.Key == VirtualKey.GamepadLeftThumbstickLeft ||
                e.Key == VirtualKey.GamepadLeftThumbstickRight)
            {
                FocusCapture.Focus(FocusState.Programmatic);
            }
        }
    }
}
