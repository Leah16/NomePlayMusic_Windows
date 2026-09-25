// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using Gnome_Music_WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Gnome_Music_WinUI.Views;

/// <summary>
/// About (gnomemusic/about.py, AdwAboutDialog), with the help that the primary menu
/// had: the app, its version and links, then the credits and the legal notice.
/// </summary>
public sealed partial class PreferencesView
{
    public const string HelpUrl = "https://help.gnome.org/users/gnome-music/stable/";
    public const string Website = "https://apps.gnome.org/Music/";
    public const string IssueUrl = "https://gitlab.gnome.org/GNOME/gnome-music/-/issues/";
    public const string SourceUrl = "https://gitlab.gnome.org/GNOME/gnome-music";
    public const string LicenseUrl = "https://www.gnu.org/licenses/old-licenses/gpl-2.0.html";

    /// <summary>This port's developers and designers, listed before GNOME Music's.</summary>
    private static readonly string[] PortCredits = { "Murph Leah", "Claude" };

    private static readonly string[] Developers =
    {
        "Abhinav Singh", "Adam Blanchet", "Adrian Solom", "Alberto Fanjul", "Alexander Mikhaylenko",
        "Alireza Shabani", "Alpesh Jamgade", "Andre Klapper", "Andreas Nilsson", "Apostol Bakalov",
        "Arnel A. Borja", "Ashwani Singh Tanwar", "Ashwin Mohan", "Atharva Veer", "Automeris Naranja",
        "Benoît Legat", "Bilal Elmoussaoui", "Billy Barrow", "Bruce Cowan", "Carlos Garnacho",
        "Carlos Soriano", "Chinmay Gurjar", "Christophe van den Abbeele", "Christopher Davis", "Clayton G. Hobbs",
        "Divyanshu Vishwakarma", "Dominique Leuenberger", "Eslam Mostafa", "Elias Entrup", "Erik Inkinen",
        "Evan Nehring", "Evandro Giovanini", "Ezike Ebuka", "Fabiano Fidêncio", "Feliks Weber",
        "Felipe Borges", "Florian Darfeuille", "Gaurav Narula", "Georges Basile Stavracas Neto", "Guillaume Quintard",
        "Gyanesh Malhotra", "Harry Xie", "Hugo Posnic", "Ishaan Shah", "Islam Bahnasy",
        "Jakub Steiner", "James A. Baker", "Jan Alexander Steffens", "Janne Körkkö", "Jan-Michael Brummer",
        "Jean Felder", "Jeremy Bicha", "Jesus Bermudez Velazquez", "Jordan Petridis", "Juan José González",
        "Juan Suarez", "Kainaat Singh", "Kalev Lember", "Kevin Haller", "Konstantin Pospelov",
        "Koushik Sahu", "Lucy Coleclough", "Marinus Schraal", "Michael Catanzaro", "Mohanna Datta Yelugoti",
        "Mpho Jele", "Nick Richards", "Niels De Graef", "Nikolay Yanchuk", "Nils Reuße",
        "Pablo Palácios", "Phil Dawson", "Piotr Drąg", "Prashant Tyagi", "Rafael Coelho",
        "Rashi Sah", "Rasmus Thomsen", "Reuben Dsouza", "Robert Greener", "Sabri Ünal",
        "Sagar Lakhani", "Sai Suman Prayaga", "Sam Hewitt", "Sam Thursfield", "Sambhav Kothari",
        "Seif Lotfy", "Shema Angelo Verlain", "Shivani Poddar", "Shivansh Handa", "Simon McVittie",
        "Sophie Herold", "Subhadip Jana", "Sumaid Syed", "Suyash Garg", "Tapasweni Pathak",
        "Tau Gärtli", "Taylor Garcia", "Tjipke van der Heide", "Vadim Rutkovsky", "Veerasamy Sevagen",
        "Vincent Cottineau", "Vineet Reddy", "Walt Shabani", "Weifang Lai", "Yann Delaby",
        "Yash Singh", "Yosef Or Boczko",
    };

    private static readonly string[] Designers = { "Allan Day", "Jakub Steiner", "William Jon McCann" };

    /// <summary>GNOME Music's help (F1, and the first link of About).</summary>
    public static async void OpenHelp() => await Launcher.LaunchUriAsync(new Uri(HelpUrl));

    private void BuildAbout()
    {
        var header = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 12) };
        header.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")),
            Width = 128,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        header.Children.Add(Centered(new TextBlock { Text = Strings.AppName, Style = Resource<Style>("TitleLabelStyle") }));
        header.Children.Add(Centered(new TextBlock { Text = Strings.GnomePortProject }));
        header.Children.Add(Centered(new Border
        {
            Style = Resource<Style>("TagStyle"),
            Child = new TextBlock { Text = AppVersion(), Style = Resource<Style>("CaptionTextBlockStyle") },
        }));
        header.Children.Add(Centered(new TextBlock
        {
            Text = Strings.PortDescription,
            Style = Resource<Style>("SecondaryLabelStyle"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalTextAlignment = TextAlignment.Center,
            MaxWidth = 520,
            Margin = new Thickness(0, 6, 0, 6),
        }));

        // The help first: it was in the primary menu.
        var links = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
        links.Children.Add(new HyperlinkButton { Content = Strings.Help, NavigateUri = new Uri(HelpUrl) });
        links.Children.Add(new HyperlinkButton { Content = Strings.Website, NavigateUri = new Uri(Website) });
        links.Children.Add(new HyperlinkButton { Content = Strings.AboutReportIssue, NavigateUri = new Uri(IssueUrl) });
        links.Children.Add(new HyperlinkButton { Content = Strings.AboutSourceCode, NavigateUri = new Uri(SourceUrl) });
        header.Children.Add(links);
        AboutPage.Children.Add(header);

        AboutPage.Children.Add(Section(Strings.AboutDevelopers, Credits(Developers)));
        AboutPage.Children.Add(Section(Strings.AboutDesigners, Credits(Designers)));
        var translators = Strings.TranslatorCredits;
        if (!string.IsNullOrWhiteSpace(translators) && translators != "TranslatorCredits")
            AboutPage.Children.Add(Section(Strings.AboutTranslators, translators));

        // Legal, as AdwAboutDialog shows GNOME Music's license type (Gtk.License.GPL_2_0):
        // the copyright, then the notice with the license's name linking to its text.
        var notice = Strings.LicenseNotice.Split("{0}");
        var license = new Hyperlink { NavigateUri = new Uri(LicenseUrl) };
        license.Inlines.Add(new Run { Text = Strings.LicenseName });
        AboutPage.Children.Add(Section(Strings.AboutLegal,
            new Run { Text = Strings.Copyright }, new LineBreak(), new LineBreak(),
            new Run { Text = notice[0] }, license, new Run { Text = notice.Length > 1 ? notice[1] : "" }));
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    private static FrameworkElement Centered(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return element;
    }

    // This port's people first, then a blank line and GNOME Music's.
    private static string Credits(string[] gnome) =>
        $"{string.Join("\n", PortCredits)}\n\n{string.Join("\n", gnome)}";

    private static Expander Section(string header, string body) => Section(header, new Run { Text = body });

    private static Expander Section(string header, params Inline[] body)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        foreach (var inline in body)
            text.Inlines.Add(inline);

        return new()
        {
            Header = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
            Content = text,
        };
    }

    private static string AppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (InvalidOperationException)
        {
            return typeof(PreferencesView).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }
}
