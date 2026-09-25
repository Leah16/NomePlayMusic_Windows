// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>The songs of one disc of an album (widgets/discbox.py).</summary>
public sealed partial class DiscBox : UserControl
{
    public static readonly DependencyProperty DiscProperty = DependencyProperty.Register(
        nameof(Disc), typeof(CoreDisc), typeof(DiscBox), new PropertyMetadata(null, OnDiscChanged));

    public DiscBox()
    {
        InitializeComponent();
    }

    /// <summary>A song row was activated (clicked or chosen "Play").</summary>
    public event EventHandler<CoreSong>? SongActivated;

    public CoreDisc? Disc
    {
        get => (CoreDisc?)GetValue(DiscProperty);
        set => SetValue(DiscProperty, value);
    }

    private static void OnDiscChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (DiscBox)d;
        var disc = e.NewValue as CoreDisc;
        box.TitleText.Text = disc?.Title ?? "";
        box.TitleText.Visibility = disc?.ShowTitle == true ? Visibility.Visible : Visibility.Collapsed;
        box.SongsList.ItemsSource = disc?.Songs;
    }

    private void OnSongClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CoreSong song)
            SongActivated?.Invoke(this, song);
    }

    private void OnSongPlayRequested(object? sender, CoreSong song) => SongActivated?.Invoke(this, song);
}
