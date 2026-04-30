using System.Collections.ObjectModel;
using System.Text.Json;
using Handoff.WinUI.Models;
using Microsoft.UI.Xaml.Controls;

namespace Handoff.WinUI.Pages;

public sealed partial class ActivityView : UserControl
{
    private const string AllPeopleSentinel = "All people";

    private readonly ObservableCollection<ActivityRow> _activity = new ObservableCollection<ActivityRow>();
    private IReadOnlyList<SharedContext> _allContexts = Array.Empty<SharedContext>();
    private bool _suppressFilterReload;

    public ActivityView()
    {
        this.InitializeComponent();
        this.ActivityList.ItemsSource = this._activity;
    }

    public void RenderSharedContexts(IReadOnlyList<SharedContext> contexts, bool supabaseReachable)
    {
        this._allContexts = contexts;
        this.RebuildPersonFilter();
        this.ApplyFilters();

        SharedContext? latest = contexts
            .OrderByDescending(c => c.UpdatedAt ?? DateTime.MinValue)
            .FirstOrDefault();

        this.LatestSyncText.Text = latest?.UpdatedAt is null
            ? "No rows"
            : latest.UpdatedAt.Value.ToLocalTime().ToString("HH:mm:ss");
        _ = supabaseReachable;
    }

    public void RenderLoading()
    {
        this.LatestSyncText.Text = "Loading";
        this.ContextRowsText.Text = this._activity.Count.ToString();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._suppressFilterReload)
        {
            return;
        }
        this.ApplyFilters();
    }

    private void RebuildPersonFilter()
    {
        // Preserve current selection so re-rendering on each sync cycle does not
        // wipe what the user picked.
        string? previous = this.PersonFilter.SelectedItem as string;

        this._suppressFilterReload = true;
        this.PersonFilter.Items.Clear();
        this.PersonFilter.Items.Add(AllPeopleSentinel);

        IEnumerable<string> people = this._allContexts
            .Select(c => string.IsNullOrWhiteSpace(c.MemberName) ? "(unknown)" : c.MemberName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        foreach (string person in people)
        {
            this.PersonFilter.Items.Add(person);
        }

        this.PersonFilter.SelectedItem = previous != null && this.PersonFilter.Items.Contains(previous)
            ? previous
            : AllPeopleSentinel;
        this._suppressFilterReload = false;
    }

    private void ApplyFilters()
    {
        string? person = this.PersonFilter.SelectedItem as string;
        string range = (this.RangeFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        DateTime? cutoff = ResolveCutoff(range);

        IEnumerable<SharedContext> filtered = this._allContexts;
        if (!string.IsNullOrEmpty(person) && person != AllPeopleSentinel)
        {
            filtered = filtered.Where(c =>
                string.Equals(
                    string.IsNullOrWhiteSpace(c.MemberName) ? "(unknown)" : c.MemberName,
                    person,
                    StringComparison.OrdinalIgnoreCase));
        }
        if (cutoff is DateTime since)
        {
            filtered = filtered.Where(c =>
                c.UpdatedAt is DateTime u &&
                ToLocalTime(u) >= since);
        }

        List<SharedContext> visibleContexts = filtered
            .OrderByDescending(c => c.UpdatedAt ?? DateTime.MinValue)
            .ToList();

        this._activity.Clear();
        foreach (SharedContext context in visibleContexts)
        {
            this._activity.Add(ActivityRow.FromContext(context));
        }
        this.ContextRowsText.Text = visibleContexts.Count.ToString();
    }

    private static DateTime? ResolveCutoff(string range)
    {
        DateTime today = DateTime.Today;
        return range switch
        {
            "today" => today,
            "week" => today.AddDays(-(int)today.DayOfWeek),
            "month" => new DateTime(today.Year, today.Month, 1),
            _ => null,
        };
    }

    private static DateTime ToLocalTime(DateTime value)
    {
        return value.Kind == DateTimeKind.Local ? value : value.ToLocalTime();
    }

    private sealed class ActivityRow
    {
        public string UpdatedAt { get; private init; } = "";

        public string MemberName { get; private init; } = "";

        public string Branch { get; private init; } = "";

        public string Title { get; private init; } = "";

        public string Detail { get; private init; } = "";

        public string CommitSha { get; private init; } = "";

        public string Metadata { get; private init; } = "";

        public static ActivityRow FromContext(SharedContext context)
        {
            string title = FirstNonEmpty(context.Summary, context.CommitMessage, "Shared context updated");
            string detail = FirstNonEmpty(context.CommitMessage, context.Summary, "No summary");
            string commit = string.IsNullOrWhiteSpace(context.CommitSha) ? "(no sha)" : context.CommitSha;

            return new ActivityRow
            {
                UpdatedAt = context.UpdatedAt is null
                    ? "(no time)"
                    : context.UpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                MemberName = string.IsNullOrWhiteSpace(context.MemberName) ? "(unknown)" : context.MemberName,
                Branch = string.IsNullOrWhiteSpace(context.Branch) ? "(no branch)" : context.Branch,
                Title = title,
                Detail = detail,
                CommitSha = commit,
                Metadata = BuildMetadata(context),
            };
        }

        private static string BuildMetadata(SharedContext context)
        {
            int changedFiles = CountJsonArray(context.ChangedFiles);
            if (changedFiles > 0)
            {
                return changedFiles + " files";
            }
            return "No metadata";
        }

        private static int CountJsonArray(JsonElement? value)
        {
            if (value is null || value.Value.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }
            return value.Value.GetArrayLength();
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return string.Empty;
        }
    }
}
