using System;
using System.Windows;

namespace TempMonitor;

public partial class WelcomeWindow : Window
{
    public event Action? SettingsRequested;

    public WelcomeWindow()
    {
        InitializeComponent();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
        Close();
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Close();
}
