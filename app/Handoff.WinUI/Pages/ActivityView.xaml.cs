using System.Collections.ObjectModel;
using System.Text.Json;
using Handoff.WinUI.Models;
using Microsoft.UI.Xaml.Controls;

namespace Handoff.WinUI.Pages;

public sealed partial class ActivityView : UserControl
{
    private readonly ObservableCollection<ActivityRow> _activity = new ObservableCollection<ActivityRow>();

    public ActivityView()
    {
        this.InitializeComponent();
        this.ActivityList.ItemsSource = this._activity;
    }

    public void RenderSharedContexts(IReadOnlyList<SharedContext> contexts, bool supabaseReachable)
    {
        this._activity.Clear();

        foreach (SharedContext context in contexts
            .OrderByDescending(c => c.UpdatedAt ?? DateTime.MinValue))
        {
            this._activity.Add(ActivityRow.FromContext(context));
        }

        SharedContext? latest = contexts
            .OrderByDescending(c => c.UpdatedAt ?? DateTime.MinValue)
            .FirstOrDefault();

        this.LatestSyncText.Text = latest?.UpdatedAt is null
            ? "No rows"
            : latest.UpdatedAt.Value.ToLocalTime().ToString("HH:mm:ss");
        this.ContextRowsText.Text = contexts.Count.ToString();
        this.ActivityStatusText.Text = supabaseReachable ? "Current" : "Cached";
    }

    public void RenderLoading()
    {
        this.LatestSyncText.Text = "Loading";
        this.ContextRowsText.Text = this._activity.Count.ToString();
        this.ActivityStatusText.Text = "Refreshing";
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
            int tags = CountJsonArray(context.Tags);

            if (changedFiles > 0 && tags > 0)
            {
                return changedFiles + " files, " + tags + " tags";
            }
            if (changedFiles > 0)
            {
                return changedFiles + " files";
            }
            if (tags > 0)
            {
                return tags + " tags";
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
