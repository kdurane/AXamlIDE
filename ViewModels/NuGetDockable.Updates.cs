using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AXamlIDE.Services.NuGet;
using CommunityToolkit.Mvvm.Input;

namespace AXamlIDE.ViewModels;

/// <summary>
/// The NuGet manager's Updates tab, as in Visual Studio: a check box per package, "Select all packages" above the
/// list (click to tick them all, click again to clear), "Update Selected (n)" and "Update All".
/// Ticks are kept by package id, so they survive the search filter and refreshes.
/// </summary>
public sealed partial class NuGetDockable
{
    private readonly HashSet<string> _checkedIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rows that can be ticked: the Updates tab's.</summary>
    private IEnumerable<NuGetPackageRow> CheckableRows => Tab == NuGetTab.Updates ? Packages : [];

    /// <summary>The "Select all packages" box: ticked when all are, empty when none are, partly ticked otherwise.</summary>
    public bool? AllChecked
    {
        get
        {
            var rows = CheckableRows.ToList();
            var ticked = rows.Count(row => row.IsChecked);
            return ticked == 0 ? false : ticked == rows.Count ? true : null;
        }
    }

    public int CheckedCount => CheckableRows.Count(row => row.IsChecked);

    public string UpdateCheckedText => CheckedCount > 0 ? $"Update Selected ({CheckedCount})" : "Update Selected";

    /// <summary>"Select all packages": tick every row, or clear them all when they're all ticked already.</summary>
    [RelayCommand]
    private void ToggleAllChecked()
    {
        var tick = AllChecked != true;
        foreach (var row in CheckableRows)
            row.IsChecked = tick;
        OnChecksChanged();   // also resets the box itself, which the click toggled on its own
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NuGetPackageRow.IsChecked) || sender is not NuGetPackageRow row)
            return;
        if (row.IsChecked)
            _checkedIds.Add(row.Id);
        else
            _checkedIds.Remove(row.Id);
        OnChecksChanged();
    }

    private void OnChecksChanged()
    {
        OnPropertyChanged(nameof(AllChecked));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(UpdateCheckedText));
        UpdateCheckedCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- updating

    /// <summary>"Update All": every package with a newer version.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private Task UpdateAllAsync() => UpdateAsync([.. _installed.Keys.Where(HasUpdate)]);

    private bool CanUpdateAll() => !IsBusy && UpdatesCount > 0;

    /// <summary>"Update Selected": the ticked packages.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateChecked))]
    private Task UpdateCheckedAsync() => UpdateAsync([.. CheckableRows.Where(row => row.IsChecked && HasUpdate(row.Id)).Select(row => row.Id)]);

    private bool CanUpdateChecked() => !IsBusy && CheckedCount > 0;

    /// <summary>Update the packages to their newest versions, one after another (stops at the first failure).</summary>
    private async Task UpdateAsync(IReadOnlyList<string> ids)
    {
        var updates = ids.Select(id => (Id: id, Version: _latest[id])).ToList();
        if (updates.Count == 0)
            return;

        var packages = $"{updates.Count} package{(updates.Count == 1 ? "" : "s")}";
        await ChangeAsync($"Update {packages}", async ct =>
        {
            foreach (var (id, version) in updates)
            {
                var result = await _nuget.InstallAsync(ProjectPath, id, version, ct);
                if (!result.Succeeded)
                    return result with { Output = $"{id} {version}:{Environment.NewLine}{result.Output}" };
                _checkedIds.Remove(id);
            }
            return new NuGetChangeResult(true, "");
        }, $"Updated {packages}.");
    }
}
