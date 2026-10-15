using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaEdit.CodeCompletion;
using AXaxmlIDE.Lsp;
using AXaxmlIDE.Models;
using AXaxmlIDE.Services;

namespace AXaxmlIDE.Views;

public partial class EditorPaneView : UserControl
{
    private OpenDocument? _active;
    private readonly CustomMargin? _customMargin;
    
    public EditorPaneView()
    {
        InitializeComponent();
        
    }
}