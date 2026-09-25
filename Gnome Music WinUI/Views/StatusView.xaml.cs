// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using System.Linq;
using Gnome_Music_WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.System;

namespace Gnome_Music_WinUI.Views;

/// <summary>
/// Shown instead of the views while the library is empty
/// (widgets/statusnavigationpage.py, state EMPTY).
/// </summary>
public sealed partial class StatusView : UserControl
{
    public StatusView()
    {
        InitializeComponent();
        Update();
    }

    public void Update()
    {
        var folder = MusicFolder();
        if (folder is null)
        {
            // GNOME: "Your XDG Music directory is not set."
            DescriptionBefore.Text = Strings.MusicFolderNotSet;
            DescriptionAfter.Text = FolderLinkText.Text = "";
            return;
        }

        // "The contents of your {} will appear here.", {} being a link to the folder.
        var template = Strings.ContentsWillAppear;
        int index = template.IndexOf("{0}", StringComparison.Ordinal);
        DescriptionBefore.Text = index >= 0 ? template[..index] : template;
        DescriptionAfter.Text = index >= 0 ? template[(index + 3)..] : "";
        FolderLinkText.Text = index >= 0 ? Strings.MusicFolder : "";
    }

    private static string? MusicFolder() =>
        App.Services.Settings.LibraryFolders.FirstOrDefault(Directory.Exists)
        ?? App.Services.Settings.LibraryFolders.FirstOrDefault();

    private async void OnFolderLinkClick(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        var folder = MusicFolder();
        if (folder is null)
            return;

        Directory.CreateDirectory(folder);
        await Launcher.LaunchFolderPathAsync(folder);
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e) =>
        await LibraryFolders.AddFolderAsync();
}
