using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Gelatinarm.Shared.Ui
{
    public sealed partial class LoadingOverlay : UserControl
    {
        public static readonly DependencyProperty IsLoadingProperty =
            DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(LoadingOverlay),
                new PropertyMetadata(false, OnIsLoadingChanged));

        public static readonly DependencyProperty LoadingTextProperty =
            DependencyProperty.Register(nameof(LoadingText), typeof(string), typeof(LoadingOverlay),
                new PropertyMetadata(null));

        // What had focus when the overlay came up: it gets it back when the overlay goes
        private Control _focusBefore;

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
            if (!(d is LoadingOverlay overlay))
            {
                return;
            }

            // While loading, the overlay holds focus so the page underneath cannot be navigated.
            // Queued, not immediate: the overlay's Visibility binding updates after this callback,
            // and a collapsed button takes no focus. RunOnUIThreadAsync never throws, and this
            // control resolves no logger: a focus move has nothing to report.
            if (e.NewValue is true)
            {
                overlay._focusBefore = FocusManager.GetFocusedElement() as Control;
                _ = UiHelper.RunOnUIThreadAsync(() => overlay.FocusCapture.Focus(FocusState.Programmatic),
                    logger: null, dispatcher: overlay.Dispatcher);
            }
            else if (overlay._focusBefore is Control before)
            {
                // The capture button collapses with the overlay and takes the focus with it, which
                // would strand a controller user; the page that navigated away no longer holds it,
                // and a page that has already put focus on its first tile keeps that
                overlay._focusBefore = null;
                _ = UiHelper.RunOnUIThreadAsync(() =>
                {
                    var focused = FocusManager.GetFocusedElement();
                    if ((focused == null || focused == overlay.FocusCapture) &&
                        before.IsLoaded && before.Visibility == Visibility.Visible && before.IsEnabled)
                    {
                        before.Focus(FocusState.Programmatic);
                    }
                }, logger: null, dispatcher: overlay.Dispatcher);
            }
        }

        private void FocusCapture_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Allow back button (Escape and GamepadB) to work during loading
            if (e.Key == VirtualKey.Escape || e.Key == VirtualKey.GamepadB)
            {
                return;
            }

            e.Handled = true;

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
