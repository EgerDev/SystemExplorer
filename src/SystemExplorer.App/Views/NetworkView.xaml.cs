using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SystemExplorer.Core;
using SystemExplorer.Relationships;

namespace SystemExplorer.App.Views;

public sealed record NetAdapterCardItem(string Alias, string StatusText, Brush StatusBrush, string RatesText, string MetaText);

public sealed record NetEndpointItem(
    ProcessIdentity? OwnerIdentity,
    bool CanInspect,
    string Local,
    string Remote,
    string State,
    Brush StateBrush,
    string Owner);

public sealed partial class NetworkView : UserControl
{
    public event Action<ProcessIdentity>? InspectRequested;
    public event Action<string>? InventoryRequested;

    private NetworkCounterSection? latestNet;

    public NetworkView()
    {
        InitializeComponent();
    }

    public void AnimateEntrance() => Motion.AnimateStaggeredChildren(NetKpiGrid);

    public void UpdateCounters(NetworkCounterSection? net) => latestNet = net;

    public void Update(MonitorSnapshot snapshot, RelationshipSnapshot? relationships, string search)
    {
        var green = ResolveThemeBrush("StatusLiveBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
        var violet = ResolveThemeBrush("ChartVioletBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.MediumPurple);
        var blue = ResolveThemeBrush("InteractionAccent") ?? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        var muted = ResolveThemeBrush("TextFillColorSecondaryBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

        var n = latestNet;
        RxRateCard.Value = n?.TotalReceiveBytesPerSecond.Value is { } rx ? Display.Rate(rx) : "—";
        TxRateCard.Value = n?.TotalTransmitBytesPerSecond.Value is { } tx ? Display.Rate(tx) : "—";

        // Adapters list
        if (n is not null && n.Adapters.Count > 0)
        {
            NetStatusBadge.Text = n.Status.ToString();
            var relevant = n.Adapters
                .OrderByDescending(a => a.IncludedInTotal)
                .ThenByDescending(a => string.Equals(a.OperationalStatus, "Up", StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .ToArray();
            var cards = new NetAdapterCardItem[relevant.Length];
            for (int i = 0; i < relevant.Length; i++)
            {
                var a = relevant[i];
                var isUp = string.Equals(a.OperationalStatus, "Up", StringComparison.OrdinalIgnoreCase);
                var rxS = a.ReceiveBytesPerSecond.Value is { } r1 ? Display.Rate(r1) : "—";
                var txS = a.TransmitBytesPerSecond.Value is { } t1 ? Display.Rate(t1) : "—";
                var link = a.ReceiveLinkSpeedBitsPerSecond is { } bps and > 0 ? $"{bps / 1_000_000d:0.#} Mbps" : "Link —";
                cards[i] = new NetAdapterCardItem(
                    a.Alias,
                    a.OperationalStatus,
                    isUp ? green : muted,
                    $"RX {rxS} · TX {txS}",
                    $"{a.Kind} · {link} · {a.Description}");
            }
            AdaptersList.ItemsSource = cards;
            AdaptersEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            NetStatusBadge.Text = n?.Status.ToString() ?? "Warming up";
            AdaptersList.ItemsSource = Array.Empty<NetAdapterCardItem>();
            AdaptersEmpty.Text = n?.Detail ?? "Warming up network adapter counters…";
            AdaptersEmpty.Visibility = Visibility.Visible;
        }

        // Endpoints
        var conns = relationships?.Connections ?? Array.Empty<ConnectionSnapshot>();
        var tcpCount = conns.Count(c => string.Equals(c.Protocol, "TCP", StringComparison.OrdinalIgnoreCase));
        var udpCount = conns.Count - tcpCount;
        TcpCard.Value = tcpCount.ToString("N0");
        UdpCard.Value = udpCount.ToString("N0");

        var byId = snapshot.Processes.ToDictionary(p => p.Identity);
        var query = search.Trim();
        var endpointItems = conns
            .Select(c =>
            {
                var local = $"{c.LocalAddress}:{c.LocalPort}";
                var remote = c.RemoteAddress is null ? "—" : $"{c.RemoteAddress}:{c.RemotePort}";
                var state = c.State ?? "UDP";
                var stateBrush = string.Equals(state, "Established", StringComparison.OrdinalIgnoreCase) ? green
                    : string.Equals(state, "Listen", StringComparison.OrdinalIgnoreCase) ? blue : muted;
                ProcessIdentity? verified = c.OwnerIdentity is { } oid && byId.ContainsKey(oid) ? oid : null;
                var ownerName = verified is { } vid ? $"{byId[vid].Name} ({vid.Pid})" : $"PID {c.OwningPid}";
                return new NetEndpointItem(verified, verified.HasValue, local, remote, state, stateBrush, ownerName);
            })
            .Where(e => string.IsNullOrEmpty(query) ||
                        e.Local.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.Remote.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.State.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.Owner.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => string.Equals(e.State, "Established", StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Owner, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToArray();

        EndpointCountText.Text = $"{endpointItems.Length} of {conns.Count}";
        EndpointsList.ItemsSource = endpointItems;
        EndpointsEmpty.Visibility = endpointItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action }) InventoryRequested?.Invoke(action);
    }

    private void InspectClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProcessIdentity id }) InspectRequested?.Invoke(id);
    }

    private Brush? ResolveThemeBrush(string key)
    {
        var themeKey = ActualTheme == ElementTheme.Dark ? "Default" : "Light";
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var tObj) && tObj is ResourceDictionary td &&
                td.TryGetValue(key, out var val) && val is Brush b)
                return b;
        return null;
    }
}
