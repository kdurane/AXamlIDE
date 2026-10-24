using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AXamlIDE.Models;

// one entry in a tool's ▾ menu (a checkable setting or a command)
public sealed class ToolMenuOption(string header, Func<bool> get, Action<bool> set) : ObservableObject
{
          public string Header { get; } = header;
          public bool IsChecked { get => get(); set { set(value); OnPropertyChanged(); } }
}
