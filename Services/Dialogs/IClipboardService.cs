using System.Threading.Tasks;

namespace AXamlIDE.Services.Dialogs;

/// <summary>The system clipboard, for view models (Copy Value in the debugger views...).</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}
