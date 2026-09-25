// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>A song at a position of the play order (a queue can hold a song twice).</summary>
public sealed class QueueEntry
{
    public QueueEntry(int position, CoreSong song)
    {
        Position = position;
        Song = song;
    }

    public int Position { get; }

    public CoreSong Song { get; }

    /// <summary>The list item's name for screen readers.</summary>
    public override string ToString() => Song.ToString();
}

/// <summary>
/// The play queue overlay (not in GNOME Music): the songs of the queue in play order,
/// shuffled or not, opened with the playing song at the top. Clicking a song plays it
/// and keeps the order.
/// </summary>
public sealed partial class PlayQueueView : UserControl
{
    private readonly Player _player = App.Services.Player;
    private List<CoreSong> _songs = new();

    public PlayQueueView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _player.PropertyChanged += OnPlayerPropertyChanged;
            Refresh(scrollToCurrent: true);
        };
        Unloaded += (_, _) => _player.PropertyChanged -= OnPlayerPropertyChanged;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Another queue was loaded, or shuffle reordered the songs after the current one.
        if (e.PropertyName is nameof(Player.CurrentSong) or nameof(Player.RepeatMode))
            Refresh(scrollToCurrent: false);
    }

    private void Refresh(bool scrollToCurrent)
    {
        var queue = _player.Queue;
        if (!queue.Songs.SequenceEqual(_songs))
        {
            _songs = queue.Songs.ToList();
            SongsList.ItemsSource = _songs.Select((song, i) => new QueueEntry(i, song)).ToList();
        }

        CountText.Text = Strings.SongCount(_songs.Count);
        if (scrollToCurrent && SongsList.ItemsSource is List<QueueEntry> entries && queue.Position < entries.Count)
            SongsList.ScrollIntoView(entries[queue.Position], ScrollIntoViewAlignment.Leading);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QueueEntry entry)
            _player.JumpTo(entry.Position);
    }
}
