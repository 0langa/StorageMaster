using System.ComponentModel;
using FluentAssertions;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// C8: Verifies the subscribe/unsubscribe pattern used by CleanupViewModel
/// to prevent memory leaks when PropertyChanged handlers are not removed.
///
/// We test the pattern in isolation (no WinUI dependency) using a simple
/// INotifyPropertyChanged implementation that mirrors SuggestionItem.
/// </summary>
public sealed class C8_SuggestionUnsubscribeTests
{
    private sealed class FakeObservable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool _isSelected = true;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    [Fact]
    public void NamedHandler_CanBeUnsubscribed_StopsReceivingEvents()
    {
        var item = new FakeObservable();
        int callCount = 0;

        // Named handler — same delegate ref for subscribe + unsubscribe.
        void Handler(object? s, PropertyChangedEventArgs e) => callCount++;

        item.PropertyChanged += Handler;
        item.IsSelected = false;
        callCount.Should().Be(1, "handler should fire once after first toggle");

        // Unsubscribe.
        item.PropertyChanged -= Handler;
        callCount = 0;

        item.IsSelected = true;
        callCount.Should().Be(0,
            "handler must not fire after unsubscribe — this prevents memory leaks");
    }

    [Fact]
    public void MultipleItems_AllUnsubscribed_NoLeakedHandlers()
    {
        var items = Enumerable.Range(0, 10)
            .Select(_ => new FakeObservable())
            .ToList();

        int callCount = 0;
        void Handler(object? s, PropertyChangedEventArgs e) => callCount++;

        // Subscribe to all.
        foreach (var item in items)
            item.PropertyChanged += Handler;

        // Verify all fire.
        items[0].IsSelected = false;
        callCount.Should().Be(1);

        // Unsubscribe all (mirrors CleanupViewModel.UnsubscribeAllSuggestions).
        foreach (var item in items)
            item.PropertyChanged -= Handler;

        callCount = 0;

        // None should fire now.
        foreach (var item in items)
            item.IsSelected = !item.IsSelected;

        callCount.Should().Be(0,
            "all handlers must be unsubscribed to prevent memory leaks");
    }

    [Fact]
    public void DoubleSubscribe_SingleUnsubscribe_StillFires()
    {
        // Ensures we understand the semantics: double subscribe = double fire.
        var item = new FakeObservable();
        int callCount = 0;
        void Handler(object? s, PropertyChangedEventArgs e) => callCount++;

        item.PropertyChanged += Handler;
        item.PropertyChanged += Handler; // subscribed twice

        item.IsSelected = false;
        callCount.Should().Be(2, "double subscribe means double fire");

        // Single unsubscribe removes one.
        item.PropertyChanged -= Handler;
        callCount = 0;
        item.IsSelected = true;
        callCount.Should().Be(1, "one subscription remains after single unsubscribe");

        // Second unsubscribe clears it.
        item.PropertyChanged -= Handler;
        callCount = 0;
        item.IsSelected = false;
        callCount.Should().Be(0, "fully unsubscribed now");
    }
}
