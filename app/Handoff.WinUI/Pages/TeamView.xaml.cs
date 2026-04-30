using System.Collections.ObjectModel;
using Handoff.WinUI.Models;
using Microsoft.UI.Xaml.Controls;

namespace Handoff.WinUI.Pages;

public sealed partial class TeamView : UserControl
{
    private readonly ObservableCollection<TeamRosterRow> _team = new ObservableCollection<TeamRosterRow>();
    private bool _suppressToggleEvents;

    public event EventHandler<TeamSubscriptionChangedEventArgs>? SubscriptionChanged;

    public TeamView()
    {
        this.InitializeComponent();
        this.TeamList.ItemsSource = this._team;
    }

    public void RenderTeam(HandoffConfig config, IReadOnlyList<TeamMember> members)
    {
        Dictionary<string, TeamMember> memberByName = members
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        Dictionary<string, TeamMemberEntry> configByName = config.TeamMembers
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        SortedSet<string> names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in memberByName.Keys)
        {
            if (!string.Equals(name, config.Self, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }
        foreach (string name in configByName.Keys)
        {
            if (!string.Equals(name, config.Self, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        try
        {
            this._suppressToggleEvents = true;
            this._team.Clear();
            foreach (string name in names)
            {
                memberByName.TryGetValue(name, out TeamMember? member);
                configByName.TryGetValue(name, out TeamMemberEntry? entry);

                this._team.Add(new TeamRosterRow
                {
                    Name = name,
                    Email = string.IsNullOrWhiteSpace(member?.Email) ? "(no email)" : member.Email!,
                    Subscribe = entry?.Subscribe ?? true,
                });
            }
        }
        finally
        {
            this._suppressToggleEvents = false;
        }
    }

    private void OnSubscriptionToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (this._suppressToggleEvents || sender is not ToggleSwitch toggle)
        {
            return;
        }

        string? name = toggle.Tag as string;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        this.SubscriptionChanged?.Invoke(
            this,
            new TeamSubscriptionChangedEventArgs(name, toggle.IsOn));
    }
}

public sealed class TeamRosterRow
{
    public string Name { get; init; } = "";

    public string Email { get; init; } = "";

    public bool Subscribe { get; set; }
}

public sealed class TeamSubscriptionChangedEventArgs : EventArgs
{
    public TeamSubscriptionChangedEventArgs(string name, bool subscribe)
    {
        this.Name = name;
        this.Subscribe = subscribe;
    }

    public string Name { get; }

    public bool Subscribe { get; }
}
