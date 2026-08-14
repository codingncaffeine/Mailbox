using Mailbox.Core.Settings;

namespace Mailbox.Store;

/// <summary>
/// Account order and the default, kept in the settings file.
/// </summary>
/// <remarks>
/// Addresses rather than ids: an id belongs to one store file, and the whole point of a file
/// per account is that it can be moved, restored or copied on its own. An address survives that;
/// a row id does not.
/// </remarks>
public sealed class SettingsAccountOrder(SettingsStore settings) : IAccountOrder
{
    private const string OrderKey = "accounts.order";
    private const string DefaultKey = "accounts.default";

    private readonly SettingsStore _settings = settings;

    public string? DefaultAddress
    {
        get => _settings.GetString(DefaultKey) is { Length: > 0 } address ? address : null;
        set => _settings.Set(DefaultKey, value ?? string.Empty);
    }

    public int IndexOf(string address)
    {
        var index = Order().FindIndex(
            a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));

        // Unranked accounts sort after ranked ones rather than jumping to the front.
        return index < 0 ? int.MaxValue : index;
    }

    public void Register(string address)
    {
        var order = Order();
        if (!order.Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            order.Add(address);
            Save(order);
        }

        // The first account to arrive is the default; without one nothing knows where to send.
        if (DefaultAddress is null) DefaultAddress = address;
    }

    public void Forget(string address)
    {
        var order = Order();
        order.RemoveAll(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));
        Save(order);

        if (!string.Equals(DefaultAddress, address, StringComparison.OrdinalIgnoreCase)) return;

        // Removing the default has to leave one behind, not none.
        DefaultAddress = order.FirstOrDefault();
    }

    public void Move(string address, int direction)
    {
        var order = Order();
        var index = order.FindIndex(
            a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));

        var target = index + direction;
        if (index < 0 || target < 0 || target >= order.Count) return;

        // Removed and reinserted rather than swapped. For the ↑ and ↓ buttons the two are the
        // same, but a swap over a longer move drags whatever was at the far end back to where
        // the account came from, which is not what "move up two" means.
        order.RemoveAt(index);
        order.Insert(target, address);
        Save(order);
    }

    private List<string> Order() =>
    [
        .. _settings.GetString(OrderKey)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    ];

    private void Save(List<string> order) => _settings.Set(OrderKey, string.Join('\n', order));
}
