# Nome Play Music — a WinUI 3 port of GNOME Music

A Windows port of [GNOME Music](https://gitlab.gnome.org/GNOME/gnome-music) (the GNOME
desktop's music player), written in C# with WinUI 3 / Windows App SDK. It follows the
current upstream `master` (51.alpha, September 2026): the same views, flows, strings and
playback rules, rebuilt on Windows platform services instead of GStreamer, Tracker and
GSettings.

The UI is localized in English and Simplified Chinese; the Chinese strings are GNOME's
own `zh_CN` translation.

## Download

Portable Windows x64 builds are on the
[Releases](https://github.com/Leah16/NomePlayMusic_Windows/releases) page: unzip one
anywhere and run `Gnome Music WinUI.exe` (Windows 10 1809 or later). Nothing to install:
.NET and the Windows App SDK are included. Settings, playlists and the library cache are
kept in `%LOCALAPPDATA%\GnomeMusicWinUI`. Each release's notes say what changed, in
Chinese and English (they are also in [`release-notes`](release-notes)).

## Features

- **Albums**: grid of all albums sorted by title (192 px covers, 1–10 stretched columns),
  opening an album page with the cover, "year, N minutes", composer, play button and
  album menu (Play / Add to Favorite Songs / Add to Playlist…) and a card per disc.
- **Artists**: sidebar of artists (album artist, else track artist) with the selected
  artist's albums stacked on the right. Clicking a song plays all of the artist's songs.
- **Playlists**: *Favorite Songs* and *Recently Played* (the two the port keeps of
  GNOME Music's six smart playlists), a line, then the user's playlists, newest first.
  *Favorite Songs* holds the starred songs in the user's order: a song starred goes to
  the top, one removed from the list is unstarred (with *Undo*), and the songs can be
  dragged into another order. *Recently Played* is a history of the last 100 songs, the
  latest first: a song goes to the top as soon as it starts playing; it cannot be
  reordered, added to or taken from. The first time, they show what the smart playlists
  did: the starred songs, the latest added first, and the songs by their last play.
  *New Playlist…* at the foot of the sidebar asks for a name; right-clicking a playlist
  plays it, and renames or deletes one of the user's (with *Undo*; the playlist's page
  renames inline). User playlists take songs from *Add to Playlist…* (which also lists
  Favorite Songs: adding stars the songs), lose them with *Undo* and are reordered by
  dragging.
- **Search**: type anywhere or press Ctrl+F. Artists (one row), albums (two rows) with
  "View All" pages, and songs; accent- and case-insensitive; 50 results per category.
- **Player bar**: cover and title/artist (click them for the lyrics); in the middle the repeat mode menu
  (Shuffle/Repeat Off, Repeat Song, Repeat All, Shuffle), previous / play-pause / next
  and the song's star above the seek bar; at the end the play queue, the mini player,
  the audio output and the volume popover. Unlike GNOME Music, the repeat mode and the star sit next to the playback
  buttons, and a line separates the bar from the page above it, whatever the page (the
  lyrics too). The progress bar's length depends only on the window's width: the song's
  title, artist and format line may take 200 px (less where the centre part would get
  narrower than 360 px), longer ones end in an ellipsis, rather than pushing the bar
  shorter as GNOME Music's centre box does. On narrow windows the title and artist get shorter; the window does not get
  smaller than 640 × 480 (GNOME Music: 360 × 294), so the bar always shows the cover, the
  song and all its buttons, and the title bar the view switcher or the search entry.
  Under the artist it can show the file's bit depth and sample rate ("24 bit · 96 kHz";
  the bit rate for lossy files, "DSD64 · 2.8224 MHz" for DSD), off by default.
- **Play queue** (not in GNOME Music): the queue button on the player bar opens an
  overlay with the songs of the queue in play order, shuffled or not, starting at the
  playing song. Clicking a song plays it and keeps the order.
- **Mini player** (not in GNOME Music, after the Windows media player's): the button
  next to the queue replaces the window with a small square that stays on top of the
  other windows, filled with the cover. Pointing at it shows previous, play/pause and
  next, the song and *Back to Full View* over a blurred, dimmed copy of the cover; a
  thin bar along the bottom edge shows the progress. It has no title bar: drag it
  anywhere to move it. Its edges and corners resize it, always as a square
  (`SquareWindow`), down to 200 px; below 240 px the song's title and artist hide and
  only the buttons show over the cover. It reopens where it was, for as long as the
  app runs. Preferences → Player Controls → *Mini player shows* sets what fills it:
  the *Cover* (the default, as above), the *Controls* over the blurred cover all the
  time, or the *Lyrics*: the lyrics page's lines on white in the cover's color, larger
  for the small window and fewer, which cannot be clicked, scrolled or right-clicked
  but glide to the next line like the page's (lyrics without times scroll along with
  the song), and no progress bar; pointing at it blurs the lyrics themselves under the
  controls, with a darker veil than the cover's for the white page (a small picture of
  the lyrics, blurred like the cover and taken again as the lines move: in-app acrylic
  draws nothing in a Remote Desktop session). Instrumentals and songs without lyrics
  (or while they load) show the cover instead, as the preference's note says.
- **Lyrics** (not in GNOME Music): clicking the song on the player bar (its cover, title
  or artist) covers the window with its lyrics from [LRCLIB](https://lrclib.net), or
  from its lyrics file: the `.lrc` file with the song's name in its folder
  (`Song.flac` → `Song.lrc`), as other players keep them, or the app's own lyrics
  cache (`lyrics` in its data folder). Preferences → Player Controls → Lyrics has four
  options. *Load local lyrics* (off by default) reads that file first, then the cache,
  and asks LRCLIB only when there is neither; off, neither is read and LRCLIB is asked
  every time (once per session). *Download lyrics* (off by default) saves what LRCLIB
  finds where *Save lyrics to* says: the song's folder (the default) or the lyrics
  cache, which leaves the music folders untouched; lyrics already saved, maybe the
  user's own, are never overwritten. *Don't search online again for lyrics not found*
  (off by default) lists the songs LRCLIB has no lyrics for in the cache
  (`not-found.txt`); from then on, without local lyrics they show "No lyrics found" at
  once, LRCLIB is not asked again (lyrics saved for the song later, from its
  properties, take it off the list; clearing the cache forgets them all). Lyrics files are read in UTF-8 or UTF-16 with a
  BOM, UTF-8, or the system's ANSI code page (GBK on Chinese Windows), and written as
  UTF-8 with a BOM. A song set as instrumental (in its properties) has no lyrics
  looked up anywhere; its page shows "Instrumental", the title, the album and the
  artist. Clicking the song again, the back button at the top left, or Esc, goes back;
  right-clicking the page offers *Manage Lyrics*, the song's properties at their
  lyrics page. The page is plain white with large bold lines
  in the color of the cover, and there is no title bar over it: the lines scroll up to
  the top edge, under the caption buttons, which stay in place (that band still drags
  the window). While a song plays and the pointer rests in the window for 3 s, or
  leaves it, the player bar, the back button and the caption buttons step aside and
  the lines show down to the bottom edge; moving the pointer in the window, tabbing
  into the bar or playing again after a pause brings them back. They stay while
  nothing plays, while the pointer rests on them and while a flyout of the bar (the
  play queue, the audio output, the volume, the repeat modes) is open. Synced lyrics follow
  the song: the current line is in full color and stays a little above the middle,
  played lines are faint and the next ones in between. The lines glide up to the next
  one (400 ms, easing out) with an animation of their own, so they do even with the
  system's animations off (as in a Remote Desktop session), where the scroller's own
  animation would jump; clicking a line plays the song
  from there, and after scrolling by hand the lyrics follow the song again 4 s later.
  Lyrics without times say "Lyrics not synced" at the top; when LRCLIB has none, the
  page says "No lyrics found" (or shows the song as an instrumental when LRCLIB lists
  it as one). With the Simplified Chinese UI, lyrics in traditional characters show in
  simplified ones (downloaded lyrics are saved that way too). When the lyrics file
  changes, say in an editor, the lyrics load again once the window is active again.
- **Playback** follows GNOME Music's queue rules: gapless transitions, "previous"
  restarts after 5 s, shuffle that keeps played songs in place, play counts after 50 %
  of a song has actually been heard, failed files skipped and marked. Without any
  playback device (the only one unplugged, a remote session disconnected) nothing is
  skipped or marked: the song pauses where it was, a toast says so, and Play goes on
  from there once there is a device; a device that comes back within 10 s of the
  music losing it plays it on by itself. When the song playing leaves the library (its
  folder is taken out of the music folders, or deleted), the music stops, the queue
  empties and the player bar goes. The port adds
  polarity inversion, mono output and swapped left and right, which apply at once.
- **Music output** (not in GNOME Music): WASAPI, shared (through the Windows mixer) or
  exclusive (the device alone, at each file's own rate and bit depth: bit-perfect at
  full volume), DirectSound, or ASIO (the sound card's own driver); any device of the
  interface, or the default one, which the music follows when Windows changes it. For
  the chosen device the preferences list the PCM rates, bit depths, channel counts and
  DSD rates it takes (DoP, and native DSD for ASIO drivers that support it). When the
  chosen output cannot be opened, the music plays through the default device and a
  toast says so. The audio output button on the player bar (the speaker next to the
  volume) switches the interface, exclusive mode and the device in one click each; a
  song playing goes on through the new output. Its accent (the selected interface and
  device, exclusive mode switched on) takes the color of the cover, like the play button.
- **DSD** (not in GNOME Music): DSF and DSDIFF files (uncompressed; DST is not
  supported), with their ID3 tags (or the DSDIFF title and artist). They play converted
  to PCM at 88.2 kHz (96 kHz for the 48 kHz family) with a gain of 0 to +6 dB, since
  DSD's reference level comes out at −6 dBFS; as DoP (for DACs that unpack it, through
  WASAPI exclusive or ASIO); or natively through an ASIO driver that takes DSD. DoP and
  native DSD skip the volume, ReplayGain and mono; polarity inversion and swapped
  channels still apply. When the output cannot take DoP or native DSD, the file is
  converted to PCM.
- **Song properties** (not in GNOME Music): ⋯ → *Properties* on a song opens a window
  in two panes. *Properties* has the file (type, size, date, location), the
  audio stream (duration, format, sample rate, bit depth, channels, bit rate, encoder,
  tag format), the play statistics and the ReplayGain values. *Tags* has the cover and
  every tag: title, artists, album, year, track and disc with their totals, genre,
  composer, conductor, publisher, grouping and comment. The tags come from the file
  (FLAC's Vorbis comments, ID3v2 in MP3, WAV, AIFF and DSD files; `TagDump`), else from
  Windows. Both are read only. *Lyrics* needs the lyrics, *Load local lyrics* and
  *Download lyrics* all on: otherwise it only says so, with a button to Player
  Controls, and nothing on it (instrumental, edit, search) can be used. With them on it
  shows the song's lyrics as the lyrics page would, with their times and where they
  come from. The lyrics page's *Manage Lyrics* opens the
  properties there. At the top, *Set as instrumental* (kept in
  `songs.json`) stops all lookups for the song. *Edit* opens the local lyrics (the
  lyrics file, else the cached ones) in
  another app: the one Windows opens `.lrc` files with (it asks when there is none), or
  one picked from the menu; it needs the lyrics saved locally, and the
  page shows the changes when the window is active again. *Search Online* searches
  LRCLIB for a title and an artist the user can change; the results (synced ones and
  those of about the song's duration first, a check on the one in use) show their
  lyrics when selected, and the download button saves the selected one in place of
  the local lyrics (once confirmed), else where *Save lyrics to* says (*Download to
  Song Folder* or *Download to Lyrics Cache*).
- **Favorites** (star) make up *Favorite Songs*; play counts and last-played dates
  show in the song's properties.
- **Preferences**, a view like the albums, artists and playlists rather than a dialog:
  the title bar's ☰ button, where GNOME Music has its primary menu, shows it and is selected
  while it shows (Ctrl+, from anywhere). Its categories are in a sidebar, like Windows
  Settings: *Playback* (repeat mode, ReplayGain, the channel processing above, inhibit
  suspend while playing), *Music Output*, *Player Controls* and — Windows only — *Music
  Folders* and *Reset*; at the sidebar's foot, what the primary menu held besides: *Keyboard
  Shortcuts* (Ctrl+? opens them) and *About*, whose links start with the GNOME help
  (F1 opens it too). Under Player Controls the mini player, the audio output button
  (*Quick Device Switch*), the volume control and the lyrics can each be turned off (all
  on by default): their buttons and shortcuts go, and without the volume control the
  music plays at full volume. Under the lyrics are where they come from and go: *Load
  local lyrics*, *Download lyrics*, *Save lyrics to* (while downloads are on) and
  *Don't search online again for lyrics not found* (see *Lyrics* above). The
  playlists are always there. *Reset* clears the lyrics cache (it shows how many files
  it holds and their size; the lyrics next to the songs stay), clears the play counts
  (favorites, last played dates and Recently Played stay), and resets every preference
  to its default (the music folders and the window size stay); each asks first. The
  settings go without descriptions: their names say what they do, all but two notes:
  the mini player's lyrics mode says that songs without lyrics show the cover, and the
  volume control that without it the app's output volume is fixed at 100%. The ☰ item
  is not a tab stop (a selector bar selects the item that gets the focus, which opened
  the preferences over the welcome page of an empty library); the keyboard uses Ctrl+,.
- **System media controls**: media keys, the Windows media flyout and the lock screen
  show the song, cover and play state and can seek (the MPRIS equivalent).
- **Taskbar** (not in GNOME Music): previous, play/pause and next under the window's
  thumbnail, the mini player's too (`TaskbarButtons`, the ITaskbarList3 thumbnail
  toolbar). Like other players' they are always there: previous and next disabled when
  the queue has none, play disabled with nothing to play (it starts an ended queue
  again, like the mini player's). Their glyphs are the player bar's, drawn from
  the icon font (`GlyphIcon`) in the text color of the taskbar's light or dark theme,
  and again when the theme or the scale changes.

## How GNOME's Linux services map to Windows

| GNOME Music | This port |
|---|---|
| Tracker / LocalSearch indexer | `LibraryScanner`: walks the music folders and reads tags through the Windows property system; results are cached in `library.json`, so only new or changed files are read on later starts. Folders are watched for changes. |
| GStreamer tag parsing (used by LocalSearch) | `TagReader` covers what the Windows property system misses or garbles: the `id3 ` chunk of WAV and AIFF files (Windows reads only the RIFF INFO list of WAV files), and UTF-8 text in legacy-encoded tags (RIFF INFO, ID3v2 frames marked ISO-8859-1). Text that is not valid UTF-8 is decoded with the ANSI code page, as Windows does. |
| Tracker database (favorites, play counts, playlists) | JSON files in the app's local data folder (`songs.json`, `playlists.json`) |
| GStreamer `playbin3` | `AudioEngine` over its own pipeline (`Services/Audio`): Media Foundation's source reader decodes (DSF and DSDIFF are read by the port), a thread fills a ring of frames — resampled and remapped for the device, or DoP frames and DSD bytes — and the output's thread takes them through WASAPI, DirectSound or ASIO. The next song joins in the same ring when the device format allows (gapless). |
| rgvolume / rglimiter (ReplayGain) | `ReplayGain`: reads REPLAYGAIN_* tags from ID3v2/APEv2, FLAC and MP4 and scales the volume |
| MediaArt cache + embedded art | Windows thumbnail of the first song (re-encoded to JPEG), else the picture in its ID3v2 tag (WAV files have no Windows thumbnails), else `cover.jpg`, `folder.jpg`, … next to the songs |
| MPRIS | System Media Transport Controls (`MediaControls`), driven by hand: a `MediaPlayer` that never plays only provides them |
| logind inhibitor / PrepareForSleep | `PowerCreateRequest` ("Playing music") / `PowerManager.SystemSuspendStatusChanged` |
| GSettings `org.gnome.Music` | `settings.json` with the same keys |
| libadwaita widgets and style | WinUI controls in the [Fluent 2](https://fluent2.microsoft.design) design language (see *Look* below), with GNOME Music's layout |

GNOME Music can fetch missing album covers and artist pictures online (MusicBrainz,
Last.fm and TheAudioDB through Grilo). The port does not: albums without art show the
placeholder, and artists show one of their album covers. Its only network requests are
for lyrics: while the lyrics show, the title, artist, album and duration of the playing
song and of the next one are sent to LRCLIB (`LyricsService`, `LrclibClient`), as are
those of a song whose lyrics page is open in its properties, and the title and artist
searched there. Found lyrics are kept for the session only, unless *Download lyrics*
saves them, next to the songs or in the lyrics cache; with *Don't search online again
for lyrics not found*, songs LRCLIB had nothing for are not sent again. (Earlier
versions kept what they found as JSON files in the same `lyrics` folder; those are not
read, and clearing the cache deletes them too.)

## Look

The layout is GNOME Music's; the styling follows [Fluent 2](https://fluent2.microsoft.design)
through WinUI's own design tokens, so high contrast themes apply. The accent color is
the app's own rather than the Windows one: the red-pink of its icon, #FF2D55.
`App.xaml` sets the `SystemAccentColor` ramp that the WinUI styles read, in steps of
lightness of that hue; accent fills use its darker step #DB0040, for a 5:1 contrast
with their white glyphs. For now the app always uses the light theme (`RequestedTheme="Light"`
in `App.xaml`; remove it to follow the system theme).

- **Materials**: no Mica. The window is opaque, and each layer is a lighter solid
  fill (WinUI's `SolidBackgroundFillColor*` tokens) than the one below it: the artists
  and playlists sidebars and the player bar sit on the base; the content is a layer
  on top, outlined by a stroke, with rounded inner corners next to a sidebar; cards on
  it are white. A line (`SurfaceStrokeColorDefault`) sets the player bar apart from
  every page above it, and the app's playlists apart from the user's. Only the title bar is acrylic, through a `SystemBackdropElement` that
  fills it. Toasts and the play queue are solid white cards; menus and the other
  flyouts keep the system's acrylic; dialogs dim the window with smoke. The lyrics page
  is solid white and covers the title bar too: its lines reach up to the top edge.
- **Title bar**: icons only. The view switcher shows the albums, artists and
  playlists icons (their names are tooltips); search and ☰ (the preferences) are on
  the right, next to the caption buttons, and keep their places while searching; the
  ☰ is selected like the switcher's views while the preferences show. Pushed pages (an
  album, an artist, "View All") keep the same header, with the view they were opened
  from selected; tapping that view goes back to it (GNOME Music shows just a back
  button there). The back button on the left shows while it leads somewhere: it leaves
  a pushed page, then search, then goes back through the views shown before (albums,
  artists, playlists, preferences); with nowhere to go, on the welcome page and over
  the lyrics it is not there. Alt+← and the mouse's back button do the same. Without
  music (no music folder, or none with songs the app can play) the title bar has only
  ☰ and the caption buttons, in the preferences too (plus the back button there): no
  view switcher and no search, whose views would be empty.
- **Motion**: pushed pages slide in from the right and slide back out to the right;
  the lyrics come up from the player bar and fade back into it.
- **Buttons**: subtle icon buttons in the title bar, the song rows and the player
  bar; standard buttons with an elevation edge, also for play on the artist and
  playlist pages; the accent color for the primary actions of dialogs and the
  welcome page, and for toggled buttons.
- **Cover colors**: the color of a cover (`CoverColors`: its most prominent colorful
  hue, or a gray, toned like the Fluent accent fills) takes the accent's place. The
  player bar's play/pause button and progress bar, the playing song in lists (its
  icon and title) and the lyrics show the color of the song's cover; the album page's
  play button shows the album's. It has a 5:1 contrast with white, so both white glyphs on it and
  its text stay readable. Without a cover it is the accent color.
- **Shapes**: 4 px corners, 8 px for large buttons, cards and covers, none at the
  window edges; circles only for artists.
- **Type**: the Windows type ramp (Segoe UI Variable): Title, Subtitle, Body strong,
  Body and Caption, with secondary text for details.

## Building and running

Requirements: Windows 10 1809 or later, Visual Studio 2022/2026 with the *WinUI
application development* workload (or the .NET 8+ SDK), Windows App SDK 2.5.

- Open `Gnome Music WinUI.slnx` and run the **Gnome Music WinUI (Package)** profile.
- By default the Windows *Music* folder is scanned; add other folders under
  ☰ (Preferences) → Music Folders, or from the welcome page.
- App data (library cache, playlists, statistics, cover cache, `gnome-music.log`)
  lives in the package's `LocalState` folder (unpackaged: `%LOCALAPPDATA%\GnomeMusicWinUI`);
  lyrics files live next to the songs.
- To distribute it as a package, use *Package and Publish → Create App Packages* in
  Visual Studio. Release builds are **not trimmed**: the collections and model objects
  bound to XAML lists get their COM wrappers through CsWinRT's reflection fallback,
  which the trimmer removes, so a trimmed build fails at start-up (see the comment in
  the `.csproj`).
- The portable version (unpackaged, self-contained, as on the Releases page) is built by
  GitHub Actions (`.github/workflows/windows-x64.yml`): on every push to `main` (kept as
  a workflow artifact), and published as a release for a version tag (`v1.2.3`) or when
  the workflow is run by hand with a version; `release-notes/v1.2.3.md`, when there is
  one, goes into that release's notes. The same build locally:
  `dotnet publish "Gnome Music WinUI/Gnome Music WinUI.csproj" -c Release -r win-x64
  -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
  --self-contained true -p:PublishReadyToRun=false -o publish`.

Supported formats are those Windows can decode: MP3, AAC/M4A (incl. ALAC), FLAC, WAV,
WMA, and Ogg/Opus when the corresponding Windows media extensions are installed; and
DSD in DSF and DSDIFF files, which the port decodes itself.

The ASIO output, DoP and native DSD are written to the ASIO and DoP specifications but
were only checked with software (no ASIO driver or DoP DAC was at hand); WASAPI shared
and DirectSound output, DSD to PCM conversion and the DoP frames are covered by tests.

## Project layout

```
Gnome Music WinUI/
  App.xaml(.cs), MainWindow.xaml(.cs)   application, window, header bar, shortcuts
  MiniPlayerWindow.xaml(.cs)            the mini player
  Models/      CoreSong, CoreAlbum/CoreDisc/CoreArtist, Playlist (user playlists,
               Favorite Songs, Recently Played), RepeatMode
  Services/    CoreModel (library, playlists, search), LibraryScanner, TagReader, Id3v2,
               Player, PlayQueue, AudioEngine, MediaControls, ArtService, CoverColors,
               CoverBlur, LyricsService, LrclibClient, LyricsFile (the .lrc next to a
               song), Lyrics (LRC), ReplayGain, Settings, OutputDevices,
               SongDetails and TagDump (the properties window),
               UserDataStore, PlaylistStore, PowerServices (inhibit suspend, pause on
               suspend), JsonStorage, Log
    Audio/     PlaybackPipeline (decoding thread, gapless, seeking, output fallback),
               MediaFoundationDecoder, Dsd (DSF/DSDIFF, DSD to PCM), Resampler and
               ChannelMapper, RenderSource (volume, polarity, mono, swap; DoP frames),
               FrameRing, WasapiOutput (with device probing and DeviceWatcher),
               DirectSoundOutput, AsioOutput, NativeCom, AudioTypes
  Views/       MainPage, AlbumsView, ArtistsView, PlaylistsView, SearchView, StatusView,
               PreferencesView (with the keyboard shortcuts and About), AlbumPage,
               ArtistPage, AlbumsSearchPage, ArtistsSearchPage
  Controls/    AlbumTile, AlbumWidget, DiscBox, SongRow, StarToggle, CoverArt, ArtistTile,
               ArtistSearchTile, ArtistAlbumsWidget, PlaylistTile, PlaylistsWidget,
               PlayerToolbar, PlayQueueView, QueueRow, OutputPicker, LyricsView,
               RepeatModeButton, VolumeButton, ToastOverlay, Clamp
  Dialogs/     PlaylistDialog, SongPropertiesDialog
  Helpers/     Strings, SongActions, TaskbarButtons and GlyphIcon (the taskbar
               thumbnail's buttons), SquareWindow (the mini player's shape), …
  Strings/     en-US and zh-Hans resources
  Themes/      Styles.xaml (Fluent 2 look: surfaces, type ramp, buttons, lists)
```

Class and file names follow GNOME Music's modules (`coremodel.py` → `CoreModel`,
`songwidget.py` → `SongRow`, …) to make it easy to compare with upstream.

## License

GNOME Music is free software released under the GNU General Public License, version 2
or (at your option) any later version. As a derivative work (design, behaviour, strings
and translations), this port is distributed under the same terms:
**GPL-2.0-or-later**. The full license text is in [`COPYING`](COPYING) (also at
<https://www.gnu.org/licenses/old-licenses/gpl-2.0.html>).

This port is developed and designed by Murph Leah and Claude. Preferences → About lists
them first under Developers and Designers, followed by GNOME Music's.

The Simplified Chinese strings come from GNOME Music's `po/zh_CN.po` (translators:
tuhaihe, Tong Hui, sphinx, Mingcong Bai, Dingzhong Chen, Cheng Lu, lumingzh,
Boyuan Yang). The app icon (drawn in `Assets/AppIcon.svg`, with `Assets/AppIconSmall.svg`
for 16–48 px; the PNG and ICO assets are rendered from them) and the welcome
illustration were made for this port, and the
UI glyphs come from the Windows Segoe Fluent Icons font; no GNOME artwork is included.

"GNOME" is a trademark of the GNOME Foundation; this is an unofficial port.
