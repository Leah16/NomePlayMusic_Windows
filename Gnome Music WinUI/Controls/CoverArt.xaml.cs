// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// Album art with a placeholder while loading or when there is none: the port of
/// AlbumCover / CoverPaintable. Set <see cref="Album"/> or <see cref="Song"/>.
/// </summary>
public sealed partial class CoverArt : UserControl
{
    public static readonly DependencyProperty AlbumProperty = DependencyProperty.Register(
        nameof(Album), typeof(CoreAlbum), typeof(CoverArt), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty SongProperty = DependencyProperty.Register(
        nameof(Song), typeof(CoreSong), typeof(CoverArt), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(CoverArt), new PropertyMetadata(160.0, OnSizeChanged));

    /// <summary>folder-music-symbolic: the placeholder of albums and songs.</summary>
    public const string AlbumGlyph = "";

    /// <summary>music-artist-symbolic: the placeholder of artists.</summary>
    public const string ArtistGlyph = "";

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(CoverArt), new PropertyMetadata(AlbumGlyph, OnGlyphChanged));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(CoverArt), new PropertyMetadata(8.0, OnRadiusChanged));

    private int _loadId;

    public CoverArt()
    {
        InitializeComponent();
        ApplySize();
    }

    public CoreAlbum? Album
    {
        get => (CoreAlbum?)GetValue(AlbumProperty);
        set => SetValue(AlbumProperty, value);
    }

    public CoreSong? Song
    {
        get => (CoreSong?)GetValue(SongProperty);
        set => SetValue(SongProperty, value);
    }

    /// <summary>Edge length in DIPs.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Placeholder icon (Segoe Fluent Icons glyph).</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Corner radius; use half the size for a circle.</summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CoverArt)d).Reload();

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cover = (CoverArt)d;
        cover.ApplySize();
        cover.Reload();
    }

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CoverArt)d).PlaceholderIcon.Glyph = (string)e.NewValue;

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CoverArt)d).Root.CornerRadius = new CornerRadius((double)e.NewValue);

    private void ApplySize()
    {
        Width = Size;
        Height = Size;

        // coverpaintable.py draws the placeholder icon at a third of the cover size.
        PlaceholderIcon.FontSize = Math.Max(10, Math.Round(Size / 3));
    }

    private async void Reload()
    {
        int id = ++_loadId;
        ArtImage.Source = null;
        ArtImage.Opacity = 0;

        var album = Album;
        var song = Song;
        if (album is null && song is null)
            return;

        string? path;
        try
        {
            var art = App.Services.Art;
            path = album is not null ? await art.GetAlbumArtAsync(album) : await art.GetSongArtAsync(song!);
        }
        catch (Exception)
        {
            path = null;
        }

        if (id != _loadId || path is null || !File.Exists(path))
            return;

        double scale = XamlRoot?.RasterizationScale ?? 2.0;
        var bitmap = new BitmapImage
        {
            DecodePixelWidth = (int)Math.Ceiling(Size * scale),
            DecodePixelType = DecodePixelType.Physical,
            UriSource = new Uri(path),
        };
        ArtImage.Source = bitmap;
        ArtImage.Opacity = 1;
    }
}
