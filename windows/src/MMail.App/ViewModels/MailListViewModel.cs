using System.Collections.ObjectModel;
using MMail.Core.Models;
using MMail.Core.State;

namespace MMail.App.ViewModels;

/// The rows of the mail list (section titles and messages, one flat list), kept in
/// step with the controller with moves rather than resets, so the list never jumps.
/// While the pointer is over the list the order is frozen: rows update in place and
/// only leave when they no longer belong (archived, deleted), never reshuffle.
public sealed class MailListViewModel(MailController model)
{
    public ObservableCollection<object> Rows { get; } = [];
    private readonly Dictionary<string, MessageItem> _items = [];
    private readonly Dictionary<string, SectionHeaderItem> _headers = [];
    private bool _frozen, _pendingReorder;

    public bool Frozen
    {
        get => _frozen;
        set
        {
            _frozen = value;
            if (!value && _pendingReorder) Sync();
        }
    }

    public MessageItem Item(Email email)
    {
        if (_items.TryGetValue(email.Id, out var item) && item.Email == email) return item;
        item = new MessageItem(email, model);
        email.PropertyChanged += (_, _) => item.Refresh();
        _items[email.Id] = item;
        return item;
    }

    public IEnumerable<MessageItem> MessageRows => Rows.OfType<MessageItem>();

    public void Sync()
    {
        var visible = model.VisibleEmails();
        var target = new List<object>();
        foreach (var (title, items) in model.Sections(visible))
        {
            if (title is not null)
            {
                if (!_headers.TryGetValue(title, out var header)) _headers[title] = header = new SectionHeaderItem(title);
                target.Add(header);
            }
            foreach (var e in items)
            {
                var item = Item(e);
                item.ShowAccountDot = model.CurrentAccount == "all" && model.Accounts.Count > 1;
                item.AccountColorHex = Sender.StableColorHex(e.Account);
                target.Add(item);
            }
        }

        if (_frozen)
        {
            // Keep the order on screen: drop what left, refresh the rest, add nothing.
            var keep = target.ToHashSet();
            for (var i = Rows.Count - 1; i >= 0; i--)
                if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);
            DropOrphanHeaders();
            _pendingReorder = !Rows.SequenceEqual(target);
        }
        else
        {
            Apply(target);
            _pendingReorder = false;
        }
        foreach (var row in Rows.OfType<MessageItem>()) row.Refresh();
    }

    private void DropOrphanHeaders()
    {
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (Rows[i] is SectionHeaderItem && (i == Rows.Count - 1 || Rows[i + 1] is SectionHeaderItem))
                Rows.RemoveAt(i);
    }

    /// Turns Rows into target with removes, moves and inserts.
    private void Apply(List<object> target)
    {
        var wanted = target.ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Rows[i])) Rows.RemoveAt(i);
        for (var i = 0; i < target.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], target[i])) continue;
            var at = IndexOf(target[i], i + 1);
            if (at >= 0) Rows.Move(at, i);
            else Rows.Insert(i, target[i]);
        }
        while (Rows.Count > target.Count) Rows.RemoveAt(Rows.Count - 1);
    }

    private int IndexOf(object row, int from)
    {
        for (var i = from; i < Rows.Count; i++)
            if (ReferenceEquals(Rows[i], row)) return i;
        return -1;
    }
}
