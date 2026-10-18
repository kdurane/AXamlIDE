using System;
using System.Collections.Generic;
using System.ComponentModel;
using AvaloniaEdit.CodeCompletion;
using AXamlIDE.Models;

namespace AXamlIDE.Views.Editor.SignatureHelp;

public sealed class SignatureOverloadProvider : IOverloadProvider
{
    private readonly IReadOnlyList<SignatureInsightItem> _items;
    private int _selectedIndex;

    public SignatureOverloadProvider(
        IReadOnlyList<SignatureInsightItem> items,
        int selectedIndex = 0)
    {
        _items = items;
        SelectedIndex = selectedIndex;
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var index = Math.Clamp(value, 0, Count - 1);
            if (_selectedIndex == index)
                return;

            _selectedIndex = index;
            OnPropertyChanged(nameof(SelectedIndex));
            OnPropertyChanged(nameof(CurrentHeader));
            OnPropertyChanged(nameof(CurrentContent));
            OnPropertyChanged(nameof(CurrentIndexText));
        }
    }

    public int Count => _items.Count;

    public string CurrentIndexText =>
        $"{SelectedIndex + 1} of {Count}";

    public object CurrentHeader =>
        _items[SelectedIndex].Header;

    public object CurrentContent =>
        _items[SelectedIndex].Content;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [System.Runtime.CompilerServices.CallerMemberName]
              string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this, new PropertyChangedEventArgs(propertyName));
    }
}
